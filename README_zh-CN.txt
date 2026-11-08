Passport 语音输入 / Passport Dictation
Windows 0.1.0-preview.1，设备固件 2.5.0-pd1

本玩法需要配套设备固件和 Windows 接收程序。
本包为开发预览版，完整设备、云端与电脑输入链路仍待真机验证。

中文上手步骤

1. 下载并解压 Windows 配套包，运行 Windows\PassportDictation.exe，允许局域网访问。https://github.com/sasama1990/passport-dictation/releases/tag/v0.1.0-preview.1
2. 首次开机自动配网，换网时待机长按下键。手机或电脑连接屏幕显示的 Xiaozhi-XXXX 热点，浏览器打开 http://192.168.4.1，选择与电脑同一局域网的 2.4 GHz Wi-Fi，输入密码点“连接”，看到“配置成功！”即完成。失败可重连热点重试。
3. 如显示验证码，到 https://xiaozhi.me 登录绑定。电脑点“配对 Passport”，设备提示“电脑已配对”后继续。重配时长按上键再开启配对。
4. 光标留在目标输入框，按住 OK，听到录音提示后说话，松开等待文字填入。识别期间保持焦点。
5. 在电脑程序点“退出”结束，再次运行即可继续使用。

运行 Windows\PassportDictation.exe 即可打开接收程序。
窗口关闭或最小化后进入托盘，双击托盘图标可重新打开。

设备安装

社区页面提供完整固件安装。这个配套包另附分段固件和手动刷机脚本。
如选择手动安装，先在支持数据传输的 USB 线上连接设备并确认串口名。
使用安装了 esptool 5 的 Python，在本目录手动运行下面的命令。

    .\flash-firmware.ps1 -Port COM5 -PythonPath python

将 COM5 换成实际串口名称。脚本会先备份设备完整镜像，成功后写入听写固件。
执行后设备切换为本玩法。备份位于 backups 目录，请自行保管。

English quick start

1. Download and extract the Windows companion ZIP, run Windows\PassportDictation.exe, and allow local-network access. https://github.com/sasama1990/passport-dictation/releases/tag/v0.1.0-preview.1
2. First boot opens Wi-Fi setup automatically. To change networks later, hold DOWN while idle. On a phone or computer, join the Xiaozhi-XXXX hotspot shown on the device. Open http://192.168.4.1, select a 2.4 GHz Wi-Fi network on the same LAN as the PC, enter its password, and select Connect. “Configuration successful!” confirms setup. Rejoin the hotspot and retry if it fails.
3. If an activation code appears, sign in at https://xiaozhi.me and bind the device with that code. Select “配对 Passport” (Pair Passport) in the Windows app and wait for “电脑已配对” (Computer paired). To pair again, hold UP and reopen pairing on the PC.
4. Keep the cursor in the chosen input field. Hold OK, wait for the recording prompt, speak, and release. Wait for the text to appear while keeping that field focused.
5. Select “退出” (Exit) in the Windows app to stop. Launch it again to continue.

Run Windows\PassportDictation.exe after extraction. Closing or minimizing the receiver hides it to the system tray. Double-click its tray icon to reopen it.

For optional manual firmware installation, connect the device with a data-capable USB cable, confirm its COM port, and use Python with esptool 5. Run the command above with the correct port. The script saves a complete backup before installing this play.

Known validation limits

22 local Windows protocol checks passed. Firmware compilation and image verification passed.
The physical-device workflow, cloud-model behavior and real input-field compatibility still await testing.
Keep the original input field focused during recognition. Browser fields can share a native control.

Source https://github.com/sasama1990/passport-dictation
Release https://github.com/sasama1990/passport-dictation/releases/tag/v0.1.0-preview.1
