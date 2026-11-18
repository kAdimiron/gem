#pragma once

#include <cstddef>
#include <cstdint>
#include <ctime>

#include <atomic>
#include <condition_variable>
#include <mutex>
#include <thread>

#include <libtorrent/session.hpp>
#include <libtorrent/settings_pack.hpp>
#include <libtorrent/torrent_handle.hpp>
#include <libtorrent/torrent_info.hpp>
#include <libtorrent/torrent_status.hpp>

#ifdef _WIN32
#define TSW_EXPORT __declspec(dllexport)
#define TSW_CALL __cdecl
#else
#define TSW_EXPORT __attribute__((visibility("default")))
#define TSW_CALL
#endif

struct tsw_session {
    lt::session session;
    std::mutex cb_mutex;
    void (TSW_CALL *callback)(void*) = nullptr;
    bool include_unmapped = false;

    std::atomic<bool> stopping{false};
    std::mutex wake_mutex;
    std::condition_variable wake_cv;
    bool has_alerts = false;
    std::thread worker;

    explicit tsw_session(lt::session_params const& params) : session(params) {}
    ~tsw_session();
};

#pragma pack(push, 8)

enum tsw_torrent_state : int32_t {
    tsw_state_unknown = 0,
    tsw_state_checking = 1,
    tsw_state_checking_resume = 2,
    tsw_state_downloading_metadata = 3,
    tsw_state_downloading = 4,
    tsw_state_seeding = 5,
    tsw_state_finished = 6,
    tsw_state_error = 7
};

enum tsw_alert_type : int32_t {
    tsw_alert_generic = 0,
    tsw_alert_torrent_status = 1,
    tsw_alert_client_performance = 2,
    tsw_alert_peer = 3,
    tsw_alert_torrent_removed = 4,
    tsw_alert_metadata_received = 5,
    tsw_alert_read_piece = 6,
    tsw_alert_file_renamed = 7,
    tsw_alert_storage_moved = 8,
    tsw_alert_scrape = 9,
    tsw_alert_resume_data = 10,
    tsw_alert_session_stats = 11,
    tsw_alert_file_error = 12,
    tsw_alert_listen_failed = 13,
    tsw_alert_listen_succeeded = 14,
    tsw_alert_portmap = 15,
    tsw_alert_portmap_error = 16,
    tsw_alert_external_ip = 17,
    tsw_alert_piece_finished = 18,
    tsw_alert_hash_failed = 19,
    tsw_alert_torrent_finished = 20,
    tsw_alert_torrent_error = 21,
    tsw_alert_metadata_failed = 22
};

enum tsw_peer_alert_type : uint8_t {
    tsw_peer_connected_in = 0,
    tsw_peer_connected_out = 1,
    tsw_peer_disconnected = 2,
    tsw_peer_banned = 3,
    tsw_peer_snubbed = 4,
    tsw_peer_unsnubbed = 5,
    tsw_peer_errored = 6
};

struct tsw_metadata_info {
    char* name;
    char* creator;
    char* comment;
    int32_t total_files;
    int64_t total_size;
    int64_t creation_epoch;
    uint8_t info_hash_sha1[20];
    uint8_t info_hash_sha256[32];
};

struct tsw_peer_info {
    char* address;
    char* client;
    int64_t total_download;
    int64_t total_upload;
    int32_t download_rate;
    int32_t upload_rate;
    uint8_t encryption_type;
    uint8_t direction;
    uint8_t is_seed;
    uint8_t we_are_choking;
    uint8_t they_are_choking;
    uint8_t am_interested;
    uint8_t is_interested;
};

struct tsw_peer_list {
    int32_t length;
    tsw_peer_info* peers;
};

struct tsw_peer_progress_info {
    char* address;
    int32_t progress_ppm;
    int32_t reserved;
};

struct tsw_peer_progress_list {
    int32_t length;
    tsw_peer_progress_info* peers;
};

struct tsw_session_stats_metric {
    char* name;
    int32_t value_index;
    uint8_t type;
};

struct tsw_session_stats_metric_list {
    int32_t length;
    tsw_session_stats_metric* metrics;
};

struct tsw_torrent_file_info {
    int32_t index;
    int64_t offset;
    int64_t file_size;
    int64_t modified_time;
    char* file_name;
    char* file_path;
    uint8_t file_path_is_absolute;
    uint8_t pad_file;
};

struct tsw_torrent_file_list {
    int32_t length;
    tsw_torrent_file_info* items;
};

struct tsw_tracker_info {
    int32_t tier;
    char* url;
    uint8_t verified;
    uint8_t fails;
    uint8_t updating;
    char* warning_message;
    char* failure_message;
};

struct tsw_tracker_list {
    int32_t length;
    tsw_tracker_info* trackers;
};

struct tsw_torrent_status {
    tsw_torrent_state state;
    float progress;
    int32_t peer_count;
    int32_t seed_count;
    int64_t bytes_uploaded;
    int64_t bytes_downloaded;
    int64_t upload_rate;
    int64_t download_rate;
    int64_t total_wanted;
    int64_t total_wanted_done;
    int64_t all_time_upload;
    int64_t all_time_download;
    int64_t added_time;
    int32_t connection_count;
    int32_t queue_position;
    int32_t distributed_full_copies;
    uint8_t is_finished;
    uint8_t moving_storage;
};

struct tsw_piece_request {
    int32_t piece;
    int32_t offset;
    int32_t length;
};

struct tsw_event_base {
    tsw_alert_type type;
    int32_t category;
    int64_t timestamp;
    const char* message;
};

struct tsw_external_ip_event {
    tsw_event_base info;
    const char* external_address;
};

struct tsw_file_error_event {
    tsw_event_base info;
    int32_t error_value;
    uint8_t operation;
    uint8_t info_hash[20];
    const char* filename;
};

struct tsw_file_renamed_event {
    tsw_event_base info;
    int32_t file_index;
    uint8_t succeeded;
    uint8_t info_hash[20];
};

struct tsw_hash_failed_event {
    tsw_event_base info;
    int32_t piece_index;
    uint8_t info_hash[20];
};

struct tsw_listen_failed_event {
    tsw_event_base info;
    int32_t error_value;
    uint8_t operation;
    uint8_t socket_type;
    int32_t port;
    const char* listen_interface;
};

struct tsw_listen_succeeded_event {
    tsw_event_base info;
    uint8_t socket_type;
    int32_t port;
    const char* address;
};

struct tsw_metadata_failed_event {
    tsw_event_base info;
    int32_t error_value;
    uint8_t info_hash[20];
};

struct tsw_metadata_received_event {
    tsw_event_base info;
    uint8_t info_hash[20];
};

struct tsw_peer_event {
    tsw_event_base info;
    lt::torrent_handle* handle;
    tsw_peer_alert_type notification_type;
    uint8_t info_hash[20];
    uint8_t v6_address[16];
};

struct tsw_performance_warning_event {
    tsw_event_base info;
    uint8_t warning_code;
};

struct tsw_piece_finished_event {
    tsw_event_base info;
    int32_t piece_index;
    uint8_t info_hash[20];
};

struct tsw_portmap_error_event {
    tsw_event_base info;
    int32_t error_value;
    uint8_t transport;
};

struct tsw_portmap_event {
    tsw_event_base info;
    int32_t external_port;
    uint8_t transport;
};

struct tsw_read_piece_event {
    tsw_event_base info;
    int32_t piece;
    int32_t size;
    uint8_t succeeded;
    uint8_t info_hash[20];
    const char* buffer;
};

struct tsw_resume_data_event {
    tsw_event_base info;
    uint8_t succeeded;
    uint8_t info_hash[20];
    int32_t size;
    const char* buffer;
    uint8_t not_modified;
};

struct tsw_scrape_event {
    tsw_event_base info;
    uint8_t succeeded;
    int32_t incomplete;
    int32_t complete;
    uint8_t info_hash[20];
};

struct tsw_session_stats_event {
    tsw_event_base info;
    int32_t count;
    const int64_t* values;
};

struct tsw_storage_moved_event {
    tsw_event_base info;
    uint8_t succeeded;
    uint8_t info_hash[20];
};

struct tsw_torrent_error_event {
    tsw_event_base info;
    int32_t error_value;
    uint8_t info_hash[20];
    const char* filename;
};

struct tsw_torrent_finished_event {
    tsw_event_base info;
    uint8_t info_hash[20];
};

struct tsw_torrent_removed_event {
    tsw_event_base info;
    uint8_t info_hash[20];
};

struct tsw_torrent_status_event {
    tsw_event_base info;
    tsw_torrent_state old_state;
    tsw_torrent_state new_state;
    uint8_t info_hash[20];
};

#pragma pack(pop)

// Compile-time ABI verification against TorrentSharp.Wrap 4.0.0
static_assert(sizeof(tsw_metadata_info) == 104, "tsw_metadata_info size");
static_assert(sizeof(tsw_peer_info) == 48, "tsw_peer_info size");
static_assert(sizeof(tsw_peer_list) == 16, "tsw_peer_list size");
static_assert(sizeof(tsw_peer_progress_info) == 16, "tsw_peer_progress_info size");
static_assert(sizeof(tsw_peer_progress_list) == 16, "tsw_peer_progress_list size");
static_assert(sizeof(tsw_session_stats_metric) == 16, "tsw_session_stats_metric size");
static_assert(sizeof(tsw_session_stats_metric_list) == 16, "tsw_session_stats_metric_list size");
static_assert(sizeof(tsw_torrent_file_info) == 56, "tsw_torrent_file_info size");
static_assert(sizeof(tsw_torrent_file_list) == 16, "tsw_torrent_file_list size");
static_assert(sizeof(tsw_tracker_info) == 40, "tsw_tracker_info size");
static_assert(sizeof(tsw_tracker_list) == 16, "tsw_tracker_list size");
static_assert(sizeof(tsw_torrent_status) == 104, "tsw_torrent_status size");
static_assert(sizeof(tsw_piece_request) == 12, "tsw_piece_request size");
static_assert(sizeof(tsw_event_base) == 24, "tsw_event_base size");
static_assert(sizeof(tsw_external_ip_event) == 32, "tsw_external_ip_event size");
static_assert(sizeof(tsw_file_error_event) == 64, "tsw_file_error_event size");
static_assert(sizeof(tsw_file_renamed_event) == 56, "tsw_file_renamed_event size");
static_assert(sizeof(tsw_hash_failed_event) == 48, "tsw_hash_failed_event size");
static_assert(sizeof(tsw_listen_failed_event) == 48, "tsw_listen_failed_event size");
static_assert(sizeof(tsw_listen_succeeded_event) == 40, "tsw_listen_succeeded_event size");
static_assert(sizeof(tsw_metadata_failed_event) == 48, "tsw_metadata_failed_event size");
static_assert(sizeof(tsw_metadata_received_event) == 48, "tsw_metadata_received_event size");
static_assert(sizeof(tsw_peer_event) == 72, "tsw_peer_event size");
static_assert(sizeof(tsw_performance_warning_event) == 32, "tsw_performance_warning_event size");
static_assert(sizeof(tsw_piece_finished_event) == 48, "tsw_piece_finished_event size");
static_assert(sizeof(tsw_portmap_error_event) == 32, "tsw_portmap_error_event size");
static_assert(sizeof(tsw_portmap_event) == 32, "tsw_portmap_event size");
static_assert(sizeof(tsw_read_piece_event) == 64, "tsw_read_piece_event size");
static_assert(sizeof(tsw_resume_data_event) == 72, "tsw_resume_data_event size");
static_assert(sizeof(tsw_scrape_event) == 56, "tsw_scrape_event size");
static_assert(sizeof(tsw_session_stats_event) == 40, "tsw_session_stats_event size");
static_assert(sizeof(tsw_storage_moved_event) == 48, "tsw_storage_moved_event size");
static_assert(sizeof(tsw_torrent_error_event) == 56, "tsw_torrent_error_event size");
static_assert(sizeof(tsw_torrent_finished_event) == 48, "tsw_torrent_finished_event size");
static_assert(sizeof(tsw_torrent_removed_event) == 48, "tsw_torrent_removed_event size");
static_assert(sizeof(tsw_torrent_status_event) == 56, "tsw_torrent_status_event size");

static_assert(offsetof(tsw_metadata_info, info_hash_sha1) == 48, "info_hash_sha1 offset");
static_assert(offsetof(tsw_metadata_info, info_hash_sha256) == 68, "info_hash_sha256 offset");
static_assert(offsetof(tsw_peer_event, v6_address) == 53, "v6_address offset");
static_assert(offsetof(tsw_resume_data_event, not_modified) == 64, "not_modified offset");
static_assert(offsetof(tsw_torrent_status, is_finished) == 100, "is_finished offset");
static_assert(offsetof(tsw_torrent_status, moving_storage) == 101, "moving_storage offset");

void tsw_start_event_worker(tsw_session* session);
void tsw_stop_event_worker(tsw_session* session);
tsw_torrent_state tsw_map_torrent_state(lt::torrent_status::state_t st);
