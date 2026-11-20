#include "tsw.h"

#include <algorithm>
#include <cstring>
#include <fstream>
#include <memory>
#include <string>
#include <vector>

#include <libtorrent/bdecode.hpp>
#include <libtorrent/bencode.hpp>
#include <libtorrent/entry.hpp>
#include <libtorrent/ip_filter.hpp>
#include <libtorrent/magnet_uri.hpp>
#include <libtorrent/peer_info.hpp>
#include <libtorrent/read_resume_data.hpp>
#include <libtorrent/session_stats.hpp>
#include <libtorrent/write_resume_data.hpp>

namespace {

char* alloc_cstr(const char* s) {
    if (s == nullptr) {
        char* p = new char[1];
        p[0] = '\0';
        return p;
    }
    std::size_t const len = std::strlen(s);
    char* p = new char[len + 1];
    std::memcpy(p, s, len + 1);
    return p;
}

char* alloc_cstr(std::string const& s) {
    char* p = new char[s.size() + 1];
    std::memcpy(p, s.data(), s.size());
    p[s.size()] = '\0';
    return p;
}

} // namespace

tsw_session::~tsw_session() = default;

tsw_torrent_state tsw_map_torrent_state(lt::torrent_status::state_t st) {
    switch (st) {
        case lt::torrent_status::checking_files:
            return tsw_state_checking;
        case lt::torrent_status::checking_resume_data:
            return tsw_state_checking_resume;
        case lt::torrent_status::downloading_metadata:
            return tsw_state_downloading_metadata;
        case lt::torrent_status::downloading:
            return tsw_state_downloading;
        case lt::torrent_status::seeding:
            return tsw_state_seeding;
        case lt::torrent_status::finished:
            return tsw_state_finished;
        default:
            return tsw_state_unknown;
    }
}

extern "C" {

// ---------------- Session ----------------

TSW_EXPORT tsw_session* TSW_CALL create_session(lt::settings_pack* pack, const uint8_t* state, int64_t state_length) {
    try {
        lt::session_params params;
        if (pack != nullptr) {
            params.settings = *pack;
        }

        auto* sw = new tsw_session(params);

        if (state != nullptr && state_length > 0) {
            lt::error_code ec;
            lt::bdecode_node node = lt::bdecode(
                lt::span<char const>(reinterpret_cast<const char*>(state), static_cast<std::ptrdiff_t>(state_length)),
                ec);
            if (!ec && node.type() == lt::bdecode_node::dict_t) {
                sw->session.load_state(node);
                // Re-apply caller's explicit settings_pack so saved state doesn't clobber fresh limits.
                if (pack != nullptr) {
                    sw->session.apply_settings(*pack);
                }
            }
        }

        tsw_start_event_worker(sw);
        return sw;
    } catch (...) {
        return nullptr;
    }
}

TSW_EXPORT uint8_t TSW_CALL get_session_state(tsw_session* sw, uint8_t** data, int64_t* size) {
    if (sw == nullptr || data == nullptr || size == nullptr) {
        return 0;
    }

    try {
        lt::entry e;
        sw->session.save_state(e, lt::session_handle::save_dht_state | lt::session_handle::save_dht_settings);
        std::vector<char> buf;
        lt::bencode(std::back_inserter(buf), e);

        auto* out = new uint8_t[buf.size()];
        if (!buf.empty()) {
            std::memcpy(out, buf.data(), buf.size());
        }
        *data = out;
        *size = static_cast<int64_t>(buf.size());
        return 1;
    } catch (...) {
        *data = nullptr;
        *size = 0;
        return 0;
    }
}

TSW_EXPORT void TSW_CALL destroy_session(tsw_session* sw) {
    if (sw == nullptr) {
        return;
    }

    sw->session.set_alert_notify(nullptr);
    tsw_stop_event_worker(sw);
    sw->session.abort();
    delete sw;
}

TSW_EXPORT void TSW_CALL clear_event_callback(tsw_session* sw) {
    if (sw == nullptr) {
        return;
    }

    sw->session.set_alert_notify(nullptr);
    std::lock_guard<std::mutex> lk(sw->cb_mutex);
    sw->callback = nullptr;
}

TSW_EXPORT void TSW_CALL set_event_callback(tsw_session* sw, void (TSW_CALL *callback)(void*), int32_t include_unmapped_events) {
    if (sw == nullptr) {
        return;
    }

    if (callback == nullptr) {
        clear_event_callback(sw);
        return;
    }

    {
        std::lock_guard<std::mutex> lk(sw->cb_mutex);
        sw->callback = callback;
        sw->include_unmapped = (include_unmapped_events != 0);
    }

    sw->session.set_alert_notify([sw]() {
        {
            std::lock_guard<std::mutex> lk(sw->wake_mutex);
            sw->has_alerts = true;
        }
        sw->wake_cv.notify_one();
    });

    // Wake worker once in case alerts were already queued prior to setting callback.
    {
        std::lock_guard<std::mutex> lk(sw->wake_mutex);
        sw->has_alerts = true;
    }
    sw->wake_cv.notify_one();
}

TSW_EXPORT void TSW_CALL apply_settings(tsw_session* sw, lt::settings_pack* settings) {
    if (sw == nullptr || settings == nullptr) {
        return;
    }
    sw->session.apply_settings(*settings);
}

TSW_EXPORT uint8_t TSW_CALL is_session_dht_running(tsw_session* sw) {
    if (sw == nullptr) {
        return 0;
    }
    return sw->session.is_dht_running() ? 1 : 0;
}

TSW_EXPORT void TSW_CALL add_session_dht_node(tsw_session* sw, const char* host, int32_t port) {
    if (sw == nullptr || host == nullptr) {
        return;
    }
    sw->session.add_dht_node(std::make_pair(std::string(host), port));
}

TSW_EXPORT void TSW_CALL set_session_ip_filter(tsw_session* sw, const char** first_ips, const char** last_ips, const uint8_t* blocked, int32_t count) {
    if (sw == nullptr || first_ips == nullptr || last_ips == nullptr || blocked == nullptr || count < 0) {
        return;
    }

    lt::ip_filter filter;
    for (int32_t i = 0; i < count; ++i) {
        if (first_ips[i] == nullptr || last_ips[i] == nullptr) {
            continue;
        }
        lt::error_code ec1, ec2;
        auto a1 = lt::make_address(first_ips[i], ec1);
        auto a2 = lt::make_address(last_ips[i], ec2);
        if (!ec1 && !ec2) {
            filter.add_rule(a1, a2, blocked[i] ? lt::ip_filter::blocked : 0);
        }
    }
    sw->session.set_ip_filter(filter);
}

TSW_EXPORT void TSW_CALL get_session_stats_metrics(tsw_session_stats_metric_list* metrics_list) {
    if (metrics_list == nullptr) {
        return;
    }

    auto const metrics = lt::session_stats_metrics();
    int32_t const n = static_cast<int32_t>(metrics.size());
    auto* arr = new tsw_session_stats_metric[n];
    for (int32_t i = 0; i < n; ++i) {
        arr[i].name = alloc_cstr(metrics[i].name ? metrics[i].name : "");
        arr[i].value_index = metrics[i].value_index;
        arr[i].type = static_cast<uint8_t>(metrics[i].type);
    }
    metrics_list->length = n;
    metrics_list->metrics = arr;
}

TSW_EXPORT void TSW_CALL destroy_session_stats_metric_list(tsw_session_stats_metric_list* metrics_list) {
    if (metrics_list == nullptr || metrics_list->metrics == nullptr) {
        return;
    }
    for (int32_t i = 0; i < metrics_list->length; ++i) {
        delete[] metrics_list->metrics[i].name;
    }
    delete[] metrics_list->metrics;
    metrics_list->metrics = nullptr;
    metrics_list->length = 0;
}

TSW_EXPORT void TSW_CALL post_session_stats(tsw_session* sw) {
    if (sw == nullptr) {
        return;
    }
    sw->session.post_session_stats();
}

// ---------------- TorrentInfo ----------------

TSW_EXPORT lt::torrent_info* TSW_CALL create_torrent_file(const char* file_path) {
    if (file_path == nullptr) {
        return nullptr;
    }
    try {
        return new lt::torrent_info(std::string(file_path));
    } catch (...) {
        return nullptr;
    }
}

TSW_EXPORT lt::torrent_info* TSW_CALL create_torrent_bytes(const char* data, int64_t length) {
    if (data == nullptr || length <= 0) {
        return nullptr;
    }
    try {
        lt::load_torrent_limits cfg;
        return new lt::torrent_info(
            lt::span<char const>(data, static_cast<std::ptrdiff_t>(length)),
            cfg,
            lt::from_span);
    } catch (...) {
        return nullptr;
    }
}

TSW_EXPORT void TSW_CALL destroy_torrent(lt::torrent_info* torrent) {
    delete torrent;
}

TSW_EXPORT tsw_metadata_info* TSW_CALL get_torrent_info(lt::torrent_info* torrent) {
    if (torrent == nullptr) {
        return nullptr;
    }

    auto* info = new tsw_metadata_info{};
    info->name = alloc_cstr(torrent->name());
    info->creator = alloc_cstr(torrent->creator());
    info->comment = alloc_cstr(torrent->comment());
    info->total_files = torrent->num_files();
    info->total_size = torrent->total_size();
    info->creation_epoch = static_cast<int64_t>(torrent->creation_date());

    auto const& h = torrent->info_hash();
    std::copy(h.begin(), h.end(), info->info_hash_sha1);
    std::fill_n(info->info_hash_sha256, 32, 0);
    return info;
}

TSW_EXPORT void TSW_CALL destroy_torrent_info(tsw_metadata_info* info) {
    if (info == nullptr) {
        return;
    }
    delete[] info->name;
    delete[] info->creator;
    delete[] info->comment;
    delete info;
}

TSW_EXPORT uint8_t TSW_CALL save_torrent_to_file(lt::torrent_info* torrent, const char* file_path) {
    if (torrent == nullptr || file_path == nullptr) {
        return 0;
    }
    try {
        lt::add_torrent_params p;
        p.ti = std::make_shared<lt::torrent_info>(*torrent);
        auto const buf = lt::write_torrent_file_buf(p, {});
        std::ofstream out(file_path, std::ios::binary);
        if (!out) {
            return 0;
        }
        out.write(buf.data(), static_cast<std::streamsize>(buf.size()));
        return out.good() ? 1 : 0;
    } catch (...) {
        return 0;
    }
}

TSW_EXPORT void TSW_CALL get_torrent_bytes(lt::torrent_info* torrent, uint8_t** out_data, int64_t* out_size) {
    if (torrent == nullptr || out_data == nullptr || out_size == nullptr) {
        return;
    }
    try {
        lt::add_torrent_params p;
        p.ti = std::make_shared<lt::torrent_info>(*torrent);
        auto const buf = lt::write_torrent_file_buf(p, {});
        auto* alloc = new uint8_t[buf.size()];
        if (!buf.empty()) {
            std::memcpy(alloc, buf.data(), buf.size());
        }
        *out_data = alloc;
        *out_size = static_cast<int64_t>(buf.size());
    } catch (...) {
        *out_data = nullptr;
        *out_size = 0;
    }
}

TSW_EXPORT void TSW_CALL free_torrent_bytes(void* data) {
    delete[] static_cast<uint8_t*>(data);
}

TSW_EXPORT uint8_t TSW_CALL create_torrent_bytes_from_resume_data(const uint8_t* resume_data, int64_t resume_len, uint8_t** out_data, int64_t* out_size) {
    if (resume_data == nullptr || resume_len <= 0 || out_data == nullptr || out_size == nullptr) {
        return 0;
    }
    try {
        lt::error_code ec;
        lt::add_torrent_params p = lt::read_resume_data(
            lt::span<char const>(reinterpret_cast<const char*>(resume_data), static_cast<std::ptrdiff_t>(resume_len)),
            ec);
        if (ec || !p.ti) {
            *out_data = nullptr;
            *out_size = 0;
            return 0;
        }
        auto const buf = lt::write_torrent_file_buf(p, {});
        auto* alloc = new uint8_t[buf.size()];
        if (!buf.empty()) {
            std::memcpy(alloc, buf.data(), buf.size());
        }
        *out_data = alloc;
        *out_size = static_cast<int64_t>(buf.size());
        return 1;
    } catch (...) {
        *out_data = nullptr;
        *out_size = 0;
        return 0;
    }
}

TSW_EXPORT void TSW_CALL get_torrent_file_list(lt::torrent_info* torrent, tsw_torrent_file_list* file_list) {
    if (torrent == nullptr || file_list == nullptr) {
        return;
    }

    auto const& files = torrent->files();
    int32_t const num_files = files.num_files();
    auto* list = new tsw_torrent_file_info[num_files];

    for (lt::file_index_t i(0); i != files.end_file(); ++i) {
        int32_t const idx = static_cast<int32_t>(i);
        list[idx].index = idx;
        list[idx].offset = files.file_offset(i);
        list[idx].file_size = files.file_size(i);
        list[idx].modified_time = static_cast<int64_t>(files.mtime(i));
        list[idx].file_name = alloc_cstr(files.file_name(i).to_string());
        list[idx].file_path = alloc_cstr(files.file_path(i));
        list[idx].file_path_is_absolute = files.file_absolute_path(i) ? 1 : 0;
        list[idx].pad_file = files.pad_file_at(i) ? 1 : 0;
    }

    file_list->length = num_files;
    file_list->items = list;
}

TSW_EXPORT void TSW_CALL destroy_torrent_file_list(tsw_torrent_file_list* file_list) {
    if (file_list == nullptr || file_list->items == nullptr) {
        return;
    }
    for (int32_t i = 0; i < file_list->length; ++i) {
        delete[] file_list->items[i].file_name;
        delete[] file_list->items[i].file_path;
    }
    delete[] file_list->items;
    file_list->items = nullptr;
    file_list->length = 0;
}

TSW_EXPORT tsw_piece_request TSW_CALL map_file_range(lt::torrent_info* torrent, int32_t file_index, int64_t offset, int32_t size) {
    tsw_piece_request req{};
    if (torrent == nullptr) {
        return req;
    }
    auto const mapped = torrent->map_file(static_cast<lt::file_index_t>(file_index), offset, size);
    req.piece = static_cast<int32_t>(mapped.piece);
    req.offset = mapped.start;
    req.length = mapped.length;
    return req;
}

// ---------------- Attach / Detach ----------------

TSW_EXPORT lt::torrent_handle* TSW_CALL attach_torrent(tsw_session* sw, lt::torrent_info* torrent, const char* save_path, const uint8_t* resume_data, int32_t resume_len, uint8_t default_dont_download) {
    if (sw == nullptr || torrent == nullptr || save_path == nullptr) {
        return nullptr;
    }

    try {
        lt::add_torrent_params params;
        bool has_resume = false;

        if (resume_data != nullptr && resume_len > 0) {
            lt::error_code ec;
            params = lt::read_resume_data(
                lt::span<char const>(reinterpret_cast<const char*>(resume_data), resume_len),
                ec);
            if (!ec) {
                has_resume = true;
            } else {
                params = lt::add_torrent_params{};
            }
        }

        params.ti = std::make_shared<lt::torrent_info>(*torrent);
        params.save_path = std::string(save_path);

        if (!has_resume && default_dont_download != 0) {
            params.file_priorities.assign(static_cast<std::size_t>(torrent->num_files()), lt::dont_download);
        }

        params.flags |= lt::torrent_flags::paused;
        params.flags &= ~lt::torrent_flags::auto_managed;

        lt::error_code add_ec;
        auto handle = std::make_unique<lt::torrent_handle>(sw->session.add_torrent(params, add_ec));
        if (add_ec || !handle->is_valid()) {
            return nullptr;
        }
        return handle.release();
    } catch (...) {
        return nullptr;
    }
}

TSW_EXPORT lt::torrent_handle* TSW_CALL attach_magnet(tsw_session* sw, const char* magnet_uri, const char* save_path, const uint8_t* resume_data, int32_t resume_len, uint8_t default_dont_download) {
    if (sw == nullptr || magnet_uri == nullptr || save_path == nullptr) {
        return nullptr;
    }

    try {
        lt::add_torrent_params params;
        bool has_resume = false;

        if (resume_data != nullptr && resume_len > 0) {
            lt::error_code ec;
            params = lt::read_resume_data(
                lt::span<char const>(reinterpret_cast<const char*>(resume_data), resume_len),
                ec);
            if (!ec) {
                has_resume = true;
            } else {
                params = lt::add_torrent_params{};
            }
        }

        if (!has_resume) {
            lt::error_code mag_ec;
            params = lt::parse_magnet_uri(std::string(magnet_uri), mag_ec);
            if (mag_ec) {
                return nullptr;
            }
        } else {
            lt::error_code mag_ec;
            lt::add_torrent_params parsed = lt::parse_magnet_uri(std::string(magnet_uri), mag_ec);
            if (!mag_ec) {
                if (params.trackers.empty()) {
                    params.trackers = std::move(parsed.trackers);
                }
                if (params.name.empty()) {
                    params.name = std::move(parsed.name);
                }
            }
        }

        params.save_path = std::string(save_path);
        params.flags |= lt::torrent_flags::paused;
        params.flags &= ~lt::torrent_flags::auto_managed;
        (void)default_dont_download;

        lt::error_code add_ec;
        auto handle = std::make_unique<lt::torrent_handle>(sw->session.add_torrent(params, add_ec));
        if (add_ec || !handle->is_valid()) {
            return nullptr;
        }
        return handle.release();
    } catch (...) {
        return nullptr;
    }
}

TSW_EXPORT void TSW_CALL detach_torrent(tsw_session* sw, lt::torrent_handle* torrent) {
    if (sw == nullptr || torrent == nullptr) {
        return;
    }
    if (torrent->is_valid()) {
        torrent->pause();
        sw->session.remove_torrent(*torrent);
    }
    delete torrent;
}

TSW_EXPORT lt::torrent_info* TSW_CALL get_handle_torrent_info(lt::torrent_handle* handle) {
    if (handle == nullptr || !handle->is_valid()) {
        return nullptr;
    }
    auto const ti = handle->torrent_file();
    if (!ti) {
        return nullptr;
    }
    return new lt::torrent_info(*ti);
}

TSW_EXPORT void TSW_CALL get_torrent_handle_info_hash(lt::torrent_handle* handle, uint8_t* hash_out) {
    if (handle == nullptr || hash_out == nullptr) {
        return;
    }
    auto const h = handle->info_hash();
    std::copy(h.begin(), h.end(), hash_out);
}

// ---------------- Handle Control & Status ----------------

TSW_EXPORT void TSW_CALL start_torrent(lt::torrent_handle* torrent) {
    if (torrent != nullptr && torrent->is_valid()) {
        torrent->resume();
    }
}

TSW_EXPORT void TSW_CALL stop_torrent(lt::torrent_handle* torrent) {
    if (torrent != nullptr && torrent->is_valid()) {
        torrent->pause();
    }
}

TSW_EXPORT void TSW_CALL force_recheck(lt::torrent_handle* torrent) {
    if (torrent != nullptr && torrent->is_valid()) {
        torrent->force_recheck();
    }
}

TSW_EXPORT void TSW_CALL rename_torrent_file(lt::torrent_handle* torrent, int32_t file_index, const char* new_name) {
    if (torrent != nullptr && torrent->is_valid() && new_name != nullptr) {
        torrent->rename_file(static_cast<lt::file_index_t>(file_index), std::string(new_name));
    }
}

TSW_EXPORT void TSW_CALL move_torrent_storage(lt::torrent_handle* torrent, const char* new_path) {
    if (torrent != nullptr && torrent->is_valid() && new_path != nullptr) {
        torrent->move_storage(std::string(new_path));
    }
}

TSW_EXPORT void TSW_CALL reannounce_torrent(lt::torrent_handle* torrent, int32_t seconds, uint8_t force) {
    if (torrent == nullptr || !torrent->is_valid()) {
        return;
    }
    lt::reannounce_flags_t flags{};
    if (force != 0) {
        flags |= lt::torrent_handle::ignore_min_interval;
    }
    torrent->force_reannounce(seconds, -1, flags);
}

TSW_EXPORT void TSW_CALL get_torrent_status(lt::torrent_handle* torrent, tsw_torrent_status* out_status) {
    if (torrent == nullptr || out_status == nullptr || !torrent->is_valid()) {
        return;
    }

    lt::torrent_status const s = torrent->status();
    if (s.errc) {
        out_status->state = tsw_state_error;
    } else {
        out_status->state = tsw_map_torrent_state(s.state);
    }

    out_status->progress = s.progress;
    out_status->peer_count = s.num_peers;
    out_status->seed_count = s.num_seeds;
    out_status->bytes_uploaded = s.total_payload_upload;
    out_status->bytes_downloaded = s.total_payload_download;
    out_status->upload_rate = s.upload_payload_rate;
    out_status->download_rate = s.download_payload_rate;
    out_status->total_wanted = s.total_wanted;
    out_status->total_wanted_done = s.total_wanted_done;
    out_status->all_time_upload = s.all_time_upload;
    out_status->all_time_download = s.all_time_download;
    out_status->added_time = static_cast<int64_t>(s.added_time);
    out_status->connection_count = s.num_connections;
    out_status->queue_position = static_cast<int32_t>(s.queue_position);
    out_status->distributed_full_copies = s.distributed_full_copies;
    out_status->is_finished = s.is_finished ? 1 : 0;
    out_status->moving_storage = s.moving_storage ? 1 : 0;
}

TSW_EXPORT uint8_t TSW_CALL get_file_dl_priority(lt::torrent_handle* torrent, int32_t file_index) {
    if (torrent == nullptr || !torrent->is_valid()) {
        return 0;
    }
    return static_cast<uint8_t>(torrent->file_priority(static_cast<lt::file_index_t>(file_index)));
}

TSW_EXPORT void TSW_CALL set_file_dl_priority(lt::torrent_handle* torrent, int32_t file_index, uint8_t priority) {
    if (torrent != nullptr && torrent->is_valid()) {
        torrent->file_priority(static_cast<lt::file_index_t>(file_index), static_cast<lt::download_priority_t>(priority));
    }
}

TSW_EXPORT void TSW_CALL set_piece_deadline(lt::torrent_handle* torrent, int32_t piece_index, int32_t deadline_ms) {
    if (torrent != nullptr && torrent->is_valid()) {
        torrent->set_piece_deadline(static_cast<lt::piece_index_t>(piece_index), deadline_ms);
    }
}

TSW_EXPORT void TSW_CALL reset_piece_deadline(lt::torrent_handle* torrent, int32_t piece_index) {
    if (torrent != nullptr && torrent->is_valid()) {
        torrent->reset_piece_deadline(static_cast<lt::piece_index_t>(piece_index));
    }
}

TSW_EXPORT uint8_t TSW_CALL have_piece(lt::torrent_handle* torrent, int32_t piece_index) {
    if (torrent == nullptr || !torrent->is_valid()) {
        return 0;
    }
    return torrent->have_piece(static_cast<lt::piece_index_t>(piece_index)) ? 1 : 0;
}

TSW_EXPORT int32_t TSW_CALL get_torrent_piece_count(lt::torrent_handle* torrent) {
    if (torrent == nullptr || !torrent->is_valid()) {
        return 0;
    }
    auto const ti = torrent->torrent_file();
    return ti ? ti->num_pieces() : 0;
}

TSW_EXPORT void TSW_CALL get_torrent_piece_map(lt::torrent_handle* torrent, uint8_t* pieces_out, int32_t pieces_len) {
    if (torrent == nullptr || pieces_out == nullptr || pieces_len <= 0 || !torrent->is_valid()) {
        return;
    }
    lt::torrent_status const s = torrent->status(lt::torrent_handle::query_pieces);
    int32_t const count = std::min<int32_t>(pieces_len, s.pieces.size());
    std::fill_n(pieces_out, pieces_len, 0);
    for (int32_t i = 0; i < count; ++i) {
        pieces_out[i] = s.pieces.get_bit(static_cast<lt::piece_index_t>(i)) ? 1 : 0;
    }
}

TSW_EXPORT uint8_t TSW_CALL get_piece_priority(lt::torrent_handle* torrent, int32_t piece_index) {
    if (torrent == nullptr || !torrent->is_valid()) {
        return 0;
    }
    return static_cast<uint8_t>(torrent->piece_priority(static_cast<lt::piece_index_t>(piece_index)));
}

TSW_EXPORT void TSW_CALL set_piece_priority(lt::torrent_handle* torrent, int32_t piece_index, uint8_t priority) {
    if (torrent != nullptr && torrent->is_valid()) {
        torrent->piece_priority(static_cast<lt::piece_index_t>(piece_index), static_cast<lt::download_priority_t>(priority));
    }
}

TSW_EXPORT void TSW_CALL get_torrent_piece_priorities(lt::torrent_handle* torrent, uint8_t* priorities_out, int32_t priorities_len) {
    if (torrent == nullptr || priorities_out == nullptr || priorities_len <= 0 || !torrent->is_valid()) {
        return;
    }
    auto const ps = torrent->get_piece_priorities();
    int32_t const count = std::min<int32_t>(priorities_len, static_cast<int32_t>(ps.size()));
    for (int32_t i = 0; i < count; ++i) {
        priorities_out[i] = static_cast<uint8_t>(ps[i]);
    }
}

TSW_EXPORT void TSW_CALL set_torrent_piece_priorities(lt::torrent_handle* torrent, const uint8_t* priorities, int32_t priorities_len) {
    if (torrent == nullptr || priorities == nullptr || priorities_len <= 0 || !torrent->is_valid()) {
        return;
    }
    std::vector<lt::download_priority_t> vec(static_cast<std::size_t>(priorities_len));
    for (int32_t i = 0; i < priorities_len; ++i) {
        vec[static_cast<std::size_t>(i)] = static_cast<lt::download_priority_t>(priorities[i]);
    }
    torrent->prioritize_pieces(vec);
}

TSW_EXPORT void TSW_CALL save_torrent_resume_data(lt::torrent_handle* torrent) {
    if (torrent != nullptr && torrent->is_valid()) {
        torrent->save_resume_data();
    }
}

TSW_EXPORT void TSW_CALL save_torrent_resume_data_flags(lt::torrent_handle* torrent, uint32_t flags) {
    if (torrent == nullptr || !torrent->is_valid()) {
        return;
    }
    lt::resume_data_flags_t f{};
    if ((flags & 1u) != 0) f |= lt::torrent_handle::flush_disk_cache;
    if ((flags & 2u) != 0) f |= lt::torrent_handle::save_info_dict;
    if ((flags & 4u) != 0) f |= lt::torrent_handle::only_if_modified;
    torrent->save_resume_data(f);
}

TSW_EXPORT void TSW_CALL get_torrent_peers(lt::torrent_handle* torrent, tsw_peer_list* peer_list) {
    if (torrent == nullptr || peer_list == nullptr || !torrent->is_valid()) {
        return;
    }
    std::vector<lt::peer_info> peers;
    torrent->get_peer_info(peers);
    int32_t const n = static_cast<int32_t>(peers.size());
    auto* arr = new tsw_peer_info[n]{};
    for (int32_t i = 0; i < n; ++i) {
        auto const& p = peers[static_cast<std::size_t>(i)];
        std::string const addr = p.ip.address().to_string() + ":" + std::to_string(p.ip.port());
        arr[i].address = alloc_cstr(addr);
        arr[i].client = alloc_cstr(p.client);
        arr[i].total_download = p.total_download;
        arr[i].total_upload = p.total_upload;
        arr[i].download_rate = p.payload_down_speed;
        arr[i].upload_rate = p.payload_up_speed;

        if (p.flags & lt::peer_info::rc4_encrypted) {
            arr[i].encryption_type = 1; // Rc4
        } else if (p.flags & lt::peer_info::plaintext_encrypted) {
            arr[i].encryption_type = 2; // Obfuscated
        } else {
            arr[i].encryption_type = 0; // None
        }

        arr[i].direction = (p.flags & lt::peer_info::local_connection) ? 1 : 0;
        arr[i].is_seed = (p.flags & lt::peer_info::seed) ? 1 : 0;
        arr[i].we_are_choking = (p.flags & lt::peer_info::choked) ? 1 : 0;
        arr[i].they_are_choking = (p.flags & lt::peer_info::remote_choked) ? 1 : 0;
        arr[i].am_interested = (p.flags & lt::peer_info::interesting) ? 1 : 0;
        arr[i].is_interested = (p.flags & lt::peer_info::remote_interested) ? 1 : 0;
    }
    peer_list->length = n;
    peer_list->peers = arr;
}

TSW_EXPORT void TSW_CALL destroy_torrent_peer_list(tsw_peer_list* peer_list) {
    if (peer_list == nullptr || peer_list->peers == nullptr) {
        return;
    }
    for (int32_t i = 0; i < peer_list->length; ++i) {
        delete[] peer_list->peers[i].address;
        delete[] peer_list->peers[i].client;
    }
    delete[] peer_list->peers;
    peer_list->peers = nullptr;
    peer_list->length = 0;
}

TSW_EXPORT void TSW_CALL get_torrent_peer_progress(lt::torrent_handle* torrent, tsw_peer_progress_list* list) {
    if (list == nullptr) {
        return;
    }
    list->length = 0;
    list->peers = nullptr;
    if (torrent == nullptr || !torrent->is_valid()) {
        return;
    }
    std::vector<lt::peer_info> peers;
    torrent->get_peer_info(peers);
    int32_t const n = static_cast<int32_t>(peers.size());
    if (n <= 0) {
        return;
    }
    auto* arr = new tsw_peer_progress_info[n]{};
    for (int32_t i = 0; i < n; ++i) {
        auto const& p = peers[static_cast<std::size_t>(i)];
        std::string const addr = p.ip.address().to_string() + ":" + std::to_string(p.ip.port());
        arr[i].address = alloc_cstr(addr);
        int32_t ppm = p.progress_ppm;
        if ((p.flags & lt::peer_info::seed) && ppm <= 0) {
            ppm = 1000000;
        }
        arr[i].progress_ppm = std::clamp<int32_t>(ppm, 0, 1000000);
    }
    list->length = n;
    list->peers = arr;
}

TSW_EXPORT void TSW_CALL destroy_torrent_peer_progress_list(tsw_peer_progress_list* list) {
    if (list == nullptr || list->peers == nullptr) {
        return;
    }
    for (int32_t i = 0; i < list->length; ++i) {
        delete[] list->peers[i].address;
    }
    delete[] list->peers;
    list->peers = nullptr;
    list->length = 0;
}

TSW_EXPORT void TSW_CALL get_torrent_trackers(lt::torrent_handle* torrent, tsw_tracker_list* tracker_list) {
    if (torrent == nullptr || tracker_list == nullptr || !torrent->is_valid()) {
        return;
    }
    auto const trks = torrent->trackers();
    int32_t const n = static_cast<int32_t>(trks.size());
    auto* arr = new tsw_tracker_info[n]{};
    for (int32_t i = 0; i < n; ++i) {
        auto const& tr = trks[static_cast<std::size_t>(i)];
        arr[i].tier = static_cast<int32_t>(tr.tier);
        arr[i].url = alloc_cstr(tr.url);
        arr[i].verified = tr.verified ? 1 : 0;

        uint8_t max_fails = 0;
        bool any_updating = false;
        std::string warn_msg;
        std::string fail_msg;

        for (auto const& ep : tr.endpoints) {
            if (ep.updating) {
                any_updating = true;
            }
            if (ep.fails > max_fails) {
                max_fails = ep.fails;
            }
            if (ep.last_error && fail_msg.empty()) {
                fail_msg = ep.last_error.message();
                if (!ep.message.empty()) {
                    fail_msg += " (" + ep.message + ")";
                }
            } else if (!ep.message.empty() && warn_msg.empty()) {
                warn_msg = ep.message;
            }
        }

        arr[i].fails = max_fails;
        arr[i].updating = any_updating ? 1 : 0;
        arr[i].warning_message = alloc_cstr(warn_msg);
        arr[i].failure_message = alloc_cstr(fail_msg);
    }
    tracker_list->length = n;
    tracker_list->trackers = arr;
}

TSW_EXPORT void TSW_CALL destroy_torrent_tracker_list(tsw_tracker_list* tracker_list) {
    if (tracker_list == nullptr || tracker_list->trackers == nullptr) {
        return;
    }
    for (int32_t i = 0; i < tracker_list->length; ++i) {
        delete[] tracker_list->trackers[i].url;
        delete[] tracker_list->trackers[i].warning_message;
        delete[] tracker_list->trackers[i].failure_message;
    }
    delete[] tracker_list->trackers;
    tracker_list->trackers = nullptr;
    tracker_list->length = 0;
}

TSW_EXPORT void TSW_CALL add_torrent_tracker(lt::torrent_handle* torrent, const char* url, uint8_t tier) {
    if (torrent == nullptr || url == nullptr || !torrent->is_valid()) {
        return;
    }
    lt::announce_entry entry{std::string(url)};
    entry.tier = tier;
    torrent->add_tracker(entry);
}

TSW_EXPORT void TSW_CALL replace_torrent_trackers(lt::torrent_handle* torrent, const char** urls, const uint8_t* tiers, int32_t count) {
    if (torrent == nullptr || !torrent->is_valid() || count < 0) {
        return;
    }
    std::vector<lt::announce_entry> list;
    list.reserve(static_cast<std::size_t>(count));
    for (int32_t i = 0; i < count; ++i) {
        if (urls != nullptr && urls[i] != nullptr) {
            lt::announce_entry e(std::string(urls[i]));
            e.tier = (tiers != nullptr) ? tiers[i] : 0;
            list.push_back(std::move(e));
        }
    }
    torrent->replace_trackers(list);
}

TSW_EXPORT void TSW_CALL add_torrent_url_seed(lt::torrent_handle* torrent, const char* url) {
    if (torrent != nullptr && url != nullptr && torrent->is_valid()) {
        torrent->add_url_seed(std::string(url));
    }
}

TSW_EXPORT void TSW_CALL add_torrent_http_seed(lt::torrent_handle* torrent, const char* url) {
    if (torrent != nullptr && url != nullptr && torrent->is_valid()) {
        torrent->add_http_seed(std::string(url));
    }
}

TSW_EXPORT void TSW_CALL scrape_torrent_tracker(lt::torrent_handle* torrent, int32_t tracker_index) {
    if (torrent != nullptr && torrent->is_valid()) {
        torrent->scrape_tracker(tracker_index);
    }
}

TSW_EXPORT int32_t TSW_CALL get_torrent_upload_limit(lt::torrent_handle* torrent) {
    return (torrent && torrent->is_valid()) ? torrent->upload_limit() : 0;
}

TSW_EXPORT void TSW_CALL set_torrent_upload_limit(lt::torrent_handle* torrent, int32_t limit) {
    if (torrent && torrent->is_valid()) {
        torrent->set_upload_limit(limit);
    }
}

TSW_EXPORT int32_t TSW_CALL get_torrent_download_limit(lt::torrent_handle* torrent) {
    return (torrent && torrent->is_valid()) ? torrent->download_limit() : 0;
}

TSW_EXPORT void TSW_CALL set_torrent_download_limit(lt::torrent_handle* torrent, int32_t limit) {
    if (torrent && torrent->is_valid()) {
        torrent->set_download_limit(limit);
    }
}

TSW_EXPORT int32_t TSW_CALL get_torrent_max_connections(lt::torrent_handle* torrent) {
    return (torrent && torrent->is_valid()) ? torrent->max_connections() : -1;
}

TSW_EXPORT void TSW_CALL set_torrent_max_connections(lt::torrent_handle* torrent, int32_t limit) {
    if (torrent && torrent->is_valid()) {
        torrent->set_max_connections(limit);
    }
}

TSW_EXPORT int32_t TSW_CALL get_torrent_queue_position(lt::torrent_handle* torrent) {
    return (torrent && torrent->is_valid()) ? static_cast<int32_t>(torrent->queue_position()) : -1;
}

TSW_EXPORT void TSW_CALL torrent_queue_position_up(lt::torrent_handle* torrent) {
    if (torrent && torrent->is_valid()) torrent->queue_position_up();
}

TSW_EXPORT void TSW_CALL torrent_queue_position_down(lt::torrent_handle* torrent) {
    if (torrent && torrent->is_valid()) torrent->queue_position_down();
}

TSW_EXPORT void TSW_CALL torrent_queue_position_top(lt::torrent_handle* torrent) {
    if (torrent && torrent->is_valid()) torrent->queue_position_top();
}

TSW_EXPORT void TSW_CALL torrent_queue_position_bottom(lt::torrent_handle* torrent) {
    if (torrent && torrent->is_valid()) torrent->queue_position_bottom();
}

TSW_EXPORT uint8_t TSW_CALL get_torrent_sequential_download(lt::torrent_handle* torrent) {
    return (torrent && torrent->is_valid() && (torrent->flags() & lt::torrent_flags::sequential_download)) ? 1 : 0;
}

TSW_EXPORT void TSW_CALL set_torrent_sequential_download(lt::torrent_handle* torrent, uint8_t value) {
    if (torrent && torrent->is_valid()) {
        if (value) torrent->set_flags(lt::torrent_flags::sequential_download);
        else torrent->unset_flags(lt::torrent_flags::sequential_download);
    }
}

TSW_EXPORT uint8_t TSW_CALL get_torrent_super_seeding(lt::torrent_handle* torrent) {
    return (torrent && torrent->is_valid() && (torrent->flags() & lt::torrent_flags::super_seeding)) ? 1 : 0;
}

TSW_EXPORT void TSW_CALL set_torrent_super_seeding(lt::torrent_handle* torrent, uint8_t value) {
    if (torrent && torrent->is_valid()) {
        if (value) torrent->set_flags(lt::torrent_flags::super_seeding);
        else torrent->unset_flags(lt::torrent_flags::super_seeding);
    }
}

TSW_EXPORT uint8_t TSW_CALL get_torrent_share_mode(lt::torrent_handle* torrent) {
    return (torrent && torrent->is_valid() && (torrent->flags() & lt::torrent_flags::share_mode)) ? 1 : 0;
}

TSW_EXPORT void TSW_CALL set_torrent_share_mode(lt::torrent_handle* torrent, uint8_t value) {
    if (torrent && torrent->is_valid()) {
        if (value) torrent->set_flags(lt::torrent_flags::share_mode);
        else torrent->unset_flags(lt::torrent_flags::share_mode);
    }
}

TSW_EXPORT uint8_t TSW_CALL get_torrent_upload_mode(lt::torrent_handle* torrent) {
    return (torrent && torrent->is_valid() && (torrent->flags() & lt::torrent_flags::upload_mode)) ? 1 : 0;
}

TSW_EXPORT void TSW_CALL set_torrent_upload_mode(lt::torrent_handle* torrent, uint8_t value) {
    if (torrent && torrent->is_valid()) {
        if (value) torrent->set_flags(lt::torrent_flags::upload_mode);
        else torrent->unset_flags(lt::torrent_flags::upload_mode);
    }
}

} // extern "C"
