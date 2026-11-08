#pragma once

#include <atomic>
#include <functional>
#include <string>

#include <freertos/FreeRTOS.h>
#include <freertos/queue.h>
#include <freertos/task.h>

// The worker owns discovery, credentials and HTTP. Audio/main callbacks only
// enqueue bounded jobs and receive results through the supplied callback.
class DictationBridge {
public:
    using ResultCallback =
        std::function<void(const std::string&, const std::string&, const std::string&)>;

    static DictationBridge& GetInstance();
    void Start(ResultCallback callback);
    bool Begin(const std::string& id);
    bool SendText(const std::string& id, const std::string& text);
    void Cancel(const std::string& id);
    void ResetPairing();

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
    std::string device_id_;
    std::string receiver_id_;
    std::string token_;
    std::string host_;
    std::string nonce_;
    int port_ = 18765;
    int socket_ = -1;
    int64_t last_discovery_ms_ = -10000;

    bool Enqueue(const std::string& action, const std::string& id, const std::string& text);
    void Run();
    void Discover();
    void PollDiscovery();
    std::string Post(const Job& job);
    std::string Proof(const std::string& value) const;
};
