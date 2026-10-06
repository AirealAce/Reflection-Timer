using ReflectionTimer.Core;
using System.Collections.Immutable;

namespace ReflectionTimer.Accessible;

internal sealed class FocusModeMonitor : IDisposable
{
    private readonly TimerEngine engine;
    private readonly IFocusTargetSource source;
    private readonly Action<bool> alert;
    private readonly Action<bool>? glow;
    private readonly FocusModeGate gate = new();
    private readonly SavedFocusWindows savedWindows;
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
    private sealed record Capture(Guid Session, FocusTarget? Window, IReadOnlyList<FocusTarget> Windows,FocusTarget[] Choices)
    {
        internal IReadOnlyList<FocusTarget> Targets { get; set; } = [];
        internal bool Completed { get; set; }
    }
    private bool alerting, glowing, disposed;
    private string status = "Off";
    internal event Action<string>? StatusChanged;
    internal string Status => status;
    internal bool ScreenEdgeGlow => glowing;
    internal FocusModeMonitor(TimerEngine engine, IFocusTargetSource source, Action<bool> alert, Action<bool>? glow = null)
    {
        this.engine = engine; this.source = source; this.alert = alert; this.glow = glow;
        savedWindows=new(engine,source);
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
            var choices = state.FocusMode.SelectedTargets.Where(t => t.UseFocused).ToArray();
            if (choices.Length > 0) {
                FocusTarget? foreground;
                try { foreground = source.CaptureForeground(); } catch { foreground = null; }
                IReadOnlyList<FocusTarget> open;
                try{open=choices.Any(t=>t.CaptureScope!=FocusCaptureScope.Focused)?source.CaptureOpenWindows():[];}catch{open=[];}
                var capture = new Capture(session, foreground, open,choices);
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
            try { capturing = source.CaptureSelectionsAsync(next.Window,next.Windows,next.Choices); }
            catch { capturing = Task.FromResult<IReadOnlyList<FocusTarget>>([]); }
            CompleteCapture();
        }
    }
    private FocusModeSettings Effective(AppState state)
    {
        var captured = state.Timer.SessionId is {} id ? captures.GetValueOrDefault(id)?.Targets : null;
        var targets = state.FocusMode.SelectedTargets.SelectMany(t => t.UseFocused
            ? captured?.Where(c=>c.Kind==t.Kind&&c.CaptureScope==t.CaptureScope).ToArray() is {Length:>0} matches?matches:[t] : new[]{t})
            .DistinctBy(t => t.Key).ToImmutableArray();
        return state.FocusMode with { Target = targets.FirstOrDefault(), Targets = targets };
    }
    private string ProbeKey(FocusModeSettings settings) => $"{generation}:{settings.SelectionKey}";
    private async Task<FocusPresence> CheckEffectiveAsync(FocusModeSettings settings, Guid? session)
    {
        var captured = session is {} id ? captures.GetValueOrDefault(id) : null;
        var pending = settings.SelectedTargets.Any(t => t.UseFocused && (captured?.Completed != true || !captured.Choices.Any(c=>c.Key==t.Key)));
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
        else if (!settings.ScreenEdgeGlow && glowing) { glowing = false; glow?.Invoke(false); }
    }
    internal void Poll()
    {
        if (disposed) return;
        savedWindows.Poll();
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
        if (glowing != decision.ScreenEdgeGlow) { glowing = decision.ScreenEdgeGlow; glow?.Invoke(glowing); }
        if (status != decision.Status) { status = decision.Status; StatusChanged?.Invoke(status); }
    }
    internal void RestoreWindows(IReadOnlyList<FocusTarget> open)=>savedWindows.Apply(open);
    internal FocusTarget CurrentWindow(FocusTarget target)=>savedWindows.Current(target);
    internal string[] PreviousWindowKeys(FocusTarget target)=>savedWindows.PreviousKeys(target);
    public void Dispose() { if (disposed) return; disposed = true; engine.ActivityRecorded -= Activity; engine.Changed -= Changed; alert(false); glow?.Invoke(false); source.Dispose(); }
}
