namespace PassportDictation;

internal readonly record struct InputTarget(nint Window, nint Focus);

internal interface ITextInput
{
    Task<InputTarget?> CaptureAsync(long? deadline = null);
    Task<string> SubmitAsync(InputTarget target, string text, long? deadline = null);
}
