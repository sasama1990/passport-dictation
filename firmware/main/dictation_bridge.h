#pragma once

#include <atomic>
#include <functional>
#include <string>

#include <esp_http_server.h>
#include <freertos/FreeRTOS.h>
#include <freertos/queue.h>
#include <freertos/task.h>

// The worker owns discovery, credentials and HTTP. Audio/main callbacks only
// enqueue bounded jobs and receive results through the supplied callback.
class DictationBridge {
public:
    enum class ConnectionState {
        Unpaired,
        Pairing,
        Ready,
        NetworkError,
        RelayForbidden,
        PairingExpired
    };
    using ResultCallback =
        std::function<void(const std::string&, const std::string&, const std::string&)>;

    static DictationBridge& GetInstance();
    void Start(ResultCallback callback);
    std::string Begin(const std::string& id);
    bool SendText(const std::string& id, const std::string& text);
    void Cancel(const std::string& id);
    void CancelPendingBegin(const std::string& id);
    void ResetPairing();
    bool Configure(bool cloud, const std::string& url, const std::string& code);
    std::string SetupUrl() const;

private:
    struct Job {
        std::string action;
        std::string id;
        std::string text;
        int64_t created_ms;
    };

    QueueHandle_t queue_ = nullptr;
    ResultCallback callback_;
    std::atomic<bool> reset_pairing_{false};
    enum class BeginPhase { Idle, Pending, Cancelled };
    std::atomic<BeginPhase> begin_phase_{BeginPhase::Idle};
    std::atomic<ConnectionState> connection_state_{ConnectionState::Unpaired};
    std::string device_id_;
    std::string receiver_id_;
    std::string token_;
    std::string host_;
    std::string nonce_;
    int port_ = 18765;
    int socket_ = -1;
    int64_t last_discovery_ms_ = -10000;
    bool cloud_enabled_ = false;
    std::string relay_url_;
    std::string cloud_receiver_id_;
    std::string cloud_token_;
    std::string cloud_pair_code_;
    int64_t last_pair_ms_ = -5000;
    std::atomic<int64_t> setup_until_ms_{0};
    httpd_handle_t setup_server_ = nullptr;
    std::string setup_nonce_;

    struct HttpReply {
        int code;
        std::string body;
    };

    bool Enqueue(const std::string& action, const std::string& id, const std::string& text);
    void Run();
    void SetConnectionState(ConnectionState state);
    void RefreshConnectionState();
    void Discover();
    void PollDiscovery();
    std::string Post(const Job& job);
    std::string Proof(const std::string& value) const;
    HttpReply Request(const std::string& url, const std::string& body, const std::string& token,
                      int timeout_ms);
    static std::string RandomToken();
    static bool ValidRelayUrl(const std::string& url);
    static std::string NormalizeCode(const std::string& code);
    void LoadCloud();
    bool SaveCloud();
    void ConfigureCloud(const Job& job);
    void PairCloud();
    bool SetupAllowed() const;
    void StartSetupServer();
    void StopSetupServer();
};
