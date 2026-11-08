using System.Runtime.InteropServices;
using System.Diagnostics;

namespace PassportDictation;

internal sealed class WindowsTextInput(Control dispatcher) : ITextInput
{
    public nint TestWindow { get; set; }
    public bool RestrictToTestWindow { get; set; }

    public async Task<InputTarget?> CaptureAsync(long? deadline = null)
    {
        return await dispatcher.InvokeAsync(() =>
        {
            if (deadline is long limit && Stopwatch.GetTimestamp() >= limit)
                return (InputTarget?)null;
            var window = Native.GetForegroundWindow();
            if (window == 0)
                return (InputTarget?)null;
            if (RestrictToTestWindow && window != TestWindow)
                return null;
            var thread = Native.GetWindowThreadProcessId(window, out var process);
            if (process == Environment.ProcessId && window != TestWindow)
                return null;
            var info = new Native.GuiThreadInfo { Size = Marshal.SizeOf<Native.GuiThreadInfo>() };
            if (!Native.GetGUIThreadInfo(thread, ref info) || info.Focus == 0)
                return null;
            return new InputTarget(window, info.Focus);
        });
    }

    public async Task<string> SubmitAsync(InputTarget target, string text, long? deadline = null)
    {
        return await dispatcher.InvokeAsync(() =>
        {
            if (deadline is long limit && Stopwatch.GetTimestamp() >= limit)
                return "expired";
            if (RestrictToTestWindow && target.Window != TestWindow)
                return "focus_changed";
            if (Native.GetForegroundWindow() != target.Window)
                return "focus_changed";
            var thread = Native.GetWindowThreadProcessId(target.Window, out _);
            var info = new Native.GuiThreadInfo { Size = Marshal.SizeOf<Native.GuiThreadInfo>() };
            if (!Native.GetGUIThreadInfo(thread, ref info) || info.Focus != target.Focus)
                return "focus_changed";
            if (new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C }.Any(key => Native.GetAsyncKeyState(key) < 0))
                return "keyboard_busy";

            var events = new Native.Input[text.Length * 2];
            for (var index = 0; index < text.Length; index++)
            {
                events[index * 2] = Native.Input.Unicode(text[index], keyUp: false);
                events[index * 2 + 1] = Native.Input.Unicode(text[index], keyUp: true);
            }
            if (deadline is long sendLimit && Stopwatch.GetTimestamp() >= sendLimit)
                return "expired";
            var sent = Native.SendInput((uint)events.Length, events, Marshal.SizeOf<Native.Input>());
            if (sent == events.Length)
                return "submitted";
            return sent == 0 ? "input_blocked" : "uncertain";
        });
    }
}

internal static class Native
{
    [DllImport("user32.dll")]
    internal static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    internal static extern uint GetWindowThreadProcessId(nint window, out uint process);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);

    [DllImport("user32.dll")]
    internal static extern short GetAsyncKeyState(int key);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint SendInput(uint count, Input[] input, int size);

    [StructLayout(LayoutKind.Sequential)]
    internal struct GuiThreadInfo
    {
        public int Size;
        public uint Flags;
        public nint Active;
        public nint Focus;
        public nint Capture;
        public nint MenuOwner;
        public nint MoveSize;
        public nint Caret;
        public Rectangle CaretRectangle;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Input
    {
        public uint Type;
        public InputUnion Data;

        public static Input Unicode(char character, bool keyUp) => new()
        {
            Type = 1,
            Data = new InputUnion
            {
                Keyboard = new KeyboardInput { Scan = character, Flags = keyUp ? 0x0006u : 0x0004u }
            }
        };
    }

    [StructLayout(LayoutKind.Explicit)]
    internal struct InputUnion
    {
        [FieldOffset(0)] public MouseInput Mouse;
        [FieldOffset(0)] public KeyboardInput Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort Scan;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MouseInput
    {
        public int X;
        public int Y;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }
}
