using System.Diagnostics;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;

namespace PassportDictation;

internal static class RelaySmokeTests
{
    public static async Task RunAsync(string url, string directory, Action<bool, string> check)
    {
        var store = new StateStore(Path.Combine(directory, "cloud"));
        var originalPairing = store.Pairing;
        var settings = store.Cloud with { Enabled = true, Url = url, PairNonce = Guid.NewGuid().ToString("N") };
        store.SetCloud(settings);
        var reloaded = new StateStore(Path.Combine(directory, "cloud"));
        check(reloaded.Cloud == settings && reloaded.Pairing == originalPairing,
            "Cloud settings persist alongside existing LAN credentials");
        var saveFailed = false;
        using (var locked = new FileStream(Path.Combine(directory, "cloud", "receiver.json.tmp"),
            FileMode.OpenOrCreate, FileAccess.Write, FileShare.None))
        {
            try { store.SetCloud(settings with { Enabled = false }); }
            catch (IOException) { saveFailed = true; }
        }
        check(saveFailed && store.Cloud == settings
            && new StateStore(Path.Combine(directory, "cloud")).Cloud == settings,
            "Failed cloud settings writes preserve the previous configuration");
        var pairing = await RelayClient.OpenPairingAsync(originalPairing.ReceiverId, settings, CancellationToken.None);
        var repeat = await RelayClient.OpenPairingAsync(originalPairing.ReceiverId, settings, CancellationToken.None);
        check(pairing.Code == repeat.Code, "Cloud pairing request is idempotent");

        const string deviceId = "02:00:00:00:00:02";
        var deviceToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        using var device = new HttpClient { BaseAddress = new Uri(RelayClient.NormalizeUrl(url) + "/"), Timeout = TimeSpan.FromSeconds(15) };
        using var paired = await device.PostAsJsonAsync("v1/pair", new
        {
            code = pairing.Code, device_id = deviceId, device_token = deviceToken,
        });
        check(paired.IsSuccessStatusCode, "Simulated device pairs through the relay");
        device.DefaultRequestHeaders.Authorization = new("Bearer", deviceToken);

        async Task<string> Send(string action, string id, string text = "")
        {
            using var response = await device.PostAsJsonAsync($"v1/device/{originalPairing.ReceiverId}/{action}",
                new { device_id = deviceId, id, text });
            using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return result.RootElement.GetProperty("status").GetString()!;
        }

        var input = new SimulatedInput();
        var receiver = new DictationReceiver(store, input);
        await using (var client = new RelayClient(originalPairing.ReceiverId, settings, receiver))
        {
            client.Start();
            check(await Send("begin", "cloud_utterance_01") == "ready", "Cloud begin reaches the Windows receiver");
            check(await Send("begin", "cloud_utterance_01") == "ready" && input.Captures == 1,
                "Cloud begin retries preserve the original input target");
            const string sample = "跨网络语音输入。Hello 123 🐾";
            var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => Send("text", "cloud_utterance_01", sample)));
            check(results.All(status => status == "submitted") && input.Count == 1 && input.LastText == sample,
                "Cloud Unicode delivery and concurrent retries inject once");

            await Send("begin", "cloud_focus_01");
            input.Field = 2;
            check(await Send("text", "cloud_focus_01", "焦点已切换") == "focus_changed" && input.Count == 1,
                "Cloud delivery protects a changed input field");
            input.Field = 1;
            await Send("begin", "cloud_cancel_01");
            await Send("cancel", "cloud_cancel_01");
            check(await Send("text", "cloud_cancel_01", "已取消") == "unknown_utterance" && input.Count == 1,
                "Cloud cancellation prevents later input");
            receiver.Enabled = false;
            check(await Send("begin", "cloud_paused_01") == "paused", "Pause blocks cloud recordings");
            receiver.Enabled = true;

            await receiver.BeginAsync("cloud_deadline_01");
            input.DelayMs = 500;
            var deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 3;
            var expired = await receiver.DeliverAsync("cloud_deadline_01", "排队后过期", deadline);
            check(expired.Status == "expired" && input.Count == 1, "Queued input respects the monotonic delivery deadline");
        }

        var restartedInput = new SimulatedInput();
        var restartedReceiver = new DictationReceiver(new StateStore(Path.Combine(directory, "cloud")), restartedInput);
        await using var restartedClient = new RelayClient(originalPairing.ReceiverId, reloaded.Cloud, restartedReceiver);
        restartedClient.Start();
        check(await Send("text", "cloud_utterance_01", "跨网络语音输入。Hello 123 🐾") == "submitted" && restartedInput.Count == 0,
            "Cloud replay after receiver restart remains idempotent");
        check(await Send("begin", "cloud_restart_01") == "ready"
            && await Send("text", "cloud_restart_01", "重启后继续输入") == "submitted" && restartedInput.Count == 1,
            "A restarted Windows receiver reconnects using its saved cloud credentials");
    }

    private sealed class SimulatedInput : ITextInput
    {
        public int Captures { get; private set; }
        public int Count { get; private set; }
        public string LastText { get; private set; } = "";
        public int Field { get; set; } = 1;
        public int DelayMs { get; set; }

        public Task<InputTarget?> CaptureAsync(long? deadline = null)
        {
            Captures++;
            return Task.FromResult<InputTarget?>(new InputTarget(1, Field));
        }

        public async Task<string> SubmitAsync(InputTarget target, string text, long? deadline = null)
        {
            if (DelayMs > 0) await Task.Delay(DelayMs);
            if (deadline is long limit && Stopwatch.GetTimestamp() >= limit) return "expired";
            if (target.Focus != Field) return "focus_changed";
            LastText = text;
            Count++;
            return "submitted";
        }
    }
}
