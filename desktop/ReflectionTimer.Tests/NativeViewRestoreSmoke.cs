using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Web.WebView2.WinForms;
using ReflectionTimer.Accessible;
using ReflectionTimer.Core;
using ReflectionTimer.Desktop;

// Real message loops and encrypted synthetic profiles, never production data,
// real hotkeys, audio, Sheets, or accessibility/keyboard tests.
static class NativeViewRestoreSmoke
{
    internal static void Run()
    {
        var passed = 0;
        foreach (var mode in Enum.GetValues<SessionMode>())
        foreach (var layout in new[] { "app", "compact", "time-only", "tray" })
        foreach (var tray in new[] { false, true })
        {
            var directory = Path.Combine(Path.GetTempPath(), "ReflectionTimer-ViewRestore-" + Guid.NewGuid().ToString("N"));
            var store = new EncryptedStore(directory);
            var saved = new AppState {
                ShowAppView = layout == "app", ShowFloatingTimer = layout is "compact" or "time-only",
                FloatingTimeOnly = layout == "time-only", LoggingEnabled = false, AutoSendIncompleteReflections = false,
                Theme = AppColorTheme.Glamour, FloatingPlacement = FloatingTimerPlacement.Custom,
                FloatingTimerLeft = 24, FloatingTimerTop = 48,
                Timer = new() { Mode = mode, Volume = 0, SessionId = Guid.NewGuid(), ElapsedMilliseconds = 12345,
                    RemainingSeconds = 812, PausedRemainingMilliseconds = 812000 }
            };
            store.Save(saved);
            var original = JsonSerializer.Serialize(saved, DataJson.Options);
            void Check(bool condition, string message) {
                if (!condition) throw new Exception(message);
                passed++;
                Console.WriteLine($"PASS {mode}/{layout}/tray={tray}: {message}");
            }
            RunApplication(directory, tray, async app => {
                var main = (PreviewWindow)app.MainForm!;
                var mainVisible = layout == "app" && !tray;
                Check(main.Visible == mainVisible, "message-loop startup restores App view visibility");
                if (!mainVisible) { main.Show(); Check(!main.Visible, "implicit Show cannot reopen a hidden App view"); }
                var compact = Windows(app).SingleOrDefault(w => w.View == "compact");
                if (compact is not null) {
                    await Ready(compact).WaitAsync(TimeSpan.FromSeconds(20));
                    await Until(() => Task.FromResult(compact.ClientSize.Height < (layout == "time-only" ? 100 : 500)));
                    var tiny = layout == "time-only";
                    Check(compact.Visible && compact.IsTimeOnly == tiny, "floating native window restores the saved layout");
                    Check(JsonDocument.Parse(await Browser(compact).CoreWebView2.ExecuteScriptAsync("document.body.dataset.tiny")).RootElement.GetString() == tiny.ToString().ToLowerInvariant(), "first ready browser matches the saved layout");
                    var styles = GetWindowLongPtr(compact.Handle, -20).ToInt64();
                    Check(!compact.ShowInTaskbar && (styles & 0x80) != 0 && (styles & 0x40000) == 0, "floating viewer retains non-Alt-Tab tool-window styles");
                    Check(compact.Location == new Point(24, 48), "saved custom position survives initial layout sizing");
                }
                else Check(!saved.ShowFloatingTimer, "hidden floating viewer stays hidden");
                if (mainVisible) await Ready(main).WaitAsync(TimeSpan.FromSeconds(20));
                Check(JsonSerializer.Serialize(store.Load(), DataJson.Options) == original, "startup does not overwrite saved state");
            });
            Check(JsonSerializer.Serialize(store.Load(), DataJson.Options) == original, "normal app exit retains view, session and saved preferences");
        }
        var roundTrip = Path.Combine(Path.GetTempPath(), "ReflectionTimer-ViewRoundTrip-" + Guid.NewGuid().ToString("N"));
        var disk = new EncryptedStore(roundTrip);
        disk.Save(new() { LoggingEnabled = false, ShowFloatingTimer = true, Timer = new() { Volume = 0 } });
        RunApplication(roundTrip, true, async app => {
            var main = (PreviewWindow)app.MainForm!;
            Assert(!main.Visible, "legacy --tray launch remains hidden");
            app.Open("main"); main.Close();
            await Ready(main).WaitAsync(TimeSpan.FromSeconds(20));
            Assert(!main.Visible && !main.IsDisposed && disk.Load().ShowAppView == false, "X saves hidden App view and late browser readiness cannot reopen it");
            var compact = Windows(app).Single(w => w.View == "compact");
            await Ready(compact).WaitAsync(TimeSpan.FromSeconds(20));
            compact.Post(new { type = "shrinkCompact" });
            await Until(() => Task.FromResult(compact.IsTimeOnly && disk.Load().FloatingTimeOnly == true));
            Assert(true, "shrinking saves time-only layout immediately");
        });
        RunApplication(roundTrip, false, async app => {
            var compact = Windows(app).Single(w => w.View == "compact");
            await Ready(compact).WaitAsync(TimeSpan.FromSeconds(20));
            Assert(!app.MainForm!.Visible && compact.IsTimeOnly, "ordinary reopen restores time-only without App view");
            compact.Post(new { type = "expandCompact" });
            await Until(() => Task.FromResult(!compact.IsTimeOnly && disk.Load().FloatingTimeOnly == false));
            Assert(true, "expanding saves compact layout immediately");
            app.ToggleCompactVisibility();
            Assert(!compact.Visible && !disk.Load().ShowFloatingTimer, "hiding floating viewer is durable");
            app.Open("main");
            await Ready((PreviewWindow)app.MainForm!).WaitAsync(TimeSpan.FromSeconds(20));
            Assert(app.MainForm.Visible && disk.Load().ShowAppView == true, "explicit App request remains available and is saved");
        });
        RunApplication(roundTrip, false, async app => {
            await Ready((PreviewWindow)app.MainForm!).WaitAsync(TimeSpan.FromSeconds(20));
            Assert(app.MainForm!.Visible && Windows(app).All(w => w.View != "compact"), "next reopen restores App-only view after an ordinary quit");
        });
        Console.WriteLine($"{passed} native view restoration checks passed.");
        void Assert(bool condition, string message) {
            if (!condition) throw new Exception(message);
            passed++; Console.WriteLine("PASS " + message);
        }
    }
    private static void RunApplication(string directory, bool tray, Func<PreviewApplication, Task> exercise)
    {
        Exception? failure = null;
        var thread = new Thread(() => {
            try {
                using var app = new PreviewApplication(new PreviewSession(new EncryptedStore(directory), isolatedProfile: true),
                    directory, startInTray: tray, profileName: "view-restore", shortcutRegistration: new Registration());
                ((System.Windows.Forms.Timer)typeof(PreviewApplication).GetField("pulse", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(app)!).Stop();
                _ = app.MainForm!.Handle;
                app.MainForm.BeginInvoke(async () => {
                    try { await exercise(app); }
                    catch (Exception error) { failure = error; }
                    finally { await app.CloseMainAsync(); }
                });
                Application.Run(app);
            } catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(45))) throw new TimeoutException("View restore checks timed out.");
        if (failure is not null) throw new Exception("View restore checks failed.", failure);
    }
    private static async Task Until(Func<Task<bool>> ready) {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!await ready()) await Task.Delay(25, limit.Token);
    }
    private static List<PreviewWindow> Windows(PreviewApplication app) => (List<PreviewWindow>)typeof(PreviewApplication).GetField("windows", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(app)!;
    private static WebView2 Browser(PreviewWindow window) => (WebView2)typeof(PreviewWindow).GetField("browser", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
    private static Task Ready(PreviewWindow window) => ((TaskCompletionSource)typeof(PreviewWindow).GetField("interfaceReady", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!).Task;
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint window, int index);
    private sealed class Registration : IHotKeyRegistration
    {
        public bool Register(nint window, int id, uint modifiers, uint key) => true;
        public bool Unregister(nint window, int id) => true;
    }
}
