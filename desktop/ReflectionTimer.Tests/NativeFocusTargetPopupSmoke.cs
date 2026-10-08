using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Web.WebView2.WinForms;
using ReflectionTimer.Accessible;
using ReflectionTimer.Core;
using ReflectionTimer.Desktop;

// Real native popup and timer viewers, with a separate synthetic input queue.
// No browser discovery, personal profile, real registrations or delivery.
static class NativeFocusTargetPopupSmoke
{
    internal static void RunInteractive()
    {
        Exception? failure = null;
        var thread = new Thread(() => {
            Application.EnableVisualStyles();
            using var launcher = new Form { Text = "Synthetic Focus target popup checks", Size = new(460, 150), StartPosition = FormStartPosition.CenterScreen };
            launcher.Shown += (_, _) => launcher.BeginInvoke(() => WindowActivation.Focus(launcher));
            var run = new Button { Text = "Run Focus target popup checks", Dock = DockStyle.Fill };
            run.Click += (_, _) => { try { Run(); } catch (Exception error) { failure = error; } finally { launcher.Close(); } };
            launcher.Controls.Add(run); Application.Run(launcher);
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure is not null) throw new Exception("Interactive Focus target popup checks failed", failure);
    }
    internal static void Run()
    {
        Exception? failure = null; var passed = 0;
        void Check(bool value, string message) { if (!value) throw new Exception(message); passed++; Console.WriteLine("PASS " + message); }
        var thread = new Thread(() => {
            PreviewApplication? app = null;
            try {
                Application.SetHighDpiMode(HighDpiMode.PerMonitorV2); Application.EnableVisualStyles();
                var store = new MemoryStore { State = new() { Csv = new() { Enabled = false }, AutoSendIncompleteReflections = false,
                    LoggingEnabled = false, VoiceAnnouncements = false, ShowAppView = false, ShowFloatingTimer = false, Timer = new() { Volume = 0 } } };
                var session = new PreviewSession(store, isolatedProfile: true);
                using var source = new ExternalEditor();
                app = new(session, Path.Combine(Path.GetTempPath(), "ReflectionTimer-FocusPopup-" + Guid.NewGuid().ToString("N")),
                    startInTray: true, profileName: "focus-popup", shortcutRegistration: new Registration(), screenReaderNotification: (_, _, _) => true);
                ((System.Windows.Forms.Timer)Field(app, "pulse")).Stop();
                ((System.Windows.Forms.Timer)Field(app, "focusPulse")).Stop(); // No native inventory polling in this synthetic test.
                _ = app.MainForm!.Handle;
                app.MainForm.BeginInvoke(async () => {
                    try {
                        await source.Ready;
                        var window = new FocusTarget(Guid.NewGuid(), FocusTargetKind.Window, "Synthetic browser", "chrome", source.Handle.ToInt64(), Environment.ProcessId, 123) { WindowName = "Synthetic browser" };
                        FocusTarget[] choices = [window,
                            window with { Id = Guid.NewGuid(), Kind = FocusTargetKind.BrowserTab, Name = "Synthetic tab", TabRuntimeId = "tab" },
                            window with { Id = Guid.NewGuid(), Kind = FocusTargetKind.BrowserTabGroup, Name = "Synthetic group", TabRuntimeId = "group" },
                            window with { Id = Guid.NewGuid(), Kind = FocusTargetKind.Site, Name = "study.example", SiteHost = "study.example" }];
                        var activations = 0; var watched = new HashSet<PreviewWindow>();
                        void WatchViews() { foreach (var view in Windows(app)) if (watched.Add(view)) view.Activated += (_, _) => activations++; }
                        WatchViews();
                        foreach (var viewState in new[] { "cold tray", "hidden App", "minimized App", "App", "Compact", "Time-only", "reflection" }) {
                            foreach (var reflection in Windows(app).Where(w => w.View == "reflection")) reflection.CloseAfterSave();
                            if (viewState != "cold tray") {
                                app.Open("main"); await Ready((PreviewWindow)app.MainForm);
                                if (viewState == "minimized App") app.MainForm.WindowState = FormWindowState.Minimized;
                                else if (viewState != "App") app.HideAppView();
                            }
                            session.Engine.SetFloatingTimer(false); app.ApplyDisplayPreferences();
                            if (viewState is "Compact" or "Time-only") {
                                app.Open("compact"); var compact = Windows(app).Single(w => w.View == "compact"); await Ready(compact);
                                compact.SetCompactMode(viewState == "Time-only", false);
                                await Until(async () => await Bool(compact, "document.body.dataset.tiny===" + JsonSerializer.Serialize(viewState == "Time-only" ? "true" : "false")));
                            }
                            if (viewState == "reflection") {
                                var id = session.Engine.TestPrompt(); app.Open("reflection", id);
                                await Until(() => Task.FromResult(Windows(app).Any(w => w.ReflectionOpen && w.Visible)));
                                await Ready(Windows(app).Single(w => w.ReflectionOpen));
                            }
                            WatchViews();
                            foreach (var initiallyChecked in new[] { false, true }) foreach (var selection in new[] { "Window", "Tab", "Tab Group", "Site", "Escape" }) {
                                session.Engine.SetFocusMode(new() { IdleEnabled = true, MultipleTargets = true, Targets = initiallyChecked ? [.. choices] : [] });
                                await source.FocusAsync(); await Until(source.FocusedAsync);
                                var before = Visibility(app, session); var beforeTargets = session.Engine.SettingsSnapshot.FocusMode.SelectionKey; var beforeActivations = activations;
                                var label = viewState + "/" + (initiallyChecked ? "checked" : "unchecked") + "/" + selection;
                                var pending = app.ChooseBrowserFocusTargetAsync(source.Handle, choices, [window]);
                                await Until(() => Task.FromResult(Application.OpenForms.OfType<FocusTargetToggleDialog>().Any(d => d.Visible)));
                                var dialog = Application.OpenForms.OfType<FocusTargetToggleDialog>().Single(d => d.Visible);
                                await Until(() => Task.FromResult(WindowActivation.IsForeground(dialog) && dialog.Choices[0].Focused));
                                var styles = GetWindowLongPtr(dialog.Handle, -20).ToInt64();
                                Check(dialog.ShowInTaskbar && (styles & 0x40000) != 0 && (styles & 0x08000080) == 0,
                                    label + ": native popup is selectable with Alt+Tab and exposes an active window");
                                Check(dialog.AccessibilityObject.Role == AccessibleRole.Dialog && dialog.Choices.All(c => c.AccessibilityObject.Role == AccessibleRole.CheckButton
                                    && c.AccessibilityObject.State.HasFlag(AccessibleStates.Checked) == initiallyChecked),
                                    label + ": screen readers receive native named checkboxes and saved checked states");
                                Check(Visibility(app, session) == before && activations == beforeActivations && Windows(app).All(w => w.Enabled),
                                    label + ": opening the popup neither shows, activates nor disables timer viewers");
                                var index = selection == "Escape" ? 0 : Array.IndexOf(new[] { "Window", "Tab", "Tab Group", "Site" }, selection);
                                for (var i = 0; i < index; i++) dialog.ChooseKey(Keys.Down);
                                Check(dialog.Choices[index].Focused && dialog.Choices[index].AccessibilityObject.State.HasFlag(AccessibleStates.Focused),
                                    label + ": arrow navigation focuses the corresponding native checkbox");
                                bool? closingChecked = null; bool? closingAccessibleChecked = null;
                                dialog.FormClosing += (_, _) => {
                                    closingChecked = dialog.Choices[index].Checked;
                                    closingAccessibleChecked = dialog.Choices[index].AccessibilityObject.State.HasFlag(AccessibleStates.Checked);
                                };
                                if (selection == "Escape") DialogKey(dialog, Keys.Escape); else dialog.ChooseKey(Keys.Space);
                                var result = await pending.WaitAsync(TimeSpan.FromSeconds(8));
                                Check(selection == "Escape" ? result is null : result?.Key == choices[index].Key && closingChecked == !initiallyChecked && closingAccessibleChecked == !initiallyChecked,
                                    label + ": Space confirms one native checkbox toggle, while Escape cancels");
                                await Until(source.FocusedAsync); await Task.Delay(150);
                                Check(await source.FocusedAsync() && Visibility(app, session) == before && activations == beforeActivations
                                    && session.Engine.SettingsSnapshot.FocusMode.SelectionKey == beforeTargets,
                                    label + ": closing restores the initiating editor and its selection without raising App or changing unrelated targets");
                            }
                        }
                        foreach (var key in new[] { Keys.Enter, Keys.D4, Keys.NumPad4 }) {
                            await source.FocusAsync(); await Until(source.FocusedAsync);
                            var before = Visibility(app, session); var priorActivations = activations;
                            var pending = app.ChooseBrowserFocusTargetAsync(source.Handle, choices, [window]);
                            var dialog = Application.OpenForms.OfType<FocusTargetToggleDialog>().Single(d => d.Visible);
                            await Until(() => Task.FromResult(WindowActivation.IsForeground(dialog) && dialog.Choices[0].Focused));
                            if (key == Keys.Enter) for (var i = 0; i < 3; i++) dialog.ChooseKey(Keys.Down);
                            dialog.ChooseKey(key);
                            Check((await pending.WaitAsync(TimeSpan.FromSeconds(8)))?.Kind == FocusTargetKind.Site,
                                key + ": alternative native confirmation returns Site exactly once");
                            await Until(source.FocusedAsync);
                            Check(Visibility(app, session) == before && activations == priorActivations,
                                key + ": alternative confirmation preserves timer viewer state and external focus");
                        }
                        using (var destination = new ExternalEditor()) {
                            await destination.Ready; await source.FocusAsync(); await Until(source.FocusedAsync);
                            var before = Visibility(app, session); var priorActivations = activations;
                            var pending = app.ChooseBrowserFocusTargetAsync(source.Handle, choices, [window]);
                            var dialog = Application.OpenForms.OfType<FocusTargetToggleDialog>().Single(d => d.Visible);
                            await Until(() => Task.FromResult(WindowActivation.IsForeground(dialog) && dialog.Choices[0].Focused));
                            await destination.FocusAsync(); await Until(destination.FocusedAsync);
                            dialog.ChooseKey(Keys.D4); await pending.WaitAsync(TimeSpan.FromSeconds(8)); await Task.Delay(150);
                            Check(await destination.FocusedAsync() && Visibility(app, session) == before && activations == priorActivations,
                                "Switching away while the popup is open keeps focus in the new external editor after confirmation");
                        }
                        using (var closedSource = new ExternalEditor()) {
                            await closedSource.Ready; await closedSource.FocusAsync(); await Until(closedSource.FocusedAsync);
                            var before = Visibility(app, session); var priorActivations = activations;
                            var pending = app.ChooseBrowserFocusTargetAsync(closedSource.Handle, choices, [window]);
                            var dialog = Application.OpenForms.OfType<FocusTargetToggleDialog>().Single(d => d.Visible);
                            await Until(() => Task.FromResult(WindowActivation.IsForeground(dialog) && dialog.Choices[0].Focused));
                            closedSource.Dispose(); dialog.ChooseKey(Keys.D4); await pending.WaitAsync(TimeSpan.FromSeconds(8)); await Task.Delay(150);
                            Check(Visibility(app, session) == before && activations == priorActivations,
                                "A source that closed while choosing does not cause timer viewers to be restored or activated");
                        }
                        Check(!store.State.Csv.Enabled && store.State.Outbox.Count == 0 && store.State.Connection.WebAppUrl.Length == 0,
                            "Popup checks never export, submit or connect to personal data");
                        await source.FocusAsync(); await Until(source.FocusedAsync);
                        var shutdown = app.ChooseBrowserFocusTargetAsync(source.Handle, choices, [window]);
                        var shutdownDialog = Application.OpenForms.OfType<FocusTargetToggleDialog>().Single(d => d.Visible);
                        await Until(() => Task.FromResult(WindowActivation.IsForeground(shutdownDialog) && shutdownDialog.Choices[0].Focused));
                        var closing = app.CloseMainAsync();
                        Check(await shutdown.WaitAsync(TimeSpan.FromSeconds(8)) is null && shutdownDialog.IsDisposed,
                            "Application shutdown cancels a pending popup and completes its async operation");
                        await closing;
                    } catch (Exception error) { failure = error; foreach (var dialog in Application.OpenForms.OfType<FocusTargetToggleDialog>().ToArray()) dialog.Close(); }
                    finally { if (app.MainForm is { IsDisposed: false }) await app.CloseMainAsync(); }
                });
                Application.Run(app);
            } catch (Exception error) { failure = error; }
            finally { app?.Dispose(); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(100))) throw new TimeoutException("Native Focus target popup checks timed out.");
        if (failure is not null) throw new Exception("Native Focus target popup checks failed", failure);
        Console.WriteLine($"{passed} native Focus target popup checks passed.");
    }
    private static string Visibility(PreviewApplication app, PreviewSession session) => JsonSerializer.Serialize(new {
        Views = Windows(app).Select(w => new { w.View, w.Visible, w.WindowState, w.IsTimeOnly, w.ReflectionOpen }).ToArray(),
        app.AppViewMayShow, session.Engine.SettingsSnapshot.ShowAppView, session.Engine.SettingsSnapshot.ShowFloatingTimer, session.Engine.SettingsSnapshot.FloatingTimeOnly });
    private static void DialogKey(Form dialog, Keys key) => typeof(Form).GetMethod("ProcessDialogKey", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(dialog, [key]);
    private static object Field(object value, string name) => value.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(value)!;
    private static List<PreviewWindow> Windows(PreviewApplication app) => (List<PreviewWindow>)Field(app, "windows");
    private static Task Ready(PreviewWindow window) => ((TaskCompletionSource)Field(window, "interfaceReady")).Task.WaitAsync(TimeSpan.FromSeconds(20));
    private static Task<string> Script(PreviewWindow window, string script) => ((WebView2)Field(window, "browser")).CoreWebView2.ExecuteScriptAsync(script);
    private static async Task<bool> Bool(PreviewWindow window, string script) { using var result = JsonDocument.Parse(await Script(window, script)); return result.RootElement.GetBoolean(); }
    private static async Task Until(Func<Task<bool>> condition) { using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(12)); while (!await condition()) await Task.Delay(25, limit.Token); }
    private sealed class Registration : IHotKeyRegistration
    {
        public bool Register(nint window, int id, uint modifiers, uint key) => true;
        public bool Unregister(nint window, int id) => true;
    }
    private sealed class ExternalEditor : IDisposable
    {
        private readonly Thread thread;
        private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Form window = null!; private TextBox editor = null!;
        internal ExternalEditor()
        {
            thread = new Thread(() => {
                using var form = new Form { Text = "Synthetic browser input queue", Size = new(460, 150), StartPosition = FormStartPosition.CenterScreen };
                window = form; using var input = new TextBox { Text = "Keep the original editor and selection.", Multiline = true, Dock = DockStyle.Fill }; editor = input;
                form.Controls.Add(input); form.Shown += (_, _) => ready.TrySetResult(); Application.Run(form);
            });
            thread.SetApartmentState(ApartmentState.STA); thread.Start();
        }
        internal Task Ready => ready.Task.WaitAsync(TimeSpan.FromSeconds(12));
        internal nint Handle => window.Handle;
        private Task<T> OnThread<T>(Func<T> action)
        {
            var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            window.BeginInvoke(() => { try { result.SetResult(action()); } catch (Exception error) { result.SetException(error); } }); return result.Task;
        }
        internal Task FocusAsync() => OnThread(() => { WindowActivation.Focus(window); editor.Focus(); editor.Select(9, 5); return true; });
        internal Task<bool> FocusedAsync() => OnThread(() => GetForegroundWindow() == window.Handle && GetFocus() == editor.Handle
            && editor.Focused && editor.SelectionStart == 9 && editor.SelectionLength == 5);
        public void Dispose() { if (window is not null && !window.IsDisposed) window.BeginInvoke(window.Close); thread.Join(TimeSpan.FromSeconds(10)); }
    }
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern nint GetFocus();
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint window, int index);
}
