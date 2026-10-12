using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using ReflectionTimer.Accessible;
using ReflectionTimer.Core;
using ReflectionTimer.Desktop;

// Exercise production shortcut and native notification routing with fake hotkey
// registration, isolated memory, muted audio, and hidden forms. No physical keys
// are sent and no personal app or profile is driven.
static class NativeTimerStatusSmoke
{
    internal static void Run()
    {
        Exception? failure = null; var passed = 0;
        void Check(bool value, string name) { if (!value) throw new Exception(name); passed++; Console.WriteLine("PASS " + name); }
        var thread = new Thread(() => {
            try {
                var now = DateTimeOffset.Now;
                var store = new MemoryStore { State = new() { LoggingEnabled = false, SessionEndPopups = false,
                    AutoSendIncompleteReflections = false, ShowAppView = false, ShowFloatingTimer = false,
                    ViewerAutoHide = false, VoiceAnnouncements = false, Csv = new() { Enabled = false }, Timer = new() { Volume = 0 } } };
                var session = new PreviewSession(store, () => now, isolatedProfile: true);
                var messages = new List<(string Text, bool Supplementary, bool Accepted)>(); var speech = new Speech();
                using var app = new PreviewApplication(session, Path.Combine(Path.GetTempPath(), "ReflectionTimer-StatusReader-" + Guid.NewGuid().ToString("N")),
                    startInTray: true, profileName: "status-reader-smoke", shortcutRegistration: new Registration(), speech: speech,
                    screenReaderNotification: (provider, text, extra) => {
                        var accepted = ScreenReaderAnnouncements.TryAnnounce(provider, text, extra);
                        messages.Add((text, extra, accepted)); return accepted;
                    });
                ((System.Windows.Forms.Timer)Field(app, "pulse")).Stop();
                _ = app.MainForm!.Handle; // Create the hidden notification provider before measuring layout.
                var keys = (PreviewShortcuts)Field(app, "shortcuts");
                var changes = 0; session.Engine.Changed += () => changes++;
                string Windows() => JsonSerializer.Serialize(((List<PreviewWindow>)Field(app, "windows"))
                    .Select(w => new { w.View, w.Visible, w.Bounds, w.IsTimeOnly, w.WindowState }));
                void Read(string expected, string label, TimeSpan queueDelay = default)
                {
                    foreach (var (voice, volume) in new[] { (false, 0), (true, 0), (true, 55) }) {
                        session.Engine.SetVoiceAnnouncements(voice); session.Engine.SetAppVolume(volume);
                        messages.Clear(); speech.Messages.Clear();
                        var before = JsonSerializer.Serialize(session.Engine.Snapshot); var changed = changes;
                        var windowState = Windows(); var foreground = GetForegroundWindow(); var keyboard = KeyboardFocus(foreground);
                        var name = label + "/voice-" + voice + "/volume-" + volume;
                        Check(keys.Dispatch(GlobalShortcut.HotKeyMessage, GlobalShortcut.TimerStatusId, queueDelay), name + ": Ctrl+Alt+Space dispatches through production registration");
                        Check(messages.SequenceEqual(new[] { (expected, false, true) }), name + ": hidden native reader provider accepts exactly one accurate status message");
                        Check(speech.Messages.SequenceEqual(voice && volume > 0 ? new[] { expected } : Array.Empty<string>()),
                            name + ": optional voice follows opt-out and mute independently of screen-reader feedback");
                        Check(JsonSerializer.Serialize(session.Engine.Snapshot) == before && changes == changed && Windows() == windowState
                            && ((List<PreviewWindow>)Field(app, "windows")).All(w => !w.Visible),
                            name + ": reading leaves timer, reflection drafts, Outbox, hidden viewers and layout untouched");
                        Check(GetForegroundWindow() == foreground && KeyboardFocus(foreground) == keyboard,
                            name + ": foreground window and its native keyboard focus remain unchanged");
                    }
                    // Unmuting is only for the injected speech channel while
                    // querying. Keep every later lifecycle/audio event muted.
                    session.Engine.SetAppVolume(0); session.Engine.SetVoiceAnnouncements(false);
                }
                Read("Timer ready, 15 minutes set.", "Timer ready");
                session.Engine.Start(125, false, 0); now = now.AddMilliseconds(60500);
                Read("Timer running, 1 minute 5 seconds remaining.", "Timer running", TimeSpan.FromSeconds(8));
                session.Engine.Pause(); now = now.AddMinutes(10);
                Read("Timer paused, 1 minute 5 seconds remaining.", "Timer paused");
                session.Engine.Resume(); now = now.AddMilliseconds(64500); session.Tick();
                Read("Timer finished, 2 minutes 5 seconds set.", "Timer finished");
                session.Engine.SwitchMode(SessionMode.Stopwatch);
                Read("Stopwatch ready, 0 seconds elapsed.", "Stopwatch ready");
                session.Engine.StartStopwatch(); now = now.AddMilliseconds(241500);
                Read("Stopwatch running, 4 minutes 1 second elapsed.", "Stopwatch running", TimeSpan.FromSeconds(8));
                session.Engine.Pause(); now = now.AddMinutes(10);
                Read("Stopwatch paused, 4 minutes 1 second elapsed.", "Stopwatch paused");
                var prompt = session.Engine.ReviewStopwatch(); session.Engine.QueueReflection(prompt, "Synthetic status test", localOnly: true);
                Read("Stopwatch finished, 0 seconds elapsed.", "Stopwatch finished");
                // The separate transport shortcut must keep its old behavior.
                session.Engine.SetVoiceAnnouncements(false); session.Engine.SetAppVolume(0);
                session.Engine.SwitchMode(SessionMode.Timer); session.Engine.Reset(90);
                Check(keys.Dispatch(GlobalShortcut.HotKeyMessage, GlobalShortcut.TimerToggleId) && session.Engine.CurrentTimer.IsRunning,
                    "Ctrl+Space still starts a ready timer after Ctrl+Alt+Space is remapped");
                now = now.AddSeconds(3);
                Check(keys.Dispatch(GlobalShortcut.HotKeyMessage, GlobalShortcut.TimerToggleId)
                    && TimerEngine.IsPaused(session.Engine.CurrentTimer) && session.Engine.CurrentTimer.RemainingSeconds == 87,
                    "Ctrl+Space still pauses the running timer with its committed remaining time");
                Check(keys.Dispatch(GlobalShortcut.HotKeyMessage, GlobalShortcut.TimerToggleId) && session.Engine.CurrentTimer.IsRunning,
                    "Ctrl+Space still resumes the existing session");
                Check(!app.MainForm!.Visible && store.State.Connection.WebAppUrl.Length == 0 && !store.State.Csv.Enabled
                    && store.State.Outbox.All(o => o.LocalOnly), "Status and playback tests remain hidden, isolated and disconnected from all logging destinations");
                app.MainForm.Dispose();
            } catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(35))) throw new TimeoutException("Native timer status checks timed out.");
        if (failure is not null) throw new Exception("Native timer status checks failed", failure);
        Console.WriteLine($"{passed} native timer status checks passed; actual screen-reader speech is not automated.");
    }
    private static object Field(object value, string name) => value.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(value)!;
    private static nint KeyboardFocus(nint window)
    {
        var info = new GuiThreadInfo { Size = Marshal.SizeOf<GuiThreadInfo>() };
        return GetGUIThreadInfo(GetWindowThreadProcessId(window, out _), ref info) ? info.Focus : 0;
    }
    private sealed class Registration : IHotKeyRegistration
    {
        public bool Register(nint window, int id, uint modifiers, uint key) => true;
        public bool Unregister(nint window, int id) => true;
    }
    private sealed class Speech : IVoiceOutput
    {
        internal readonly List<string> Messages = [];
        public void Speak(string text, int volume) => Messages.Add(text);
        public void SetVolume(int volume) { }
        public void Stop() { }
        public bool TakeFailure() => false;
        public void Dispose() { }
    }
    [StructLayout(LayoutKind.Sequential)] private struct GuiThreadInfo
    {
        internal int Size, Flags;
        internal nint Active, Focus, Capture, MenuOwner, MoveSize, Caret;
        internal int Left, Top, Right, Bottom;
    }
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);
}
