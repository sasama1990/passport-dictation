using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PassportDictation;

internal sealed class RelayPairing
{
    public string Code { get; set; } = "";
    public int ExpiresIn { get; set; }
}

internal sealed class RelayCommand
{
    public string CommandId { get; set; } = "";
    public string Id { get; set; } = "";
    public string Action { get; set; } = "";
    public string Text { get; set; } = "";
    public int TtlMs { get; set; }
}

internal sealed class RelayPoll
{
    public string DeviceId { get; set; } = "";
    public List<RelayCommand> Commands { get; set; } = [];
}

internal sealed class RelayClient : IAsyncDisposable
{
    public const string DefaultUrl = "https://passport-dictation-relay-sasama.gabledhill.chatgpt.site";
    private readonly DictationReceiver receiver;
    private readonly string room;
    private readonly HttpClient http;
    private readonly CancellationTokenSource stop = new();
    private Task? running;
    public Action<string>? Notice { get; set; }

    public RelayClient(string room, CloudSettings settings, DictationReceiver receiver, bool testLoopback = false)
    {
        this.room = room;
        this.receiver = receiver;
        http = CreateHttp(settings, testLoopback);
    }

    public static string NormalizeUrl(string value, bool testLoopback = false)
    {
        if (value.Length > 192 || !Uri.TryCreate(value.Trim().TrimEnd('/') + "/", UriKind.Absolute, out var uri)
            || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0
            || !(uri.Scheme == "https" || testLoopback && uri.Scheme == "http" && uri.IsLoopback))
            throw new ArgumentException("请输入 HTTPS 中继地址。", nameof(value));
        return uri.AbsoluteUri.TrimEnd('/');
    }

    private static HttpClient CreateHttp(CloudSettings settings, bool testLoopback)
    {
        var result = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            BaseAddress = new Uri(NormalizeUrl(settings.Url, testLoopback) + "/"),
            Timeout = TimeSpan.FromSeconds(15),
            MaxResponseContentBufferSize = 65536,
        };
        result.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", settings.Token);
        return result;
    }

    public static async Task<RelayPairing> OpenPairingAsync(string room, CloudSettings settings,
        CancellationToken cancellationToken, bool testLoopback = false)
    {
        using var client = CreateHttp(settings, testLoopback);
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var response = await client.PostAsJsonAsync("v1/pairing",
                    new { receiver_id = room, pair_nonce = settings.PairNonce }, cancellationToken);
                response.EnsureSuccessStatusCode();
                var result = await response.Content.ReadFromJsonAsync<RelayPairing>(LocalServer.JsonOptions, cancellationToken);
                if (result == null || result.Code.Length != 8 || result.ExpiresIn <= 0)
                    throw new InvalidDataException("中继返回了无效配对码。");
                return result;
            }
            catch (HttpRequestException) when (attempt < 2 && !cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(500, cancellationToken);
            }
        }
    }

    public void Start() => running ??= Task.Run(RunAsync);

    private async Task RunAsync()
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                var requestStarted = Stopwatch.GetTimestamp();
                using var response = await http.GetAsync($"v1/receiver/{room}/next?wait_ms=5000", stop.Token);
                response.EnsureSuccessStatusCode();
                var result = await response.Content.ReadFromJsonAsync<RelayPoll>(LocalServer.JsonOptions, stop.Token);
                if (result == null || result.DeviceId == null || result.Commands == null || result.Commands.Count > 4
                    || result.Commands.Any(command => command == null || command.Text == null || command.Text.Length > 4096
                        || !Regex.IsMatch(command.CommandId ?? "", "^[0-9a-f]{32}$")
                        || !Regex.IsMatch(command.Id ?? "", "^[a-zA-Z0-9_-]{8,64}$")
                        || command.Action is not ("begin" or "text" or "cancel")))
                    throw new InvalidDataException("无效的中继响应。");
                Notice?.Invoke(result.DeviceId.Length > 0 ? "跨网设备已配对  " + result.DeviceId : "跨网连接已建立，等待设备配对");
                foreach (var command in result.Commands)
                {
                    stop.Token.ThrowIfCancellationRequested();
                    // Subtract the entire round trip, including the server wait.
                    // This gives a conservative deadline without trusting PC clock time.
                    var remaining = Math.Clamp(command.TtlMs, 0, 10000) - Stopwatch.GetElapsedTime(requestStarted).TotalMilliseconds;
                    var deadline = Stopwatch.GetTimestamp() + (long)(Math.Max(0, remaining) * Stopwatch.Frequency / 1000);
                    DeliveryResult delivery;
                    try
                    {
                        delivery = command.Action switch
                        {
                            "begin" => await receiver.BeginAsync(command.Id, deadline),
                            "text" => await receiver.DeliverAsync(command.Id, command.Text, deadline),
                            "cancel" => await receiver.CancelAsync(command.Id),
                            _ => new("invalid_action", 400),
                        };
                    }
                    catch (Exception) when (!stop.IsCancellationRequested)
                    {
                        delivery = new(command.Action == "text" ? "uncertain" : "receiver_error");
                    }
                    using var acknowledged = await http.PostAsJsonAsync($"v1/receiver/{room}/ack", new
                    {
                        command_id = command.CommandId, status = delivery.Status, code = delivery.Code,
                    }, stop.Token);
                    acknowledged.EnsureSuccessStatusCode();
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException or InvalidDataException)
            {
                Notice?.Invoke("跨网连接中断，正在重连。可在跨网络设置中重新配对。");
                await Task.Delay(2000, stop.Token);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await stop.CancelAsync();
        if (running != null)
        {
            try { await running; }
            catch (OperationCanceledException) { }
        }
        http.Dispose();
        stop.Dispose();
    }
}
