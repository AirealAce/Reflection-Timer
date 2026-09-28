using System.Collections.Immutable;

namespace ReflectionTimer.Core;

public sealed partial class TimerEngine
{
    private TimerState BeginPause(TimerState timer, long at)
    {
        if (!timer.IsRunning || timer.PauseElapsedSince.HasValue) return timer;
        return timer with {
            SessionId = timer.SessionId ?? Guid.NewGuid(), PauseElapsedSince = at,
            Pauses = timer.Pauses.Add(new(Guid.NewGuid(), CalendarTimestamp(at)))
        };
    }

    private static TimerState FinishPause(TimerState timer, long at)
    {
        if (timer.PauseElapsedSince is not { } since || timer.Pauses.Count == 0) return timer;
        var index = timer.Pauses.Count - 1;
        return timer with { PauseElapsedSince = null, Pauses = timer.Pauses.SetItem(index,
            timer.Pauses[index] with { DurationMilliseconds = Math.Clamp(at - since, 0, MaxDuration * 1000L) }) };
    }

    public ImmutableList<SessionPause> PausesFor(ReflectionPrompt prompt)
    {
        lock (gate) return SessionTimer(state, prompt.CheckInSessionId) is { } timer ? timer.Pauses : prompt.Pauses;
    }

    private static ReflectionPrompt SavePauseReasons(AppState value, ReflectionPrompt prompt, IReadOnlyDictionary<Guid, string>? reasons)
    {
        var timer = SessionTimer(value, prompt.CheckInSessionId);
        var pauses = MergePauseReasons(timer?.Pauses ?? prompt.Pauses, reasons);
        if (timer is not null) {
            if (timer.SessionId == value.Timer.SessionId) value.Timer = value.Timer with { Pauses = pauses };
            else value.ParkedTimer = value.ParkedTimer! with { Pauses = pauses };
        }
        return prompt with { Pauses = pauses };
    }

    private static ImmutableList<SessionPause> MergePauseReasons(ImmutableList<SessionPause> pauses, IReadOnlyDictionary<Guid, string>? reasons)
    {
        if (reasons is not null) {
            if (reasons.Any(pair => pair.Value is null || pair.Value.Length > 1000 || !pauses.Any(p => p.Id == pair.Key)))
                throw new ArgumentException("Use an existing pause and keep each pause reason within 1,000 characters.");
            pauses = pauses.Select(p => reasons.TryGetValue(p.Id, out var reason) ? p with { Reason = reason } : p).ToImmutableList();
        }
        return pauses;
    }

    private static void SyncPauseDrafts(AppState value)
    {
        value.Prompts = value.Prompts.Select(p => SessionTimer(value, p.CheckInSessionId) is { } timer
            ? p with { Pauses = timer.Pauses } : p).ToList();
    }
}
