using System.Reflection;
using System.Runtime.InteropServices;

namespace ReflectionTimer.Accessible;

internal interface IJawsSpeech : IDisposable
{
    bool Say(string text, bool interrupt);
}

// Use the API registered by an already-running JAWS. Never install a library,
// start a screen reader, run its scripts, or change its settings.
internal sealed class JawsScreenReaderOutput : IDisposable
{
    private readonly Func<nint> runningWindow;
    private readonly Func<IJawsSpeech?> create;
    private IJawsSpeech? speech;
    private nint connectedWindow;
    private bool disposed;

    internal JawsScreenReaderOutput(Func<nint>? runningWindow = null, Func<IJawsSpeech?>? create = null)
    {
        this.runningWindow = runningWindow ?? (() => FindWindow("JFWUI2", null));
        this.create = create ?? JawsComSpeech.Create;
    }

    internal bool TryAnnounce(string text, bool supplementary)
    {
        if (disposed || string.IsNullOrWhiteSpace(text)) return false;
        try {
            var current = runningWindow();
            if (current == 0) { Reset(); return false; }
            if (current != connectedWindow) { Reset(); connectedWindow = current; }
            speech ??= create();
            if (speech?.Say(text, !supplementary) == true) return true;
        } catch { /* A missing, stopped or disconnected reader permits UIA. */ }
        Reset();
        return false;
    }

    private void Reset()
    {
        var previous = speech; speech = null; connectedWindow = 0;
        try { previous?.Dispose(); } catch { }
    }
    public void Dispose() { if (disposed) return; disposed = true; Reset(); }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint FindWindow(string className, string? windowName);

    private sealed class JawsComSpeech(object controller) : IJawsSpeech
    {
        internal static IJawsSpeech? Create()
        {
            var type = Type.GetTypeFromProgID("FreedomSci.JawsApi", throwOnError: false);
            return type is null ? null : Activator.CreateInstance(type) is { } api ? new JawsComSpeech(api) : null;
        }
        public bool Say(string text, bool interrupt) => controller.GetType().InvokeMember(
            "SayString", BindingFlags.InvokeMethod, null, controller, [text, interrupt]) is true;
        public void Dispose()
        {
            if (Marshal.IsComObject(controller)) Marshal.FinalReleaseComObject(controller);
        }
    }
}

// UIA returning true means Windows accepted the event, not that JAWS spoke it.
// Select one reader route per message so successful JAWS output is not doubled.
internal sealed class ScreenReaderAnnouncementRouter(
    Func<string, bool, bool> jaws,
    Func<Control?, string, bool, bool> native)
{
    internal bool TryAnnounce(Control? owner, string message, bool supplementary)
    {
        if (owner is null || owner.IsDisposed || owner.Disposing || string.IsNullOrWhiteSpace(message)) return false;
        try { if (jaws(message, supplementary)) return true; } catch { }
        try { return native(owner, message, supplementary); } catch { return false; }
    }
}
