using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Web.WebView2.WinForms;
using Microsoft.Web.WebView2.Core;
using ReflectionTimer.Accessible;
using ReflectionTimer.Core;
using ReflectionTimer.Desktop;

// Opt-in real WebView/native-window regression. Only a synthetic external editor,
// isolated profile, muted audio and fake global hotkeys; never uses personal apps.
static class NativeGlobalPlaybackFocusSmoke
{
    internal static void RunInteractive()
    {
        Exception? failure = null;
        var thread = new Thread(() => {
            Application.EnableVisualStyles();
            using var launcher = new Form { Text = "Synthetic global playback focus checks", Size = new(460, 160), StartPosition = FormStartPosition.CenterScreen };
            var button = new Button { Text = "Run global playback focus checks", Dock = DockStyle.Fill };
            button.Click += (_, _) => { try { Run(); } catch (Exception error) { failure = error; } finally { launcher.Close(); } };
            launcher.Controls.Add(button); Application.Run(launcher);
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure is not null) throw new Exception("Interactive global playback focus checks failed", failure);
    }
    internal static void Run()
    {
        Exception? failure = null; var passed = 0; var issues = new List<string>();
        void Check(bool condition, string name) { if (condition) { passed++; Console.WriteLine("PASS " + name); } else { issues.Add(name); Console.WriteLine("FAIL " + name); } }
        var thread = new Thread(() => {
            PreviewApplication? app = null;
            try {
                Application.SetHighDpiMode(HighDpiMode.PerMonitorV2); Application.EnableVisualStyles();
                var now = DateTimeOffset.Now;
                var store = new MemoryStore { State = new() { LoggingEnabled = false, VoiceAnnouncements = false,
                    Csv = new() { Enabled = false }, AutoSendIncompleteReflections = false,
                    ShowAppView = true, ShowFloatingTimer = false, Timer = new() { Volume = 0 } } };
                var session = new PreviewSession(store, () => now, isolatedProfile: true);
                using var external = new Form { Text = "Synthetic external playback editor", Size = new(460, 160), StartPosition = FormStartPosition.CenterScreen };
                using var editor = new TextBox { Text = "External input focus must stay here.", Dock = DockStyle.Fill, Multiline = true };
                external.Controls.Add(editor);
                var focusLosses = 0; editor.LostFocus += (_, _) => focusLosses++;
                app = new(session, Path.Combine(Path.GetTempPath(), "ReflectionTimer-GlobalPlaybackFocus-" + Guid.NewGuid().ToString("N")),
                    startInTray: false, profileName: "global-playback-focus", shortcutRegistration: new Registration(), screenReaderNotification: (_, _, _) => true);
                var observed = new HashSet<WebView2>();
                void ObserveBrowsers()
                {
                    foreach (var window in Windows(app)) {
                        var view = (WebView2)Field(window, "browser");
                        if (!observed.Add(view)) continue;
                        view.CoreWebView2InitializationCompleted += (_, result) => {
                            if (!result.IsSuccess) { Console.WriteLine("DIAGNOSTIC initialization failure: " + result.InitializationException); return; }
                            view.CoreWebView2.ProcessFailed += (_, error) => Console.WriteLine($"DIAGNOSTIC {window.View} process failure: kind={error.ProcessFailedKind}, reason={error.Reason}, exit={error.ExitCode}");
                        };
                    }
                }
                ObserveBrowsers(); session.Engine.Changed += ObserveBrowsers;
                ((System.Windows.Forms.Timer)Field(app, "pulse")).Stop(); _ = app.MainForm!.Handle;
                app.MainForm.BeginInvoke(async () => {
                    try {
                        (await app.BrowserEnvironmentAsync()).BrowserProcessExited += (_, exit) => Console.WriteLine("DIAGNOSTIC browser process exited: " + exit.BrowserProcessExitKind);
                        var keys = (PreviewShortcuts)Field(app, "shortcuts");
                        PreviewWindow? Compact() => Windows(app).FirstOrDefault(w => w.View == "compact");
                        async Task FocusEditor()
                        {
                            external.Show(); WindowActivation.Focus(external); editor.Focus(); editor.Select(9, 5);
                            try { await Until(() => Task.FromResult(ExternalFocused(external, editor))); }
                            catch (Exception error) { throw new Exception($"Synthetic editor could not obtain initial focus: foreground={GetForegroundWindow()}, editor-window={external.Handle}, keyboard={KeyboardFocus(external.Handle)}, editor={editor.Handle}, focused={editor.Focused}", error); }
                        }
                        async Task Invoke(int chord, bool running, string label, bool? tiny = null, bool? visible = null)
                        {
                            await FocusEditor();
                            var foreground = GetForegroundWindow(); var nativeFocus = KeyboardFocus(external.Handle); var lost = focusLosses;
                            Check(keys.Dispatch(GlobalShortcut.HotKeyMessage, chord), label + ": production global shortcut dispatches");
                            Check(session.Engine.CurrentTimer.IsRunning == running, label + ": timer state changes synchronously");
                            void Preserved(string phase) => Check(GetForegroundWindow() == foreground && KeyboardFocus(external.Handle) == nativeFocus
                                && ExternalFocused(external, editor) && editor.SelectionStart == 9 && editor.SelectionLength == 5 && focusLosses == lost,
                                label + ": external foreground, input focus and selection survive " + phase + $" (foreground={GetForegroundWindow()}, expected={foreground}; keyboard={KeyboardFocus(external.Handle)}, expected={nativeFocus}; losses={focusLosses-lost})");
                            Preserved("the synchronous action");
                            if (Compact() is { Visible: true } compact) {
                                await Ready(compact);
                                await Until(async () => await Bool(compact, $"document.querySelector('#toggle').getAttribute('aria-label').startsWith('{(running ? "Pause" : "Resume")}')"));
                                if (tiny is { } layout) {
                                    try { await Layout(compact, layout); }
                                    catch (Exception error) { throw new Exception(label + ": layout update timed out; native=" + compact.IsTimeOnly + "; DOM=" + await Script(compact, "JSON.stringify({tiny:document.body.dataset.tiny,label:document.querySelector('#toggle').getAttribute('aria-label'),error:document.querySelector('#error').textContent})"), error); }
                                }
                            }
                            // Renderer messages, measurement/resize and deferred DOM focus all
                            // get time to run; LostFocus also catches a transient steal/restore.
                            await Task.Delay(300); Preserved("the WebView and resize updates");
                            if (tiny is { } expectedTiny) Check(Compact()?.IsTimeOnly == expectedTiny, label + ": floating layout is preserved");
                            if (visible is { } expectedVisible) Check(Compact()?.Visible == expectedVisible, label + ": intended visibility is preserved");
                        }
                        await FocusEditor(); var startupLosses = focusLosses;
                        await Ready((PreviewWindow)app.MainForm); await Task.Delay(300);
                        Check(ExternalFocused(external, editor) && focusLosses == startupLosses,
                            "Cold App initialization does not reclaim external input focus after the user switches away");
                        app.HideAppView();
                        foreach (var chord in new[] { GlobalShortcut.TimerToggleId, GlobalShortcut.TimerToggleAltId }) {
                            var name = chord == GlobalShortcut.TimerToggleId ? "Ctrl+Space" : "Ctrl+Alt+Space";
                            session.Engine.SwitchMode(SessionMode.Timer); session.Engine.Reset(); session.SetDurationDraft(["0", "0", "0"]);
                            await FocusEditor(); var lost = focusLosses;
                            Check(keys.Dispatch(GlobalShortcut.HotKeyMessage, chord), name + ": invalid duration dispatches through production error handler");
                            await Task.Delay(300);
                            Check(ExternalFocused(external, editor) && focusLosses == lost && !app.MainForm.Visible && !session.Engine.CurrentTimer.IsRunning,
                                name + ": invalid duration reports failure without showing App or stealing focus");
                            if (app.MainForm.Visible) app.HideAppView();
                            session.SetDurationDraft(["0", "2", "0"]); await FocusEditor(); lost = focusLosses;
                            store.Fail = true;
                            try { Check(keys.Dispatch(GlobalShortcut.HotKeyMessage, chord), name + ": failed storage dispatches through production error handler"); }
                            finally { store.Fail = false; }
                            await Task.Delay(300);
                            Check(ExternalFocused(external, editor) && focusLosses == lost && !app.MainForm.Visible && !session.Engine.CurrentTimer.IsRunning,
                                name + ": failed storage retains timer and external focus without showing App");
                            if (app.MainForm.Visible) app.HideAppView();
                        }
                        foreach (var chord in new[] { GlobalShortcut.TimerToggleId, GlobalShortcut.TimerToggleAltId }) foreach (var popups in new[] { false, true }) {
                            session.Engine.SetViewerAutoHide(false, 1); session.Engine.SwitchMode(SessionMode.Timer); session.Engine.Reset();
                            session.Engine.SetSessionEndPopups(popups); session.Engine.Start(2, false, 0, lowTime: new() { Enabled = false });
                            await FocusEditor(); var lost = focusLosses; var foreground = GetForegroundWindow(); var keyboard = KeyboardFocus(external.Handle);
                            now = now.AddSeconds(2);
                            var label = (chord == GlobalShortcut.TimerToggleId ? "Ctrl+Space" : "Ctrl+Alt+Space") + "/exact-deadline/popups-" + popups;
                            Check(keys.Dispatch(GlobalShortcut.HotKeyMessage, chord), label + ": shortcut handles the completion boundary");
                            Check(GetForegroundWindow() == foreground && KeyboardFocus(external.Handle) == keyboard && focusLosses == lost,
                                label + ": synchronous completion preserves external foreground and keyboard focus");
                            Check(!session.Engine.CurrentTimer.IsRunning && session.Engine.Snapshot.Prompts.Count == 1,
                                label + ": completion saves one reflection without starting a new session");
                            if (popups) {
                                await Until(() => Task.FromResult(Windows(app).Any(w => w.View == "reflection" && w.Visible)));
                                await Ready(Windows(app).Single(w => w.View == "reflection"));
                            }
                            await Task.Delay(350);
                            Check(ExternalFocused(external, editor) && focusLosses == lost && editor.SelectionStart == 9 && editor.SelectionLength == 5,
                                label + ": deferred reflection loading/showing preserves the external editor");
                            Check(Windows(app).Any(w => w.View == "reflection" && w.Visible) == popups,
                                label + ": completion respects the popup preference");
                            foreach (var reflection in Windows(app).Where(w => w.View == "reflection").ToArray()) reflection.CloseAfterSave();
                            foreach (var prompt in session.Engine.Snapshot.Prompts) session.Engine.SkipPrompt(prompt.Id);
                        }
                        foreach (var mode in Enum.GetValues<SessionMode>()) foreach (var chord in new[] { GlobalShortcut.TimerToggleId, GlobalShortcut.TimerToggleAltId }) {
                            var name = mode + "/" + (chord == GlobalShortcut.TimerToggleId ? "Ctrl+Space" : "Ctrl+Alt+Space");
                            foreach (var tiny in new[] { false, true }) {
                                session.Engine.SetViewerAutoHide(false, 1); session.Engine.SwitchMode(mode); session.Engine.Reset(); session.SetDurationDraft(["0", "2", "0"]);
                                session.Engine.SetAlwaysOnTop(!tiny, tiny, true);
                                app.Open("compact"); var compact = Compact()!; await Ready(compact); compact.SetCompactMode(tiny, false); await Layout(compact, tiny);
                                Check(NativeTopMost(compact) == (tiny ? session.Engine.SettingsSnapshot.TimeOnlyAlwaysOnTop : session.Engine.SettingsSnapshot.CompactAlwaysOnTop),
                                    name + ": initial native topmost style follows the chosen floating layout");
                                var label = name + "/" + (tiny ? "Time-only" : "Compact");
                                await Invoke(chord, true, label + "/start", true, true);
                                compact.SetCompactMode(tiny, false); await Layout(compact, tiny);
                                await Invoke(chord, false, label + "/pause", tiny, true);
                                await Invoke(chord, true, label + "/resume", true, true);
                                Check(NativeTopMost(compact) == session.Engine.SettingsSnapshot.TimeOnlyAlwaysOnTop,
                                    name + ": automatic shrink updates native topmost style without activation");
                            }
                            session.Engine.SetViewerAutoHide(false, 1); session.Engine.Reset();
                            session.Engine.SetFloatingTimer(false); app.ApplyDisplayPreferences();
                            await Invoke(chord, true, name + "/manually-hidden/start", visible: false);
                            await Invoke(chord, false, name + "/manually-hidden/pause", visible: false);
                            await Invoke(chord, true, name + "/manually-hidden/resume", visible: false);
                        }
                        // Each cold case starts a fresh tray app with no Compact ever
                        // created. Normal close/hide reuses the controller in production;
                        // forced teardown/recreation would instead stress WebView lifetime.
                        foreach (var mode in Enum.GetValues<SessionMode>()) foreach (var chord in new[] { GlobalShortcut.TimerToggleId, GlobalShortcut.TimerToggleAltId }) foreach (var tiny in new[] { false, true })
                            await Task.Run(() => ColdViewer(Check, mode, chord, tiny));
                        Check(!app.MainForm.Visible && session.Engine.Snapshot.Connection.WebAppUrl.Length == 0 && session.Engine.Snapshot.Outbox.Count == 0,
                            "Global playback never shows App view, submits a response or configures a destination");
                        if (issues.Count > 0) throw new Exception(string.Join(Environment.NewLine, issues));
                    } catch (Exception error) { failure = error; }
                    finally { external.Close(); await app.CloseMainAsync(); }
                });
                Application.Run(app);
            } catch (Exception error) { failure = error; }
            finally { app?.Dispose(); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(110))) throw new TimeoutException("Global playback focus checks timed out.");
        if (failure is not null) throw new Exception("Native global playback focus checks failed", failure);
        Console.WriteLine($"{passed} native global playback focus checks passed.");
    }
    private static void ColdViewer(Action<bool, string> check, SessionMode mode, int chord, bool tiny)
    {
        Exception? failure = null;
        var thread = new Thread(() => {
            PreviewApplication? app = null;
            try {
                var store = new MemoryStore { State = new() { LoggingEnabled = false, VoiceAnnouncements = false,
                    Csv = new() { Enabled = false }, AutoSendIncompleteReflections = false, ShowAppView = false,
                    ShowFloatingTimer = false, ViewerAutoHide = true, ViewerAutoHideSeconds = 1, FloatingTimeOnly = tiny,
                    CompactAlwaysOnTop = !tiny, TimeOnlyAlwaysOnTop = tiny, Timer = new() { Volume = 0 } } };
                var now = DateTimeOffset.Now; var session = new PreviewSession(store, () => now, isolatedProfile: true);
                session.Engine.SwitchMode(mode);
                if (mode == SessionMode.Timer) session.Engine.Start(120, false, 0); else session.Engine.StartStopwatch();
                using var external = new Form { Text = "Synthetic cold-viewer external editor", Size = new(460, 160), StartPosition = FormStartPosition.CenterScreen };
                using var editor = new TextBox { Text = "External input focus must stay here.", Multiline = true, Dock = DockStyle.Fill };
                external.Controls.Add(editor); var losses = 0; editor.LostFocus += (_, _) => losses++;
                app = new(session, Path.Combine(Path.GetTempPath(), "ReflectionTimer-ColdPlaybackFocus-" + Guid.NewGuid().ToString("N")),
                    startInTray: true, profileName: "cold-playback-focus", shortcutRegistration: new Registration(), screenReaderNotification: (_, _, _) => true);
                ((System.Windows.Forms.Timer)Field(app, "pulse")).Stop(); _ = app.MainForm!.Handle;
                app.MainForm.BeginInvoke(async () => {
                    try {
                        var label = mode + "/" + (chord == GlobalShortcut.TimerToggleId ? "Ctrl+Space" : "Ctrl+Alt+Space") + "/cold-hidden/" + (tiny ? "Time-only" : "Compact");
                        check(Windows(app).All(w => w.View != "compact"), label + ": tray start has no floating controller");
                        external.Show(); WindowActivation.Focus(external); editor.Focus(); editor.Select(9, 5);
                        await Until(() => Task.FromResult(ExternalFocused(external, editor)));
                        var foreground = GetForegroundWindow(); var keyboard = KeyboardFocus(external.Handle); var before = losses;
                        void Preserved(string phase) => check(GetForegroundWindow() == foreground && KeyboardFocus(external.Handle) == keyboard
                            && editor.SelectionStart == 9 && editor.SelectionLength == 5 && losses == before,
                            label + ": external foreground, native child focus and selection survive " + phase);
                        var keys = (PreviewShortcuts)Field(app, "shortcuts");
                        check(keys.Dispatch(GlobalShortcut.HotKeyMessage, chord), label + ": production pause dispatches");
                        Preserved("synchronous pause and cold show");
                        var compact = Windows(app).Single(w => w.View == "compact");
                        await Ready(compact); await Layout(compact, tiny); await Task.Delay(300);
                        Preserved("cold WebView initialization and resize");
                        check(!session.Engine.CurrentTimer.IsRunning && compact.Visible && NativeTopMost(compact),
                            label + ": pause restores the saved layout with its native topmost style");
                        check(keys.Dispatch(GlobalShortcut.HotKeyMessage, chord), label + ": production resume dispatches");
                        Preserved("synchronous resume");
                        await Layout(compact, true); await Task.Delay(300); Preserved("resume WebView updates and shrink");
                        check(session.Engine.CurrentTimer.IsRunning && NativeTopMost(compact) == store.State.TimeOnlyAlwaysOnTop,
                            label + ": resume keeps running and applies the Time-only topmost preference");
                        await Until(() => Task.FromResult(!compact.Visible)); Preserved("resumed auto-hide");
                        check(!app.MainForm.Visible && store.State.Outbox.Count == 0 && !store.State.Csv.Enabled,
                            label + ": the cold lifecycle never shows App, sends or exports data");
                    } catch (Exception error) { failure = error; }
                    finally { external.Close(); await app.CloseMainAsync(); }
                });
                Application.Run(app);
            } catch (Exception error) { failure = error; }
            finally { app?.Dispose(); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(40))) throw new TimeoutException("Cold global playback focus check timed out.");
        if (failure is not null) throw new Exception("Cold global playback focus check failed", failure);
    }
    private static bool ExternalFocused(Form window, TextBox editor) => GetForegroundWindow() == window.Handle && KeyboardFocus(window.Handle) == editor.Handle && editor.Focused;
    private static nint KeyboardFocus(nint window)
    {
        var info = new GuiThreadInfo { Size = Marshal.SizeOf<GuiThreadInfo>() };
        return GetGUIThreadInfo(GetWindowThreadProcessId(window, out _), ref info) ? info.Focus : 0;
    }
    private static bool NativeTopMost(Form window) => (GetWindowLongPtr(window.Handle, -20).ToInt64() & 8) != 0;
    private static object Field(object value, string name) => value.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(value)!;
    private static List<PreviewWindow> Windows(PreviewApplication app) => (List<PreviewWindow>)Field(app, "windows");
    private static Task Ready(PreviewWindow window) => ((TaskCompletionSource)Field(window, "interfaceReady")).Task.WaitAsync(TimeSpan.FromSeconds(20));
    private static Task<string> Script(PreviewWindow window, string script) => ((WebView2)Field(window, "browser")).CoreWebView2.ExecuteScriptAsync(script);
    private static async Task<bool> Bool(PreviewWindow window, string script) { using var result = JsonDocument.Parse(await Script(window, script)); return result.RootElement.GetBoolean(); }
    private static Task Layout(PreviewWindow window, bool tiny) => Until(async () => window.IsTimeOnly == tiny && await Bool(window, "document.body.dataset.tiny===" + JsonSerializer.Serialize(tiny ? "true" : "false")));
    private static async Task Until(Func<Task<bool>> condition) { using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(12)); while (!await condition()) await Task.Delay(25, limit.Token); }
    private sealed class Registration : IHotKeyRegistration
    {
        public bool Register(nint window, int id, uint modifiers, uint key) => true;
        public bool Unregister(nint window, int id) => true;
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
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint window, int index);
}
