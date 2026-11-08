# Passport 语音输入 / Passport Dictation

把 AI Passport 变成 Windows 电脑的随身语音输入按钮。按住 OK 说话，松开后将云端识别文字送入选中的输入框。

这是开发预览版。Windows 本机协议测试已完成，设备按键、录音、当前云模型及真实输入框兼容性仍需真机验证。

## 下载与配套关系

本玩法需要同时使用设备听写固件与 Windows 接收程序，并将设备和电脑连入同一局域网。设备通过小智云服务识别语音，因此需要联网。

Windows 配套包解压后，运行 `Windows/PassportDictation.exe`。该程序已经包含运行所需组件。
包内也提供分段固件、手动备份刷写脚本及许可文件。社区安装使用单独提供的完整固件镜像。

[下载 Windows 配套包及固件](https://github.com/sasama1990/passport-dictation/releases/tag/v0.1.0-preview.1)

## 上手步骤

1. 下载并解压 Windows 配套包，运行 Windows\PassportDictation.exe，允许局域网访问。https://github.com/sasama1990/passport-dictation/releases/tag/v0.1.0-preview.1
2. 首次开机自动配网，换网时待机长按下键。手机或电脑连接屏幕显示的 Xiaozhi-XXXX 热点，浏览器打开 http://192.168.4.1，选择与电脑同一局域网的 2.4 GHz Wi-Fi，输入密码点“连接”，看到“配置成功！”即完成。失败可重连热点重试。
3. 如显示验证码，到 https://xiaozhi.me 登录绑定。电脑点“配对 Passport”，设备提示“电脑已配对”后继续。重配时长按上键再开启配对。
4. 光标留在目标输入框，按住 OK，听到录音提示后说话，松开等待文字填入。识别期间保持焦点。
5. 在电脑程序点“退出”结束，再次运行即可继续使用。

## 输入行为

- 录音开始时记录选中的输入框，识别期间请保持原输入位置。
- 焦点变化或目标程序拦截输入时，可在 Windows 程序中复制最近的识别文字。
- 浏览器中的多个网页输入框可能共用原生输入控件，网页内也需要保持光标位置。
- 单次录音最长 60 秒，较长内容可以分段输入。
- 窗口关闭或最小化后进入托盘，双击托盘图标可重新打开。使用“退出”结束接收服务。

## 验证范围

交付的 Windows 程序通过 22 项本机协议测试，覆盖配对、鉴权、文字传输、异常输入、并发重试防重和重启后的投递处理。
固件构建、镜像校验、中文提示字体覆盖及修改文件的格式检查已完成。
上游主机测试与干净基线存在相同的 17 个失败和 23 个错误，涉及此单板分发包缺少的其他开发板与资源。

实际按键录音、云端结束时机、局域网连通性、各软件输入兼容性及掉线恢复仍待设备联调。
`validation/` 保存本版本的验证记录。

## English quick start

Turn your AI Passport into a handheld dictation button for Windows. Hold OK to speak, then release to send the cloud transcription into the selected text field.

This development preview requires both the device firmware and the companion Windows receiver on the same LAN. The device also needs an internet connection to its XiaoZhi cloud service.

1. Download and extract the Windows companion ZIP, run Windows\PassportDictation.exe, and allow local-network access. https://github.com/sasama1990/passport-dictation/releases/tag/v0.1.0-preview.1
2. First boot opens Wi-Fi setup automatically. To change networks later, hold DOWN while idle. On a phone or computer, join the Xiaozhi-XXXX hotspot shown on the device. Open http://192.168.4.1, select a 2.4 GHz Wi-Fi network on the same LAN as the PC, enter its password, and select Connect. “Configuration successful!” confirms setup. Rejoin the hotspot and retry if it fails.
3. If an activation code appears, sign in at https://xiaozhi.me and bind the device with that code. Select “配对 Passport” (Pair Passport) in the Windows app and wait for “电脑已配对” (Computer paired). To pair again, hold UP and reopen pairing on the PC.
4. Keep the cursor in the chosen input field. Hold OK, wait for the recording prompt, speak, and release. Wait for the text to appear while keeping that field focused.
5. Select “退出” (Exit) in the Windows app to stop. Launch it again to continue.

Keep the same input field focused while recognition completes. Browser fields can share a native control, so keep the caret in place within the page as well. If input is blocked or focus changes, copy the latest transcript from the receiver.

The Windows receiver passed 22 local protocol checks. Firmware compilation and image verification passed. Physical-device recording, the selected cloud model, and real application input compatibility still need end-to-end testing.

## 源码与构建

`windows/` 是接收程序源码，`firmware/` 是独立的设备听写工程。构建步骤见 [BUILDING.md](BUILDING.md)。
本项目以 FoloToy 小智工程的干净快照为基础建立，EVE 仅作参考。
原始来源和许可见 `firmware/NOTICE` 与 `firmware/LICENSE`，依赖许可随下载包分发。

## 封面

封面为 AI 生成功能示意图。

![语音输入概念示意图](docs/cover.png)
