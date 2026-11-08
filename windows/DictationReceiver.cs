using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Diagnostics;

namespace PassportDictation;

internal sealed record DeliveryResult(string Status, int Code = 200);
internal sealed record ReceiverNotice(string Status, string Text = "");

internal sealed class DictationReceiver(StateStore store, ITextInput input)
{
    private readonly SemaphoreSlim gate = new(1);
    private string activeId = "";
    private InputTarget? target;
    private long started;
    public bool Enabled { get; set; } = true;
    public Action<ReceiverNotice>? Notice { get; set; }

    public async Task<DeliveryResult> BeginAsync(string id, long? deadline = null)
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
            if (deadline is long limit && Stopwatch.GetTimestamp() >= limit)
                return new("expired", 408);
            if (activeId.Length > 0 && Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(120))
                return new("busy", 409);
            var captured = await input.CaptureAsync(deadline);
            if (captured == null)
                return new("no_target", 409);
            activeId = id;
            target = captured;
            started = Stopwatch.GetTimestamp();
            Notice?.Invoke(new("listening"));
            return new("ready");
        }
        finally { gate.Release(); }
    }

    public async Task<DeliveryResult> DeliverAsync(string id, string text, long? deadline = null)
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
            {
                Notice?.Invoke(new("unknown_utterance", text));
                return new("unknown_utterance", 409);
            }
            if (!Enabled || Stopwatch.GetElapsedTime(started) >= TimeSpan.FromSeconds(120)
                || deadline is long limit && Stopwatch.GetTimestamp() >= limit)
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
            var inputDeadline = Math.Min(deadline ?? long.MaxValue, started + 120L * Stopwatch.Frequency);
            try { status = await input.SubmitAsync(target.Value, text, inputDeadline); }
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

    public async Task ResetActiveAsync()
    {
        await gate.WaitAsync();
        try
        {
            activeId = "";
            target = null;
        }
        finally { gate.Release(); }
    }

    private static bool ValidId(string id) => id != null && Regex.IsMatch(id, "^[a-zA-Z0-9_-]{8,64}$");
}
