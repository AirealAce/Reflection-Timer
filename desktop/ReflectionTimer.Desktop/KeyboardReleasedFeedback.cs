using System.Runtime.InteropServices;

namespace ReflectionTimer.Accessible;

// Speech alone waits for the chord to finish; the session action is already
// committed. In particular Ctrl is also JAWS's interrupt-speech key.
internal sealed class KeyboardReleasedFeedback(
    Action<Action> schedule, Func<bool> keysPressed, Func<long> now) : IDisposable
{
    internal const long SettledMilliseconds = 80;
    private Action? primary, supplement;
    private long quietSince;
    private bool waiting, quiet, disposed;

    internal void Publish(Action action, bool supplementary = false)
    {
        if (disposed) return;
        if (supplementary) supplement = action; else primary = action;
        if (waiting) return;
        waiting = true; quiet = false;
        schedule(Poll);
    }

    internal void Poll()
    {
        if (disposed || !waiting) return;
        var at = now();
        if (keysPressed()) quiet = false;
        else if (!quiet) { quiet = true; quietSince = at; }
        if (!quiet || at - quietSince < SettledMilliseconds) {
            schedule(Poll); return;
        }
        var action = primary; var extra = supplement;
        primary = supplement = null; waiting = quiet = false;
        try { action?.Invoke(); } finally { if (!disposed) extra?.Invoke(); }
    }

    public void Dispose() { disposed = true; primary = supplement = null; waiting = false; }

    internal static bool ShortcutKeysPressed() => KeyDown(0x11) || KeyDown(0x12) || KeyDown(0x20);
    private static bool KeyDown(int key) => (GetAsyncKeyState(key) & 0x8000) != 0;
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
}
