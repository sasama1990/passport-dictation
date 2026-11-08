using System.Net.Http.Json;
using System.Text.Json;
using PassportDictation;

var passed = new List<string>();
var directory = Path.Combine(Path.GetFullPath(args[1]), Guid.NewGuid().ToString("N"));
var report = Path.GetFullPath(args[2]);
Directory.CreateDirectory(directory);
void Check(bool success, string name)
{
    if (!success) throw new InvalidOperationException("Failed: " + name);
    passed.Add(name);
    Console.WriteLine("Passed " + name);
}

try
{
    await RelaySmokeTests.RunAsync(args[0], directory, Check);
    var store = new StateStore(Path.Combine(directory, "lan"));
    store.Pair("02:00:00:00:00:03");
    var input = new ProtocolInput();
    var receiver = new DictationReceiver(store, input);
    await using var local = new LocalServer(store, receiver);
    await local.StartAsync(18766, 47626);
    using var client = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:18766") };
    var body = new { device_id = store.Pairing.DeviceId, id = "lan_regression_01", text = "局域网依然可用 Hello 🐾" };
    using var unauthorized = await client.PostAsJsonAsync("v1/begin", body);
    Check((int)unauthorized.StatusCode == 401, "LAN authentication remains required");
    client.DefaultRequestHeaders.Authorization = new("Bearer", store.Pairing.Token);
    async Task<string> Post(string action)
    {
        using var response = await client.PostAsJsonAsync("v1/" + action, body);
        using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return result.RootElement.GetProperty("status").GetString()!;
    }
    Check(await Post("begin") == "ready", "LAN begin still captures its input target");
    Check(await Post("text") == "submitted" && input.Count == 1, "LAN Unicode delivery remains available");
    Check(await Post("text") == "submitted" && input.Count == 1, "LAN retries remain idempotent");
    Directory.CreateDirectory(Path.GetDirectoryName(report)!);
    File.WriteAllText(report, JsonSerializer.Serialize(new { success = true, passed, native_input_tested = false }, new JsonSerializerOptions { WriteIndented = true }));
}
catch (Exception exception)
{
    Directory.CreateDirectory(Path.GetDirectoryName(report)!);
    File.WriteAllText(report, JsonSerializer.Serialize(new { success = false, passed, error = exception.ToString(), native_input_tested = false }, new JsonSerializerOptions { WriteIndented = true }));
    Console.Error.WriteLine(exception);
    Environment.ExitCode = 1;
}

internal sealed class ProtocolInput : ITextInput
{
    public int Count { get; private set; }
    public Task<InputTarget?> CaptureAsync(long? deadline = null) => Task.FromResult<InputTarget?>(new InputTarget(1, 1));
    public Task<string> SubmitAsync(InputTarget target, string text, long? deadline = null)
    {
        Count++;
        return Task.FromResult("submitted");
    }
}
