using ReflectionTimer.Core;

namespace ReflectionTimer.Accessible;

internal sealed class FocusModeMonitor : IDisposable
{
    private readonly TimerEngine engine;
    private readonly IFocusTargetSource source;
    private readonly Action<bool> alert;
    private readonly FocusModeGate gate = new();
    private Task<FocusPresence>? reading;
    private FocusTarget? readingTarget;
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
        if (readingTarget != state.FocusMode.Target) reading = null;
        // Stop synchronously on pause/reset/disable/selection change, even if
        // another application's accessibility provider is still answering.
        if (!state.Timer.IsRunning || !state.FocusMode.Enabled || state.FocusMode.Target != readingTarget) Apply(gate.Evaluate(state.FocusMode, state.Timer, FocusPresence.Unknown, engine.ElapsedNow));
    }
    internal void Poll()
    {
        if (disposed) return;
        var state = engine.SettingsSnapshot;
        if (!state.Timer.IsRunning || !state.FocusMode.Enabled || state.FocusMode.Target is null) { Changed(); return; }
        if (reading is null) { readingTarget = state.FocusMode.Target; readingStarted = engine.ElapsedNow; reading = source.CheckAsync(readingTarget); }
        if (!reading.IsCompleted) {
            if (engine.ElapsedNow - readingStarted > 2000) Apply(gate.Evaluate(state.FocusMode, state.Timer, FocusPresence.Unknown, engine.ElapsedNow));
            return; // Never accumulate requests behind a hung UIA provider.
        }
        var presence = reading.IsCompletedSuccessfully && state.FocusMode.Target == readingTarget ? reading.Result : FocusPresence.Unknown;
        reading = null;
        Apply(gate.Evaluate(state.FocusMode, state.Timer, presence, engine.ElapsedNow));
    }
    private void Apply(FocusModeDecision decision)
    {
        if (alerting != decision.Alert) { alerting = decision.Alert; alert(alerting); }
        if (status != decision.Status) { status = decision.Status; StatusChanged?.Invoke(status); }
    }
    public void Dispose() { if (disposed) return; disposed = true; engine.Changed -= Changed; alert(false); source.Dispose(); }
}
