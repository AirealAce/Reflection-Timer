namespace ReflectionTimer.Core;

public sealed partial class TimerEngine
{
    private readonly Func<DateTimeOffset> calendarClock;
    private readonly TimeProvider? elapsedClock;
    private readonly long timestampOrigin;
    private readonly long elapsedOrigin;
    private ClockReading? operationClock;
    private long savedClockOffset;
    private readonly record struct ClockReading(DateTimeOffset Calendar, long Elapsed)
    {
        public long Wall => Calendar.ToUnixTimeMilliseconds();
        public long CalendarTimestamp(long elapsed) => Wall + (elapsed - Elapsed);
    }
    private ClockReading ReadClock()
    {
        var calendar = calendarClock();
        return new(calendar, elapsedClock is null ? calendar.ToUnixTimeMilliseconds()
            : elapsedOrigin + elapsedClock.GetElapsedTime(timestampOrigin).Ticks / TimeSpan.TicksPerMillisecond);
    }
    /// <summary>Calendar time for schedules, retry dates and persisted timestamps.</summary>
    public long Now { get { lock (gate) return (operationClock ?? ReadClock()).Wall; } }
    /// <summary>Process-local elapsed coordinate for runtime timer fields and input intent.</summary>
    public long ElapsedNow { get { lock (gate) return (operationClock ?? ReadClock()).Elapsed; } }
    private DateTimeOffset CalendarNow => (operationClock ?? ReadClock()).Calendar;
    public long CalendarTimestamp(long elapsed) { lock (gate) return (operationClock ?? ReadClock()).CalendarTimestamp(elapsed); }
    public string? ClockRecoveryNotice { get; private set; }

    private void RestoreClocks()
    {
        var at = ReadClock();
        savedClockOffset = at.Wall - at.Elapsed;
        var running = new[] { state.Timer, state.ParkedTimer }.OfType<TimerState>().Where(t => t.IsRunning).ToArray();
        if (running.Length > 0) {
            var reversed = running.Any(t => t.ClockSavedAt > at.Wall || (t.ClockSavedAt is null && t.RunningSince > at.Wall));
            ClockRecoveryNotice = reversed
                ? "The PC clock is earlier than a saved running-session checkpoint. Saved work was retained, but time while the app was closed could not be verified. Review the recovered time before submitting."
                : "A running session was recovered using the saved PC-clock time, including time while the app was closed. Clock changes during that time cannot be verified; review the recovered time before submitting.";
        }
        state.Timer = Restore(state.Timer, at);
        if (state.ParkedTimer is { } parked) state.ParkedTimer = Restore(parked, at);
    }
    private static TimerState Restore(TimerState timer, ClockReading at)
    {
        if (!timer.IsRunning) return timer with { ClockSavedAt = null, RemainingMillisecondsAtSave = null };
        var downtime = timer.ClockSavedAt is { } saved ? Math.Max(0, at.Wall - saved) : (long?)null;
        return timer with {
            EndTime = timer.EndTime is { } end ? downtime is { } away && timer.RemainingMillisecondsAtSave is { } remaining
                ? at.Elapsed + remaining - away : at.Elapsed + (end - at.Wall) : null,
            RunningSince = timer.RunningSince is { } since ? timer.Mode == SessionMode.Stopwatch && downtime is { } elapsedAway
                ? at.Elapsed - elapsedAway : Math.Min(at.Elapsed, at.Elapsed + (since - at.Wall)) : null,
            ClockSavedAt = null, RemainingMillisecondsAtSave = null
        };
    }
    private static TimerState PortableTimer(TimerState timer, ClockReading at)
    {
        if (!timer.IsRunning) return timer with { ClockSavedAt = null, RemainingMillisecondsAtSave = null };
        return timer with {
            ClockSavedAt = at.Wall,
            // Retain a signed overdue deadline so restarting cannot move its
            // historical completion date forward to the checkpoint time.
            RemainingMillisecondsAtSave = timer.Mode == SessionMode.Timer && timer.EndTime is { } deadline ? deadline - at.Elapsed : null,
            EndTime = timer.EndTime is { } end ? at.CalendarTimestamp(end) : null,
            ElapsedMilliseconds = timer.Mode == SessionMode.Stopwatch ? StopwatchMilliseconds(timer, at.Elapsed) : timer.ElapsedMilliseconds,
            RunningSince = timer.RunningSince is { } since ? timer.Mode == SessionMode.Stopwatch ? at.Wall : at.CalendarTimestamp(since) : null
        };
    }
    // Ordinary encrypted saves include a portable timing checkpoint. No extra
    // history clone or independent settings file is needed for the projection.
    private void SavePortable(AppState next, ClockReading at)
    {
        store.Save(next with { Timer = PortableTimer(next.Timer, at),
            ParkedTimer = next.ParkedTimer is { } parked ? PortableTimer(parked, at) : null });
        savedClockOffset = at.Wall - at.Elapsed;
    }
    public void Checkpoint() => Change("timer.checkpoint", _ => { });

    public static TimerState PauseRecoveredTimer(TimerState timer, DateTimeOffset calendar)
    {
        var at = new ClockReading(calendar, calendar.ToUnixTimeMilliseconds());
        timer = Restore(timer, at);
        if (!timer.IsRunning) return timer;
        return timer.Mode == SessionMode.Stopwatch ? PauseStopwatch(timer, at.Elapsed)
            : timer with { IsRunning = false, EndTime = null, RunningSince = null,
                RemainingSeconds = Remaining(timer, at.Elapsed), PausedRemainingMilliseconds = RemainingMilliseconds(timer, at.Elapsed) };
    }
}
