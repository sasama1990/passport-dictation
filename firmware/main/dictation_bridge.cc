#include "dictation_bridge.h"

#include <esp_log.h>
#include <esp_random.h>
#include <esp_timer.h>
#include <cJSON.h>
#include <fcntl.h>
#include <lwip/inet.h>
#include <lwip/sockets.h>
#include <psa/crypto.h>

#include <algorithm>
#include <cstring>
#include <memory>
#include <new>

#include <wifi_manager.h>
#include "board.h"
#include "settings.h"
#include "system_info.h"

namespace {
constexpr char kTag[] = "DictationBridge";
constexpr char kService[] = "passport-dictation";
constexpr int kDiscoveryPort = 47625;

int64_t NowMs() { return esp_timer_get_time() / 1000; }

std::string StringField(const cJSON* root, const char* name) {
    const auto* value = cJSON_GetObjectItemCaseSensitive(root, name);
    return cJSON_IsString(value) ? value->valuestring : "";
}

std::string Hex(const uint8_t* data, size_t size) {
    constexpr char digits[] = "0123456789abcdef";
    std::string result(size * 2, '0');
    for (size_t index = 0; index < size; index++) {
        result[index * 2] = digits[data[index] >> 4];
        result[index * 2 + 1] = digits[data[index] & 15];
    }
    return result;
}
}  // namespace

DictationBridge& DictationBridge::GetInstance() {
    static DictationBridge bridge;
    return bridge;
}

void DictationBridge::Start(ResultCallback callback) {
    if (queue_ != nullptr) {
        return;
    }
    callback_ = std::move(callback);
    device_id_ = SystemInfo::GetMacAddress();
    queue_ = xQueueCreate(4, sizeof(Job*));
    if (queue_ == nullptr) {
        ESP_LOGE(kTag, "Cannot allocate bridge queue");
        return;
    }
    if (xTaskCreate([](void* context) { static_cast<DictationBridge*>(context)->Run(); },
                    "dictation_bridge", 6144, this, 2, nullptr) != pdPASS) {
        vQueueDelete(queue_);
        queue_ = nullptr;
        ESP_LOGE(kTag, "Cannot create bridge task");
    }
}

bool DictationBridge::Enqueue(const std::string& action, const std::string& id,
                              const std::string& text) {
    if (queue_ == nullptr || text.size() > 12288) {
        return false;
    }
    auto* job = new (std::nothrow) Job{action, id, text, NowMs()};
    if (job == nullptr) {
        return false;
    }
    if (xQueueSend(queue_, &job, 0) != pdTRUE) {
        delete job;
        return false;
    }
    return true;
}

bool DictationBridge::Begin(const std::string& id) { return Enqueue("begin", id, ""); }

bool DictationBridge::SendText(const std::string& id, const std::string& text) {
    return Enqueue("text", id, text);
}

void DictationBridge::Cancel(const std::string& id) {
    if (!id.empty()) {
        Enqueue("cancel", id, "");
    }
}

void DictationBridge::ResetPairing() { reset_pairing_.store(true); }

std::string DictationBridge::RandomToken() {
    uint8_t random[32];
    esp_fill_random(random, sizeof(random));
    return Hex(random, sizeof(random));
}

void DictationBridge::Run() {
    LoadCloud();
    {
        Settings settings("dictation");
        receiver_id_ = settings.GetString("receiver");
        token_ = settings.GetString("token");
        host_ = settings.GetString("host");
        port_ = settings.GetInt("port", 18765);
    }
    // WifiManager initializes esp_netif and its tcpip thread. Creating a socket
    // before that point asserts in lwIP because the TCP/IP mailbox is absent.
    while (!WifiManager::GetInstance().IsInitialized()) {
        vTaskDelay(pdMS_TO_TICKS(100));
    }
    socket_ = socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP);
    if (socket_ >= 0) {
        int broadcast = 1;
        setsockopt(socket_, SOL_SOCKET, SO_BROADCAST, &broadcast, sizeof(broadcast));
        fcntl(socket_, F_SETFL, O_NONBLOCK);
    }

    while (true) {
        if (reset_pairing_.exchange(false)) {
            receiver_id_.clear();
            token_.clear();
            host_.clear();
            Settings settings("dictation", true);
            settings.SetString("receiver", "");
            settings.SetString("token", "");
            settings.SetString("host", "");
            cloud_receiver_id_.clear();
            cloud_token_.clear();
            cloud_pair_code_.clear();
            SaveCloud();
            setup_until_ms_.store(NowMs() + 600000);
            last_discovery_ms_ = -10000;
            if (callback_) {
                callback_("setup", SetupUrl(), "open");
            }
        }
        if (WifiManager::GetInstance().IsInitialized() && SetupAllowed()) {
            StartSetupServer();
        } else {
            StopSetupServer();
        }
        if (cloud_enabled_ && cloud_receiver_id_.empty() && !cloud_pair_code_.empty() &&
            WifiManager::GetInstance().IsConnected() && NowMs() - last_pair_ms_ >= 5000) {
            PairCloud();
        }
        if (!cloud_enabled_ &&
            NowMs() - last_discovery_ms_ >= (receiver_id_.empty() ? 1000 : 10000)) {
            Discover();
        }
        if (!cloud_enabled_)
            PollDiscovery();
        Job* queued = nullptr;
        if (xQueueReceive(queue_, &queued, pdMS_TO_TICKS(100)) == pdTRUE) {
            std::unique_ptr<Job> job(queued);
            if (job->action == "configure_cloud" || job->action == "configure_lan") {
                ConfigureCloud(*job);
                continue;
            }
            if (!cloud_enabled_ && host_.empty()) {
                Discover();
                for (int attempt = 0; attempt < 8 && host_.empty(); attempt++) {
                    vTaskDelay(pdMS_TO_TICKS(100));
                    PollDiscovery();
                }
            }
            auto status = Post(*job);
            if (callback_) {
                callback_(job->action, job->id, status);
            }
        }
    }
}

void DictationBridge::Discover() {
    last_discovery_ms_ = NowMs();
    if (socket_ < 0) {
        return;
    }
    uint8_t random[16];
    esp_fill_random(random, sizeof(random));
    nonce_ = Hex(random, sizeof(random));
    cJSON* root = cJSON_CreateObject();
    cJSON_AddStringToObject(root, "service", kService);
    cJSON_AddNumberToObject(root, "version", 1);
    cJSON_AddStringToObject(root, "device_id", device_id_.c_str());
    cJSON_AddStringToObject(root, "receiver_id", receiver_id_.c_str());
    cJSON_AddStringToObject(root, "nonce", nonce_.c_str());
    char* body = cJSON_PrintUnformatted(root);
    cJSON_Delete(root);
    if (body == nullptr) {
        return;
    }
    sockaddr_in destination = {};
    destination.sin_family = AF_INET;
    destination.sin_port = htons(kDiscoveryPort);
    destination.sin_addr.s_addr = htonl(INADDR_BROADCAST);
    sendto(socket_, body, strlen(body), 0, reinterpret_cast<sockaddr*>(&destination),
           sizeof(destination));
    cJSON_free(body);
}

std::string DictationBridge::Proof(const std::string& value) const {
    if (psa_crypto_init() != PSA_SUCCESS) {
        return "";
    }
    psa_key_attributes_t attributes = PSA_KEY_ATTRIBUTES_INIT;
    psa_set_key_type(&attributes, PSA_KEY_TYPE_HMAC);
    psa_set_key_algorithm(&attributes, PSA_ALG_HMAC(PSA_ALG_SHA_256));
    psa_set_key_usage_flags(&attributes, PSA_KEY_USAGE_SIGN_MESSAGE);
    psa_key_id_t key = PSA_KEY_ID_NULL;
    auto status = psa_import_key(&attributes, reinterpret_cast<const uint8_t*>(token_.data()),
                                 token_.size(), &key);
    psa_reset_key_attributes(&attributes);
    if (status != PSA_SUCCESS) {
        return "";
    }
    uint8_t digest[32];
    size_t digest_size = 0;
    status = psa_mac_compute(key, PSA_ALG_HMAC(PSA_ALG_SHA_256),
                             reinterpret_cast<const uint8_t*>(value.data()), value.size(), digest,
                             sizeof(digest), &digest_size);
    psa_destroy_key(key);
    return status == PSA_SUCCESS ? Hex(digest, digest_size) : "";
}

void DictationBridge::PollDiscovery() {
    if (socket_ < 0 || nonce_.empty()) {
        return;
    }
    for (int count = 0; count < 3; count++) {
        char buffer[1536];
        sockaddr_in source = {};
        socklen_t source_size = sizeof(source);
        int size = recvfrom(socket_, buffer, sizeof(buffer) - 1, 0,
                            reinterpret_cast<sockaddr*>(&source), &source_size);
        if (size <= 0) {
            return;
        }
        buffer[size] = '\0';
        std::unique_ptr<cJSON, decltype(&cJSON_Delete)> root(cJSON_Parse(buffer), cJSON_Delete);
        if (root == nullptr || StringField(root.get(), "service") != kService ||
            StringField(root.get(), "nonce") != nonce_) {
            continue;
        }
        auto* version = cJSON_GetObjectItemCaseSensitive(root.get(), "version");
        auto* port = cJSON_GetObjectItemCaseSensitive(root.get(), "port");
        const auto receiver = StringField(root.get(), "receiver_id");
        const auto host = StringField(root.get(), "host");
        char address[INET_ADDRSTRLEN];
        inet_ntop(AF_INET, &source.sin_addr, address, sizeof(address));
        if (!cJSON_IsNumber(version) || version->valueint != 1 || !cJSON_IsNumber(port) ||
            port->valueint < 1024 || port->valueint > 65535 || receiver.size() != 32 ||
            host != address) {
            continue;
        }
        bool paired_now = receiver_id_.empty();
        if (paired_now) {
            const auto offered_token = StringField(root.get(), "token");
            if (offered_token.size() != 64) {
                continue;
            }
            token_ = offered_token;
        } else if (receiver_id_ != receiver) {
            continue;
        }
        auto expected_proof =
            Proof(nonce_ + "\n" + receiver + "\n" + host + "\n" + std::to_string(port->valueint));
        if (expected_proof.empty() || StringField(root.get(), "proof") != expected_proof) {
            if (paired_now) {
                token_.clear();
            }
            continue;
        }
        if (receiver_id_ != receiver || host_ != host || port_ != port->valueint) {
            receiver_id_ = receiver;
            host_ = host;
            port_ = port->valueint;
            Settings settings("dictation", true);
            settings.SetString("receiver", receiver_id_);
            settings.SetString("token", token_);
            settings.SetString("host", host_);
            settings.SetInt("port", port_);
        }
        if (paired_now && callback_) {
            callback_("pair", "", "paired");
        }
    }
}

std::string DictationBridge::Post(const Job& job) {
    if (cloud_enabled_ ? cloud_receiver_id_.empty() || cloud_token_.size() != 64
                       : host_.empty() || token_.size() != 64) {
        return "not_paired";
    }
    cJSON* root = cJSON_CreateObject();
    if (root == nullptr)
        return "out_of_memory";
    cJSON_AddStringToObject(root, "device_id", device_id_.c_str());
    cJSON_AddStringToObject(root, "id", job.id.c_str());
    if (!job.text.empty()) {
        cJSON_AddStringToObject(root, "text", job.text.c_str());
    }
    char* serialized = cJSON_PrintUnformatted(root);
    cJSON_Delete(root);
    if (serialized == nullptr) {
        return "out_of_memory";
    }
    std::string body(serialized);
    cJSON_free(serialized);
    const auto url = cloud_enabled_
                         ? relay_url_ + "/v1/device/" + cloud_receiver_id_ + "/" + job.action
                         : "http://" + host_ + ":" + std::to_string(port_) + "/v1/" + job.action;
    for (int attempt = 0; attempt < 3; attempt++) {
        const int remaining = (cloud_enabled_ ? 14000 : 10000) - (NowMs() - job.created_ms);
        if (remaining <= 0) {
            return "expired";
        }
        auto response = Request(url, body, cloud_enabled_ ? cloud_token_ : token_,
                                std::min(remaining, cloud_enabled_ ? 12000 : 2500));
        const int code = response.code;
        if (code >= 500 || code == 0) {
            continue;
        }
        if (code == 401) {
            return "not_paired";
        }
        std::unique_ptr<cJSON, decltype(&cJSON_Delete)> reply(cJSON_Parse(response.body.c_str()),
                                                              cJSON_Delete);
        if (reply != nullptr && StringField(reply.get(), "id") == job.id) {
            auto status = StringField(reply.get(), "status");
            if (!status.empty()) {
                return status;
            }
        }
        return "rejected";
    }
    return "network_error";
}

DictationBridge::HttpReply DictationBridge::Request(const std::string& url, const std::string& body,
                                                    const std::string& token, int timeout_ms) {
    auto http = Board::GetInstance().GetNetwork()->CreateHttp(0);
    if (!http)
        return {0, ""};
    http->SetTimeout(timeout_ms);
    http->SetKeepAlive(false);
    http->SetHeader("Content-Type", "application/json");
    if (!token.empty())
        http->SetHeader("Authorization", "Bearer " + token);
    http->SetContent(std::string(body));
    if (!http->Open("POST", url))
        return {0, ""};
    const int code = http->GetStatusCode().value_or(0);
    char response[768];
    size_t used = 0;
    while (used < sizeof(response) - 1) {
        auto read = http->Read(response + used, sizeof(response) - 1 - used);
        if (!read || *read <= 0)
            break;
        used += *read;
    }
    response[used] = '\0';
    http->Close();
    return {code, std::string(response, used)};
}
