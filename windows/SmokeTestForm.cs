using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PassportDictation;

internal sealed class SmokeTestForm : Form
{
    private readonly string directory;
    private readonly string reportPath;
    private readonly bool protocolOnly;
    private readonly TextBox editor = new() { Multiline = true, Dock = DockStyle.Top, Height = 130 };
    private readonly TextBox secondEditor = new() { Multiline = true, Dock = DockStyle.Top, Height = 90 };
    private readonly Label progress = new() { Text = "正在自检…", AutoSize = true, Dock = DockStyle.Bottom };
    private readonly List<string> passed = [];

    public SmokeTestForm(string directory, string reportPath, bool protocolOnly)
    {
        this.directory = directory;
        this.reportPath = reportPath;
        this.protocolOnly = protocolOnly;
        Text = "Passport 语音输入 · 自动自检";
        Size = new Size(620, 360);
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;
        Controls.AddRange([secondEditor, editor, progress]);
        Shown += async (_, _) => await RunAsync();
    }

    private async Task RunAsync()
    {
        LocalServer? server = null;
        try
        {
            var testDirectory = Path.Combine(directory, Guid.NewGuid().ToString("N"));
            var store = new StateStore(testDirectory);
            var input = new TestInput();
            var receiver = new DictationReceiver(store, input);
            server = new LocalServer(store, receiver);
            await server.StartAsync(18766, 47626);
            server.OpenPairing();
            using var udp = new UdpClient(AddressFamily.InterNetwork);
            var discovery = new
            {
                service = LocalServer.ServiceName,
                version = 1,
                device_id = "02:00:00:00:00:01",
                receiver_id = "",
                nonce = "0123456789abcdef"
            };
            await udp.SendAsync(JsonSerializer.SerializeToUtf8Bytes(discovery), new IPEndPoint(IPAddress.Loopback, 47626));
            using var discoveryTimeout = new CancellationTokenSource(5000);
            var offer = await udp.ReceiveAsync(discoveryTimeout.Token);
            using var offerJson = JsonDocument.Parse(offer.Buffer);
            Check(offerJson.RootElement.GetProperty("token").GetString()?.Length == 64, "UDP discovery and pairing");
            await udp.SendAsync(JsonSerializer.SerializeToUtf8Bytes(discovery), new IPEndPoint(IPAddress.Loopback, 47626));
            var repeatedOffer = await udp.ReceiveAsync(discoveryTimeout.Token);
            using var repeatedOfferJson = JsonDocument.Parse(repeatedOffer.Buffer);
            Check(repeatedOfferJson.RootElement.GetProperty("token").GetString() == store.Pairing.Token,
                "Lost pairing reply can be retried");

            foreach (var field in new[] { "device_id", "receiver_id", "nonce" })
            {
                var invalid = new Dictionary<string, object?>
                {
                    ["service"] = LocalServer.ServiceName,
                    ["version"] = 1,
                    ["device_id"] = discovery.device_id,
                    ["receiver_id"] = "",
                    ["nonce"] = discovery.nonce
                };
                invalid[field] = null;
                await udp.SendAsync(JsonSerializer.SerializeToUtf8Bytes(invalid), new IPEndPoint(IPAddress.Loopback, 47626));
            }
            var refresh = new
            {
                discovery.service,
                discovery.version,
                discovery.device_id,
                receiver_id = store.Pairing.ReceiverId,
                discovery.nonce
            };
            await udp.SendAsync(JsonSerializer.SerializeToUtf8Bytes(refresh), new IPEndPoint(IPAddress.Loopback, 47626));
            var refreshOffer = await udp.ReceiveAsync(discoveryTimeout.Token);
            using var refreshJson = JsonDocument.Parse(refreshOffer.Buffer);
            Check(refreshJson.RootElement.GetProperty("token").ValueKind == JsonValueKind.Null,
                "Discovery survives null fields and hides an existing token");
            var proofText = refresh.nonce + "\n" + refresh.receiver_id + "\n"
                + refreshJson.RootElement.GetProperty("host").GetString() + "\n18766";
            var proof = Convert.ToHexString(HMACSHA256.HashData(
                Encoding.UTF8.GetBytes(store.Pairing.Token), Encoding.UTF8.GetBytes(proofText))).ToLowerInvariant();
            Check(refreshJson.RootElement.GetProperty("proof").GetString() == proof,
                "Paired discovery HMAC is valid");

            using var client = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:18766") };
            var deviceId = store.Pairing.DeviceId;
            var unauthenticated = await client.PostAsJsonAsync("/v1/begin", new { device_id = deviceId, id = "test_auth_01" });
            Check(unauthenticated.StatusCode == HttpStatusCode.Unauthorized, "Unauthorized request rejected");
            client.DefaultRequestHeaders.Authorization = new("Bearer", store.Pairing.Token);

            using var malformed = await client.PostAsync("/v1/begin", new StringContent("{", Encoding.UTF8, "application/json"));
            Check(malformed.StatusCode == HttpStatusCode.BadRequest, "Malformed JSON rejected");
            using var nullId = await client.PostAsJsonAsync("/v1/begin", new { device_id = deviceId, id = (string?)null });
            Check(nullId.StatusCode == HttpStatusCode.BadRequest, "Null utterance ID rejected");
            using var nullText = await client.PostAsJsonAsync("/v1/text", new { device_id = deviceId, id = "test_invalid_01", text = (string?)null });
            Check(nullText.StatusCode == HttpStatusCode.BadRequest, "Null text rejected");
            using var oversized = await client.PostAsync("/v1/text", new StringContent(new string('a', 32769), Encoding.UTF8, "application/json"));
            Check(oversized.StatusCode == HttpStatusCode.RequestEntityTooLarge, "Oversized HTTP body rejected");

            async Task<(HttpStatusCode Code, string Status)> Post(string path, string id, string text = "")
            {
                using var response = await client.PostAsJsonAsync(path, new { device_id = deviceId, id, text });
                using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                return (response.StatusCode, body.RootElement.GetProperty("status").GetString()!);
            }

            Check((await Post("/v1/begin", "test_utterance_01")).Status == "ready", "Begin accepted");
            Check((await Post("/v1/begin", "test_utterance_01")).Status == "ready", "Repeated begin is idempotent");
            Check((await Post("/v1/begin", "test_utterance_02")).Status == "busy", "Concurrent utterance rejected");
            var sample = "你好，Passport。Hello 123 🐾";
            Check((await Post("/v1/text", "test_utterance_01", new string('a', 4097))).Code == HttpStatusCode.BadRequest,
                "Oversized text rejected without consuming the utterance");
            Check((await Post("/v1/text", "test_utterance_01", sample)).Status == "submitted" && input.Count == 1, "UTF-8 text delivery");
            var duplicateResults = await Task.WhenAll(Enumerable.Range(0, 5)
                .Select(_ => Post("/v1/text", "test_utterance_01", sample)));
            Check(duplicateResults.All(result => result.Status == "submitted") && input.Count == 1, "Concurrent retries inject once");
            Check((await Post("/v1/text", "test_utterance_01", "不同内容")).Status == "conflicting_text", "Conflicting replay rejected");
            Check((await Post("/v1/text", "unknown_utterance", sample)).Status == "unknown_utterance", "Unsolicited text rejected");
            await Post("/v1/begin", "test_utterance_02");
            await Post("/v1/text", "test_utterance_02", sample);
            Check(input.Count == 2, "Identical speech in a new utterance is accepted");
            await Post("/v1/begin", "test_cancel_01");
            await Post("/v1/cancel", "test_cancel_01");
            Check((await Post("/v1/text", "test_cancel_01", sample)).Status == "unknown_utterance", "Cancelled recording cannot type");
            receiver.Enabled = false;
            Check((await Post("/v1/begin", "test_paused_01")).Status == "paused", "Pause blocks recording");
            receiver.Enabled = true;

            var restarted = new StateStore(testDirectory);
            var replayInput = new TestInput();
            var restartedReceiver = new DictationReceiver(restarted, replayInput);
            Check((await restartedReceiver.DeliverAsync("test_utterance_01", sample)).Status == "submitted" && replayInput.Count == 0,
                "Receipt survives receiver restart");

            store.Claim("test_interrupted_01", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sample))));
            var interruptedReceiver = new DictationReceiver(new StateStore(testDirectory), replayInput);
            Check((await interruptedReceiver.DeliverAsync("test_interrupted_01", sample)).Status == "uncertain" && replayInput.Count == 0,
                "Interrupted input is not replayed after restart");

            if (!protocolOnly)
                await VerifyNativeInputAsync(input, Post, sample);

            Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
            File.WriteAllText(reportPath, JsonSerializer.Serialize(new { success = true, passed, native_input_tested = !protocolOnly, native_text = editor.Text }, new JsonSerializerOptions { WriteIndented = true }));
            Environment.ExitCode = 0;
        }
        catch (Exception exception)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
            File.WriteAllText(reportPath, JsonSerializer.Serialize(new { success = false, passed, error = exception.ToString() }, new JsonSerializerOptions { WriteIndented = true }));
            Environment.ExitCode = 1;
        }
        finally
        {
            if (server != null) await server.DisposeAsync();
            Close();
        }
    }

    private async Task VerifyNativeInputAsync(TestInput input,
        Func<string, string, string, Task<(HttpStatusCode Code, string Status)>> Post, string sample)
    {
        progress.Text = "正在验证真实 Windows 输入，请保持此窗口在前台…";
        var native = new WindowsTextInput(this) { TestWindow = Handle, RestrictToTestWindow = true };
        input.Native = native;
        Activate();
        editor.Focus();
        var focusDeadline = DateTime.UtcNow.AddSeconds(30);
        while (Native.GetForegroundWindow() != Handle && DateTime.UtcNow < focusDeadline)
            await Task.Delay(200);
        Check(Native.GetForegroundWindow() == Handle, "Self-test has its own foreground window");
        editor.Focus();
        await Task.Delay(100);
        var nativeTarget = await native.CaptureAsync();
        Check(nativeTarget?.Window == Handle && nativeTarget?.Focus == editor.Handle,
            "Self-test target is exactly its own editor");
        Check((await Post("/v1/begin", "test_native_01", "")).Status == "ready", "Native focus capture");
        Check((await Post("/v1/text", "test_native_01", sample)).Status == "submitted", "SendInput accepted Unicode events");
        for (var wait = 0; wait < 25 && editor.Text != sample; wait++)
            await Task.Delay(200);
        Check(editor.Text == sample,
            $"Native editor contains Chinese, English and emoji (actual={editor.Text}, second={secondEditor.Text}, foreground={Native.GetForegroundWindow()}, form={Handle})");

        editor.Focus();
        var beginFocus = await Post("/v1/begin", "test_focus_01", "");
        var beforeFocus = await native.CaptureAsync();
        ActiveControl = secondEditor;
        secondEditor.Focus();
        await Task.Delay(100);
        var afterFocus = await native.CaptureAsync();
        var focusDelivery = await Post("/v1/text", "test_focus_01", "不应写入");
        Check(focusDelivery.Status == "focus_changed" && secondEditor.Text.Length == 0,
            $"Changed input control is protected (begin={beginFocus.Status}, result={focusDelivery.Status}, before={beforeFocus}, after={afterFocus}, second={secondEditor.Text})");

    }

    private void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException("Failed: " + name);
        passed.Add(name);
        progress.Text = name;
    }

    private sealed class TestInput : ITextInput
    {
        public int Count { get; private set; }
        public ITextInput? Native { get; set; }
        public Task<InputTarget?> CaptureAsync() => Native?.CaptureAsync()
            ?? Task.FromResult<InputTarget?>(new InputTarget(1, 1));
        public Task<string> SubmitAsync(InputTarget target, string text)
        {
            Count++;
            return Native?.SubmitAsync(target, text) ?? Task.FromResult("submitted");
        }
    }
}
