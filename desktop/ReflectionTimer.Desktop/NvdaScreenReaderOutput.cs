using System.Runtime.InteropServices;
using System.Text;
using System.Xml;

namespace ReflectionTimer.Accessible;

internal interface INvdaSpeech : IDisposable
{
    int TestIfRunning();
    int SpeakText(string text);
    int SpeakImportantText(string text) => SpeakText(text);
    int BrailleMessage(string text);
}

// The official controller talks to an already-running NVDA on this desktop.
// RPC can stall when a reader stops responding, so no native call runs on the
// UI thread. Keep the latest pending action, confirmation and view message.
internal sealed class NvdaScreenReaderOutput : IDisposable
{
    private readonly object gate = new();
    private readonly AutoResetEvent available = new(false);
    private readonly Func<INvdaSpeech?> create;
    private readonly Func<bool> canAnnounce;
    private readonly Thread worker;
    private Work? primary, notice, supplement, inFlight;
    private bool disposed;
    private enum QueueKind { Primary, Notice, Supplement }

    private sealed record Work(string Text, QueueKind Kind)
    {
        internal TaskCompletionSource<bool> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    internal NvdaScreenReaderOutput(Func<INvdaSpeech?>? create = null, Func<bool>? canAnnounce = null)
    {
        this.create = create ?? NvdaControllerSpeech.Create;
        this.canAnnounce = canAnnounce ?? IsCurrentInputDesktop;
        worker = new(Run) { IsBackground = true, Name = "Reflection Timer NVDA feedback" };
        worker.Start();
    }

    // True means NVDA accepted the speech request, not that its synthesizer was
    // audible. A superseded/disposed request is cancelled instead of triggering
    // another route for an obsolete message. No reader or settings are changed.
    internal Task<bool> TryAnnounceAsync(string text, bool supplementary)
        => Enqueue(text, supplementary ? QueueKind.Supplement : QueueKind.Primary);

    internal Task<bool> TryAnnounceNoticeAsync(string text) => Enqueue(text, QueueKind.Notice);

    private Task<bool> Enqueue(string text, QueueKind kind)
    {
        lock (gate) { if (disposed) return Task.FromCanceled<bool>(new CancellationToken(true)); }
        if (string.IsNullOrWhiteSpace(text) || !SafeDesktop()) return Task.FromResult(false);
        var request = new Work(text, kind);
        lock (gate) {
            if (disposed) return Task.FromCanceled<bool>(new CancellationToken(true));
            if (kind == QueueKind.Supplement) { supplement?.Completion.TrySetCanceled(); supplement = request; }
            else if (kind == QueueKind.Notice) { notice?.Completion.TrySetCanceled(); notice = request; }
            else { primary?.Completion.TrySetCanceled(); primary = request; }
            available.Set();
        }
        return request.Completion.Task;
    }

    private bool SafeDesktop()
    {
        try { return canAnnounce(); } catch { return false; }
    }

    private void Run()
    {
        INvdaSpeech? speech = null;
        void Reset() { var previous = speech; speech = null; try { previous?.Dispose(); } catch { } }
        try {
            while (true) {
                available.WaitOne();
                while (true) {
                    Work? request;
                    lock (gate) {
                        if (disposed) return;
                        request = primary ?? notice ?? supplement;
                        if (primary is not null) primary = null;
                        else if (notice is not null) notice = null;
                        else supplement = null;
                        inFlight = request;
                    }
                    if (request is null) break;
                    try {
                        if (!SafeDesktop()) { request.Completion.TrySetResult(false); continue; }
                        speech ??= create();
                        if (speech is null || speech.TestIfRunning() != 0) {
                            Reset(); request.Completion.TrySetResult(false); continue;
                        }
                        lock (gate) { if (disposed) { request.Completion.TrySetCanceled(); return; } }
                        if (!SafeDesktop()) { request.Completion.TrySetResult(false); continue; }
                        var accepted = (request.Kind == QueueKind.Primary
                            ? speech.SpeakImportantText(request.Text) : speech.SpeakText(request.Text)) == 0;
                        if (!accepted) { Reset(); request.Completion.TrySetResult(false); continue; }
                        request.Completion.TrySetResult(true);
                        // Braille uses the reader's own display and preferences;
                        // its absence/failure never negates accepted speech.
                        lock (gate) { if (disposed) return; }
                        if (SafeDesktop()) { try { speech.BrailleMessage(request.Text); } catch { } }
                    } catch { Reset(); request.Completion.TrySetResult(false); }
                    finally { lock (gate) { if (ReferenceEquals(inFlight, request)) inFlight = null; } }
                }
            }
        } finally { Reset(); available.Dispose(); }
    }

    public void Dispose()
    {
        lock (gate) {
            if (disposed) return;
            disposed = true;
            primary?.Completion.TrySetCanceled(); notice?.Completion.TrySetCanceled(); supplement?.Completion.TrySetCanceled(); inFlight?.Completion.TrySetCanceled();
            primary = notice = supplement = null;
            available.Set();
        }
        // A blocked RPC must not delay app shutdown. The background worker owns
        // and unloads the library only when its outstanding call returns.
    }

    internal static string LibraryPath => Path.Combine(AppContext.BaseDirectory, "nvda", "nvdaControllerClient.dll");

    internal static int SpeakImportant(string text, Func<string, int> plain, Func<string, int>? markup)
    {
        string ssml;
        try {
            // Write text as XML content, not markup. In particular window/site
            // names cannot inject SSML, and malformed XML characters are rejected.
            var buffer = new StringBuilder();
            using (var writer = XmlWriter.Create(buffer, new() { OmitXmlDeclaration = true, CheckCharacters = true, NewLineHandling = NewLineHandling.Entitize })) {
                writer.WriteStartElement("speak"); writer.WriteString(text); writer.WriteEndElement();
            }
            ssml = buffer.ToString();
        } catch (Exception error) when (error is XmlException or ArgumentException) { return 87; } // ERROR_INVALID_PARAMETER
        if (markup is null) return plain(text);
        var result = markup(ssml);
        // The stable v1 interface remains usable with older NVDA servers.
        return result is 1717 or 1745 ? plain(text) : result; // RPC_S_UNKNOWN_IF / RPC_S_PROCNUM_OUT_OF_RANGE
    }

    // Direct controller output must not expose a private session while Windows
    // is locked or displaying a secure desktop. These APIs only inspect names.
    internal static bool IsCurrentInputDesktop()
    {
        var input = OpenInputDesktop(0, false, 0x0001); // DESKTOP_READOBJECTS
        if (input == 0) return false;
        try {
            var current = GetThreadDesktop(GetCurrentThreadId());
            var inputName = DesktopName(input);
            return inputName.Equals("Default", StringComparison.OrdinalIgnoreCase)
                && inputName.Equals(DesktopName(current), StringComparison.OrdinalIgnoreCase);
        } catch { return false; }
        finally { CloseDesktop(input); }
    }

    private static string DesktopName(nint desktop)
    {
        if (desktop == 0) return "";
        var name = new StringBuilder(256);
        return GetUserObjectInformation(desktop, 2, name, name.Capacity * sizeof(char), out _) ? name.ToString() : ""; // UOI_NAME
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern nint OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll")] private static extern bool CloseDesktop(nint desktop);
    [DllImport("user32.dll")] private static extern nint GetThreadDesktop(uint thread);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetUserObjectInformation(nint handle, int index, StringBuilder information, int length, out int needed);

    private sealed class NvdaControllerSpeech : INvdaSpeech
    {
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int TestRunning();
        [UnmanagedFunctionPointer(CallingConvention.Winapi, CharSet = CharSet.Unicode)] private delegate int Speak([MarshalAs(UnmanagedType.LPWStr)] string text);
        [UnmanagedFunctionPointer(CallingConvention.Winapi, CharSet = CharSet.Unicode)]
        private delegate int SpeakSsml([MarshalAs(UnmanagedType.LPWStr)] string text, int symbolLevel, int priority, [MarshalAs(UnmanagedType.I1)] bool asynchronous);
        private readonly nint library;
        private readonly TestRunning test;
        private readonly Speak speak;
        private readonly Speak? braille;
        private readonly SpeakSsml? markup;
        private NvdaControllerSpeech(nint library, TestRunning test, Speak speak, Speak? braille, SpeakSsml? markup)
        { this.library = library; this.test = test; this.speak = speak; this.braille = braille; this.markup = markup; }
        internal static INvdaSpeech? Create()
        {
            // Pin discovery to our installed x64 library; never search the
            // working directory, PATH, or another app's NVDA installation.
            if (!File.Exists(LibraryPath) || !NativeLibrary.TryLoad(LibraryPath, typeof(NvdaScreenReaderOutput).Assembly,
                DllImportSearchPath.UseDllDirectoryForDependencies | DllImportSearchPath.System32, out var library)) return null;
            try {
                var test = Marshal.GetDelegateForFunctionPointer<TestRunning>(NativeLibrary.GetExport(library, "nvdaController_testIfRunning"));
                var speak = Marshal.GetDelegateForFunctionPointer<Speak>(NativeLibrary.GetExport(library, "nvdaController_speakText"));
                var braille = NativeLibrary.TryGetExport(library, "nvdaController_brailleMessage", out var address)
                    ? Marshal.GetDelegateForFunctionPointer<Speak>(address) : null;
                var markup = NativeLibrary.TryGetExport(library, "nvdaController_speakSsml", out var ssmlAddress)
                    ? Marshal.GetDelegateForFunctionPointer<SpeakSsml>(ssmlAddress) : null;
                return new NvdaControllerSpeech(library, test, speak, braille, markup);
            } catch { NativeLibrary.Free(library); throw; }
        }
        public int TestIfRunning() => test();
        public int SpeakText(string text) => speak(text);
        public int SpeakImportantText(string text) => SpeakImportant(text, value => speak(value),
            markup is null ? null : value => markup(value, -1, 2, true)); // Unchanged symbol level, NOW priority, asynchronous RPC.
        public int BrailleMessage(string text) => braille?.Invoke(text) ?? 120; // ERROR_CALL_NOT_IMPLEMENTED
        public void Dispose() => NativeLibrary.Free(library);
    }
}
