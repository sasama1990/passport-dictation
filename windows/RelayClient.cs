using System.Diagnostics;
using System.Net;
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
    public const string DefaultUrl = "http://8.133.186.187:23000/passport-dictation";
    public const string LegacyDefaultUrl = "https://passport-dictation-relay-sasama.gabledhill.chatgpt.site";
    private readonly DictationReceiver receiver;
    private readonly string room;
    private readonly HttpClient http;
    private readonly CancellationTokenSource stop = new();
    private Task? running;
    public Action<string>? Notice { get; set; }

    public RelayClient(string room, CloudSettings settings, DictationReceiver receiver)
    {
        this.room = room;
        this.receiver = receiver;
        http = CreateHttp(settings);
    }

    public static string NormalizeUrl(string value)
    {
        if (value.Length > 192 || !Uri.TryCreate(value.Trim().TrimEnd('/') + "/", UriKind.Absolute, out var uri)
            || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0
            || uri.Scheme is not ("http" or "https"))
            throw new ArgumentException("请输入 HTTP 或 HTTPS 中继地址。", nameof(value));
        return uri.AbsoluteUri.TrimEnd('/');
    }

    private static HttpClient CreateHttp(CloudSettings settings)
    {
        var result = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            BaseAddress = new Uri(NormalizeUrl(settings.Url) + "/"),
            Timeout = TimeSpan.FromSeconds(15),
            MaxResponseContentBufferSize = 65536,
        };
        result.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", settings.Token);
        return result;
    }

    public static async Task<RelayPairing> OpenPairingAsync(string room, CloudSettings settings,
        CancellationToken cancellationToken)
    {
        using var client = CreateHttp(settings);
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var response = await client.PostAsJsonAsync("v1/pairing",
                    new { receiver_id = room, pair_nonce = settings.PairNonce }, cancellationToken);
                response.EnsureSuccessStatusCode();
                var result = await response.Content.ReadFromJsonAsync<RelayPairing>(LocalServer.JsonOptions, cancellationToken);
                if (result == null || result.Code == null || result.Code.Length != 8 || result.ExpiresIn <= 0)
                    throw new InvalidDataException("中继返回了无效配对码。");
                return result;
            }
            catch (HttpRequestException exception) when (attempt < 2 && !cancellationToken.IsCancellationRequested
                && (exception.StatusCode == null || (int)exception.StatusCode >= 500))
            {
                await Task.Delay(500, cancellationToken);
            }
        }
    }

    public void Start() => running ??= Task.Run(RunAsync);

    public static string ConnectionErrorMessage(Exception exception) => exception switch
    {
        HttpRequestException { StatusCode: HttpStatusCode.Forbidden } =>
            "中继入口拒绝访问（HTTP 403）。请更换可访问的中继地址，两端使用相同地址后重新配对。",
        HttpRequestException { StatusCode: HttpStatusCode.Unauthorized } =>
            "跨网配对凭据已失效（HTTP 401）。请生成新配对码，并在设备设置中保存。",
        HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests } =>
            "中继请求受限（HTTP 429），请稍后再试。",
        HttpRequestException { StatusCode: not null } httpError =>
            $"中继返回 HTTP {(int)httpError.StatusCode.Value}，请检查服务状态。",
        TaskCanceledException => "连接中继超时，请检查网络和中继地址。",
        HttpRequestException => "无法连接中继，请检查网络、DNS 和中继地址。",
        _ => "中继响应无效，请检查中继地址和服务版本。",
    };

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
                Notice?.Invoke(result.DeviceId.Length > 0 ? "中继已连接，设备已配对  " + result.DeviceId
                    : "中继已连接，等待设备配对");
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
                Notice?.Invoke(ConnectionErrorMessage(exception));
                await Task.Delay(exception is HttpRequestException { StatusCode: HttpStatusCode.Forbidden
                    or HttpStatusCode.Unauthorized or HttpStatusCode.TooManyRequests } ? 10000 : 2000, stop.Token);
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
