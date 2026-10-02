using ReflectionTimer.Core;

namespace ReflectionTimer.Accessible;

internal sealed class FocusModeMonitor : IDisposable
{
    private readonly TimerEngine engine;
    private readonly IFocusTargetSource source;
    private readonly Action<bool> alert;
    private readonly FocusModeGate gate = new();
    private Task<FocusPresence>? reading;
    private string? readingTargets;
    private string? checkedTargets;
    private FocusPresence lastPresence = FocusPresence.Unknown;
    private long readingStarted;
    private bool alerting, disposed;
    private string status = "Off";
    internal event Action<string>? StatusChanged;
    internal string Status => status;
    internal FocusModeMonitor(TimerEngine engine, IFocusTargetSource source, Action<bool> alert)
    {
        this.engine = engine; this.source = source; this.alert = alert;
        engine.Changed += Changed;
    }
    private void Changed()
    {
        var state = engine.SettingsSnapshot;
        // Stop synchronously on pause/reset/disable/selection change, even if
        // another application's accessibility provider is still answering.
        if (!state.Timer.IsRunning || !state.FocusMode.Enabled || state.FocusMode.SelectionKey != readingTargets) Apply(gate.Evaluate(state.FocusMode, state.Timer, FocusPresence.Unknown, engine.ElapsedNow));
    }
    internal void Poll()
    {
        if (disposed) return;
        var state = engine.SettingsSnapshot;
        if (!state.Timer.IsRunning || !state.FocusMode.Enabled) { Changed(); return; }
        var idle = state.FocusMode.IdleEnabled ? source.IdleMilliseconds : null;
        if (state.FocusMode.SelectedTargets.Length == 0) { Apply(gate.Evaluate(state.FocusMode, state.Timer, FocusPresence.Unknown, engine.ElapsedNow, idle)); return; }
        if (reading is null) { readingTargets = state.FocusMode.SelectionKey; readingStarted = engine.ElapsedNow; reading = source.CheckAnyAsync(state.FocusMode.SelectedTargets); }
        if (!reading.IsCompleted) {
            if (engine.ElapsedNow - readingStarted > 2000 || readingTargets != state.FocusMode.SelectionKey || state.FocusMode.IdleEnabled)
                Apply(gate.Evaluate(state.FocusMode, state.Timer, engine.ElapsedNow - readingStarted <= 2000 && checkedTargets == state.FocusMode.SelectionKey ? lastPresence : FocusPresence.Unknown, engine.ElapsedNow, idle));
            return; // Never accumulate requests behind a hung UIA provider.
        }
        var presence = reading.IsCompletedSuccessfully && state.FocusMode.SelectionKey == readingTargets ? reading.Result : FocusPresence.Unknown;
        checkedTargets = state.FocusMode.SelectionKey; lastPresence = presence;
        reading = null;
        Apply(gate.Evaluate(state.FocusMode, state.Timer, presence, engine.ElapsedNow, idle));
    }
    private void Apply(FocusModeDecision decision)
    {
        if (alerting != decision.Alert) { alerting = decision.Alert; alert(alerting); }
        if (status != decision.Status) { status = decision.Status; StatusChanged?.Invoke(status); }
    }
    public void Dispose() { if (disposed) return; disposed = true; engine.Changed -= Changed; alert(false); source.Dispose(); }
}
