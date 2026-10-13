using System.Runtime.InteropServices;
using ReflectionTimer.Accessible;

// Opt-in only: speaks one test sentence through the installed, running JAWS.
// Opens no profile or timer window, registers no shortcut, changes no settings.
static class NativeJawsAnnouncementSmoke
{
    internal static void Run()
    {
        Exception? failure = null;
        var thread = new Thread(() => {
            try {
                var before = GetForegroundWindow();
                using var reader = new JawsScreenReaderOutput();
                using var context = new ApplicationContext();
                using var poll = new System.Windows.Forms.Timer { Interval = 40 };
                using var keepAlive = new System.Windows.Forms.Timer { Interval = 4000 };
                Action? next = null;
                using var delivery = new KeyboardReleasedFeedback(callback => { next = callback; poll.Start(); },
                    KeyboardReleasedFeedback.ShortcutKeysPressed, () => Environment.TickCount64);
                poll.Tick += (_, _) => { poll.Stop(); var callback = next; next = null; callback?.Invoke(); };
                keepAlive.Tick += (_, _) => { keepAlive.Stop(); context.ExitThread(); };
                delivery.Publish(() => {
                    try {
                        if (!reader.TryAnnounce("Reflection Timer announcement test. Keyboard focus has not moved.", false))
                            throw new Exception("The running JAWS did not accept the test phrase through its installed API.");
                        // Match the real app's persistent STA message loop;
                        // queued speech must not lose its client on immediate exit.
                        keepAlive.Start();
                    } catch (Exception error) { failure = error; context.ExitThread(); }
                });
                Application.Run(context);
                if (failure is not null) return;
                if (GetForegroundWindow() != before) throw new Exception("Foreground changed during the JAWS test.");
                Console.WriteLine("JAWS accepted one test phrase; foreground unchanged. Actual audible output requires listener confirmation.");
            } catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(15))) throw new TimeoutException("The JAWS test did not complete within fifteen seconds.");
        if (failure is not null) throw new Exception("JAWS announcement smoke failed.", failure);
    }
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
}
