#include "dictation_bridge.h"

#include <cstring>
#include <memory>

#include <esp_log.h>
#include <cJSON.h>

extern const char kSetupPage[] asm("_binary_dictation_setup_html_start");

namespace {

std::string Field(const cJSON* root, const char* name) {
    auto* value = cJSON_GetObjectItemCaseSensitive(root, name);
    return cJSON_IsString(value) ? value->valuestring : "";
}
}  // namespace

void DictationBridge::StartSetupServer() {
    if (setup_server_)
        return;
    setup_nonce_ = RandomToken();
    httpd_config_t config = HTTPD_DEFAULT_CONFIG();
    config.server_port = 18766;
    config.ctrl_port = 32769;
    config.stack_size = 4096;
    config.max_open_sockets = 2;
    config.max_uri_handlers = 2;
    config.lru_purge_enable = true;
    config.recv_wait_timeout = 3;
    config.send_wait_timeout = 3;
    if (httpd_start(&setup_server_, &config) != ESP_OK) {
        setup_server_ = nullptr;
        return;
    }
    httpd_uri_t page = {};
    page.uri = "/";
    page.method = HTTP_GET;
    page.user_ctx = this;
    page.handler = [](httpd_req_t* request) -> esp_err_t {
        auto* bridge = static_cast<DictationBridge*>(request->user_ctx);
        httpd_resp_set_hdr(request, "Cache-Control", "no-store");
        httpd_resp_set_hdr(request, "X-Frame-Options", "DENY");
        httpd_resp_set_hdr(request, "X-Content-Type-Options", "nosniff");
        if (!bridge->SetupAllowed())
            return httpd_resp_send_err(request, HTTPD_403_FORBIDDEN, "Setup window closed");
        std::string html(kSetupPage);
        const std::string url =
            ValidRelayUrl(CONFIG_PASSPORT_RELAY_URL) ? CONFIG_PASSPORT_RELAY_URL : "";
        html.replace(html.find("@@URL@@"), strlen("@@URL@@"), url);
        html.replace(html.find("@@NONCE@@"), strlen("@@NONCE@@"), bridge->setup_nonce_);
        httpd_resp_set_type(request, "text/html; charset=utf-8");
        return httpd_resp_send(request, html.c_str(), html.size());
    };
    if (httpd_register_uri_handler(setup_server_, &page) != ESP_OK) {
        StopSetupServer();
        return;
    }
    httpd_uri_t configure = {};
    configure.uri = "/configure";
    configure.method = HTTP_POST;
    configure.user_ctx = this;
    configure.handler = [](httpd_req_t* request) -> esp_err_t {
        auto* bridge = static_cast<DictationBridge*>(request->user_ctx);
        httpd_resp_set_hdr(request, "Cache-Control", "no-store");
        char nonce[65];
        char type[64];
        if (!bridge->SetupAllowed() ||
            httpd_req_get_hdr_value_str(request, "X-Passport-Setup", nonce, sizeof(nonce)) !=
                ESP_OK ||
            bridge->setup_nonce_ != nonce)
            return httpd_resp_send_err(request, HTTPD_403_FORBIDDEN, "Setup window closed");
        if (httpd_req_get_hdr_value_str(request, "Content-Type", type, sizeof(type)) != ESP_OK ||
            strncmp(type, "application/json", strlen("application/json")) != 0)
            return httpd_resp_send_err(request, HTTPD_400_BAD_REQUEST, "JSON required");
        if (request->content_len == 0 || request->content_len > 768)
            return httpd_resp_send_err(request, HTTPD_400_BAD_REQUEST, "Invalid request size");
        std::string body(request->content_len, '\0');
        size_t used = 0;
        while (used < body.size()) {
            int received = httpd_req_recv(request, body.data() + used, body.size() - used);
            if (received <= 0)
                return httpd_resp_send_err(request, HTTPD_400_BAD_REQUEST, "Incomplete request");
            used += received;
        }
        std::unique_ptr<cJSON, decltype(&cJSON_Delete)> root(cJSON_Parse(body.c_str()),
                                                             cJSON_Delete);
        const auto mode = Field(root.get(), "mode");
        if ((mode != "cloud" && mode != "lan") ||
            !bridge->Configure(mode == "cloud", Field(root.get(), "url"),
                               Field(root.get(), "code")))
            return httpd_resp_send_err(request, HTTPD_400_BAD_REQUEST,
                                       "Check relay URL and pairing code");
        httpd_resp_set_status(request, "202 Accepted");
        httpd_resp_set_type(request, "application/json");
        return httpd_resp_sendstr(request, "{\"status\":\"queued\"}");
    };
    if (httpd_register_uri_handler(setup_server_, &configure) != ESP_OK)
        StopSetupServer();
}

void DictationBridge::StopSetupServer() {
    if (!setup_server_)
        return;
    httpd_stop(setup_server_);
    setup_server_ = nullptr;
    setup_nonce_.clear();
}
