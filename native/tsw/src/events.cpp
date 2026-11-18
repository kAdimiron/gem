#include "tsw.h"

#include <algorithm>
#include <string>
#include <vector>

#include <libtorrent/alert_types.hpp>
#include <libtorrent/write_resume_data.hpp>

namespace {

void copy_info_hash(lt::sha1_hash const& hash, uint8_t* out_buf) {
    std::copy(hash.begin(), hash.end(), out_buf);
}

void fill_event_base(tsw_event_base* base, lt::alert const* alert, tsw_alert_type type, std::string& msg_storage) {
    base->type = type;
    base->category = static_cast<int32_t>(static_cast<uint32_t>(alert->category()));
    base->timestamp = static_cast<int64_t>(std::time(nullptr));
    msg_storage = alert->message();
    base->message = msg_storage.c_str();
}

void dispatch_alert_batch(tsw_session* sw, void (TSW_CALL *callback)(void*), bool include_unmapped) {
    if (callback == nullptr) {
        return;
    }

    std::vector<lt::alert*> alerts;
    std::string msg_tmp;
    std::string aux_str;

    while (true) {
        alerts.clear();
        sw->session.pop_alerts(&alerts);
        if (alerts.empty()) {
            break;
        }

        for (lt::alert* alert : alerts) {
            if (alert == nullptr) {
                continue;
            }

            switch (alert->type()) {
                case lt::state_changed_alert::alert_type: {
                    auto* a = lt::alert_cast<lt::state_changed_alert>(alert);
                    tsw_torrent_status_event ev{};
                    ev.old_state = tsw_map_torrent_state(a->prev_state);
                    ev.new_state = tsw_map_torrent_state(a->state);
                    copy_info_hash(a->handle.info_hash(), ev.info_hash);
                    fill_event_base(&ev.info, alert, tsw_alert_torrent_status, msg_tmp);
                    callback(&ev);
                    break;
                }

                case lt::performance_alert::alert_type: {
                    auto* a = lt::alert_cast<lt::performance_alert>(alert);
                    tsw_performance_warning_event ev{};
                    ev.warning_code = static_cast<uint8_t>(a->warning_code);
                    fill_event_base(&ev.info, alert, tsw_alert_client_performance, msg_tmp);
                    callback(&ev);
                    break;
                }

                case lt::peer_connect_alert::alert_type: {
                    auto* a = lt::alert_cast<lt::peer_connect_alert>(alert);
                    tsw_peer_event ev{};
                    ev.handle = &a->handle;
                    ev.notification_type = tsw_peer_connected_out;
                    copy_info_hash(a->handle.info_hash(), ev.info_hash);
                    auto bytes = a->endpoint.address().to_v6().to_bytes();
                    std::copy(bytes.begin(), bytes.end(), ev.v6_address);
                    fill_event_base(&ev.info, alert, tsw_alert_peer, msg_tmp);
                    callback(&ev);
                    break;
                }

                case lt::peer_disconnected_alert::alert_type: {
                    auto* a = lt::alert_cast<lt::peer_disconnected_alert>(alert);
                    tsw_peer_event ev{};
                    ev.handle = &a->handle;
                    ev.notification_type = tsw_peer_disconnected;
                    copy_info_hash(a->handle.info_hash(), ev.info_hash);
                    auto bytes = a->endpoint.address().to_v6().to_bytes();
                    std::copy(bytes.begin(), bytes.end(), ev.v6_address);
                    fill_event_base(&ev.info, alert, tsw_alert_peer, msg_tmp);
                    callback(&ev);
                    break;
                }

                case lt::peer_ban_alert::alert_type: {
                    auto* a = lt::alert_cast<lt::peer_ban_alert>(alert);
                    tsw_peer_event ev{};
                    ev.handle = &a->handle;
                    ev.notification_type = tsw_peer_banned;
                    copy_info_hash(a->handle.info_hash(), ev.info_hash);
                    auto bytes = a->endpoint.address().to_v6().to_bytes();
                    std::copy(bytes.begin(), bytes.end(), ev.v6_address);
                    fill_event_base(&ev.info, alert, tsw_alert_peer, msg_tmp);
                    callback(&ev);
                    break;
                }

                case lt::peer_snubbed_alert::alert_type: {
                    auto* a = lt::alert_cast<lt::peer_snubbed_alert>(alert);
                    tsw_peer_event ev{};
                    ev.handle = &a->handle;
                    ev.notification_type = tsw_peer_snubbed;
                    copy_info_hash(a->handle.info_hash(), ev.info_hash);
                    auto bytes = a->endpoint.address().to_v6().to_bytes();
                    std::copy(bytes.begin(), bytes.end(), ev.v6_address);
                    fill_event_base(&ev.info, alert, tsw_alert_peer, msg_tmp);
                    callback(&ev);
                    break;
                }

                case lt::peer_unsnubbed_alert::alert_type: {
                    auto* a = lt::alert_cast<lt::peer_unsnubbed_alert>(alert);
                    tsw_peer_event ev{};
                    ev.handle = &a->handle;
                    ev.notification_type = tsw_peer_unsnubbed;
                    copy_info_hash(a->handle.info_hash(), ev.info_hash);
                    auto bytes = a->endpoint.address().to_v6().to_bytes();
                    std::copy(bytes.begin(), bytes.end(), ev.v6_address);
                    fill_event_base(&ev.info, alert, tsw_alert_peer, msg_tmp);
                    callback(&ev);
                    break;
                }

                case lt::peer_error_alert::alert_type: {
                    auto* a = lt::alert_cast<lt::peer_error_alert>(alert);
                    tsw_peer_event ev{};
                    ev.handle = &a->handle;
                    ev.notification_type = tsw_peer_errored;
                    copy_info_hash(a->handle.info_hash(), ev.info_hash);
                    auto bytes = a->endpoint.address().to_v6().to_bytes();
                    std::copy(bytes.begin(), bytes.end(), ev.v6_address);
                    fill_event_base(&ev.info, alert, tsw_alert_peer, msg_tmp);
                    callback(&ev);
                    break;
                }

                case lt::torrent_removed_alert::alert_type: {
                    auto* a = lt::alert_cast<lt::torrent_removed_alert>(alert);
                    tsw_torrent_removed_event ev{};
                    copy_info_hash(a->info_hash, ev.info_hash);
                    fill_event_base(&ev.info, alert, tsw_alert_torrent_removed, msg_tmp);
                    callback(&ev);
                    break;
                }

                case lt::metadata_received_alert::alert_type: {
                    auto* a = lt::alert_cast<lt::metadata_received_alert>(alert);
                    tsw_metadata_received_event ev{};
                    copy_info_hash(a->handle.info_hash(), ev.info_hash);
                    fill_event_base(&ev.info, alert, tsw_alert_metadata_received, msg_tmp);
                    callback(&ev);
                    break;
                }

                case lt::metadata_failed_alert::alert_type: {
                    auto* a = lt::alert_cast<lt::metadata_failed_alert>(alert);
                    tsw_metadata_failed_event ev{};
                    ev.error_value = a->error.value();
                    copy_info_hash(a->handle.info_hash(), ev.info_hash);
                    fill_event_base(&ev.info, alert, tsw_alert_metadata_failed, msg_tmp);
                    callback(&ev);
                    break;
                }

                case lt::read_piece_alert::alert_type: {
                    auto* a = lt::alert_cast<lt::read_piece_alert>(alert);
                    tsw_read_piece_event ev{};
                    ev.piece = static_cast<int32_t>(a->piece);
                    ev.size = a->size;
                    ev.succeeded = (!a->error && a->buffer) ? 1 : 0;
                    copy_info_hash(a->handle.info_hash(), ev.info_hash);
                    ev.buffer = a->buffer ? a->buffer.get() : nullptr;
                    fill_event_base(&ev.info, alert, tsw_alert_read_piece, msg_tmp);
                    callback(&ev);
                    break;
                }

                case lt::file_renamed_alert::alert_type: {
                    auto* a = lt::alert_cast<lt::file_renamed_alert>(alert);
                    tsw_file_renamed_event ev{};
                    ev.file_index = static_cast<int32_t>(a->index);
                    ev.succeeded = 1;
                    copy_info_hash(a->handle.info_hash(), ev.info_hash);
                    fill_event_base(&ev.info, alert, tsw_alert_file_renamed, msg_tmp);
                    callback(&ev);
                    break;
                }

                case lt::file_rename_failed_alert::alert_type: {
                    auto* a = lt::alert_cast<lt::file_rename_failed_alert>(alert);
                    tsw_file_renamed_event ev{};
                    ev.file_index = static_cast<int32_t>(a->index);
                    ev.succeeded = 0;
                    copy_info_hash(a->handle.info_hash(), ev.info_hash);
                    fill_event_base(&ev.info, alert, tsw_alert_file_renamed, msg_tmp);
                    callback(&ev);
                    break;
                }

                case lt::storage_moved_alert::alert_type: {
                    auto* a = lt::alert_cast<lt::storage_moved_alert>(alert);
                    tsw_storage_moved_event ev{};
                    ev.succeeded = 1;
                    copy_info_hash(a->handle.info_hash(), ev.info_hash);
                    fill_event_base(&ev.info, alert, tsw_alert_storage_moved, msg_tmp);
                    callback(&ev);
                    break;
                }

                case lt::storage_moved_failed_alert::alert_type: {
                    auto* a = lt::alert_cast<lt::storage_moved_failed_alert>(alert);
                    tsw_storage_moved_event ev{};
                    ev.succeeded = 0;
                    copy_info_hash(a->handle.info_hash(), ev.info_hash);
                    fill_event_base(&ev.info, alert, tsw_alert_storage_moved, msg_tmp);
                    callback(&ev);
                    break;
                }

                case lt::scrape_reply_alert::alert_type: {
                    auto* a = lt::alert_cast<lt::scrape_reply_alert>(alert);
                    tsw_scrape_event ev{};
                    ev.succeeded = 1;
                    ev.incomplete = a->incomplete;
                    ev.complete = a->complete;
                    copy_info_hash(a->handle.info_hash(), ev.info_hash);
                    fill_event_base(&ev.info, alert, tsw_alert_scrape, msg_tmp);
                    callback(&ev);
                    break;
                }

                case lt::scrape_failed_alert::alert_type: {
                    auto* a = lt::alert_cast<lt::scrape_failed_alert>(alert);
                    tsw_scrape_event ev{};
                    ev.succeeded = 0;
                    ev.incomplete = -1;
                    ev.complete = -1;
                    copy_info_hash(a->handle.info_hash(), ev.info_hash);
                    fill_event_base(&ev.info, alert, tsw_alert_scrape, msg_tmp);
                    callback(&ev);
                    break;
                }

                case lt::save_resume_data_alert::alert_type: {
                    auto* a = lt::alert_cast<lt::save_resume_data_alert>(alert);
                    std::vector<char> buf = lt::write_resume_data_buf(a->params);
                    tsw_resume_data_event ev{};
                    ev.succeeded = 1;
                    copy_info_hash(a->handle.info_hash(), ev.info_hash);
                    ev.size = static_cast<int32_t>(buf.size());
                    ev.buffer = buf.empty() ? nullptr : buf.data();
                    ev.not_modified = 0;
                    fill_event_base(&ev.info, alert, tsw_alert_resume_data, msg_tmp);
                    callback(&ev);
                    break;
                }

                case lt::save_resume_data_failed_alert::alert_type: {
                    auto* a = lt::alert_cast<lt::save_resume_data_failed_alert>(alert);
                    tsw_resume_data_event ev{};
                    bool const not_mod = (a->error == lt::errors::resume_data_not_modified);
                    ev.succeeded = not_mod ? 1 : 0;
                    copy_info_hash(a->handle.info_hash(), ev.info_hash);
                    ev.size = 0;
                    ev.buffer = nullptr;
                    ev.not_modified = not_mod ? 1 : 0;
                    fill_event_base(&ev.info, alert, tsw_alert_resume_data, msg_tmp);
                    callback(&ev);
                    break;
                }

                case lt::session_stats_alert::alert_type: {
                    auto* a = lt::alert_cast<lt::session_stats_alert>(alert);
                    auto counters = a->counters();
                    tsw_session_stats_event ev{};
                    ev.count = static_cast<int32_t>(counters.size());
                    ev.values = counters.empty() ? nullptr : counters.data();
                    fill_event_base(&ev.info, alert, tsw_alert_session_stats, msg_tmp);
                    callback(&ev);
                    break;
                }

                case lt::file_error_alert::alert_type: {
                    auto* a = lt::alert_cast<lt::file_error_alert>(alert);
                    tsw_file_error_event ev{};
                    ev.error_value = a->error.value();
                    ev.operation = static_cast<uint8_t>(a->op);
                    copy_info_hash(a->handle.info_hash(), ev.info_hash);
                    ev.filename = a->filename();
                    fill_event_base(&ev.info, alert, tsw_alert_file_error, msg_tmp);
                    callback(&ev);
                    break;
                }

                case lt::listen_failed_alert::alert_type: {
                    auto* a = lt::alert_cast<lt::listen_failed_alert>(alert);
                    tsw_listen_failed_event ev{};
                    ev.error_value = a->error.value();
                    ev.operation = static_cast<uint8_t>(a->op);
                    ev.socket_type = static_cast<uint8_t>(a->socket_type);
                    ev.port = a->port;
                    aux_str = a->listen_interface();
                    ev.listen_interface = aux_str.c_str();
                    fill_event_base(&ev.info, alert, tsw_alert_listen_failed, msg_tmp);
                    callback(&ev);
                    break;
                }

                case lt::listen_succeeded_alert::alert_type: {
                    auto* a = lt::alert_cast<lt::listen_succeeded_alert>(alert);
                    tsw_listen_succeeded_event ev{};
                    ev.socket_type = static_cast<uint8_t>(a->socket_type);
                    ev.port = a->port;
                    aux_str = a->address.to_string();
                    ev.address = aux_str.c_str();
                    fill_event_base(&ev.info, alert, tsw_alert_listen_succeeded, msg_tmp);
                    callback(&ev);
                    break;
                }

                case lt::portmap_alert::alert_type: {
                    auto* a = lt::alert_cast<lt::portmap_alert>(alert);
                    tsw_portmap_event ev{};
                    ev.external_port = a->external_port;
                    ev.transport = static_cast<uint8_t>(a->map_transport);
                    fill_event_base(&ev.info, alert, tsw_alert_portmap, msg_tmp);
                    callback(&ev);
                    break;
                }

                case lt::portmap_error_alert::alert_type: {
                    auto* a = lt::alert_cast<lt::portmap_error_alert>(alert);
                    tsw_portmap_error_event ev{};
                    ev.error_value = a->error.value();
                    ev.transport = static_cast<uint8_t>(a->map_transport);
                    fill_event_base(&ev.info, alert, tsw_alert_portmap_error, msg_tmp);
                    callback(&ev);
                    break;
                }

                case lt::external_ip_alert::alert_type: {
                    auto* a = lt::alert_cast<lt::external_ip_alert>(alert);
                    tsw_external_ip_event ev{};
                    aux_str = a->external_address.to_string();
                    ev.external_address = aux_str.c_str();
                    fill_event_base(&ev.info, alert, tsw_alert_external_ip, msg_tmp);
                    callback(&ev);
                    break;
                }

                case lt::piece_finished_alert::alert_type: {
                    auto* a = lt::alert_cast<lt::piece_finished_alert>(alert);
                    tsw_piece_finished_event ev{};
                    ev.piece_index = static_cast<int32_t>(a->piece_index);
                    copy_info_hash(a->handle.info_hash(), ev.info_hash);
                    fill_event_base(&ev.info, alert, tsw_alert_piece_finished, msg_tmp);
                    callback(&ev);
                    break;
                }

                case lt::hash_failed_alert::alert_type: {
                    auto* a = lt::alert_cast<lt::hash_failed_alert>(alert);
                    tsw_hash_failed_event ev{};
                    ev.piece_index = static_cast<int32_t>(a->piece_index);
                    copy_info_hash(a->handle.info_hash(), ev.info_hash);
                    fill_event_base(&ev.info, alert, tsw_alert_hash_failed, msg_tmp);
                    callback(&ev);
                    break;
                }

                case lt::torrent_finished_alert::alert_type: {
                    auto* a = lt::alert_cast<lt::torrent_finished_alert>(alert);
                    tsw_torrent_finished_event ev{};
                    copy_info_hash(a->handle.info_hash(), ev.info_hash);
                    fill_event_base(&ev.info, alert, tsw_alert_torrent_finished, msg_tmp);
                    callback(&ev);
                    break;
                }

                case lt::torrent_error_alert::alert_type: {
                    auto* a = lt::alert_cast<lt::torrent_error_alert>(alert);
                    tsw_torrent_error_event ev{};
                    ev.error_value = a->error.value();
                    copy_info_hash(a->handle.info_hash(), ev.info_hash);
                    ev.filename = a->filename();
                    fill_event_base(&ev.info, alert, tsw_alert_torrent_error, msg_tmp);
                    callback(&ev);
                    break;
                }

                default: {
                    if (!include_unmapped) {
                        break;
                    }
                    tsw_event_base ev{};
                    fill_event_base(&ev, alert, tsw_alert_generic, msg_tmp);
                    callback(&ev);
                    break;
                }
            }
        }
    }
}

void worker_loop(tsw_session* sw) {
    while (!sw->stopping.load(std::memory_order_acquire)) {
        {
            std::unique_lock<std::mutex> lk(sw->wake_mutex);
            sw->wake_cv.wait(lk, [sw] {
                return sw->has_alerts || sw->stopping.load(std::memory_order_acquire);
            });
            sw->has_alerts = false;
        }

        if (sw->stopping.load(std::memory_order_acquire)) {
            break;
        }

        void (TSW_CALL *cb)(void*) = nullptr;
        bool unmapped = false;
        {
            std::lock_guard<std::mutex> lk(sw->cb_mutex);
            cb = sw->callback;
            unmapped = sw->include_unmapped;
        }

        if (cb != nullptr) {
            dispatch_alert_batch(sw, cb, unmapped);
        }
    }
}

} // namespace

void tsw_start_event_worker(tsw_session* session) {
    if (session == nullptr || session->worker.joinable()) {
        return;
    }
    session->worker = std::thread(worker_loop, session);
}

void tsw_stop_event_worker(tsw_session* session) {
    if (session == nullptr) {
        return;
    }
    session->stopping.store(true, std::memory_order_release);
    session->wake_cv.notify_all();
    if (session->worker.joinable()) {
        session->worker.join();
    }
}
