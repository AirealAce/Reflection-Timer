using ReflectionTimer.Core;
using ReflectionTimer.Desktop;

namespace ReflectionTimer.Accessible;

internal sealed partial class PreviewWindow
{
    private readonly ViewerAutoHideDeadline autoHideDeadline = new();
    private System.Windows.Forms.Timer? autoHideTimer;
    private (bool Enabled, int Seconds)? autoHidePreference;
    private TimerState? autoHideSession;
    private bool movingViewer;

    internal void ConfigureAutoHide()
    {
        if (View != "compact" || IsDisposed) return;
        var state = app.Session.Engine.SettingsSnapshot;
        var preference = (Enabled: state.ViewerAutoHide, Seconds: Math.Clamp(state.ViewerAutoHideSeconds, 1, TimerEngine.MaxDuration));
        var previous = autoHideSession;
        var preferenceChanged = autoHidePreference != preference;
        autoHideSession = state.Timer;
        autoHidePreference = preference;
        if (!preference.Enabled || !state.Timer.IsRunning) { CancelAutoHide(); return; }
        // Observe committed session transitions, including scheduled/automatic
        // starts and saving a stopwatch review. Initial load is not a start.
        if (previous is not null && (!previous.IsRunning || previous.SessionId != state.Timer.SessionId
            || previous.RunningSince != state.Timer.RunningSince)) RestartAutoHide();
        else if (preferenceChanged) {
            // Editing the delay retains the original start/resume timestamp.
            autoHideDeadline.ChangeDelay(preference.Seconds);
            ScheduleAutoHide();
        }
    }

    private void CancelAutoHide()
    {
        autoHideTimer?.Stop();
        autoHideDeadline.Cancel();
    }

    private void RestartAutoHide()
    {
        CancelAutoHide();
        if (View != "compact" || !Visible || IsDisposed
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
        if (IsDisposed || !Visible || !app.Session.Engine.CurrentTimer.IsRunning || autoHidePreference is not { Enabled: true }) {
            CancelAutoHide(); return;
        }
        // Loading/recovery can finish after the deadline. Keep its original
        // timestamp and resume the pending timeout when the interface is ready.
        if (!ready || recoveringInterface) return;
        if (autoHideDeadline.RemainingMilliseconds is not { } remaining) return;
        if (remaining > 0) { ScheduleAutoHide(); return; }
        // Never hide the owner of a confirmation/file dialog or interrupt a drag.
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
