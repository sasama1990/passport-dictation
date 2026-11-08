#include "application.h"
#include "assets/lang_config.h"
#include "board.h"
#include "dictation_bridge.h"
#include "display.h"

#include <esp_random.h>
#include <esp_timer.h>

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
            CancelDictation("已结束，请按住OK说话");
        } else if (dictation_phase_ == DictationPhase::Recording) {
            audio_service_.EnableVoiceProcessing(false);
            dictation_phase_ = DictationPhase::Draining;
            dictation_phase_ms_ = esp_timer_get_time() / 1000;
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
    auto* display = Board::GetInstance().GetDisplay();
    if (!dictation_pressed_.load()) {
        return;
    }
    if (dictation_phase_ != DictationPhase::Idle) {
        display->ShowNotification("请等待本轮完成");
        return;
    }
    if (GetDeviceState() != kDeviceStateIdle || protocol_ == nullptr) {
        display->ShowNotification("请先完成联网和小智绑定");
        return;
    }
    char identifier[48];
    snprintf(identifier, sizeof(identifier), "dict_%08lx%08lx_%08lx",
             static_cast<unsigned long>(esp_random()), static_cast<unsigned long>(esp_random()),
             static_cast<unsigned long>(++dictation_sequence_));
    dictation_id_ = identifier;
    dictation_cloud_session_.clear();
    dictation_phase_ = DictationPhase::Preparing;
    dictation_phase_ms_ = esp_timer_get_time() / 1000;
    esp_timer_start_periodic(dictation_timer_, 20000);
    display->ShowNotification("连接电脑和云端，请稍候");
    if (!DictationBridge::GetInstance().Begin(dictation_id_)) {
        CancelDictation("发送队列忙，请重试");
    }
}

void Application::HandleDictationBridge(const std::string& action, const std::string& id,
                                        const std::string& status) {
    auto* display = Board::GetInstance().GetDisplay();
    if (action == "setup") {
        display->SetChatMessage("system", id.c_str());
        display->ShowNotification("手机打开设置页输入配对码", 10000);
        return;
    }
    if (action == "pair") {
        if (status == "paired_cloud")
            display->ShowNotification("跨网电脑已配对");
        else if (status == "pairing_cloud")
            display->ShowNotification("联网后自动跨网配对");
        else if (status == "pairing_expired")
            display->ShowNotification("请在电脑重新获取配对码", 10000);
        else if (status == "storage_error")
            display->ShowNotification("保存失败，请重新设置");
        else
            display->ShowNotification(status == "paired" ? "电脑已配对" : "请在电脑上开启配对");
        return;
    }
    if (id != dictation_id_) {
        if (action == "begin" && status == "ready") {
            DictationBridge::GetInstance().Cancel(id);
        }
        return;
    }
    if (action == "begin") {
        if (status != "ready") {
            if (status == "receiver_offline")
                CancelDictation("电脑离线，请先启动接收程序");
            else
                CancelDictation(status == "not_paired" ? "请先与Windows程序配对"
                                                       : "电脑未就绪，请选中输入框重试");
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
            display->ShowNotification("文字已提交到电脑");
            audio_service_.PlaySound(Lang::Sounds::OGG_SUCCESS);
        } else if (status == "focus_changed" || status == "keyboard_busy") {
            display->ShowNotification("请在电脑程序中复制文字", 8000);
        } else {
            display->ShowNotification("请检查电脑输入结果", 8000);
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
        CancelDictation("连接超时，请检查电脑");
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
    Board::GetInstance().GetDisplay()->ShowNotification("识别完成，发送到电脑");
    protocol_->SendAbortSpeaking(kAbortReasonNone);
    protocol_->CloseAudioChannel();
    SetDeviceState(kDeviceStateIdle);
}

void Application::CancelDictation(const char* message) {
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
    DictationBridge::GetInstance().Cancel(id);
    SetDeviceState(kDeviceStateIdle);
    Board::GetInstance().GetDisplay()->ShowNotification(message, 6000);
}
