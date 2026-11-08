using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace PassportDictation;

internal sealed class DictationRequest
{
    public string DeviceId { get; set; } = "";
    public string Id { get; set; } = "";
    public string Text { get; set; } = "";
}

internal sealed class DiscoveryRequest
{
    public string Service { get; set; } = "";
    public int Version { get; set; }
    public string DeviceId { get; set; } = "";
    public string ReceiverId { get; set; } = "";
    public string Nonce { get; set; } = "";
}

internal sealed class LocalServer(StateStore store, DictationReceiver receiver) : IAsyncDisposable
{
    public const int HttpPort = 18765;
    public const int DiscoveryPort = 47625;
    public const string ServiceName = "passport-dictation";
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    private readonly CancellationTokenSource stop = new();
    private readonly object pairingGate = new();
    private WebApplication? application;
    private UdpClient? discovery;
    private Task? discoveryTask;
    private DateTimeOffset pairingUntil;
    private string pairingDeviceId = "";
    public Action<string>? DeviceSeen { get; set; }
    public Action<string>? Error { get; set; }
    public bool IsPairing { get { lock (pairingGate) return pairingUntil > DateTimeOffset.UtcNow; } }

    public void OpenPairing()
    {
        lock (pairingGate)
        {
            pairingUntil = DateTimeOffset.UtcNow.AddSeconds(60);
            pairingDeviceId = "";
        }
    }

    public async Task StartAsync(int httpPort = HttpPort, int discoveryPort = DiscoveryPort)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.ListenAnyIP(httpPort);
            options.Limits.MaxRequestBodySize = 32768;
            options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(5);
        });
        application = builder.Build();
        application.MapGet("/health", () => Results.Json(new { service = ServiceName, version = 1 }));
        application.MapPost("/v1/{action}", (HttpContext context, string action) => HandleAsync(context, action));
        await application.StartAsync(stop.Token);
        try
        {
            discovery = new UdpClient(new IPEndPoint(IPAddress.Any, discoveryPort));
            discoveryTask = DiscoverAsync(httpPort);
        }
        catch
        {
            await application.DisposeAsync();
            application = null;
            throw;
        }
    }

    private async Task HandleAsync(HttpContext context, string action)
    {
        try
        {
            var request = await JsonSerializer.DeserializeAsync<DictationRequest>(
                context.Request.Body, JsonOptions, context.RequestAborted);
            if (request == null || !store.Authorize(request.DeviceId, context.Request.Headers.Authorization.ToString()))
            {
                context.Response.StatusCode = 401;
                return;
            }
            var result = action switch
            {
                "begin" => await receiver.BeginAsync(request.Id),
                "text" => await receiver.DeliverAsync(request.Id, request.Text),
                "cancel" => await receiver.CancelAsync(request.Id),
                _ => new DeliveryResult("not_found", 404)
            };
            context.Response.StatusCode = result.Code;
            await context.Response.WriteAsJsonAsync(new { id = request.Id, status = result.Status }, JsonOptions);
        }
        catch (JsonException)
        {
            context.Response.StatusCode = 400;
        }
        catch (BadHttpRequestException exception)
        {
            context.Response.StatusCode = exception.StatusCode;
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested || context.RequestAborted.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Error?.Invoke("接收失败，请检查本地配置目录是否可写。" + exception.GetType().Name);
            if (!context.Response.HasStarted)
                context.Response.StatusCode = 500;
        }
    }

    private async Task DiscoverAsync(int httpPort)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                var packet = await discovery!.ReceiveAsync(stop.Token);
                if (packet.Buffer.Length > 1024 || IPAddress.IsLoopback(packet.RemoteEndPoint.Address) && httpPort == HttpPort)
                    continue;
                var request = JsonSerializer.Deserialize<DiscoveryRequest>(packet.Buffer, JsonOptions);
                if (request == null || request.Service != ServiceName || request.Version != 1
                    || request.DeviceId == null || request.Nonce == null || request.ReceiverId == null
                    || !Regex.IsMatch(request.DeviceId, "^([0-9a-fA-F]{2}:){5}[0-9a-fA-F]{2}$")
                    || !Regex.IsMatch(request.Nonce, "^[0-9a-f]{16,64}$"))
                    continue;

                string? issuedToken = null;
                var pairing = store.Pairing;
                if (request.ReceiverId.Length == 0)
                {
                    lock (pairingGate)
                    {
                        if (pairingUntil <= DateTimeOffset.UtcNow)
                            continue;
                        if (pairingDeviceId.Length == 0)
                        {
                            store.Pair(request.DeviceId);
                            pairingDeviceId = request.DeviceId;
                        }
                        if (pairingDeviceId != request.DeviceId)
                            continue;
                        pairing = store.Pairing;
                        issuedToken = pairing.Token;
                    }
                }
                else if (request.ReceiverId != pairing.ReceiverId || request.DeviceId != pairing.DeviceId)
                {
                    continue;
                }
                else
                {
                    lock (pairingGate) pairingUntil = DateTimeOffset.MinValue;
                }

                using var route = new UdpClient(AddressFamily.InterNetwork);
                route.Connect(packet.RemoteEndPoint);
                var host = ((IPEndPoint)route.Client.LocalEndPoint!).Address.ToString();
                var proofText = request.Nonce + "\n" + pairing.ReceiverId + "\n" + host + "\n" + httpPort;
                var proof = Convert.ToHexString(HMACSHA256.HashData(
                    Encoding.UTF8.GetBytes(pairing.Token), Encoding.UTF8.GetBytes(proofText))).ToLowerInvariant();
                var reply = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    service = ServiceName,
                    version = 1,
                    nonce = request.Nonce,
                    receiver_id = pairing.ReceiverId,
                    host,
                    port = httpPort,
                    token = issuedToken,
                    proof
                }, JsonOptions);
                await discovery.SendAsync(reply, packet.RemoteEndPoint, stop.Token);
                DeviceSeen?.Invoke(request.DeviceId);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
            catch (ObjectDisposedException) when (stop.IsCancellationRequested) { break; }
            catch (JsonException) { }
            catch (SocketException exception)
            {
                Error?.Invoke("局域网发现暂时失败。" + exception.SocketErrorCode);
                await Task.Delay(1000, stop.Token);
            }
            catch (Exception exception)
            {
                Error?.Invoke("局域网发现处理失败。" + exception.GetType().Name);
                await Task.Delay(1000, stop.Token);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await stop.CancelAsync();
        discovery?.Dispose();
        if (discoveryTask != null)
        {
            try { await discoveryTask; }
            catch (OperationCanceledException) { }
        }
        if (application != null)
            await application.DisposeAsync();
        stop.Dispose();
    }
}
