using System.Security.Cryptography;

namespace PassportDictation;

internal sealed class RelaySettingsForm : Form
{
    private readonly StateStore store;
    private readonly Func<Task> changed;
    private readonly CancellationTokenSource closing = new();
    private readonly TextBox address = new() { Dock = DockStyle.Fill };
    private readonly TextBox code = new() { ReadOnly = true, Dock = DockStyle.Fill, Font = new Font("Consolas", 23), TextAlign = HorizontalAlignment.Center };
    private readonly Label information = new() { AutoSize = true, MaximumSize = new Size(540, 0) };
    private readonly Button generate = new() { Text = "生成跨网配对码", AutoSize = true };
    private readonly Button local = new() { Text = "使用局域网", AutoSize = true };

    public RelaySettingsForm(StateStore store, Func<Task> changed)
    {
        this.store = store;
        this.changed = changed;
        Text = "跨网络连接设置";
        Font = new Font("Microsoft YaHei UI", 10);
        ClientSize = new Size(590, 380);
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        address.Text = store.Cloud.Url;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(24), ColumnCount = 1, RowCount = 6 };
        layout.Controls.Add(new Label { Text = "中继地址（HTTPS）", AutoSize = true });
        layout.Controls.Add(address);
        layout.Controls.Add(code);
        layout.Controls.Add(information);
        information.Text = "生成配对码后，手机连接 Passport 的配网热点，打开 http://192.168.4.1:18766，输入配对码并保存，再按页面入口完成 Wi-Fi 配网。设备和电脑各自联网即可。";
        var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        var copy = new Button { Text = "复制配对码", AutoSize = true };
        var close = new Button { Text = "关闭", AutoSize = true };
        actions.Controls.AddRange([generate, copy, local, close]);
        layout.Controls.Add(actions);
        Controls.Add(layout);
        generate.Click += async (_, _) => await GenerateAsync();
        copy.Click += (_, _) => { if (code.Text.Length > 0) Clipboard.SetText(code.Text); };
        local.Click += async (_, _) =>
        {
            generate.Enabled = false;
            local.Enabled = false;
            try
            {
                store.SetCloud(store.Cloud with { Enabled = false });
                await changed();
                if (!IsDisposed) information.Text = "电脑已切换局域网连接。请在设备设置页选择局域网，随后在主窗口配对。";
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                if (!IsDisposed) information.Text = "保存连接方式失败。" + exception.Message;
            }
            finally
            {
                if (!IsDisposed) { generate.Enabled = true; local.Enabled = true; }
            }
        };
        close.Click += (_, _) => Close();
        FormClosing += (_, _) => closing.Cancel();
    }

    private async Task GenerateAsync()
    {
        generate.Enabled = false;
        local.Enabled = false;
        code.Clear();
        information.Text = "正在连接中继并生成配对码…";
        try
        {
            var url = RelayClient.NormalizeUrl(address.Text);
            var previous = store.Cloud;
            var settings = previous with
            {
                Url = url,
                Enabled = true,
                PairNonce = Guid.NewGuid().ToString("N"),
                Token = url == previous.Url ? previous.Token : Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant(),
            };
            // Save the credential before making the idempotent creation request.
            store.SetCloud(settings);
            var pairing = await RelayClient.OpenPairingAsync(store.Pairing.ReceiverId, settings, closing.Token);
            await changed();
            if (IsDisposed) return;
            code.Text = pairing.Code[..4] + "-" + pairing.Code[4..];
            information.Text = $"配对码约 {Math.Ceiling(pairing.ExpiresIn / 60.0)} 分钟有效。手机连接设备热点，在 http://192.168.4.1:18766 保存配对码，再完成 Wi-Fi 配网。长按设备上键可重新打开配对设置，地址会显示在设备上。";
        }
        catch (OperationCanceledException) when (closing.IsCancellationRequested) { }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or ArgumentException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            if (!IsDisposed) information.Text = "生成配对码失败，请检查中继地址和网络后重试。" + exception.Message;
        }
        finally { if (!IsDisposed) { generate.Enabled = true; local.Enabled = true; } }
    }
}
