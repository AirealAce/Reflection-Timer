using ReflectionTimer.Core;
using System.Collections.Immutable;

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
    private AppState previous;
    private long generation;
    private readonly Dictionary<Guid, Capture> captures = [];
    private Capture? queuedCapture, activeCapture;
    private Task<IReadOnlyList<FocusTarget>>? capturing;
    private sealed record Capture(Guid Session, FocusTarget? Window, FocusTargetKind[] Kinds)
    {
        internal IReadOnlyList<FocusTarget> Targets { get; set; } = [];
        internal bool Completed { get; set; }
    }
    private bool alerting, disposed;
    private string status = "Off";
    internal event Action<string>? StatusChanged;
    internal string Status => status;
    internal FocusModeMonitor(TimerEngine engine, IFocusTargetSource source, Action<bool> alert)
    {
        this.engine = engine; this.source = source; this.alert = alert;
        previous = engine.SettingsSnapshot;
        engine.ActivityRecorded += Activity;
        engine.Changed += Changed;
    }
    private void Activity(Activity activity)
    {
        var state = engine.SettingsSnapshot;
        if (state.Timer.IsRunning != previous.Timer.IsRunning || state.Timer.SessionId != previous.Timer.SessionId
            || state.Timer.Mode != previous.Timer.Mode || state.FocusMode.SelectionKey != previous.FocusMode.SelectionKey
            || state.FocusMode.Enabled != previous.FocusMode.Enabled) generation++;
        // Observe committed transitions, including schedules, repeats and a
        // stopwatch resumed by saving its reflection. Mode changes never capture.
        if (activity.Event != "session.modeChanged" && state.Timer.IsRunning && state.Timer.SessionId is {} session
            && (!previous.Timer.IsRunning || previous.Timer.SessionId != session)) {
            var kinds = state.FocusMode.SelectedTargets.Where(t => t.UseFocused).Select(t => t.Kind).Distinct().ToArray();
            if (kinds.Length > 0) {
                FocusTarget? foreground;
                try { foreground = source.CaptureForeground(); } catch { foreground = null; }
                var capture = new Capture(session, foreground, kinds);
                captures[session] = capture; queuedCapture = capture;
                PumpCapture();
            }
        }
        foreach (var id in captures.Keys.Where(id => id != state.Timer.SessionId && id != state.ParkedTimer?.SessionId).ToArray()) captures.Remove(id);
        previous = state;
    }
    private void CompleteCapture()
    {
        if (capturing is { IsCompleted: true }) {
            if (activeCapture is {} captured && ReferenceEquals(captures.GetValueOrDefault(captured.Session), captured)) {
                captured.Targets = capturing.IsCompletedSuccessfully ? capturing.Result.Where(t => !t.UseFocused).ToArray() : [];
                captured.Completed = true;
            }
            capturing = null; activeCapture = null;
        }
    }
    private void PumpCapture()
    {
        CompleteCapture();
        // A newer start/resume replaces the waiting snapshot; never accumulate
        // requests behind a browser whose accessibility provider has stalled.
        if (capturing is null && queuedCapture is {} next) {
            queuedCapture = null;
            if (!ReferenceEquals(captures.GetValueOrDefault(next.Session), next)) return;
            activeCapture = next;
            try { capturing = next.Window is {} window ? source.CaptureAsync(window, next.Kinds) : Task.FromResult<IReadOnlyList<FocusTarget>>([]); }
            catch { capturing = Task.FromResult<IReadOnlyList<FocusTarget>>([]); }
            CompleteCapture();
        }
    }
    private FocusModeSettings Effective(AppState state)
    {
        var captured = state.Timer.SessionId is {} id ? captures.GetValueOrDefault(id)?.Targets : null;
        var targets = state.FocusMode.SelectedTargets.Select(t => t.UseFocused
            ? captured?.FirstOrDefault(c => c.Kind == t.Kind) ?? t : t).DistinctBy(t => t.Key).ToImmutableArray();
        return state.FocusMode with { Target = targets.FirstOrDefault(), Targets = targets };
    }
    private string ProbeKey(FocusModeSettings settings) => $"{generation}:{settings.SelectionKey}";
    private async Task<FocusPresence> CheckEffectiveAsync(FocusModeSettings settings, Guid? session)
    {
        var captured = session is {} id ? captures.GetValueOrDefault(id) : null;
        var pending = settings.SelectedTargets.Any(t => t.UseFocused && (captured?.Completed != true || !captured.Kinds.Contains(t.Kind)));
        var available = settings.SelectedTargets.Where(t => !t.UseFocused).ToArray();
        if (available.Length == 0) return pending ? FocusPresence.Unknown : FocusPresence.Unavailable;
        var presence = await source.CheckAnyAsync(available).ConfigureAwait(false);
        // A known missing group (for example an ungrouped active tab) cannot
        // prevent other successfully captured targets from reporting Away.
        return presence != FocusPresence.Focused && pending ? FocusPresence.Unknown : presence;
    }
    private void Changed()
    {
        var state = engine.SettingsSnapshot;
        // Stop synchronously on pause/reset/disable/selection change, even if
        // another application's accessibility provider is still answering.
        var settings = Effective(state);
        if (!state.Timer.IsRunning || !settings.Enabled || ProbeKey(settings) != readingTargets) Apply(gate.Evaluate(settings, state.Timer, FocusPresence.Unknown, engine.ElapsedNow));
    }
    internal void Poll()
    {
        if (disposed) return;
        var state = engine.SettingsSnapshot;
        if (!state.Timer.IsRunning || !state.FocusMode.Enabled) { Changed(); return; }
        PumpCapture();
        var settings = Effective(state);
        var probeKey = ProbeKey(settings);
        var idle = state.FocusMode.IdleEnabled ? source.IdleMilliseconds : null;
        if (state.FocusMode.SelectedTargets.Length == 0) { Apply(gate.Evaluate(state.FocusMode, state.Timer, FocusPresence.Unknown, engine.ElapsedNow, idle)); return; }
        if (reading is null) { readingTargets = probeKey; readingStarted = engine.ElapsedNow; reading = CheckEffectiveAsync(settings, state.Timer.SessionId); }
        if (!reading.IsCompleted) {
            if (engine.ElapsedNow - readingStarted > 2000 || readingTargets != probeKey || state.FocusMode.IdleEnabled)
                Apply(gate.Evaluate(settings, state.Timer, engine.ElapsedNow - readingStarted <= 2000 && checkedTargets == probeKey ? lastPresence : FocusPresence.Unknown, engine.ElapsedNow, idle));
            return; // Never accumulate requests behind a hung UIA provider.
        }
        var presence = reading.IsCompletedSuccessfully && probeKey == readingTargets ? reading.Result : FocusPresence.Unknown;
        checkedTargets = probeKey; lastPresence = presence;
        reading = null;
        Apply(gate.Evaluate(settings, state.Timer, presence, engine.ElapsedNow, idle));
    }
    private void Apply(FocusModeDecision decision)
    {
        if (alerting != decision.Alert) { alerting = decision.Alert; alert(alerting); }
        if (status != decision.Status) { status = decision.Status; StatusChanged?.Invoke(status); }
    }
    public void Dispose() { if (disposed) return; disposed = true; engine.ActivityRecorded -= Activity; engine.Changed -= Changed; alert(false); source.Dispose(); }
}
