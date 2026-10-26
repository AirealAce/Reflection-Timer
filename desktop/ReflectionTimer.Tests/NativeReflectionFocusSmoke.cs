using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Web.WebView2.WinForms;
using ReflectionTimer.Accessible;
using ReflectionTimer.Core;
using ReflectionTimer.Desktop;

// Opt-in native focus regression: synthetic Chromium and native-edit source
// windows, isolated memory/profile, muted audio and fake global registration.
// Does not drive a user's Chrome, Excel, screen reader or timer profile.
static class NativeReflectionFocusSmoke
{
    internal static void RunInteractive()
    {
        Exception? failure = null;
        var thread = new Thread(() => {
            Application.EnableVisualStyles();
            using var launcher = new Form { Text = "Synthetic reflection focus checks", Size = new(460, 160), StartPosition = FormStartPosition.CenterScreen };
            var button = new Button { Text = "Run reflection focus checks", Dock = DockStyle.Fill };
            // The opt-in fixture may be started with a hidden console helper.
            // Override that first-show hint for the explicitly interactive UI.
            launcher.Shown += (_, _) => WindowActivation.Focus(launcher);
            button.Click += (_, _) => { try { Run(); } catch (Exception error) { failure = error; } finally { launcher.Close(); } };
            launcher.Controls.Add(button); Application.Run(launcher);
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure is not null) throw new Exception("Interactive reflection focus checks failed", failure);
    }

    internal static void Run()
    {
        Exception? failure = null; var passed = 0;
        void Check(bool condition, string name) { if (!condition) throw new Exception(name); passed++; Console.WriteLine("PASS " + name); }
        var thread = new Thread(() => {
            PreviewApplication? app = null;
            try {
                Application.SetHighDpiMode(HighDpiMode.PerMonitorV2); Application.EnableVisualStyles();
                var now = DateTimeOffset.Now;
                var store = new MemoryStore { State = new() { LoggingEnabled = false, VoiceAnnouncements = false,
                    Csv = new() { Enabled = false }, ShowAppView = false, ShowFloatingTimer = false,
                    AutoSendIncompleteReflections = false, SessionEndPopups = false, Timer = new() { Volume = 0 } } };
                var session = new PreviewSession(store, () => now, isolatedProfile: true);
                session.Engine.Start(600, false, 0, lowTime: new() { Enabled = false });
                var promptId = session.Engine.CheckIn();
                const string response = "Keep this reflection and selection.";
                const string reason = "Optional reason retained.";
                session.Engine.SaveDraft(promptId, response, reason);
                var timer = session.Engine.CurrentTimer;
                using var nativeSource = new Form { Text = "Synthetic Excel-style reflection source", Size = new(500, 180), StartPosition = FormStartPosition.CenterScreen };
                using var nativeEditor = new TextBox { Text = "Native editor text stays untouched.", Multiline = true, Dock = DockStyle.Fill };
                nativeSource.Controls.Add(nativeEditor);
                using var chromeSource = new Form { Text = "Synthetic Chrome-style reflection source", Size = new(500, 180), StartPosition = FormStartPosition.CenterScreen };
                using var chrome = new WebView2 { Dock = DockStyle.Fill };
                chromeSource.Controls.Add(chrome);
                var directory = Path.Combine(Path.GetTempPath(), "ReflectionTimer-ReflectionFocus-" + Guid.NewGuid().ToString("N"));
                app = new(session, directory, startInTray: true, profileName: "native-reflection-focus", shortcutRegistration: new Registration(),
                    screenReaderNotification: (_, _, _) => true, speech: new SilentSpeech());
                ((System.Windows.Forms.Timer)Field(app, "pulse")).Stop(); _ = app.MainForm!.Handle;
                app.MainForm.BeginInvoke(async () => {
                    try {
                        _ = chromeSource.Handle; _ = chrome.Handle;
                        await chrome.EnsureCoreWebView2Async(await app.BrowserEnvironmentAsync());
                        chrome.CoreWebView2.NavigateToString("<!doctype html><html lang='en'><title>Synthetic Chrome source</title><label for='source-input'>Source editor</label><textarea id='source-input'>Browser source text stays untouched.</textarea></html>");
                        await Until(async () => await Bool(chrome, "Boolean(document.querySelector('#source-input'))"));
                        var keys = (PreviewShortcuts)Field(app, "shortcuts");
                        async Task FocusChrome()
                        {
                            chromeSource.Show(); WindowActivation.Focus(chromeSource); chrome.Focus();
                            await Script(chrome, "document.querySelector('#source-input').focus();document.querySelector('#source-input').setSelectionRange(3,8)");
                            await Until(async () => WindowActivation.IsForeground(chromeSource) && chrome.ContainsFocus
                                && await Bool(chrome, "document.hasFocus()&&document.activeElement.id==='source-input'"));
                            Check(IsChild(chrome.Handle, KeyboardFocus(chromeSource.Handle)), "Synthetic Chrome source owns a native WebView child before the global shortcut");
                        }
                        async Task FocusNative()
                        {
                            nativeSource.Show(); WindowActivation.Focus(nativeSource); nativeEditor.Focus(); nativeEditor.Select(3, 5);
                            await Until(() => Task.FromResult(WindowActivation.IsForeground(nativeSource) && nativeEditor.Focused
                                && KeyboardFocus(nativeSource.Handle) == nativeEditor.Handle));
                        }
                        async Task FocusedReflection(PreviewWindow reflection, string label, string? expectedResponse = null)
                        {
                            await Until(async () => !reflection.IsDisposed && reflection.Visible && reflection.ReflectionOpen
                                && WindowActivation.IsForeground(reflection) && Browser(reflection).ContainsFocus
                                && await Bool(Browser(reflection), "document.hasFocus()&&document.activeElement.id==='reflection-text'"));
                            var nativeFocus = KeyboardFocus(reflection.Handle);
                            Check(IsChild(Browser(reflection).Handle, nativeFocus), label + ": Windows keyboard focus reaches a native child of the reflection WebView");
                            var state = await Read(reflection);
                            Check(state.GetProperty("hasFocus").GetBoolean() && state.GetProperty("active").GetString() == "reflection-text",
                                label + ": the document and response text box both have real focus");
                            Check(state.GetProperty("draft").GetString() == (expectedResponse ?? response) && state.GetProperty("reason").GetString() == reason,
                                label + ": focus targeting retains both saved text fields");
                            Check(session.Engine.CurrentTimer == timer && session.Engine.Snapshot.Outbox.Count == 0 && !app.MainForm.Visible,
                                label + ": focusing neither changes the timer nor submits text or shows App view");
                        }

                        await FocusChrome();
                        Check(!Windows(app).Any(w => w.View == "reflection"), "Cold shortcut begins with no reflection window or controller");
                        Check(keys.Dispatch(GlobalShortcut.HotKeyMessage, GlobalShortcut.ReflectionFocusId), "Cold Chrome-style source uses production Ctrl+Alt+/ dispatch");
                        await Until(() => Task.FromResult(Windows(app).Any(w => w.View == "reflection" && w.Visible)));
                        var reflection = Windows(app).Single(w => w.View == "reflection");
                        var browser = Browser(reflection);
                        await FocusedReflection(reflection, "Cold Chrome-style source");
                        await Script(browser, "document.querySelector('#reflection-text').setSelectionRange(5,16,'backward')");

                        await FocusNative();
                        Check(keys.Dispatch(GlobalShortcut.HotKeyMessage, GlobalShortcut.ReflectionFocusId), "Native Excel-style source uses production Ctrl+Alt+/ dispatch");
                        await FocusedReflection(reflection, "Warm native Excel-style source");
                        var selected = await Read(reflection);
                        Check(selected.GetProperty("start").GetInt32() == 5 && selected.GetProperty("end").GetInt32() == 16
                            && selected.GetProperty("direction").GetString() == "backward", "Warm native-source handoff retains the reflection selection and its direction");
                        Check(ReferenceEquals(browser, Browser(reflection)) && nativeEditor.Text == "Native editor text stays untouched.",
                            "Native-source handoff reuses its WebView and leaves the source text untouched");

                        await FocusChrome();
                        Check(keys.Dispatch(GlobalShortcut.HotKeyMessage, GlobalShortcut.ReflectionFocusId), "Warm Chrome-style source uses production Ctrl+Alt+/ dispatch");
                        await FocusedReflection(reflection, "Warm Chrome-style source");
                        selected = await Read(reflection);
                        Check(selected.GetProperty("start").GetInt32() == 5 && selected.GetProperty("end").GetInt32() == 16,
                            "Warm Chromium-source handoff also retains the reflection selection");

                        using (var nativeDistractor = new TextBox { Text = "Synthetic native focus outside WebView", Dock = DockStyle.Top, Height = 24 }) {
                            reflection.Controls.Add(nativeDistractor); nativeDistractor.BringToFront(); nativeDistractor.Focus();
                            await Until(() => Task.FromResult(WindowActivation.IsForeground(reflection) && nativeDistractor.Focused && !browser.ContainsFocus));
                            var stale = await Read(reflection);
                            Check(stale.GetProperty("active").GetString() == "reflection-text" && !stale.GetProperty("hasFocus").GetBoolean(),
                                "A foreground Form can retain a stale DOM textarea while its WebView lacks native focus");
                            Check(keys.Dispatch(GlobalShortcut.HotKeyMessage, GlobalShortcut.ReflectionFocusId), "Foreground Form recovery uses the same global shortcut callback");
                            await FocusedReflection(reflection, "Foreground Form/native focus recovery");
                            Check(reflection.ReflectionOpen && reflection.Visible && session.Engine.Snapshot.Prompts.Single().Id == promptId,
                                "Recovering missing native focus does not mistake stale DOM focus for Save");
                            reflection.Controls.Remove(nativeDistractor);
                        }

                        Check(keys.Dispatch(GlobalShortcut.HotKeyMessage, GlobalShortcut.ReflectionFocusId), "Second shortcut from the truly focused response box dispatches Save");
                        await Until(() => Task.FromResult(!reflection.Visible && !reflection.ReflectionOpen));
                        Check(session.Engine.Snapshot.Prompts.Single().Draft == response && session.Engine.Snapshot.Outbox.Count == 0,
                            "Second press keeps the original Save-and-close behavior without submission");
                        await FocusChrome();
                        Check(keys.Dispatch(GlobalShortcut.HotKeyMessage, GlobalShortcut.ReflectionFocusId), "Hidden saved reflection reopens through production dispatch");
                        // Save prepares the default newline for a continuation;
                        // refocusing must retain that existing editing behavior.
                        await FocusedReflection(reflection, "Hidden saved reflection reopen", response + "\n");
                        Check(ReferenceEquals(browser, Browser(reflection)), "Saving and reopening preserve the same browser accessibility objects");

                        await FocusNative();
                        await Script(browser, "window.__reflectionFocusCalls=0;window.__reflectionOriginalFocus=HTMLTextAreaElement.prototype.focus;HTMLTextAreaElement.prototype.focus=function(...args){window.__reflectionFocusCalls++;return window.__reflectionOriginalFocus.apply(this,args)}");
                        Check(keys.Dispatch(GlobalShortcut.HotKeyMessage, GlobalShortcut.ReflectionFocusId), "Late-handoff cancellation starts from a real background source");
                        nativeSource.Show(); WindowActivation.Focus(nativeSource); nativeEditor.Focus(); nativeEditor.Select(3, 5);
                        await Until(() => Task.FromResult(WindowActivation.IsForeground(nativeSource) && nativeEditor.Focused));
                        var sourceFocus = KeyboardFocus(nativeSource.Handle);
                        await Task.Delay(400);
                        Check(WindowActivation.IsForeground(nativeSource) && nativeEditor.Focused && KeyboardFocus(nativeSource.Handle) == sourceFocus
                            && nativeEditor.SelectionStart == 3 && nativeEditor.SelectionLength == 5,
                            "Switching away before delayed handoff preserves the newly chosen source focus and selection");
                        Check(await Bool(browser, "window.__reflectionFocusCalls===0"), "Cancelled handoff does not issue delayed DOM textarea focus into a background WebView");
                        Check(!await Bool(browser, "document.hasFocus()") && reflection.ReflectionOpen && reflection.Visible,
                            "Cancellation leaves the reflection available without claiming background document focus");
                        await Script(browser, "HTMLTextAreaElement.prototype.focus=window.__reflectionOriginalFocus");

                        // A cold controller adds an asynchronous initialization
                        // boundary before ShowReflection can activate anything.
                        // Switching apps during that wait must win as well.
                        await reflection.FlushDraftAsync(); reflection.ClosePermanently();
                        Check(!Windows(app).Any(w => w.View == "reflection"), "Cold-cancellation check disposes the prior reflection controller");
                        await FocusChrome();
                        Check(keys.Dispatch(GlobalShortcut.HotKeyMessage, GlobalShortcut.ReflectionFocusId), "Cold-load cancellation begins through production Ctrl+Alt+/ dispatch");
                        var cold = Windows(app).Single(w => w.View == "reflection");
                        Check(!cold.Visible && !ReferenceEquals(browser, Browser(cold)), "Cold-load cancellation uses a fresh hidden editor before initialization completes");
                        nativeSource.Show(); WindowActivation.Focus(nativeSource); nativeEditor.Focus(); nativeEditor.Select(3, 5);
                        await Until(() => Task.FromResult(WindowActivation.IsForeground(nativeSource) && nativeEditor.Focused));
                        var coldSourceFocus = KeyboardFocus(nativeSource.Handle);
                        await Until(() => Task.FromResult(cold.Visible && ((TaskCompletionSource)Field(cold, "interfaceReady")).Task.IsCompletedSuccessfully));
                        await Task.Delay(350);
                        Check(WindowActivation.IsForeground(nativeSource) && nativeEditor.Focused && KeyboardFocus(nativeSource.Handle) == coldSourceFocus
                            && nativeEditor.SelectionStart == 3 && nativeEditor.SelectionLength == 5,
                            "Switching away during cold editor loading preserves the newer native source focus and selection");
                        Check(!Browser(cold).ContainsFocus && !await Bool(Browser(cold), "document.hasFocus()"),
                            "Cold-load completion shows the reflection passively without claiming native or document focus");
                        var coldState = await Read(cold);
                        Check(coldState.GetProperty("draft").GetString() == response + "\n" && coldState.GetProperty("reason").GetString() == reason,
                            "Passive cold-load completion retains the saved response continuation and reason");
                        Check(cold.ReflectionOpen && session.Engine.Snapshot.Prompts.Single().Id == promptId
                            && session.Engine.Snapshot.Outbox.Count == 0 && session.Engine.CurrentTimer == timer,
                            "Cold-load cancellation leaves the same reflection editable without sending it or changing the timer");
                        Check(session.Engine.Snapshot.Connection.WebAppUrl.Length == 0 && !session.Engine.SettingsSnapshot.Csv.Enabled
                            && session.Engine.Snapshot.Outbox.Count == 0 && !session.Engine.SettingsSnapshot.VoiceAnnouncements,
                            "All native focus checks remain isolated, muted and disconnected from personal logging");
                    } catch (Exception error) { failure = error; }
                    finally {
                        chromeSource.Close(); nativeSource.Close();
                        if (!await app.CloseMainAsync()) foreach (var window in Windows(app).ToArray()) window.ClosePermanently();
                    }
                });
                Application.Run(app);
            } catch (Exception error) { failure = error; }
            finally { app?.Dispose(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(100))) throw new TimeoutException("Native reflection focus checks timed out.");
        if (failure is not null) throw new Exception("Native reflection focus checks failed", failure);
        Console.WriteLine($"{passed} native reflection focus checks passed; actual NVDA speech/mode requires listener verification.");
    }

    private static object Field(object value, string name) => value.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(value)!;
    private static List<PreviewWindow> Windows(PreviewApplication app) => (List<PreviewWindow>)Field(app, "windows");
    private static WebView2 Browser(PreviewWindow window) => (WebView2)Field(window, "browser");
    private static Task<string> Script(WebView2 browser, string script) => browser.CoreWebView2.ExecuteScriptAsync(script);
    private static async Task<bool> Bool(WebView2 browser, string script) { using var result = JsonDocument.Parse(await Script(browser, script)); return result.RootElement.GetBoolean(); }
    private static async Task<JsonElement> Read(PreviewWindow window) => JsonDocument.Parse(await Script(Browser(window), "({hasFocus:document.hasFocus(),active:document.activeElement.id,draft:document.querySelector('#reflection-text').value,reason:document.querySelector('#early-reason').value,start:document.querySelector('#reflection-text').selectionStart,end:document.querySelector('#reflection-text').selectionEnd,direction:document.querySelector('#reflection-text').selectionDirection})")).RootElement.Clone();
    private static async Task Until(Func<Task<bool>> predicate) { using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(12)); while (!await predicate()) await Task.Delay(25, limit.Token); }
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
    private sealed class SilentSpeech : IVoiceOutput
    {
        public void Speak(string text, int volume) { }
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
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsChild(nint parent, nint child);
}
