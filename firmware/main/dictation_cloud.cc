#include "dictation_bridge.h"

#include <algorithm>
#include <cctype>
#include <memory>
#include <string_view>

#include <esp_timer.h>
#include <cJSON.h>
#include <wifi_manager.h>

#include "settings.h"

namespace {
constexpr char kAlphabet[] = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

std::string Field(const cJSON* object, const char* name) {
    auto* value = cJSON_GetObjectItemCaseSensitive(object, name);
    return cJSON_IsString(value) ? value->valuestring : "";
}

bool HexString(const std::string& value, size_t size) {
    return value.size() == size && std::all_of(value.begin(), value.end(), [](char letter) {
               return (letter >= '0' && letter <= '9') || (letter >= 'a' && letter <= 'f');
           });
}
}  // namespace

bool DictationBridge::ValidRelayUrl(const std::string& url) {
    return url.starts_with("https://") && url.size() > 8 && url.size() <= 192 &&
           std::all_of(url.begin(), url.end(), [](unsigned char letter) {
               return std::isalnum(letter) ||
                      std::string_view(":/._-").find(letter) != std::string_view::npos;
           });
}

std::string DictationBridge::NormalizeCode(const std::string& code) {
    std::string result;
    for (unsigned char letter : code) {
        if (letter == '-' || std::isspace(letter))
            continue;
        letter = std::toupper(letter);
        if (std::string_view(kAlphabet).find(letter) == std::string_view::npos)
            return "";
        result += letter;
    }
    return result.size() == 8 ? result : "";
}

bool DictationBridge::Configure(bool cloud, const std::string& url, const std::string& code) {
    auto normalized_url = url;
    while (normalized_url.ends_with('/'))
        normalized_url.pop_back();
    const auto normalized_code = NormalizeCode(code);
    if (cloud && (!ValidRelayUrl(normalized_url) || normalized_code.empty()))
        return false;
    return Enqueue(cloud ? "configure_cloud" : "configure_lan", normalized_code, normalized_url);
}

void DictationBridge::LoadCloud() {
    cloud_enabled_ = false;
    relay_url_ = CONFIG_PASSPORT_RELAY_URL;
    cloud_receiver_id_.clear();
    cloud_token_.clear();
    cloud_pair_code_.clear();
    Settings settings("dictcloud");
    const auto serialized = settings.GetString("config");
    if (serialized.empty())
        return;
    std::unique_ptr<cJSON, decltype(&cJSON_Delete)> root(cJSON_Parse(serialized.c_str()),
                                                         cJSON_Delete);
    // A malformed explicit cloud configuration must fail closed until setup.
    cloud_enabled_ = true;
    if (!root)
        return;
    auto* enabled = cJSON_GetObjectItemCaseSensitive(root.get(), "enabled");
    if (!cJSON_IsBool(enabled))
        return;
    cloud_enabled_ = cJSON_IsTrue(enabled);
    const auto url = Field(root.get(), "url");
    if (!ValidRelayUrl(url))
        return;
    relay_url_ = url;
    const auto token = Field(root.get(), "token");
    if (!HexString(token, 64))
        return;
    cloud_token_ = token;
    cloud_pair_code_ = NormalizeCode(Field(root.get(), "code"));
    const auto room = Field(root.get(), "room");
    if (HexString(room, 32))
        cloud_receiver_id_ = room;
}

bool DictationBridge::SaveCloud() {
    auto* root = cJSON_CreateObject();
    if (!root)
        return false;
    cJSON_AddBoolToObject(root, "enabled", cloud_enabled_);
    cJSON_AddStringToObject(root, "url", relay_url_.c_str());
    cJSON_AddStringToObject(root, "room", cloud_receiver_id_.c_str());
    cJSON_AddStringToObject(root, "token", cloud_token_.c_str());
    cJSON_AddStringToObject(root, "code", cloud_pair_code_.c_str());
    char* serialized = cJSON_PrintUnformatted(root);
    cJSON_Delete(root);
    if (!serialized)
        return false;
    {
        Settings settings("dictcloud", true);
        settings.SetString("config", serialized);
    }
    cJSON_free(serialized);
    return true;
}

void DictationBridge::ConfigureCloud(const Job& job) {
    if (job.action == "configure_lan") {
        cloud_enabled_ = false;
        last_discovery_ms_ = -10000;
        setup_until_ms_.store(0);
    } else {
        if (relay_url_ != job.text || cloud_pair_code_ != job.id || !HexString(cloud_token_, 64)) {
            cloud_token_ = RandomToken();
            cloud_receiver_id_.clear();
        }
        cloud_enabled_ = true;
        relay_url_ = job.text;
        cloud_pair_code_ = job.id;
        last_pair_ms_ = -5000;
    }
    if (!SaveCloud()) {
        LoadCloud();
        if (callback_)
            callback_("pair", "", "storage_error");
        return;
    }
    if (callback_)
        callback_("pair", "", cloud_enabled_ ? "pairing_cloud" : "pairing");
}

void DictationBridge::PairCloud() {
    last_pair_ms_ = esp_timer_get_time() / 1000;
    if (!ValidRelayUrl(relay_url_) || !HexString(cloud_token_, 64))
        return;
    auto* root = cJSON_CreateObject();
    if (!root)
        return;
    cJSON_AddStringToObject(root, "code", cloud_pair_code_.c_str());
    cJSON_AddStringToObject(root, "device_id", device_id_.c_str());
    cJSON_AddStringToObject(root, "device_token", cloud_token_.c_str());
    char* serialized = cJSON_PrintUnformatted(root);
    cJSON_Delete(root);
    if (!serialized)
        return;
    const std::string body(serialized);
    cJSON_free(serialized);
    auto response = Request(relay_url_ + "/v1/pair", body, "", 4000);
    if (response.code == 0 || response.code >= 500)
        return;
    std::unique_ptr<cJSON, decltype(&cJSON_Delete)> reply(cJSON_Parse(response.body.c_str()),
                                                          cJSON_Delete);
    const auto room = Field(reply.get(), "receiver_id");
    if (response.code == 200 && Field(reply.get(), "status") == "paired" && HexString(room, 32)) {
        cloud_receiver_id_ = room;
        if (!SaveCloud()) {
            LoadCloud();
            return;
        }
        setup_until_ms_.store(0);
        if (callback_)
            callback_("pair", "", "paired_cloud");
    } else if (response.code == 400 || response.code == 409) {
        cloud_pair_code_.clear();
        SaveCloud();
        if (callback_)
            callback_("pair", "", "pairing_expired");
    }
}

bool DictationBridge::SetupAllowed() const {
    return WifiManager::GetInstance().IsConfigMode() ||
           setup_until_ms_.load() > esp_timer_get_time() / 1000;
}

std::string DictationBridge::SetupUrl() const {
    auto& wifi = WifiManager::GetInstance();
    const auto host = wifi.IsConfigMode() ? "192.168.4.1" : wifi.GetIpAddress();
    return "http://" + (host.empty() ? "192.168.4.1" : host) + ":18766";
}
