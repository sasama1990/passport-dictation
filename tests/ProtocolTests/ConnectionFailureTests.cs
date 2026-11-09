using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace PassportDictation;

internal static class ConnectionFailureTests
{
    public static async Task RunAsync(string directory, Action<bool, string> check)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        await using var server = builder.Build();
        server.Urls.Add("http://127.0.0.1:0");
        var pairingRequests = 0;
        server.MapPost("/v1/pairing", (HttpContext context) =>
        {
            Interlocked.Increment(ref pairingRequests);
            context.Response.StatusCode = 403;
            return context.Response.WriteAsync("<html>Access blocked</html>");
        });
        server.MapGet("/v1/receiver/{room}/next", (HttpContext context) =>
        {
            context.Response.StatusCode = 403;
            return context.Response.WriteAsync("<html>Access blocked</html>");
        });
        await server.StartAsync();
        try
        {
            var store = new StateStore(Path.Combine(directory, "blocked-relay"));
            var settings = store.Cloud with { Url = server.Urls.Single(), Enabled = true };
            var forbidden = false;
            try
            {
                await RelayClient.OpenPairingAsync(store.Pairing.ReceiverId, settings,
                    CancellationToken.None, testLoopback: true);
            }
            catch (HttpRequestException exception)
            {
                forbidden = exception.StatusCode == HttpStatusCode.Forbidden;
            }
            check(forbidden && pairingRequests == 1, "Blocked pairing fails immediately without retrying HTTP 403");

            var notice = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var receiver = new DictationReceiver(store, new ProtocolInput());
            await using var client = new RelayClient(store.Pairing.ReceiverId, settings, receiver, testLoopback: true)
            {
                Notice = message => notice.TrySetResult(message),
            };
            client.Start();
            var message = await notice.Task.WaitAsync(TimeSpan.FromSeconds(5));
            check(message.Contains("403") && message.Contains("拒绝访问"),
                "Blocked polling identifies the relay access failure in the Windows notice");
            check(RelayClient.ConnectionErrorMessage(new HttpRequestException("unauthorized", null, HttpStatusCode.Unauthorized))
                    .Contains("配对凭据已失效"), "Expired credentials have a distinct re-pairing instruction");
            check(RelayClient.ConnectionErrorMessage(new HttpRequestException("DNS"))
                    .Contains("DNS"), "Connection failures identify the network and DNS checks");
        }
        finally { await server.StopAsync(); }
    }
}
