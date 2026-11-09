using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace PassportDictation;

internal sealed class MainForm : Form
{
    private readonly Label status = new() { AutoSize = true, Text = "正在启动接收服务…" };
    private readonly Label device = new() { AutoSize = true, Text = "局域网设备尚未配对" };
    private readonly Label hint = new() { AutoSize = true, MaximumSize = new Size(540, 0) };
    private readonly Button pairing = new() { Text = "配对 Passport", AutoSize = true };
    private readonly Button pause = new() { Text = "暂停接收", AutoSize = true };
    private readonly Button cloudSettings = new() { Text = "跨网络设置", AutoSize = true, Enabled = false };
    private readonly Label cloudStatus = new() { AutoSize = true, MaximumSize = new Size(540, 0), Text = "跨网络连接尚未配置" };
    private readonly TextBox preview = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill };
    private readonly NotifyIcon tray;
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 1000 };
    private readonly string dataDirectory;
    private DictationReceiver? receiver;
    private LocalServer? server;
    private StateStore? store;
    private RelayClient? relay;
    private readonly SemaphoreSlim relayGate = new(1);
    private DateTimeOffset lastSeen;
    private string pairedDevice = "";
    private bool exiting;

    public MainForm(string directory)
    {
        dataDirectory = directory;
        Text = "Passport 语音输入";
        Font = new Font("Microsoft YaHei UI", 10);
        ClientSize = new Size(660, 520);
        MinimumSize = new Size(560, 440);
        StartPosition = FormStartPosition.CenterScreen;
        Icon = new Icon(typeof(MainForm), "Assets.passport-dictation.ico");
        BackColor = Color.FromArgb(247, 249, 252);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, Padding = new Padding(24), ColumnCount = 1, RowCount = 9
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(new Label { Text = "Passport 语音输入", Font = new Font(Font, FontStyle.Bold), AutoSize = true, Margin = new Padding(0, 0, 0, 16) });
        layout.Controls.Add(status);
        layout.Controls.Add(device);
        layout.Controls.Add(cloudStatus);
        var addresses = NetworkInterface.GetAllNetworkInterfaces()
            .Where(network => network.OperationalStatus == OperationalStatus.Up && network.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(network => network.GetIPProperties().UnicastAddresses)
            .Where(address => address.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address.Address))
            .Select(address => address.Address.ToString());
        layout.Controls.Add(new Label { AutoSize = true, Text = "电脑地址  " + string.Join("  /  ", addresses) });
        var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 12, 0, 8) };
        var test = new Button { Text = "打开测试输入框", AutoSize = true };
        actions.Controls.AddRange([pairing, cloudSettings, pause, test]);
        layout.Controls.Add(actions);
        hint.Text = "先完成配对，把光标放入目标输入框。按住设备 OK，等提示音及“可以说话”后开始说话，松开后等待识别和电脑确认。全过程保持原输入框与光标位置。";
        hint.Margin = new Padding(0, 0, 0, 12);
        layout.Controls.Add(hint);
        layout.Controls.Add(preview);
        var footer = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 8, 0, 0) };
        var copy = new Button { Text = "复制最近文字", AutoSize = true };
        var exit = new Button { Text = "退出", AutoSize = true };
        footer.Controls.AddRange([copy, exit]);
        layout.Controls.Add(footer);
        Controls.Add(layout);

        var menu = new ContextMenuStrip();
        menu.Items.Add("打开", null, (_, _) => Restore());
        menu.Items.Add("退出", null, async (_, _) => await ExitAsync());
        tray = new NotifyIcon { Icon = Icon, Text = Text, ContextMenuStrip = menu, Visible = true };
        tray.DoubleClick += (_, _) => Restore();
        copy.Click += (_, _) => { if (preview.Text.Length > 0) Clipboard.SetText(preview.Text); };
        exit.Click += async (_, _) => await ExitAsync();
        pairing.Click += async (_, _) =>
        {
            if (receiver != null) await receiver.ResetActiveAsync();
            server?.OpenPairing();
            hint.Text = "配对窗口已开启 60 秒。将 Passport 连入同一局域网，首次会自动发现电脑。已配对过的设备可长按上键重新配对。";
        };
        cloudSettings.Click += async (_, _) =>
        {
            if (store == null) return;
            using var settings = new RelaySettingsForm(store, RestartRelayAsync) { Icon = Icon };
            settings.ShowDialog(this);
            await RestartRelayAsync();
        };
        pause.Click += async (_, _) =>
        {
            if (receiver == null) return;
            receiver.Enabled = !receiver.Enabled;
            if (!receiver.Enabled) await receiver.ResetActiveAsync();
            pause.Text = receiver.Enabled ? "暂停接收" : "恢复接收";
            status.Text = receiver.Enabled ? "接收服务已启用" : "接收已暂停";
        };
        test.Click += (_, _) =>
        {
            var editor = new Form
            {
                Text = "Passport 测试输入框",
                Size = new Size(600, 350),
                StartPosition = FormStartPosition.CenterParent,
                Icon = Icon,
            };
            var text = new TextBox { Multiline = true, Dock = DockStyle.Fill, Font = Font, AcceptsReturn = true };
            editor.Controls.Add(text);
            if (receiverInput != null)
            {
                editor.Shown += (_, _) => { receiverInput.TestWindow = editor.Handle; text.Focus(); };
                editor.FormClosed += (_, _) => receiverInput.TestWindow = 0;
            }
            editor.Show(this);
        };
        Shown += async (_, _) => await StartAsync();
        FormClosing += (_, eventArgs) =>
        {
            if (exiting) return;
            eventArgs.Cancel = true;
            Hide();
        };
        Resize += (_, _) => { if (WindowState == FormWindowState.Minimized) Hide(); };
        timer.Tick += (_, _) =>
        {
            pairing.Text = server?.IsPairing == true ? "等待设备配对…" : "配对 Passport";
            if (pairedDevice.Length > 0)
                device.Text = "局域网设备  " + pairedDevice + (DateTimeOffset.UtcNow - lastSeen < TimeSpan.FromSeconds(25) ? "  · 在线" : "  · 等待连接");
        };
        timer.Start();
    }

    private WindowsTextInput? receiverInput;

    private async Task StartAsync()
    {
        try
        {
            store = new StateStore(dataDirectory);
            pairedDevice = store.Pairing.DeviceId;
            receiverInput = new WindowsTextInput(this);
            receiver = new DictationReceiver(store, receiverInput)
            {
                Notice = notice => BeginInvoke(() => ShowNotice(notice))
            };
            server = new LocalServer(store, receiver)
            {
                DeviceSeen = id => BeginInvoke(() => { pairedDevice = id; lastSeen = DateTimeOffset.UtcNow; }),
                Error = message => BeginInvoke(() => status.Text = message)
            };
            try
            {
                await server.StartAsync();
                status.Text = "局域网接收服务已启用";
            }
            catch (Exception exception)
            {
                await server.DisposeAsync();
                server = null;
                pairing.Enabled = false;
                status.Text = "局域网服务启动失败  " + exception.Message;
            }
            cloudSettings.Enabled = true;
            await RestartRelayAsync();
        }
        catch (Exception exception)
        {
            status.Text = "启动失败  " + exception.Message;
            hint.Text = "检查端口 18765 / UDP 47625 是否被占用，或本地配置目录是否可写。";
            pairing.Enabled = false;
            pause.Enabled = false;
        }
    }

    private async Task RestartRelayAsync()
    {
        await relayGate.WaitAsync();
        try
        {
            if (relay != null)
            {
                relay.Notice = null;
                await relay.DisposeAsync();
                relay = null;
            }
            if (exiting || store == null || receiver == null) return;
            await receiver.ResetActiveAsync();
            if (!store.Cloud.Enabled)
            {
                cloudStatus.Text = "当前使用局域网连接";
                return;
            }
            try
            {
                relay = new RelayClient(store.Pairing.ReceiverId, store.Cloud, receiver)
                {
                    Notice = message => BeginInvoke(() => cloudStatus.Text = message),
                };
                cloudStatus.Text = "正在连接跨网中继…";
                relay.Start();
            }
            catch (ArgumentException)
            {
                cloudStatus.Text = "请在跨网络设置中配置 HTTP 或 HTTPS 中继地址。";
            }
        }
        finally { relayGate.Release(); }
    }

    private void ShowNotice(ReceiverNotice notice)
    {
        status.Text = notice.Status switch
        {
            "listening" => "输入位置已准备，请按设备提示开始说话。正在等待识别文字…",
            "submitted" => "文字已提交到目标输入框",
            "focus_changed" => "输入位置已变化，请复制下方文字后粘贴",
            "keyboard_busy" => "键盘快捷键正被按住，请复制下方文字后粘贴",
            "input_blocked" => "目标程序拦截了系统输入，请复制下方文字后粘贴",
            "uncertain" => "输入结果待确认，请先检查目标输入框",
            "expired" => "本轮已超时，请复制下方文字后粘贴",
            "unknown_utterance" => "本轮连接已失效，请复制下方文字后粘贴",
            "cancelled" => "本轮已取消，可按住设备 OK 等提示音后重新录音",
            _ => notice.Status
        };
        if (notice.Text.Length > 0)
            preview.Text = notice.Text;
        if (notice.Status is "focus_changed" or "input_blocked" or "uncertain")
            tray.ShowBalloonTip(5000, Text, status.Text, ToolTipIcon.Info);
    }

    private void Restore()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    private async Task ExitAsync()
    {
        if (exiting) return;
        exiting = true;
        timer.Stop();
        tray.Visible = false;
        await RestartRelayAsync();
        if (server != null)
            await server.DisposeAsync();
        tray.Dispose();
        Close();
    }
}
