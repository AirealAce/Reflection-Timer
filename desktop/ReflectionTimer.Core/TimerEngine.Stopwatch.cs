namespace ReflectionTimer.Core;

public sealed partial class TimerEngine
{
    public event Action<TimerState>? TimeReached;

    public static long StopwatchMilliseconds(TimerState timer, long now) => Math.Clamp(
        timer.ElapsedMilliseconds + (timer.IsRunning && timer.RunningSince is { } since ? Math.Max(0, now - since) : 0),
        0, MaxDuration * 1000L);

    private static TimerState PauseStopwatch(TimerState timer, long now) => timer with {
        IsRunning = false, ElapsedMilliseconds = StopwatchMilliseconds(timer, now), RunningSince = null
    };

    // The input can wait in the UI queue or behind storage. Freeze at receipt,
    // without removing already-accrued time or accepting a future timestamp.
    private static long StopwatchStopTime(TimerState timer, long requestedAt, long now) =>
        Math.Clamp(requestedAt, Math.Min(timer.RunningSince ?? requestedAt, now), now);

    private static TimerState ResumeStopwatch(TimerState timer, long now)
    {
        if (timer.SessionId is null || timer.StopwatchCompleted)
            throw new ArgumentException("Start a new stopwatch session first.");
        return timer with { IsRunning = true, RunningSince = now };
    }

    private static void ClearStopwatchResume(AppState value) => value.Prompts = value.Prompts
        .Select(p => p.CheckInSessionId == value.Timer.SessionId ? p with { ResumeStopwatchOnSave = false } : p).ToList();

    // A reflection may belong to the paused mode rather than the visible mode.
    // Looking it up by session ID prevents old drafts borrowing a newer clock.
    private static TimerState? SessionTimer(AppState value, Guid? id) => id is null ? null
        : new[] { value.Timer, value.ParkedTimer }.OfType<TimerState>()
            .FirstOrDefault(t => t.SessionId == id && HasUnfinishedSession(t));

    public void SwitchMode(SessionMode mode, long? requestedAt = null)
    {
        requestedAt ??= Now;
        if (!Enum.IsDefined(mode)) throw new ArgumentException("Choose Timer or Stopwatch.");
        lock (gate) {
            if (state.Timer.Mode == mode) return;
            Change("session.modeChanged", s => {
                var now = Now;
                var previous = s.Timer;
                ClearStopwatchResume(s);
                if (previous.Mode == SessionMode.Stopwatch) previous = PauseStopwatch(previous, StopwatchStopTime(previous, requestedAt.Value, now));
                else if (previous.IsRunning) {
                    if (previous.EndTime <= now) CompletePrompt(s, now);
                    previous = previous with { IsRunning = false, RemainingSeconds = Remaining(previous, now),
                        PausedRemainingMilliseconds = RemainingMilliseconds(previous, now), EndTime = null };
                }
                s.Timer = (s.ParkedTimer ?? new TimerState { Mode = mode }) with { Volume = previous.Volume };
                s.ParkedTimer = previous;
                ExpireAutoRestart(s, now);
            }, value: (int)mode);
        }
    }

    public void StartStopwatch() => Change("stopwatch.started", s => {
        if (s.Timer.Mode != SessionMode.Stopwatch) throw new ArgumentException("Select Stopwatch first.");
        s.Timer = s.Timer with { SessionId = Guid.NewGuid(), IsRunning = true, ElapsedMilliseconds = 0,
            RunningSince = Now, StopwatchCompleted = false, TimeReachedPlayed = false,
            AutoRestart = false, AutoRestartUntil = null, EndTime = null, PausedRemainingMilliseconds = null };
    });

    public Guid ReviewStopwatch(long? requestedAt = null)
    {
        requestedAt ??= Now;
        lock (gate) {
            if (state.Timer.Mode != SessionMode.Stopwatch || !HasUnfinishedSession(state.Timer))
                throw new ArgumentException("Start the stopwatch before opening its reflection.");
            var existing = state.Prompts.LastOrDefault(p => p.Mode == SessionMode.Stopwatch && p.CheckInSessionId == state.Timer.SessionId);
            if (!state.Timer.IsRunning && existing?.ResumeStopwatchOnSave == true) return existing.Id;
            var id = existing?.Id ?? Guid.NewGuid();
            var stoppedAt = StopwatchStopTime(state.Timer, requestedAt.Value, Now);
            Change("stopwatch.reviewOpened", s => {
                s.Timer = PauseStopwatch(s.Timer, stoppedAt);
                var prompt = new ReflectionPrompt(id, stoppedAt, 0, s.Timer.Volume, false, existing?.Draft ?? "") {
                    Mode = SessionMode.Stopwatch, SessionId = s.Timer.SessionId, IsCheckIn = true,
                    CheckInSessionId = s.Timer.SessionId, ActualDurationSeconds = ActualSeconds(s.Timer, stoppedAt),
                    ResumeStopwatchOnSave = true, ContinuationSeparator = existing?.ContinuationSeparator
                };
                s.Prompts.RemoveAll(p => p.Id == id);
                s.Prompts.Add(prompt);
            }, id, ActualSeconds(state.Timer, stoppedAt));
            return id;
        }
    }

    public void SetTimeReached(bool enabled, int seconds) => Change("stopwatch.alertChanged", s => {
        ValidateDuration(seconds);
        s.Audio = AudioSettings.From(s) with { TimeReachedEnabled = enabled, TimeReachedSeconds = seconds };
    }, value: seconds);

    private static void ResumeAfterReflectionSave(AppState value, ReflectionPrompt prompt, long now)
    {
        // Saving an old or parked draft must never start a second clock or a
        // different session. A failed save commits neither the draft nor resume.
        if (prompt.ResumeStopwatchOnSave && prompt.CheckInSessionId == value.Timer.SessionId
            && value.Timer.Mode == SessionMode.Stopwatch && IsPaused(value.Timer))
            value.Timer = ResumeStopwatch(value.Timer, now);
    }
}
