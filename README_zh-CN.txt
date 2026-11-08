Passport 语音输入 / Passport Dictation
Windows 0.2.0-preview.2，设备固件 2.5.0-pd2

同时使用本玩法固件与 Windows 接收程序。
新版 Windows 的程序文件、窗口与托盘使用社区封面同款蓝色麦克风图标。
本开发预览版已通过公网协议验证，真实设备录音、开机与软件输入兼容性仍待实测。

跨网络上手

1. 运行 Windows\PassportDictation.exe，点“跨网络设置”与“生成跨网配对码”。保留默认中继地址，记下约 10 分钟有效的配对码。电脑保持联网并运行程序。
2. 首次开机自动配网，换网时待机长按下键。用手机或电脑连接屏幕上的 Xiaozhi-XXXX 热点，打开 http://192.168.4.1:18766，选择“跨网络”，输入配对码并保存，再点“继续设置 Wi-Fi”。
3. 在 http://192.168.4.1 选择 2.4 GHz Wi-Fi，输入密码点“连接”，显示“配置成功！”即可。失败重连热点重试。电脑与设备可使用不同网络。如设备显示小智验证码，到 https://xiaozhi.me 登录绑定。
4. 设备提示“跨网电脑已配对”后，光标留在目标输入框。按住 OK，听到录音提示后说话，松开等待文字填入。识别期间保持输入框焦点与光标位置。
5. 电脑点“退出”结束，再次运行即可继续。重新配对时电脑生成新码，手机与设备连接同一 Wi-Fi，长按设备上键，在手机打开屏幕显示的地址填码。设置窗口约 10 分钟有效。换网按第 2、3 步重试。

局域网连接

将电脑和设备连入同一局域网，允许 Windows 程序的局域网访问。
Windows 的跨网络设置中选择“使用局域网”，主窗口点“配对 Passport”。
设备首次默认使用局域网发现。已启用跨网模式时，长按上键，用同网络手机打开屏幕地址，选择“局域网”并保存。
等待“电脑已配对”，随后按住 OK 录音，松开发送文字。设备仍需联网进行云端识别。

日常操作

关闭或最小化窗口后进入托盘，双击托盘图标可重新打开。
单次录音最长 60 秒。焦点变化、输入拦截或结果不确定时，先检查目标输入框，再复制程序中的最近文字。
网页内的不同输入框可能共用原生控件，识别期间也应保持网页内的光标位置。
中继通过 HTTPS 传输识别文字，并在投递期间处理明文。命令约 10 秒后过期，确认后清空文字，过期记录随服务请求清理。

设备安装

社区安装使用完整固件。本包另附分段固件和手动备份刷写脚本。
手动安装时，用支持数据传输的 USB 线连接设备，确认串口名称。
在安装了 esptool 5 的 Python 环境中运行下列命令，将 COM5 换成实际串口。

    .\flash-firmware.ps1 -Port COM5 -PythonPath python

脚本先备份设备完整镜像，再写入听写固件。备份位于 backups 目录，请自行保管。
分段写入保留原有 NVS 设置。完整合并镜像包含空白 NVS 区域，安装前应另行备份原设置。

English quick start

1. Run Windows\PassportDictation.exe, open “跨网络设置” (Cross-network settings), keep the default relay address, and select “生成跨网配对码” (Generate pairing code). The code lasts about 10 minutes. Keep the receiver running and online.
2. First boot opens Wi-Fi setup. To change networks, hold DOWN while idle. Join the device's displayed Xiaozhi-XXXX hotspot on a phone or computer. Open http://192.168.4.1:18766, select “跨网络” (Cross-network), enter and save the code, then select “继续设置 Wi-Fi” (Continue Wi-Fi setup).
3. At http://192.168.4.1, select a 2.4 GHz Wi-Fi network, enter its password, and select “连接” (Connect). “配置成功！” confirms setup. Rejoin the hotspot and retry if it fails. The PC and device can use different networks. If a XiaoZhi activation code appears, bind the device at https://xiaozhi.me.
4. Wait for “跨网电脑已配对” (Cross-network PC paired). Keep the cursor in the destination field. Hold OK, wait for the recording prompt, speak, and release. Keep the same field and caret position until the text arrives.
5. Select “退出” (Exit) to stop and reopen the app to continue. To pair again, generate a new PC code, connect a phone to the device's Wi-Fi network, hold UP, and open the setup address shown on the device. Save the code within the 10-minute setup window. Repeat steps 2–3 to change networks.

For LAN use, select “使用局域网” (Use LAN) on the PC and “局域网” (LAN) on the device setup page. Connect both to the same LAN, allow local-network access, and open “配对 Passport” on the PC. A fresh device defaults to LAN discovery. Internet access to XiaoZhi is still required for recognition.

Closing or minimizing the app hides it to the system tray. Double-click the tray icon to reopen it. Recordings last up to 60 seconds. If focus changes or input is blocked, check the actual result and copy the latest transcript. Keep the caret in place within browser pages too.

The relay transports recognized text over HTTPS and temporarily handles plaintext. Commands expire after about 10 seconds, text is cleared on acknowledgement, and expired records are cleaned during service activity.

For optional manual installation, connect a data-capable USB cable and use Python with esptool 5. Run the command above with the actual COM port. The script backs up the full device image before writing segments and preserves existing NVS settings. The separate merged firmware image contains an empty NVS region. Back up existing settings before installing it.

Validation

17 headless protocol checks against the public relay and 7 relay test groups passed.
Firmware compilation and image verification passed, including the corrected network-stack startup order.
Physical-device startup, recording, cloud recognition timing and real Windows input compatibility still await testing.

Source https://github.com/sasama1990/passport-dictation
Release https://github.com/sasama1990/passport-dictation/releases/tag/v0.2.0-preview.2
Default relay https://passport-dictation-relay-sasama.gabledhill.chatgpt.site
