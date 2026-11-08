using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PassportDictation;

internal sealed class Receipt
{
    public required string Id { get; init; }
    public required string TextHash { get; init; }
    public required string Status { get; set; }
    public DateTimeOffset Time { get; init; } = DateTimeOffset.UtcNow;
}

internal sealed class ReceiverState
{
    public string ReceiverId { get; set; } = Guid.NewGuid().ToString("N");
    public string Token { get; set; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
    public string DeviceId { get; set; } = "";
    public List<Receipt> Receipts { get; set; } = [];
    public CloudSettings Cloud { get; set; } = new();
}

internal sealed record CloudSettings
{
    public bool Enabled { get; init; }
    public string Url { get; init; } = RelayClient.DefaultUrl;
    public string Token { get; init; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
    public string PairNonce { get; init; } = Guid.NewGuid().ToString("N");
}

internal sealed class StateStore
{
    private readonly string path;
    private readonly object gate = new();
    private ReceiverState state;

    public StateStore(string directory)
    {
        Directory.CreateDirectory(directory);
        path = Path.Combine(directory, "receiver.json");
        state = File.Exists(path)
            ? JsonSerializer.Deserialize<ReceiverState>(File.ReadAllBytes(path))
                ?? throw new InvalidDataException("配对配置为空。")
            : new ReceiverState();
        if (state.ReceiverId.Length != 32 || state.Token.Length != 64)
            throw new InvalidDataException("配对配置损坏，请保留配置文件后重新配对。");
        foreach (var receipt in state.Receipts.Where(receipt => receipt.Status == "claimed"))
            receipt.Status = "uncertain";
        SaveLocked();
    }

    public (string ReceiverId, string DeviceId, string Token) Pairing
    {
        get { lock (gate) return (state.ReceiverId, state.DeviceId, state.Token); }
    }

    public CloudSettings Cloud
    {
        get { lock (gate) return state.Cloud; }
    }

    public void SetCloud(CloudSettings settings)
    {
        lock (gate)
        {
            var previous = state.Cloud;
            state.Cloud = settings;
            try { SaveLocked(); }
            catch
            {
                state.Cloud = previous;
                throw;
            }
        }
    }

    public void Pair(string deviceId)
    {
        lock (gate)
        {
            state.DeviceId = deviceId;
            state.Token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            SaveLocked();
        }
    }

    public bool Authorize(string deviceId, string authorization)
    {
        lock (gate)
        {
            var expected = Encoding.UTF8.GetBytes("Bearer " + state.Token);
            var supplied = Encoding.UTF8.GetBytes(authorization);
            return deviceId == state.DeviceId && supplied.Length == expected.Length
                && CryptographicOperations.FixedTimeEquals(expected, supplied);
        }
    }

    public Receipt? FindReceipt(string id)
    {
        lock (gate) return state.Receipts.Find(receipt => receipt.Id == id);
    }

    public void Claim(string id, string textHash)
    {
        lock (gate)
        {
            state.Receipts.Add(new Receipt { Id = id, TextHash = textHash, Status = "claimed" });
            if (state.Receipts.Count > 256)
                state.Receipts.RemoveRange(0, state.Receipts.Count - 256);
            SaveLocked();
        }
    }

    public void Finish(string id, string status)
    {
        lock (gate)
        {
            var receipt = state.Receipts.Find(receipt => receipt.Id == id)
                ?? throw new InvalidOperationException("投递记录丢失。");
            receipt.Status = status;
            SaveLocked();
        }
    }

    private void SaveLocked()
    {
        var temporary = path + ".tmp";
        using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(file, state);
            file.Flush(flushToDisk: true);
        }
        File.Move(temporary, path, overwrite: true);
    }
}
