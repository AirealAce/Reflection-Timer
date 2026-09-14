using ReflectionTimer.Core;
using ReflectionTimer.Desktop;
using System.Text.Json;

namespace ReflectionTimer.Accessible;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        (string? Profile, bool Tray, bool CheckConnection) launch;
        try { launch = PreviewStartup.Parse(args); }
        catch (ArgumentException error) { MessageBox.Show(error.Message, "Reflection Timer"); Environment.ExitCode = 1; return; }
        var root = PreviewStartup.DirectoryFor(launch.Profile);
        // Verification must not recover or save another running instance's state.
        if (launch.CheckConnection) {
            try {
                var state = EncryptedStore.Read<AppState>(Path.Combine(root, "state.dat")) ?? throw new InvalidDataException();
                using var client = new SheetsClient();
                var reply = client.Ping(state.Connection).GetAwaiter().GetResult();
                Console.WriteLine(JsonSerializer.Serialize(new { reply.Success, reply.ErrorKind, reply.SupportsSafeRetry, reply.SupportsCheckIns }));
                Environment.ExitCode = reply.Success ? 0 : 1;
            }
            catch { Console.Error.WriteLine("Connection check failed. No credentials were printed or settings changed."); Environment.ExitCode = 1; }
            return;
        }
        using var mutex = new Mutex(true, PreviewStartup.MutexName(launch.Profile), out var first);
        using var show = new EventWaitHandle(false, EventResetMode.AutoReset, PreviewStartup.ShowEventName(launch.Profile));
        if (!first) { if (!launch.Tray) show.Set(); return; }
        try {
            if (launch.Profile is null) ProfileStorage.MigrateLegacy(root, ProfileStorage.LegacyDirectory);
            var store = new EncryptedStore(root);
            var session = new PreviewSession(store, isolatedProfile: launch.Profile is not null);
            using var app = new PreviewApplication(session, root, store.RecoveryNotice, launch.Tray, launch.Profile);
            _ = app.MainForm!.Handle;
            var showRegistration = ThreadPool.RegisterWaitForSingleObject(show, (_, _) => {
                try { app.MainForm?.BeginInvoke(() => app.Open("main")); }
                catch (InvalidOperationException) { /* The window is already closing. */ }
            }, null, Timeout.Infinite, false);
            ThreadExceptionEventHandler handler = (_, _) => app.Announce("An unexpected app error occurred. Your last saved state is retained. Export diagnostics if this repeats.");
            Application.ThreadException += handler;
            try { Application.Run(app); }
            finally { showRegistration.Unregister(null); Application.ThreadException -= handler; }
        }
        catch {
            Environment.ExitCode = 1;
            MessageBox.Show("Reflection Timer could not open its local data. Nothing has been reset or erased. Check disk access and keep the data files for recovery.", "Reflection Timer — unable to start", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally { mutex.ReleaseMutex(); }
    }
}
