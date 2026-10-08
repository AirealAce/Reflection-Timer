using System.Reflection;
using System.Text.Json;
using ReflectionTimer.Accessible;
using ReflectionTimer.Core;
using ReflectionTimer.Desktop;

// Exercise the real window close handlers without ending the Windows session,
// starting WebViews, registering real hotkeys, or accessing production data.
static class NativeShutdownSmoke
{
    internal static void Run()
    {
        Exception? failure = null;
        var passed = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
            passed++;
            Console.WriteLine("PASS " + message);
        }
        var thread = new Thread(() =>
        {
            try
            {
                var store = new MemoryStore { State = new()
                {
                    LoggingEnabled = false, ShowFloatingTimer = false,
                    AutoSendIncompleteReflections = false, Timer = new() { Volume = 0 }
                } };
                var session = new PreviewSession(store, isolatedProfile: true);
                using var app = new PreviewApplication(session,
                    Path.Combine(Path.GetTempPath(), "ReflectionTimer-ShutdownSmoke-" + Guid.NewGuid().ToString("N")),
                    startInTray: true, profileName: "shutdown-smoke", shortcutRegistration: new Registration());
                using var compact = new PreviewWindow(app, "compact", null);
                using var reflection = new PreviewWindow(app, "reflection", Guid.NewGuid());
                var main = (PreviewWindow)app.MainForm!;
                var expected = JsonSerializer.Serialize(session.Engine.Snapshot, DataJson.Options);

                Check(Closing(main, CloseReason.UserClosing).Cancel, "Closing App view normally still hides it in the tray");
                foreach (var window in new[] { main, compact, reflection })
                {
                    var closing = Closing(window, CloseReason.WindowsShutDown);
                    Check(!closing.Cancel, window.View + " permits Windows shutdown and sign-out");
                    Check(!window.IsDisposed, window.View + " remains usable if another app cancels the shutdown query");
                }
                Check(Closing(main, CloseReason.UserClosing).Cancel, "A canceled system shutdown does not change later tray-close behavior");
                Check(JsonSerializer.Serialize(session.Engine.Snapshot, DataJson.Options) == expected,
                    "Shutdown queries preserve saved preferences, timer state and reflection data");
                app.Dispose();
                app.Dispose();
                Check(true, "Repeated message-loop and using-scope disposal closes update services safely");
            }
            catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(30))) throw new TimeoutException("Shutdown checks timed out.");
        if (failure is not null) throw new Exception("Shutdown checks failed.", failure);
        Console.WriteLine($"{passed} native shutdown checks passed.");
    }
    private static FormClosingEventArgs Closing(Form window, CloseReason reason)
    {
        var args = new FormClosingEventArgs(reason, false);
        typeof(Form).GetMethod("OnFormClosing", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [args]);
        return args;
    }
    private sealed class Registration : IHotKeyRegistration
    {
        public bool Register(nint window, int id, uint modifiers, uint key) => true;
        public bool Unregister(nint window, int id) => true;
    }
}
