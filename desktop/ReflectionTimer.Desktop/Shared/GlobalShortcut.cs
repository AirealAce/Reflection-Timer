using System.Runtime.InteropServices;

namespace ReflectionTimer.Desktop;

public interface IHotKeyRegistration
{
    bool Register(nint window, int id, uint modifiers, uint key);
    bool Unregister(nint window, int id);
}

// A message-only window receives just this registered chord, not a keyboard hook
// or a stream of the user's keystrokes. Keep its lifetime independent of Hide().
public sealed class GlobalShortcut : NativeWindow, IDisposable
{
    internal const int HotKeyMessage = 0x0312, HotKeyId = 0x5254;
    internal const uint Modifiers = 0x0002 | 0x0001 | 0x4000; // Control + Alt + NoRepeat
    internal const uint Key = 0x54; // T
    internal const int EndEarlyId = 0x5255;
    internal const uint EndEarlyKey = 0xC0; // VK_OEM_3: backtick/tilde on a US keyboard.
    internal const int CompactId = 0x5256;
    internal const uint CompactKey = 0xBC; // VK_OEM_COMMA: comma on a US keyboard.
    internal const int CompactFocusId = 0x5257;
    internal const uint CompactFocusKey = 0xBE; // VK_OEM_PERIOD: period on a US keyboard.
    internal const int ReflectionFocusId = 0x5258;
    internal const uint ReflectionFocusKey = 0xBF; // VK_OEM_2: slash/question mark on a US keyboard.
    internal const int TimerToggleId = 0x5259;
    internal const int TimerToggleAltId = 0x525A;
    internal const uint TimerToggleKey = 0x20; // VK_SPACE
    internal const uint TimerToggleModifiers = 0x0002 | 0x4000; // Control + NoRepeat
    private readonly IHotKeyRegistration registration;
    private readonly Action pressed;
    private readonly int hotKeyId;
    private bool disposed;
    public bool IsRegistered { get; private set; }

    public GlobalShortcut(Action pressed, IHotKeyRegistration? registration = null, uint key = Key, int id = HotKeyId, uint modifiers = Modifiers)
    {
        this.pressed = pressed; this.registration = registration ?? new WindowsHotKeyRegistration(); hotKeyId = id;
        CreateHandle(new CreateParams { Caption = "Reflection Timer shortcut", Parent = new nint(-3) }); // HWND_MESSAGE
        try { IsRegistered = this.registration.Register(Handle, hotKeyId, modifiers, key); }
        catch { DestroyHandle(); throw; }
    }

    internal bool Dispatch(int message, nint id)
    {
        if (disposed || !IsRegistered || message != HotKeyMessage || id != hotKeyId) return false;
        pressed(); return true;
    }
    protected override void WndProc(ref Message message)
    {
        if (Dispatch(message.Msg, message.WParam)) { message.Result = 0; return; }
        base.WndProc(ref message);
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        try { if (IsRegistered) registration.Unregister(Handle, hotKeyId); }
        finally { IsRegistered = false; DestroyHandle(); }
        GC.SuppressFinalize(this);
    }
}

// Only counts this registered shortcut. No keyboard hook or delayed first action.
internal sealed class ConsecutiveShortcutPresses(TimeProvider? timeProvider = null)
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private long? previous;
    internal static readonly TimeSpan DoublePressWindow = TimeSpan.FromMilliseconds(800);

    internal bool Press()
    {
        var now = clock.GetTimestamp();
        if (previous is { } last && clock.GetElapsedTime(last, now) is var elapsed &&
            elapsed >= TimeSpan.Zero && elapsed <= DoublePressWindow) {
            Reset(); return true; // Consume the pair; a third press starts a new pair.
        }
        previous = now; return false;
    }
    internal void Reset() => previous = null;
}

internal sealed class WindowsHotKeyRegistration : IHotKeyRegistration
{
    public bool Register(nint window, int id, uint modifiers, uint key) => RegisterHotKey(window, id, modifiers, key);
    public bool Unregister(nint window, int id) => UnregisterHotKey(window, id);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint key);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(nint window, int id);
}

internal static class WindowActivation
{
    internal static bool CanReceiveFocus(Form window) =>
        !window.IsDisposed && window.Enabled && (!window.IsHandleCreated || IsWindowEnabled(window.Handle));

    internal static bool IsForeground(Form window, nint? foregroundHandle = null) =>
        CanReceiveFocus(window) && window.IsHandleCreated && window.Visible &&
        window.WindowState != FormWindowState.Minimized && (foregroundHandle ?? GetForegroundWindow()) == window.Handle;

    public static void Focus(Form window)
    {
        if(IsForeground(window))return;
        window.Show();
        if (window.WindowState == FormWindowState.Minimized) ShowWindow(window.Handle, 9); // SW_RESTORE
        else ShowWindow(window.Handle, 5); // SW_SHOW also overrides a hidden launcher STARTUPINFO on first open.
        // An owned modal (including a native file picker) must remain in front of
        // its disabled owner; never dismiss it or redirect typing behind it.
        if (CanReceiveFocus(window)) {
            window.BringToFront(); SetForegroundWindow(window.Handle);
        }
        else {
            var popup = GetLastActivePopup(window.Handle);
            if (Form.FromHandle(popup) is Form modal && modal != window) { modal.BringToFront(); modal.Activate(); }
            SetForegroundWindow(popup);
        }
    }
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint window, int command);
    [DllImport("user32.dll")]
    private static extern nint GetLastActivePopup(nint window);
    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowEnabled(nint window);
}
