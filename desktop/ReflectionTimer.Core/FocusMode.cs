using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace ReflectionTimer.Core;

public enum FocusTargetKind { Window, BrowserTab, BrowserTabGroup }

// Stored only inside the encrypted profile. Runtime IDs identify the exact tab,
// rather than confusing duplicate titles or a page that changes its title.
public record FocusTarget(Guid Id, FocusTargetKind Kind, string Name, string App,
    long WindowHandle, int ProcessId, long ProcessStartedAt, string TabRuntimeId = "")
{
    public string WindowName { get; init; } = "";
    public int TabPosition { get; init; }
    [JsonIgnore] public string Key => $"{(int)Kind}:{ProcessId}:{ProcessStartedAt}:{WindowHandle}:{(Kind == FocusTargetKind.Window ? "" : TabRuntimeId)}";
}

public record FocusModeSettings
{
    public bool Enabled { get; init; }
    public int DelaySeconds { get; init; } = 5;
    public FocusTarget? Target { get; init; }
    public ImmutableArray<FocusTarget> Targets { get; init; } = [];
    public bool MultipleTargets { get; init; }
    public bool IdleEnabled { get; init; }
    public int IdleSeconds { get; init; } = 20;
    // Older encrypted profiles stored only Target. Keep that choice on upgrade.
    [JsonIgnore] public ImmutableArray<FocusTarget> SelectedTargets => !Targets.IsDefaultOrEmpty ? Targets : Target is {} target ? [target] : [];
    [JsonIgnore] public string SelectionKey => string.Join("|", SelectedTargets.Select(t => t.Key).Order(StringComparer.Ordinal));
}

public enum FocusPresence { Focused, Away, Unavailable, Unknown }
public record FocusModeDecision(bool Alert, string Status);

// Monotonic elapsed time; no dependence on wall-clock edits or sleep recovery.
public sealed class FocusModeGate
{
    private string? targets;
    private Guid? session;
    private SessionMode mode;
    private int delay;
    private long? awaySince;
    public FocusModeDecision Evaluate(FocusModeSettings settings, TimerState timer, FocusPresence presence, long now, long? idleMilliseconds = null)
    {
        if (targets != settings.SelectionKey || session != timer.SessionId || mode != timer.Mode || delay != settings.DelaySeconds) {
            awaySince = null; targets = settings.SelectionKey; session = timer.SessionId; mode = timer.Mode; delay = settings.DelaySeconds;
        }
        if (!settings.Enabled || !timer.IsRunning) {
            awaySince = null;
            return new(false, !settings.Enabled ? "Off" : "Waiting for the session to run");
        }
        var hasTargets = settings.SelectedTargets.Length > 0;
        if (hasTargets && presence == FocusPresence.Away) awaySince ??= now;
        else awaySince = null;
        var remaining = awaySince is {} since ? Math.Max(0, settings.DelaySeconds - (now - since) / 1000) : (long?)null;
        var idle = settings.IdleEnabled && idleMilliseconds >= settings.IdleSeconds * 1000L;
        if (idle) return new(true, $"Idle for {settings.IdleSeconds} seconds · alert active");
        if (remaining is {} seconds) return new(seconds == 0, seconds == 0 ? "Away from selected targets · delay reached" : $"Away from selected targets · alert in {seconds} seconds");
        return new(false, !hasTargets ? settings.IdleEnabled ? "Watching for inactivity" : "Choose a window or tab" : presence switch {
            FocusPresence.Focused => "A selected target is focused",
            FocusPresence.Unavailable => "Targets closed or moved. Choose them again.",
            _ => "Targets could not be checked. Away alert stopped."
        });
    }
}
