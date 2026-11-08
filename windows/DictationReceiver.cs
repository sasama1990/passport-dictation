using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace PassportDictation;

internal sealed record DeliveryResult(string Status, int Code = 200);
internal sealed record ReceiverNotice(string Status, string Text = "");

internal sealed class DictationReceiver(StateStore store, ITextInput input)
{
    private readonly SemaphoreSlim gate = new(1);
    private string activeId = "";
    private InputTarget? target;
    private DateTimeOffset started;
    public bool Enabled { get; set; } = true;
    public Action<ReceiverNotice>? Notice { get; set; }

    public async Task<DeliveryResult> BeginAsync(string id)
    {
        if (!ValidId(id))
            return new("invalid_id", 400);
        await gate.WaitAsync();
        try
        {
            if (!Enabled)
                return new("paused", 409);
            var existing = store.FindReceipt(id);
            if (existing != null)
                return new(existing.Status);
            if (activeId == id)
                return new("ready");
            if (activeId.Length > 0 && DateTimeOffset.UtcNow - started < TimeSpan.FromSeconds(100))
                return new("busy", 409);
            var captured = await input.CaptureAsync();
            if (captured == null)
                return new("no_target", 409);
            activeId = id;
            target = captured;
            started = DateTimeOffset.UtcNow;
            Notice?.Invoke(new("listening"));
            return new("ready");
        }
        finally { gate.Release(); }
    }

    public async Task<DeliveryResult> DeliverAsync(string id, string text)
    {
        if (!ValidId(id) || string.IsNullOrWhiteSpace(text) || text.Length > 4096 || text.Contains('\0'))
            return new("invalid_text", 400);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
        await gate.WaitAsync();
        try
        {
            var existing = store.FindReceipt(id);
            if (existing != null)
                return existing.TextHash == hash
                    ? new(existing.Status)
                    : new("conflicting_text", 409);
            if (id != activeId || target == null)
                return new("unknown_utterance", 409);
            if (!Enabled || DateTimeOffset.UtcNow - started >= TimeSpan.FromSeconds(100))
            {
                activeId = "";
                target = null;
                Notice?.Invoke(new("expired", text));
                return new("expired", 409);
            }

            // Persist the claim before touching another app. An interrupted claim
            // becomes uncertain after restart and will never be injected twice.
            store.Claim(id, hash);
            var status = "uncertain";
            try { status = await input.SubmitAsync(target.Value, text); }
            finally
            {
                store.Finish(id, status);
                activeId = "";
                target = null;
                Notice?.Invoke(new(status, text));
            }
            return new(status);
        }
        finally { gate.Release(); }
    }

    public async Task<DeliveryResult> CancelAsync(string id)
    {
        if (!ValidId(id))
            return new("invalid_id", 400);
        await gate.WaitAsync();
        try
        {
            if (id == activeId)
            {
                activeId = "";
                target = null;
                Notice?.Invoke(new("cancelled"));
            }
            return new("cancelled");
        }
        finally { gate.Release(); }
    }

    private static bool ValidId(string id) => id != null && Regex.IsMatch(id, "^[a-zA-Z0-9_-]{8,64}$");
}
