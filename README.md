# Passport 语音输入 / Passport Dictation

把 AI Passport 变成 Windows 电脑的随身语音输入按钮。按住 OK 说话，松开后将云端识别的文字送入当前光标所在的输入框。

Windows `0.2.0-preview.3` 与固件 `2.5.0-pd3` 增加持续的操作阶段提示，限制重复开始和取消请求，并区分中继拒绝访问、网络失败和配对失效。Windows 程序文件、窗口和托盘采用与社区封面一致的蓝色麦克风图标。

当前默认中继在上海电信直连测试中返回 HTTP 403，设备也观察到拒绝访问。跨网络模式需要电脑和设备均能访问的 HTTPS 中继，新入口尚未部署。下文跨网步骤以具备可用中继为前提。

这是开发预览版。协议集成、重试防重、焦点变化保护和固件编译已完成验证。设备完整录音、云端识别结束时机及真实软件输入兼容性仍需联调。

## 下载与配套关系

同时安装本玩法的设备固件和 Windows 配套程序。Windows 配套 ZIP 解压后运行 `Windows/PassportDictation.exe`，其中已包含运行所需组件。包内另附分段固件、手动备份刷写脚本和许可文件。社区安装使用单独提供的完整固件镜像。

[下载 Windows 配套包及固件](https://github.com/sasama1990/passport-dictation/releases/tag/v0.2.0-preview.3)

## 跨网络上手

1. 打开 Windows 接收程序，点“跨网络设置”，填写两端均可访问的中继地址，再点“生成跨网配对码”。记下有效期约 10 分钟的配对码。电脑保持联网并运行接收程序。
2. 首次开机自动进入配网，换网时待机长按下键。用手机或电脑连接设备屏幕显示的 `Xiaozhi-XXXX` 热点，打开 `http://192.168.4.1:18766`，选择“跨网络”，输入配对码，点“保存连接设置”，再点“继续设置 Wi-Fi”。
3. 在 `http://192.168.4.1` 选择设备要使用的 2.4 GHz Wi-Fi，输入密码，点“连接”。显示“配置成功！”即完成，失败可重连热点重试。设备与 Windows 可以使用不同网络。设备如显示小智验证码，到小智网站登录绑定。 [小智设备绑定](https://xiaozhi.me)
4. 等待设备提示“电脑已配对”。把光标放入目标输入框，按住 OK，保持按住直到听到提示音并显示“可以说话”。说完松开，依次等待“正在识别”“正在发送文字”和“电脑确认已输入”。全过程保持原输入框与光标位置。
5. Windows 点“退出”结束，重新打开即可继续。重新配对时在电脑生成新码，手机与设备连接同一 Wi-Fi，长按设备上键，在手机浏览器打开屏幕显示的地址填码。该设置窗口约 10 分钟有效。换网可按第 2、3 步操作。

程序与固件保留原默认地址。该入口存在上述访问问题，两端应填写相同且均可访问的 HTTPS 服务地址。

[默认中继](https://passport-dictation-relay-sasama.gabledhill.chatgpt.site)

## 局域网连接

1. 将电脑与设备接入同一局域网，在 Windows 允许程序的局域网访问。
2. Windows 的“跨网络设置”中点“使用局域网”，再在主窗口点“配对 Passport”，配对窗口持续 60 秒。
3. 设备首次默认使用局域网发现。已启用跨网模式时，长按上键打开设置窗口，用同网络的手机打开屏幕地址，选择“局域网”并保存。等待设备提示“电脑已配对”。
4. 把光标留在输入框，按住 OK，等提示音及“可以说话”后开始说话，松开等待“电脑确认已输入”。

设备仍需联网使用小智云端语音识别。Wi-Fi 配置入口及小智绑定方法见上面的上手步骤。

## 输入与数据处理

- 接收程序在录音开始前记录前台窗口和原生输入控件。识别期间保持该输入框焦点。
- 网页中的多个输入框可能共用原生控件，网页内也需要保持光标位置。
- 单次录音最长 60 秒，较长内容可以分段输入。
- 发生焦点变化、输入被拦截或结果不确定时，在接收程序中查看并复制最近文字。先检查输入框中的实际结果。
- 关闭或最小化窗口后程序进入托盘，双击托盘图标重新打开。“退出”结束接收服务。
- 语音由设备现有的小智云服务识别。公网中继接收识别文字与投递控制消息，通过 HTTPS 传输，并在投递期间处理明文。投递命令约 10 秒后过期，确认接收后清空其文字。过期记录随服务请求清理。
- 配对凭据保存在设备和当前 Windows 用户的本地配置中。Windows 配置位于 `%LOCALAPPDATA%/PassportDictation/receiver.json`。重新生成配对码会撤销原有的跨网设备绑定。

## 验证范围

Windows 图标更新已通过构建、成品图标提取和内嵌资源一致性检查。

新版通过 21 项无界面协议检查，使用模拟输入端，覆盖原有 17 项配对、投递、防重、焦点保护、取消、重启和局域网检查，并增加 403 拒绝访问与凭据失效的回归检查。中继的 7 组测试全部通过。这些协议检查与国内直连可用性分别验证。

固件完成编译、中文字体覆盖和修改文件的格式检查。原启动顺序修复已完成实际设备启动观察。本次固件将连接、录音、识别、发送和电脑确认分别显示，按键快速取消时合并尚未完成的连接请求，并启用多行操作提示。

上游主机测试与干净基线具有相同的 17 个失败和 23 个错误，涉及单板分发包缺少的其他开发板与测试资源。详细记录见 `validation/v0.2.0-preview.1/`。

## English quick start

Turn AI Passport into a handheld dictation button for Windows. Hold OK, wait for the recording prompt, speak, and release to send the cloud transcription to the selected text field. Version `0.2.0-preview.3` improves persistent stage prompts, cancellation and connection diagnostics. Its executable, windows and tray share the community cover's blue microphone icon.

The current default relay returns HTTP 403 on the tested Shanghai Telecom direct connection, also observed on the device. Cross-network mode requires an HTTPS relay reachable by both endpoints. A replacement entry has not been deployed. The cross-network steps below assume a working relay is available.

Install both this play's firmware and its companion Windows application.

[Download the Windows companion and firmware](https://github.com/sasama1990/passport-dictation/releases/tag/v0.2.0-preview.3)

1. Extract the companion ZIP and run `Windows/PassportDictation.exe`. Open “跨网络设置” (Cross-network settings), enter a relay address reachable by both endpoints, and select “生成跨网配对码” (Generate pairing code). The code lasts about 10 minutes. Keep the PC online with the receiver running.
2. First boot opens Wi-Fi setup. To change networks, hold DOWN while idle. On a phone or computer, join the `Xiaozhi-XXXX` hotspot shown on the device. Open `http://192.168.4.1:18766`, select “跨网络” (Cross-network), enter the code, save, and select “继续设置 Wi-Fi” (Continue Wi-Fi setup).
3. At `http://192.168.4.1`, select a 2.4 GHz Wi-Fi network, enter its password, and select “连接” (Connect). “配置成功！” confirms setup. Rejoin the hotspot and retry if it fails. The PC and device can use different networks. If the device shows a XiaoZhi activation code, sign in and bind it on the XiaoZhi website. [XiaoZhi device activation](https://xiaozhi.me)
4. Wait for “电脑已配对” (PC paired). Select the destination field and hold OK until the tone and “可以说话” (Ready to speak). Speak and release. Wait through recognition and sending until “电脑确认已输入” (PC confirmed input). Keep the same field and caret position throughout.
5. Select “退出” (Exit) to stop the receiver and launch it again to continue. To pair again, generate a new PC code, connect a phone to the device's Wi-Fi network, hold UP on the device, and open the displayed setup address on the phone. Enter and save the code within the 10-minute setup window. Use steps 2–3 to change Wi-Fi networks.

For LAN use, select “使用局域网” (Use LAN) in Windows settings and “局域网” (LAN) on the device setup page, connect both to the same LAN, allow local-network access, and open the PC's 60-second “配对 Passport” pairing window. A fresh device defaults to LAN discovery. Internet access to XiaoZhi is still required for speech recognition.

Closing or minimizing the receiver hides it to the system tray. Double-click its icon to reopen it. Recordings last up to 60 seconds. If focus changes or input is blocked, inspect the actual input result and copy the latest transcript from the receiver. Browser fields may share a native control, so keep the caret in place within the page.

The relay carries recognized text and delivery commands over HTTPS and temporarily handles plaintext. Commands expire after about 10 seconds and their text is cleared on acknowledgement. Expired metadata is cleaned during service activity. Audio recognition uses the device's XiaoZhi cloud connection.

The icon previously passed executable extraction and embedded-resource verification. This revision passed 21 headless protocol checks with simulated input, including blocked-access diagnostics, plus 7 relay test groups. Firmware compilation and font coverage checks passed. Physical-device startup was observed successfully. Complete recording, cloud recognition timing and real Windows input compatibility still require end-to-end testing. Protocol test success does not establish mainland direct relay access.

## 源码与构建

`windows/` 是 Windows 接收程序，`firmware/` 是独立的听写固件，`relay/` 是中继服务。构建和验证步骤见 [BUILDING.md](BUILDING.md)，中继部署与接口见 [relay/README.md](relay/README.md)。

本项目以 FoloToy 小智工程的干净快照为基础建立，EVE 仅作参考。原始来源和许可见 `firmware/NOTICE` 与 `firmware/LICENSE`，依赖许可随下载包分发。

## 封面

封面为 AI 生成功能示意图。

![语音输入概念示意图](docs/cover.png)
