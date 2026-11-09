#include "application.h"
#include "assets/lang_config.h"
#include "board.h"
#include "dictation_bridge.h"
#include "display.h"

#include <esp_log.h>
#include <esp_random.h>
#include <esp_timer.h>

void Application::ShowDictationStatus(const char* status, const char* message) {
    if (status != nullptr) {
        dictation_status_ = status;
        ESP_LOGI("Dictation", "Stage: %s", status);
    }
    if (message != nullptr)
        dictation_message_ = message;
    const auto state = GetDeviceState();
    if (state != kDeviceStateIdle && state != kDeviceStateConnecting &&
        state != kDeviceStateListening)
        return;
    auto* display = Board::GetInstance().GetDisplay();
    display->SetStatus(dictation_status_.c_str());
    display->SetChatMessage("system", dictation_message_.c_str());
}

void Application::ShowDictationFailure(const std::string& status) {
    ESP_LOGW("Dictation", "Result: %s", status.c_str());
    if (status == "not_paired")
        ShowDictationStatus("等待配对", "在电脑开启配对\n长按上键打开设备设置");
    else if (status == "pairing_expired")
        ShowDictationStatus("配对码已失效", "在电脑生成新的配对码\n长按上键重新设置");
    else if (status == "pairing_cloud")
        ShowDictationStatus("正在配对", "等待电脑配对完成\n完成后再按住OK");
    else if (status == "relay_forbidden")
        ShowDictationStatus("中继拒绝连接", "当前中继无法使用\n请更换中继地址后配对");
    else if (status == "wifi_required")
        ShowDictationStatus("设备未联网", "连接Wi-Fi后再按住OK\n换网请长按下键");
    else if (status == "network_error")
        ShowDictationStatus("网络连接失败", "无法连接中继或电脑\n检查网络后重新按住OK");
    else if (status == "receiver_offline")
        ShowDictationStatus("电脑未连接", "启动Windows接收程序\n联网后再按住OK");
    else if (status == "no_target")
        ShowDictationStatus("请选择输入框", "电脑点选目标输入框\n再按住OK等提示音");
    else if (status == "paused")
        ShowDictationStatus("电脑暂停接收", "在Windows程序恢复接收\n再按住OK");
    else if (status == "connection_busy" || status == "busy" || status == "queue_full")
        ShowDictationStatus("上一轮结束中", "松开OK，稍候再按住\n等提示音后开始说话");
    else if (status == "focus_changed" || status == "keyboard_busy" || status == "input_blocked" ||
             status == "unknown_utterance")
        ShowDictationStatus("电脑已收到文字", "自动输入未完成\n在接收程序中复制文字");
    else if (status == "uncertain")
        ShowDictationStatus("请核对输入结果", "电脑已收到文字\n先检查输入框，再复制");
    else if (status == "expired")
        ShowDictationStatus("本轮已超时", "先检查电脑输入结果\n再重新按住OK录音");
    else
        ShowDictationStatus("本轮未完成", "检查电脑接收程序\n松开OK后重新录音");
}

void Application::StartDictation() {
    dictation_pressed_.store(true);
    Schedule([this]() { PrepareDictation(); });
}

void Application::StopDictation() {
    // The physical release must be observable even while a cloud handshake is
    // blocking. ContinueOpenAudioChannel checks this before opening the mic.
    dictation_pressed_.store(false);
    Schedule([this]() {
        if (dictation_phase_ == DictationPhase::Preparing) {
            CancelDictation("本轮已取消\n按住OK，等提示音再说话", true);
        } else if (dictation_phase_ == DictationPhase::Recording) {
            audio_service_.EnableVoiceProcessing(false);
            dictation_phase_ = DictationPhase::Draining;
            dictation_phase_ms_ = esp_timer_get_time() / 1000;
            ShowDictationStatus("录音已结束", "正在处理录音\n请保持电脑输入框焦点");
        }
    });
}

void Application::OpenDictationSetup() {
    dictation_pressed_.store(false);
    Schedule([this]() {
        if (dictation_phase_ != DictationPhase::Idle)
            CancelDictation("进入连接设置");
        DictationBridge::GetInstance().ResetPairing();
    });
}

void Application::PrepareDictation() {
    if (!dictation_pressed_.load()) {
        return;
    }
    if (dictation_phase_ != DictationPhase::Idle) {
        ShowDictationStatus();
        return;
    }
    if (GetDeviceState() != kDeviceStateIdle || protocol_ == nullptr) {
        ShowDictationStatus("语音服务未就绪", "请先完成联网和小智绑定\n完成后再按住OK");
        return;
    }
    char identifier[48];
    snprintf(identifier, sizeof(identifier), "dict_%08lx%08lx_%08lx",
             static_cast<unsigned long>(esp_random()), static_cast<unsigned long>(esp_random()),
             static_cast<unsigned long>(++dictation_sequence_));
    const auto accepted = DictationBridge::GetInstance().Begin(identifier);
    if (accepted != "queued") {
        ShowDictationFailure(accepted);
        return;
    }
    dictation_id_ = identifier;
    dictation_cancel_id_.clear();
    dictation_cloud_session_.clear();
    dictation_phase_ = DictationPhase::Preparing;
    dictation_phase_ms_ = esp_timer_get_time() / 1000;
    esp_timer_start_periodic(dictation_timer_, 20000);
    ShowDictationStatus("连接中，请继续按住", "正在连接电脑和语音服务\n等提示音后再说话");
}

void Application::HandleDictationBridge(const std::string& action, const std::string& id,
                                        const std::string& status) {
    auto* display = Board::GetInstance().GetDisplay();
    if (action == "connection") {
        if (dictation_phase_ == DictationPhase::Idle) {
            if (status == "ready")
                ShowDictationStatus("电脑已配对", "先点选电脑输入框\n按住OK，等提示音再说话");
            else
                ShowDictationFailure(status);
        }
        return;
    }
    if (action == "setup") {
        display->SetChatMessage("system", id.c_str());
        display->ShowNotification("手机打开设置页输入配对码", 10000);
        return;
    }
    if (action == "pair") {
        if (dictation_phase_ != DictationPhase::Idle)
            return;
        if (status == "paired_cloud" || status == "paired")
            ShowDictationStatus("电脑已配对", "先点选电脑输入框\n按住OK，等提示音再说话");
        else if (status == "storage_error")
            ShowDictationStatus("设置保存失败", "请重新打开设备设置\n保存连接方式和配对码");
        else
            ShowDictationFailure(status == "pairing" ? "not_paired" : status);
        return;
    }
    if (id != dictation_id_) {
        if (id == dictation_cancel_id_ && (action == "cancel" || status == "cancelled")) {
            dictation_cancel_id_.clear();
            ShowDictationStatus("本轮已取消", "按住OK，等提示音再说话\n松开后等待电脑确认");
        }
        if (action == "begin" && status == "ready") {
            DictationBridge::GetInstance().Cancel(id);
        }
        return;
    }
    if (action == "begin") {
        if (status != "ready") {
            CancelDictation("本轮连接未完成");
            ShowDictationFailure(status);
            return;
        }
        if (!dictation_pressed_.load()) {
            CancelDictation("已结束，请重新按住说话");
            return;
        }
        play_popup_on_listening_ = true;
        HandleStartListeningEvent();
        return;
    }
    if (action == "text") {
        dictation_phase_ = DictationPhase::Idle;
        dictation_id_.clear();
        dictation_cloud_session_.clear();
        esp_timer_stop(dictation_timer_);
        if (status == "submitted") {
            ShowDictationStatus("电脑确认已输入", "文字已输入目标输入框\n按住OK可继续下一段");
            audio_service_.PlaySound(Lang::Sounds::OGG_SUCCESS);
        } else {
            ShowDictationFailure(status);
        }
    }
}

void Application::PollDictation() {
    const int64_t elapsed = esp_timer_get_time() / 1000 - dictation_phase_ms_;
    if (dictation_phase_ == DictationPhase::Draining) {
        // Allow the input callback to finish its final 60 ms frame, then wait
        // for both the encoder and send queue before declaring end-of-speech.
        if (elapsed >= 150 && audio_service_.IsEncodingIdle()) {
            protocol_->SendStopListening();
            dictation_phase_ = DictationPhase::Waiting;
            dictation_phase_ms_ = esp_timer_get_time() / 1000;
            ShowDictationStatus("正在识别", "录音已结束，等待文字\n请保持电脑输入框焦点");
            SetDeviceState(kDeviceStateIdle);
        } else if (elapsed > 1500) {
            CancelDictation("网络忙，请重新录音");
        }
    } else if (dictation_phase_ == DictationPhase::Recording && elapsed > 60000) {
        CancelDictation("单次最长60秒，请松开重试");
    } else if (dictation_phase_ == DictationPhase::Waiting && elapsed > 20000) {
        CancelDictation("未收到云端识别文字，请重试");
    } else if ((dictation_phase_ == DictationPhase::Preparing ||
                dictation_phase_ == DictationPhase::Delivering) &&
               elapsed > 15000) {
        CancelDictation(dictation_phase_ == DictationPhase::Delivering
                            ? "等待电脑确认超时\n请先检查电脑输入结果"
                            : "连接超时，请松开OK\n检查网络和电脑后重试");
    }
}

void Application::HandleDictationText(const std::string& text, const std::string& session) {
    if (dictation_phase_ != DictationPhase::Waiting || text.empty() ||
        (!session.empty() && session != dictation_cloud_session_)) {
        return;
    }
    if (!DictationBridge::GetInstance().SendText(dictation_id_, text)) {
        CancelDictation("识别结果过长或队列忙，请重试");
        return;
    }
    dictation_phase_ = DictationPhase::Delivering;
    dictation_phase_ms_ = esp_timer_get_time() / 1000;
    ShowDictationStatus("正在发送文字", "等待电脑确认收到\n请保持电脑输入框焦点");
    protocol_->SendAbortSpeaking(kAbortReasonNone);
    protocol_->CloseAudioChannel();
    SetDeviceState(kDeviceStateIdle);
}

void Application::CancelDictation(const char* message, bool show_cancelled) {
    const auto phase = dictation_phase_;
    const auto id = std::move(dictation_id_);
    dictation_id_.clear();
    dictation_cloud_session_.clear();
    dictation_phase_ = DictationPhase::Idle;
    dictation_pressed_.store(false);
    esp_timer_stop(dictation_timer_);
    audio_service_.EnableVoiceProcessing(false);
    if (protocol_ != nullptr && protocol_->IsAudioChannelOpened()) {
        protocol_->SendAbortSpeaking(kAbortReasonNone);
        protocol_->CloseAudioChannel();
    }
    dictation_cancel_id_ = show_cancelled ? id : "";
    if (phase == DictationPhase::Preparing)
        DictationBridge::GetInstance().CancelPendingBegin(id);
    else
        DictationBridge::GetInstance().Cancel(id);
    SetDeviceState(kDeviceStateIdle);
    ShowDictationStatus("本轮已结束", message);
}
