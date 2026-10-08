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
    private readonly Dictionary<Guid, Dictionary<string, Capture>> captures = [];
    private readonly List<Capture> queuedCaptures = [];
    private Capture? activeCapture;
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
        source.Configure(previous.FocusMode,previous.Timer);
        engine.ActivityRecorded += Activity;
        engine.Changed += Changed;
    }
    private void Activity(Activity activity)
    {
        var state = engine.SettingsSnapshot;
        source.Configure(state.FocusMode,state.Timer);
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
                captures[session] = [];
                QueueCapture(new(session, foreground, open,choices));
            }
            else captures.Remove(session);
        }
        else if (state.Timer.IsRunning && state.Timer.SessionId is {} runningSession
            && state.FocusMode.SelectionKey != previous.FocusMode.SelectionKey) {
            // Background choices can be resolved from the windows open at save.
            // Keep already selected choices pinned to their own earlier snapshot;
            // foreground-only choices still resolve at the next start/resume.
            var added = state.FocusMode.SelectedTargets.Where(t => t.UseFocused && t.CaptureScope != FocusCaptureScope.Focused
                && !previous.FocusMode.SelectedTargets.Any(p => p.Key == t.Key)).ToArray();
            if (added.Length > 0) {
                IReadOnlyList<FocusTarget> open;
                try { open = source.CaptureOpenWindows(); } catch { open = []; }
                QueueCapture(new(runningSession, null, open, added));
            }
        }
        foreach (var id in captures.Keys.Where(id => id != state.Timer.SessionId && id != state.ParkedTimer?.SessionId).ToArray()) captures.Remove(id);
        if (state.Timer.SessionId is {} current && captures.TryGetValue(current, out var selections))
            foreach (var key in selections.Keys.Where(key => !state.FocusMode.SelectedTargets.Any(t => t.UseFocused && t.Key == key)).ToArray()) selections.Remove(key);
        queuedCaptures.RemoveAll(c => !Current(c));
        previous = state;
    }
    private Capture? Captured(Guid? session, string key) => session is {} id && captures.TryGetValue(id, out var selections)
        ? selections.GetValueOrDefault(key) : null;
    private bool Current(Capture capture) => capture.Choices.Any(t => ReferenceEquals(Captured(capture.Session, t.Key), capture));
    private void QueueCapture(Capture capture)
    {
        if (!captures.TryGetValue(capture.Session, out var selections)) captures[capture.Session] = selections = [];
        foreach (var choice in capture.Choices) selections[choice.Key] = capture;
        queuedCaptures.RemoveAll(c => !Current(c));
        queuedCaptures.Add(capture);
        PumpCapture();
    }
    private void CompleteCapture()
    {
        if (capturing is { IsCompleted: true }) {
            if (activeCapture is {} captured && Current(captured)) {
                captured.Targets = capturing.IsCompletedSuccessfully ? capturing.Result.Where(t => !t.UseFocused).ToArray() : [];
                captured.Completed = true;
            }
            capturing = null; activeCapture = null;
        }
    }
    private void PumpCapture()
    {
        CompleteCapture();
        // Each selected choice owns at most one waiting snapshot. A newer
        // start/resume or remove/re-add discards its obsolete pending batch.
        queuedCaptures.RemoveAll(c => !Current(c));
        if (capturing is null && queuedCaptures.Count > 0) {
            var next = queuedCaptures[0]; queuedCaptures.RemoveAt(0);
            activeCapture = next;
            var choices=next.Choices.Where(t=>ReferenceEquals(Captured(next.Session,t.Key),next)
                && (t.Kind!=FocusTargetKind.Site||SiteTargetsAvailable())).ToArray();
            try { capturing = choices.Length==0?Task.FromResult<IReadOnlyList<FocusTarget>>([]):source.CaptureSelectionsAsync(next.Window,next.Windows,choices); }
            catch { capturing = Task.FromResult<IReadOnlyList<FocusTarget>>([]); }
            CompleteCapture();
        }
    }
    private FocusModeSettings Effective(AppState state)
    {
        var targets = state.FocusMode.SelectedTargets.SelectMany(t => t.UseFocused
            ? Captured(state.Timer.SessionId,t.Key)?.Targets.Where(c=>c.Kind==t.Kind&&c.CaptureScope==t.CaptureScope).ToArray() is {Length:>0} matches?matches:[t] : new[]{t})
            .DistinctBy(t => t.Key).ToImmutableArray();
        return state.FocusMode with { Target = targets.FirstOrDefault(), Targets = targets };
    }
    private bool SiteTargetsAvailable()=>FocusSitePolicy.Available(engine.SettingsSnapshot.FocusMode,source.BrowserConnected);
    private string ProbeKey(FocusModeSettings settings) => $"{generation}:{settings.SelectionKey}:{(settings.SelectedTargets.Any(t=>t.Kind==FocusTargetKind.Site)?SiteTargetsAvailable():false)}";
    private async Task<FocusPresence> CheckEffectiveAsync(FocusModeSettings settings, Guid? session)
    {
        var sitesAvailable=SiteTargetsAvailable();
        var pending = settings.SelectedTargets.Any(t => t.Kind==FocusTargetKind.Site&&!sitesAvailable
            ||t.UseFocused && Captured(session,t.Key)?.Completed != true);
        var available = settings.SelectedTargets.Where(t => !t.UseFocused&&(t.Kind!=FocusTargetKind.Site||sitesAvailable)).ToArray();
        if (available.Length == 0) return pending ? FocusPresence.Unknown : FocusPresence.Unavailable;
        var presence = await source.CheckAnyAsync(available,settings.TargetOnSiteLinks).ConfigureAwait(false);
        // A known missing group (for example an ungrouped active tab) cannot
        // prevent other successfully captured targets from reporting Away.
        return presence != FocusPresence.Focused && pending ? FocusPresence.Unknown : presence;
    }
    private void Changed()
    {
        var state = engine.SettingsSnapshot;
        source.Configure(state.FocusMode,state.Timer);
        // Stop synchronously on pause/reset/disable/selection change, even if
        // another application's accessibility provider is still answering.
        var settings = Effective(state);
        if (!state.Timer.IsRunning || !settings.Enabled || ProbeKey(settings) != readingTargets) {
            // Idle-only monitoring has no target probe to retain. Preserve its
            // current idle age when a checkpoint or settings save reevaluates it.
            var idle = state.Timer.IsRunning && settings.Enabled && settings.SelectedTargets.IsEmpty && settings.IdleEnabled ? source.IdleMilliseconds : null;
            Apply(gate.Evaluate(settings, state.Timer, FocusPresence.Unknown, engine.ElapsedNow, idle));
        }
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
