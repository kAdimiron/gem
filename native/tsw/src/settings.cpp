#include "tsw.h"

#include <cstring>
#include <string>

namespace {

bool is_known_lt2_only_key(const char* key) {
    static const char* const lt2_keys[] = {
        "mmap_file_size_cutoff",
        "disk_write_mode",
        "hashing_threads",
        "validate_https_trackers",
        "allow_i2p_mixed",
        "dht_privacy_lookups"
    };
    for (const char* k : lt2_keys) {
        if (std::strcmp(key, k) == 0) {
            return true;
        }
    }
    return false;
}

} // namespace

extern "C" {

TSW_EXPORT lt::settings_pack* TSW_CALL create_settings_pack() {
    return new lt::settings_pack();
}

TSW_EXPORT void TSW_CALL destroy_settings_pack(lt::settings_pack* pack) {
    delete pack;
}

TSW_EXPORT uint8_t TSW_CALL settings_pack_set_str(lt::settings_pack* pack, const char* key, const char* value) {
    if (pack == nullptr || key == nullptr || value == nullptr) {
        return 0;
    }

    int const setting_id = lt::setting_by_name(key);
    if (setting_id < 0) {
        return is_known_lt2_only_key(key) ? 1 : 0;
    }

    if ((setting_id & lt::settings_pack::type_mask) != lt::settings_pack::string_type_base) {
        return 0;
    }

    pack->set_str(setting_id, std::string(value));
    return 1;
}

TSW_EXPORT uint8_t TSW_CALL settings_pack_set_bool(lt::settings_pack* pack, const char* key, uint8_t value) {
    if (pack == nullptr || key == nullptr) {
        return 0;
    }

    int const setting_id = lt::setting_by_name(key);
    if (setting_id < 0) {
        return is_known_lt2_only_key(key) ? 1 : 0;
    }

    if ((setting_id & lt::settings_pack::type_mask) != lt::settings_pack::bool_type_base) {
        return 0;
    }

    pack->set_bool(setting_id, value != 0);
    return 1;
}

TSW_EXPORT uint8_t TSW_CALL settings_pack_set_int(lt::settings_pack* pack, const char* key, int32_t value) {
    if (pack == nullptr || key == nullptr) {
        return 0;
    }

    int const setting_id = lt::setting_by_name(key);
    if (setting_id < 0) {
        return is_known_lt2_only_key(key) ? 1 : 0;
    }

    if ((setting_id & lt::settings_pack::type_mask) != lt::settings_pack::int_type_base) {
        return 0;
    }

    pack->set_int(setting_id, value);
    return 1;
}

} // extern "C"
