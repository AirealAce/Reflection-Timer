using ReflectionTimer.Core;
using ReflectionTimer.Desktop;

namespace ReflectionTimer.Accessible;

internal sealed partial class PreviewWindow
{
    private readonly ViewerAutoHideDeadline autoHideDeadline = new();
    private System.Windows.Forms.Timer? autoHideTimer;
    private (bool Enabled, int Seconds)? autoHidePreference;
    private bool movingViewer;

    internal void ConfigureAutoHide()
    {
        if (View != "compact" || IsDisposed) return;
        var state = app.Session.Engine.SettingsSnapshot;
        var preference = (state.ViewerAutoHide, Math.Clamp(state.ViewerAutoHideSeconds, 1, TimerEngine.MaxDuration));
        if (autoHidePreference == preference) return;
        autoHidePreference = preference;
        RestartAutoHide();
    }

    private void CancelAutoHide()
    {
        autoHideTimer?.Stop();
        autoHideDeadline.Cancel();
    }

    private void RestartAutoHide()
    {
        CancelAutoHide();
        if (View != "compact" || !Visible || IsDisposed || !interfaceReady.Task.IsCompletedSuccessfully
            || autoHidePreference is not { Enabled: true } preference) return;
        autoHideDeadline.Restart(preference.Seconds);
        ScheduleAutoHide();
    }

    private void ScheduleAutoHide()
    {
        if (autoHideDeadline.RemainingMilliseconds is not { } remaining) return;
        if (autoHideTimer is null) {
            autoHideTimer = new();
            autoHideTimer.Tick += (_, _) => AutoHideViewer();
        }
        autoHideTimer.Interval = Math.Max(1, remaining);
        autoHideTimer.Start();
    }

    private void AutoHideViewer()
    {
        autoHideTimer?.Stop();
        if (IsDisposed || !Visible || !ready || recoveringInterface || autoHidePreference is not { Enabled: true }) {
            CancelAutoHide(); return;
        }
        if (autoHideDeadline.RemainingMilliseconds is not { } remaining) return;
        if (remaining > 0) { ScheduleAutoHide(); return; }
        // Never hide the owner of a confirmation/file dialog or interrupt a drag.
        // Activation/move-end grants a fresh delay after those interactions.
        if (!WindowActivation.CanReceiveFocus(this) || movingViewer || Capture) {
            autoHideTimer!.Interval = 250; autoHideTimer.Start(); return;
        }
        CancelAutoHide();
        try {
            app.Session.Engine.SetFloatingTimer(false);
            app.ApplyDisplayPreferences(); // Uses the normal, tested focus handoff.
        } catch {
            app.Announce("Could not save the hidden viewer. It is staying visible; your timer is unchanged.");
        }
    }
}
