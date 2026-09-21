using System.Reflection;
using System.Runtime.InteropServices;
using ReflectionTimer.Accessible;
using ReflectionTimer.Core;

// Exercise the real dialog and Windows focus/styles, with synthetic owners.
// No installed profile, global registrations, keyboard injection, or delivery.
static class NativeResetDialogSmoke
{
    internal static void RunInteractive()
    {
        // An unattended console test cannot take foreground from another app.
        // Start by a real click to grant the same activation rights as a hotkey.
        var thread = new Thread(() => {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2); Application.EnableVisualStyles();
            using var launcher = new Form { Text = "Reflection Timer — reset dialog checks", ClientSize = new(400, 100), StartPosition = FormStartPosition.CenterScreen };
            var preview = new Button { Text = "Preview reset confirmation", Dock = DockStyle.Top, Height = 45 };
            preview.Click += (_, _) => {
                launcher.Hide();
                using var dialog = new ResetConfirmationDialog(new(SessionMode.Timer, true, true), AppColorTheme.Dark);
                dialog.ShowDialog(launcher);
                launcher.Show(); launcher.Activate();
            };
            var run = new Button { Text = "Run reset dialog checks", Dock = DockStyle.Fill };
            run.Click += (_, _) => { Run(requireForeground: true); launcher.Close(); };
            launcher.Controls.Add(run);
            launcher.Controls.Add(preview);
            Application.Run(launcher);
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
    }
    internal static void Run(bool requireForeground = false)
    {
        Exception? failure = null; var passed = 0;
        void Check(bool value, string message) {
            if(!value)throw new Exception(message);
            passed++; Console.WriteLine("PASS " + message);
        }
        var thread = new Thread(() => {
            try {
                Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
                Application.EnableVisualStyles();
                foreach(var source in new[]{"hidden App", "App", "Compact", "Time-only", "reflection"})
                foreach(var action in new[]{"OK", "Cancel", "Escape", "Close"}) {
                    var tool = source is "Compact" or "Time-only" or "reflection";
                    using var owner = new Form {
                        Text = "Synthetic " + source, ShowInTaskbar = !tool, TopMost = tool,
                        FormBorderStyle = tool ? FormBorderStyle.FixedToolWindow : FormBorderStyle.Sizable
                    };
                    _ = owner.Handle;
                    if(source != "hidden App")owner.Show();
                    var mode = source == "Time-only" ? SessionMode.Stopwatch : SessionMode.Timer;
                    var warning = new ResetWarning(mode, true, true);
                    using var dialog = new ResetConfirmationDialog(warning, (AppColorTheme)(Array.IndexOf(new[]{"OK","Cancel","Escape","Close"}, action)));
                    var ok = (Button)dialog.Controls.Find("reset-ok", true).Single();
                    var cancel = (Button)dialog.Controls.Find("reset-cancel", true).Single();
                    var details = (TextBox)dialog.Controls.Find("reset-message", true).Single();
                    using var watchdog = new System.Windows.Forms.Timer { Interval = 5000 };
                    watchdog.Tick += (_, _) => { failure ??= new TimeoutException("Reset dialog did not finish."); dialog.Close(); };
                    dialog.Shown += (_, _) => dialog.BeginInvoke(() => {
                        try {
                            var label = source + ", " + action + ": ";
                            var styles = GetWindowLongPtr(dialog.Handle, -20).ToInt64();
                            if(requireForeground)Check(GetForegroundWindow() == dialog.Handle,
                                label + "confirmation takes foreground after explicit input");
                            Check(ok.Focused,
                                label + "actual keyboard focus starts on OK (active control: " + dialog.ActiveControl?.Name + ")");
                            Check(dialog.ShowInTaskbar && (styles & 0x40000) != 0 && (styles & 0x08000080) == 0,
                                label + "native window opts into Alt+Tab without tool/no-activate flags");
                            Check(!IsWindowEnabled(owner.Handle) && owner.Visible == (source != "hidden App"),
                                label + "modal keeps the owner disabled without showing a hidden viewer");
                            Check(dialog.AccessibilityObject.Role == AccessibleRole.Dialog && dialog.AccessibilityObject.Name == warning.Title
                                && dialog.AccessibilityObject.Description == warning.Message,
                                label + "screen readers receive the dialog title and full explanation");
                            Check(ok.AccessibilityObject.Role == AccessibleRole.PushButton && ok.AccessibilityObject.Name == "OK"
                                && ok.AccessibilityObject.Description == warning.Message && cancel.AccessibilityObject.Name == "Cancel",
                                label + "both native buttons expose names and OK exposes reset context");
                            Check(details.ReadOnly && details.TabStop && details.AccessibilityObject.Value?.Replace("\r", "") == warning.Message,
                                label + "warning text is keyboard-readable and cannot be edited");
                            Check(dialog.AcceptButton == ok && dialog.CancelButton == cancel,
                                label + "Enter and Escape have explicit confirmation/cancellation actions");
                            Key(dialog, Keys.Tab);
                            Check(cancel.Focused, label + "Tab from OK focuses Cancel");
                            Key(dialog, Keys.Tab);
                            Check(details.Focused, label + "Tab from Cancel focuses warning text");
                            Key(dialog, Keys.Shift | Keys.Tab);
                            Check(cancel.Focused, label + "Shift+Tab returns from text to Cancel");
                            if(action == "OK") { Key(dialog, Keys.Shift | Keys.Tab); Check(ok.Focused, label + "Shift+Tab returns to OK"); Key(dialog, Keys.Enter); }
                            else if(action == "Cancel")Key(dialog, Keys.Enter);
                            else if(action == "Escape") { details.Focus(); Key(dialog, Keys.Escape); }
                            else dialog.Close();
                        } catch(Exception error) { failure = error; dialog.Close(); }
                    });
                    watchdog.Start();
                    var result = dialog.ShowDialog(owner);
                    watchdog.Stop();
                    if(failure is not null)throw failure;
                    Check(result == (action == "OK" ? DialogResult.OK : DialogResult.Cancel), source + ", " + action + ": correct reset decision returned");
                    Check(IsWindowEnabled(owner.Handle) && owner.Visible == (source != "hidden App"), source + ", " + action + ": owner re-enabled with visibility preserved");
                }
            } catch(Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        if(!thread.Join(TimeSpan.FromSeconds(120)))throw new TimeoutException("Native reset dialog checks timed out.");
        if(failure is not null)throw new Exception("Native reset dialog checks failed", failure);
        Console.WriteLine($"{passed} native reset dialog checks passed.");
    }
    private static void Key(Form dialog, Keys key) => typeof(Form).GetMethod("ProcessDialogKey", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(dialog, [key]);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint window, int index);
    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowEnabled(nint window);
}
