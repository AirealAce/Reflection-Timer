namespace ReflectionTimer.Core;

public enum FocusTargetKind { Window, BrowserTab }

// Stored only inside the encrypted profile. Runtime IDs identify the exact tab,
// rather than confusing duplicate titles or a page that changes its title.
public record FocusTarget(Guid Id, FocusTargetKind Kind, string Name, string App,
    long WindowHandle, int ProcessId, long ProcessStartedAt, string TabRuntimeId = "")
{
    public string WindowName { get; init; } = "";
    public int TabPosition { get; init; }
}

public record FocusModeSettings
{
    public bool Enabled { get; init; }
    public int DelaySeconds { get; init; } = 5;
    public FocusTarget? Target { get; init; }
}

public enum FocusPresence { Focused, Away, Unavailable, Unknown }
public record FocusModeDecision(bool Alert, string Status);

// Monotonic elapsed time; no dependence on wall-clock edits or sleep recovery.
public sealed class FocusModeGate
{
    private Guid? target, session;
    private SessionMode mode;
    private int delay;
    private long? awaySince;
    public FocusModeDecision Evaluate(FocusModeSettings settings, TimerState timer, FocusPresence presence, long now)
    {
        if (target != settings.Target?.Id || session != timer.SessionId || mode != timer.Mode || delay != settings.DelaySeconds) {
            awaySince = null; target = settings.Target?.Id; session = timer.SessionId; mode = timer.Mode; delay = settings.DelaySeconds;
        }
        if (!settings.Enabled || !timer.IsRunning || settings.Target is null || presence != FocusPresence.Away) {
            awaySince = null;
            return new(false, !settings.Enabled ? "Off" : settings.Target is null ? "Choose a window or tab"
                : !timer.IsRunning ? "Waiting for the session to run" : presence switch {
                    FocusPresence.Focused => "Selected target is focused",
                    FocusPresence.Unavailable => "Target closed or moved. Choose it again.",
                    _ => "Target could not be checked. Alert stopped."
                });
        }
        awaySince ??= now;
        var remaining = Math.Max(0, settings.DelaySeconds - (now - awaySince.Value) / 1000);
        return new(remaining == 0, remaining == 0 ? "Away from selected target · delay reached" : $"Away from selected target · alert in {remaining} seconds");
    }
}
