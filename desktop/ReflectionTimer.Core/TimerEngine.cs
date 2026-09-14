namespace ReflectionTimer.Core;

// All state transitions commit a detached snapshot before becoming visible.
// No UI window or asynchronous upload owns the timer's lifetime.
public sealed class TimerEngine
{
    public const int MaxDuration = 365 * 24 * 3600;
    private readonly object gate = new();
    private readonly IStateStore store;
    private readonly Func<DateTimeOffset> clock;
    private AppState state;
    public event Action? Changed;
    public event Action<Activity>? ActivityRecorded;
    public event Action<TimerState>? LowTimeReached;
    public AppState Snapshot { get { lock (gate) return DataJson.Clone(state); } }
    public long Now => clock().ToUnixTimeMilliseconds();

    public TimerEngine(IStateStore store, Func<DateTimeOffset>? clock = null)
    {
        this.store = store;
        this.clock = clock ?? (() => DateTimeOffset.Now);
        state = store.Load();
        if (state.FormatVersion != 1) throw new InvalidDataException("Unsupported local data version. Data was not changed.");
        // Only requests already protected by the receiver's ID protocol may be retried.
        if (state.Outbox.Any(x => x.Status == DeliveryStatus.Sending))
            Change("upload.recovered", s => s.Outbox = s.Outbox.Select(x => x.Status == DeliveryStatus.Sending
                ? x with { Status = x.RetryProtected && x.Attempts < 8 ? DeliveryStatus.Pending : DeliveryStatus.NeedsReview,
                    NextAttemptAt = x.RetryProtected ? Now + 15000 : null, ErrorKind = "interrupted" } : x).ToList());
    }

    public static int Remaining(TimerState timer, long now) => timer.IsRunning && timer.EndTime.HasValue
        ? (int)Math.Clamp(Math.Ceiling((timer.EndTime.Value - now) / 1000d), 0, MaxDuration)
        : timer.RemainingSeconds;

    // A pause in the first second still rounds up to the full duration. Prefer
    // explicit millisecond state; the fallback supports older saved timers.
    public static bool IsPaused(TimerState timer) => !timer.IsRunning &&
        (timer.PausedRemainingMilliseconds is { } precise ? precise > 0
            : timer.RemainingSeconds > 0 && timer.RemainingSeconds < timer.DurationSeconds);

    private static long RemainingMilliseconds(TimerState timer, long now) => Math.Clamp(
        timer.IsRunning && timer.EndTime.HasValue ? timer.EndTime.Value - now
            : timer.PausedRemainingMilliseconds ?? timer.RemainingSeconds * 1000L,
        0, timer.DurationSeconds * 1000L);
    public static int ActualSeconds(TimerState timer, long now) =>
        (int)((timer.DurationSeconds * 1000L - RemainingMilliseconds(timer, now)) / 1000);
    private static void CompletePrompt(AppState state, long now)
    {
        var timer=state.Timer;
        var draft=state.Prompts.LastOrDefault(p=>p.IsCheckIn&&p.CheckInSessionId is {} sessionId&&sessionId==timer.SessionId);
        // Promote the same session's unsent draft in place. Keeping its ID lets
        // an already-open editor keep even keystrokes still awaiting autosave.
        var completed=new ReflectionPrompt(draft?.Id??Guid.NewGuid(),Math.Min(timer.EndTime??now,now),timer.DurationSeconds,timer.Volume,false,draft?.Draft??"") {
            ActualDurationSeconds=ActualSeconds(timer,now),EndedEarly=RemainingMilliseconds(timer,now)>0,
            EarlyEndReason=draft?.EarlyEndReason??"",ContinuationSeparator=draft?.ContinuationSeparator,SessionId=timer.SessionId
        };
        if(draft is not null)state.Prompts.RemoveAll(p=>p.Id==draft.Id);
        state.Prompts.Add(completed);
    }
    public static bool ShowEarlyEndReason(ReflectionPrompt prompt, TimerState timer, long now) => prompt.EndedEarly ||
        (prompt.IsCheckIn&&prompt.CheckInSessionId is {} id&&id==timer.SessionId&&HasUnfinishedSession(timer)&&RemainingMilliseconds(timer,now)>0);

    private void Change(string name, Action<AppState> mutation, Guid? id = null, long? value = null)
    {
        lock (gate)
        {
            var next = DataJson.Clone(state);
            mutation(next);
            // A check-in draft belongs to one session, including across restarts.
            // Freeze its elapsed time when that session ends or is replaced, so
            // a delayed submission never borrows time from the next session.
            if (state.Timer.SessionId is { } sessionId &&
                (next.Timer.SessionId != sessionId || !HasUnfinishedSession(next.Timer)))
                next.Prompts = next.Prompts.Select(p => p.IsCheckIn && p.CheckInSessionId == sessionId
                    ? p with { ActualDurationSeconds = ActualSeconds(state.Timer, Now), CheckInSessionId = null } : p).ToList();
            store.Save(next); // A failed disk write leaves the current state untouched.
            state = next;
        }
        ActivityRecorded?.Invoke(new Activity(Now, name, id, value));
        Changed?.Invoke();
    }

    public static void ValidateDuration(int seconds)
    {
        if (seconds is < 1 or > MaxDuration) throw new ArgumentException("Choose a duration greater than zero (up to one year).");
    }
    private static void ValidateCutoff(long? until, long after)
    {
        if (!until.HasValue) return;
        if (until <= after) throw new ArgumentException("Choose an auto-start cutoff after the session start (and in the future for the regular timer).");
        _ = DateTimeOffset.FromUnixTimeMilliseconds(until.Value);
    }
    private static bool CutoffDue(TimerState timer, long now) => timer.AutoRestartUntil <= now;
    private static void ExpireAutoRestart(AppState state, long now)
    {
        if (CutoffDue(state.Timer, now)) state.Timer = state.Timer with { AutoRestart = false, AutoRestartUntil = null };
    }
    private static TimerState Started(int seconds, bool repeat, int volume, long now, long? until = null, LowTimeOptions? lowTime = null) => new()
    {
        SessionId = Guid.NewGuid(), IsRunning = true, DurationSeconds = seconds, RemainingSeconds = seconds,
        EndTime = now + seconds * 1000L, AutoRestart = (repeat || until.HasValue) && !(until <= now),
        AutoRestartUntil = until > now ? until : null, Volume = Math.Clamp(volume, 0, 100), LowTime = lowTime ?? new()
    };

    public void Start(int seconds, bool repeat, int volume, long? autoRestartUntil = null, LowTimeOptions? lowTime = null)
    {
        ValidateDuration(seconds);
        Change("timer.started", s => {
            var now = Now;
            ValidateCutoff(autoRestartUntil, now);
            var options = lowTime ?? s.Timer.LowTime; AudioSettings.Validate(options);
            s.Timer = Started(seconds, repeat, volume, now, autoRestartUntil, options);
        }, value: seconds);
    }
    public void Pause() => Change("timer.paused", s => {
        var now = Now;
        ExpireAutoRestart(s, now);
        // A click can arrive after the deadline but before the one-second UI tick.
        // Pausing must not silently discard that completed session's reflection.
        if (s.Timer.IsRunning && s.Timer.EndTime <= now)
            CompletePrompt(s, now);
        s.Timer = s.Timer with { IsRunning = false, RemainingSeconds = Remaining(s.Timer, now),
            PausedRemainingMilliseconds = RemainingMilliseconds(s.Timer, now), EndTime = null };
    });
    public void Resume()
    {
        Change("timer.resumed", s => {
            ExpireAutoRestart(s, Now);
            if (s.Timer.IsRunning) return;
            ValidateDuration(s.Timer.RemainingSeconds);
            s.Timer = s.Timer with { IsRunning = true, EndTime = Now + RemainingMilliseconds(s.Timer, Now), PausedRemainingMilliseconds = null };
        });
    }
    public void Reset(int? duration = null) => Change("timer.reset", s => {
        ExpireAutoRestart(s, Now);
        var seconds = duration ?? s.Timer.DurationSeconds;
        ValidateDuration(seconds);
        s.Timer = s.Timer with { SessionId = null, IsRunning = false, DurationSeconds = seconds, RemainingSeconds = seconds, PausedRemainingMilliseconds = null, EndTime = null, LowTimePlayed = false };
    });
    public void SetPreferences(bool repeat, int volume, long? autoRestartUntil = null) => Change("timer.preferences", s => {
        ValidateCutoff(autoRestartUntil, Now);
        s.Timer = s.Timer with { AutoRestart = repeat || autoRestartUntil.HasValue,
            AutoRestartUntil = autoRestartUntil, Volume = Math.Clamp(volume, 0, 100) };
    });
    // Master-volume edits must not validate or overwrite an unfinished cutoff,
    // duration, repeat option, or any other timer draft.
    public void SetAppVolume(int volume) => Change("timer.preferences", s => {
        if (volume is < 0 or > 100) throw new ArgumentException("App sound must be between 0 and 100 percent.");
        s.Timer = s.Timer with { Volume = volume };
    });

    private static void RestartOrStop(AppState state, long now)
    {
        var timer = state.Timer;
        state.Timer = timer.AutoRestart
            ? Started(timer.DurationSeconds, true, timer.Volume, now, timer.AutoRestartUntil, timer.LowTime)
            : timer with { IsRunning = false, RemainingSeconds = 0, PausedRemainingMilliseconds = 0, EndTime = null };
    }

    public bool EndEarly()
    {
        lock (gate) {
            if (!state.Timer.IsRunning) return false;
            var now = Now;
            Change("timer.endedEarly", s => {
                ExpireAutoRestart(s, now);
                // Commit the reflection and the next timer state together. A failed
                // save must not stop the timer or create an unpersisted popup.
                CompletePrompt(s, now);
                ApplyScheduleHandoff(s, now, completed: true);
            }, value: Remaining(state.Timer, now));
            return true;
        }
    }

    public void Advance()
    {
        TimerState? lowTimeAlert = null;
        lock (gate)
        {
            var now = Now;
            var completed = state.Timer.IsRunning && state.Timer.EndTime <= now;
            var deadlineDue = state.Schedules.Any(x => x.StartTime <= now && !x.WaitingForCurrentSession && !x.AwaitingDecision)
                || completed || (!HasUnfinishedSession(state.Timer) && state.Schedules.Any(x => x.WaitingForCurrentSession));
            var cutoffDue = CutoffDue(state.Timer, now);
            var lowTimeDue = !deadlineDue && state.Timer.IsRunning && state.Timer.LowTime.Enabled && !state.Timer.LowTimePlayed
                && Remaining(state.Timer, now) > 0
                && Remaining(state.Timer, now) <= (state.Timer.LowTime.ThresholdSeconds ?? AudioSettings.From(state).LowTimeThresholdSeconds);
            if (!deadlineDue && !cutoffDue && !lowTimeDue) return;
            Change(cutoffDue ? "timer.autoRestartDisabled" : deadlineDue ? "timer.deadline" : "timer.lowTime", s => {
                // A cutoff is independent of the countdown, including while paused.
                // Expire before completion so no extra repeat starts at the boundary.
                ExpireAutoRestart(s, now);
                if (lowTimeDue) s.Timer = s.Timer with { LowTimePlayed = true };
                if (!deadlineDue) return;
                if (completed) CompletePrompt(s, now);
                ApplyScheduleHandoff(s, now, completed);
            }, value: dueCount(state, now));
            if (lowTimeDue) lowTimeAlert = state.Timer;
        }
        if (lowTimeAlert is not null) LowTimeReached?.Invoke(lowTimeAlert);
        static long dueCount(AppState value, long now) => value.Schedules.Count(x => x.StartTime <= now);
    }

    private static bool HasUnfinishedSession(TimerState timer) => timer.IsRunning || IsPaused(timer);
    private static void ApplyScheduleHandoff(AppState state, long now, bool completed)
    {
        // Natural completion and explicit early completion share one atomic
        // selection step. Never auto-start a repeat just to replace it next tick.
        var due = state.Schedules.Where(x => x.StartTime <= now && !x.WaitingForCurrentSession && !x.AwaitingDecision)
            .OrderBy(x => x.StartTime).ToList();
        var latest = due.LastOrDefault();
        var olderIds = due.Take(Math.Max(0, due.Count - 1)).Select(x => x.Id).ToHashSet();
        state.Schedules.RemoveAll(x => olderIds.Contains(x.Id));

        if (!completed && HasUnfinishedSession(state.Timer)) {
            if (latest is null) return;
            if (state.ScheduleOverlap is ScheduleOverlapPolicy.Ask or ScheduleOverlapPolicy.Wait) {
                state.Schedules = state.Schedules.Select(x => x.Id == latest.Id ? x with {
                    AwaitingDecision = state.ScheduleOverlap == ScheduleOverlapPolicy.Ask,
                    WaitingForCurrentSession = state.ScheduleOverlap == ScheduleOverlapPolicy.Wait
                } : x).ToList();
                return;
            }
            CompletePrompt(state, now);
        }

        // Merge the latest newly due appointment into the existing queue before
        // choosing. Older waiting appointments retain priority even at a deadline.
        if (latest is not null) state.Schedules = state.Schedules.Select(x => x.Id == latest.Id
            ? x with { WaitingForCurrentSession = true } : x).ToList();
        var waiting = state.Schedules.Where(x => x.WaitingForCurrentSession).OrderBy(x => x.StartTime).FirstOrDefault();
        if (waiting is not null) StartScheduled(state, waiting, now);
        else if (completed) RestartOrStop(state, now);
    }
    private static void StartScheduled(AppState state, ScheduledSession session, long now)
    {
        state.Schedules.RemoveAll(x => x.Id == session.Id);
        state.Timer = Started(session.DurationSeconds, session.AutoRestart, session.Volume, now, session.AutoRestartUntil, session.LowTime);
    }
    public void SetScheduleOverlap(ScheduleOverlapPolicy policy) => Change("schedule.policy", s => {
        if (!Enum.IsDefined(policy)) throw new ArgumentException("Choose a schedule overlap option.");
        s.ScheduleOverlap = policy;
    }, value: (int)policy);
    public void ResolveSchedule(Guid id, ScheduleDecision decision) => Change("schedule.resolved", s => {
        if (!Enum.IsDefined(decision)) throw new ArgumentException("Choose a scheduled-session action.");
        var session = s.Schedules.SingleOrDefault(x => x.Id == id && x.AwaitingDecision);
        if (session is null) return;
        if (decision == ScheduleDecision.Wait) {
            s.Schedules = s.Schedules.Select(x => x.Id == id ? x with { AwaitingDecision = false, WaitingForCurrentSession = true } : x).ToList();
        }
        else if (decision == ScheduleDecision.Skip) s.Schedules.RemoveAll(x => x.Id == id);
        else {
            if (HasUnfinishedSession(s.Timer)) CompletePrompt(s, Now);
            StartScheduled(s, session, Now);
        }
    }, id, (int)decision);

    public Guid SaveSchedule(Guid? id, DateTimeOffset start, int seconds, bool repeat, int volume, long? autoRestartUntil = null, LowTimeOptions? lowTime = null)
    {
        ValidateDuration(seconds);
        lowTime ??= new(); AudioSettings.Validate(lowTime);
        var itemId = id ?? Guid.NewGuid();
        Change("schedule.saved", s => {
            if (start.ToUnixTimeMilliseconds() <= Now) throw new ArgumentException("Choose a future session start date and time.");
            ValidateCutoff(autoRestartUntil, start.ToUnixTimeMilliseconds());
            if (id.HasValue && !s.Schedules.Any(x => x.Id == id)) throw new ArgumentException("This session already started or was removed.");
            if (!id.HasValue && s.Schedules.Count >= 50) throw new ArgumentException("You can schedule up to 50 sessions.");
            if (s.Schedules.Any(x => x.Id != itemId && x.StartTime == start.ToUnixTimeMilliseconds())) throw new ArgumentException("Another session already starts at that time.");
            s.Schedules.RemoveAll(x => x.Id == itemId);
            s.Schedules.Add(new(itemId, start.ToUnixTimeMilliseconds(), seconds, repeat || autoRestartUntil.HasValue, Math.Clamp(volume, 0, 100), autoRestartUntil) { LowTime = lowTime });
            s.Schedules = s.Schedules.OrderBy(x => x.StartTime).ToList();
        }, itemId);
        return itemId;
    }
    public void RemoveSchedule(Guid id) => Change("schedule.removed", s => s.Schedules.RemoveAll(x => x.Id == id), id);
    public void ImportSchedules(IEnumerable<ScheduledSession> entries) => Change("schedule.saved", s => {
        foreach (var entry in entries) {
            ValidateDuration(entry.DurationSeconds);
            AudioSettings.Validate(entry.LowTime);
            if (entry.StartTime <= Now || s.Schedules.Any(x => x.StartTime == entry.StartTime)) continue;
            ValidateCutoff(entry.AutoRestartUntil, entry.StartTime);
            if (s.Schedules.Count >= 50) throw new ArgumentException("The import would exceed 50 scheduled sessions. No entries were imported.");
            s.Schedules.Add(entry with { Id = Guid.NewGuid(), AutoRestart = entry.AutoRestart || entry.AutoRestartUntil.HasValue, Volume = Math.Clamp(entry.Volume, 0, 100) });
        }
        s.Schedules = s.Schedules.OrderBy(x => x.StartTime).ToList();
    });
    public Guid TestPrompt()
    {
        var id = Guid.NewGuid();
        Change("prompt.test", s => s.Prompts.Add(new(id, Now, s.Timer.DurationSeconds, s.Timer.Volume, true) {
            ActualDurationSeconds = s.Timer.DurationSeconds
        }), id);
        return id;
    }
    public Guid CheckIn()
    {
        lock (gate) {
            if (!HasUnfinishedSession(state.Timer) || RemainingMilliseconds(state.Timer, Now) <= 0)
                throw new ArgumentException("Start a timer before making a check-in. Older entries are available under Pending reflections.");
            var existing = state.Prompts.FirstOrDefault(p => p.IsCheckIn && p.CheckInSessionId is not null && p.CheckInSessionId == state.Timer.SessionId);
            if (existing is not null) return existing.Id;
            var id = Guid.NewGuid();
            Change("prompt.checkIn", s => {
                // Upgrade an already-running legacy timer without restarting it.
                s.Timer = s.Timer with { SessionId = s.Timer.SessionId ?? Guid.NewGuid() };
                s.Prompts.Add(new(id, Now, s.Timer.DurationSeconds, s.Timer.Volume, false) {
                    IsCheckIn = true, CheckInSessionId = s.Timer.SessionId, SessionId = s.Timer.SessionId,
                    ActualDurationSeconds = ActualSeconds(s.Timer, Now)
                });
            }, id);
            return id;
        }
    }
    public void SaveDraft(Guid id, string text, string? earlyEndReason = null)
    {
        if (text.Length > 5000) text = text[..5000];
        if (earlyEndReason?.Length > 1000) earlyEndReason = earlyEndReason[..1000];
        lock (gate)
        {
            var prompt = state.Prompts.SingleOrDefault(x => x.Id == id);
            if (prompt is null) return;
            text = ReflectionDrafts.Content(prompt, text);
            if (!state.Prompts.Any(x => x.Id == id && (x.Draft != text || ((x.EndedEarly||x.IsCheckIn) && earlyEndReason is not null && x.EarlyEndReason != earlyEndReason)))) return;
            Change("prompt.draftSaved", s => s.Prompts = s.Prompts.Select(x => x.Id == id ? x with {
                Draft = text, ContinuationSeparator = x.Draft == text ? x.ContinuationSeparator : null,
                EarlyEndReason = x.EndedEarly||x.IsCheckIn ? earlyEndReason ?? x.EarlyEndReason : x.EarlyEndReason
            } : x).ToList(), id);
        }
    }
    public void SaveReflectionForLater(Guid id, string text, string? earlyEndReason = null)
    {
        if (text.Length > 5000 || earlyEndReason?.Length > 1000) throw new ArgumentException("Keep the reflection within 5,000 characters and the reason within 1,000 characters.");
        Change("prompt.later", s => {
            var prompt = s.Prompts.SingleOrDefault(p => p.Id == id) ?? throw new ArgumentException("That reflection is no longer pending.");
            var saved = prompt with {
                Draft = ReflectionDrafts.Content(prompt, text), ContinuationSeparator = s.ReflectionSeparator,
                EarlyEndReason = prompt.EndedEarly || prompt.IsCheckIn ? earlyEndReason ?? prompt.EarlyEndReason : prompt.EarlyEndReason
            };
            s.Prompts = s.Prompts.Select(p => p.Id == id ? saved : p).ToList();
        }, id);
    }
    public void SkipPrompt(Guid id) => Change("prompt.skipped", s => s.Prompts.RemoveAll(x => x.Id == id), id);
    public bool AutoSendReflection(Guid promptId, bool localOnly = false)
    {
        lock(gate) {
            var prompt=state.Prompts.SingleOrDefault(p=>p.Id==promptId);
            if(prompt is null)return false; // A manual submission may already have finished.
            QueueReflection(promptId,prompt.Draft,prompt.EarlyEndReason,localOnly,autoSent:true);
            return true;
        }
    }
    public bool QueueReflection(Guid promptId, string text, string? earlyEndReason = null, bool localOnly = false, bool autoSent = false, bool endSession = false)
    {
        lock (gate) {
            var saved = state.Prompts.SingleOrDefault(p => p.Id == promptId) ?? throw new ArgumentException("This reflection has already been saved or dismissed.");
            text = ReflectionDrafts.Content(saved, text);
            text = text.Trim();
            if ((!autoSent && text.Length < 1) || text.Length > 5000) throw new ArgumentException("Write a reflection between 1 and 5,000 characters.");
            if (earlyEndReason?.Trim().Length > 1000) throw new ArgumentException("Keep the reason for ending early under 1,001 characters.");
            // Bind ending to this draft's own unfinished session. A delayed send of
            // an older reflection must never stop a newer running or paused timer.
            var complete = endSession && !autoSent && saved.IsCheckIn && saved.CheckInSessionId is {} sessionId
                && sessionId == state.Timer.SessionId && HasUnfinishedSession(state.Timer);
            Change(complete ? "reflection.endedAndQueued" : autoSent ? "reflection.autoSent" : "reflection.queued", s => {
                var prompt = s.Prompts.SingleOrDefault(x => x.Id == promptId) ?? throw new ArgumentException("This reflection has already been saved or dismissed.");
                var submittedAt = clock();
                if (complete) {
                    var now = submittedAt.ToUnixTimeMilliseconds();
                    ExpireAutoRestart(s, now);
                    CompletePrompt(s, now);
                    prompt = s.Prompts.Single(p => p.Id == promptId);
                    ApplyScheduleHandoff(s, now, completed: true);
                }
                var actual = prompt.IsCheckIn && prompt.CheckInSessionId is not null && prompt.CheckInSessionId == s.Timer.SessionId
                    ? ActualSeconds(s.Timer, submittedAt.ToUnixTimeMilliseconds()) : prompt.ActualDurationSeconds;
                s.Outbox.Add(new() {
                    Id = promptId, SessionId = prompt.SessionId ?? prompt.CheckInSessionId, Message = text, SubmittedAt = submittedAt, DurationSeconds = prompt.DurationSeconds, LocalOnly = localOnly,
                    ActualDurationSeconds = actual, EndedEarly = prompt.EndedEarly, IsCheckIn = prompt.IsCheckIn, AutoSent = autoSent,
                    EarlyEndReason = prompt.EndedEarly ? (earlyEndReason ?? prompt.EarlyEndReason).Trim() : "",
                    ReceiverUrl = s.Connection.WebAppUrl,
                    IsTest = prompt.IsTest, SheetUrl = s.Connection.SheetUrl, SheetMode = s.Connection.SheetMode, SheetName = s.Connection.SheetName
                });
                s.Prompts.RemoveAll(x => x.Id == promptId);
                // Keep at most 200 sent entries. Never automatically prune unsent work.
                var oldSent = s.Outbox.Where(x => x.Status == DeliveryStatus.Sent).OrderByDescending(x => x.SubmittedAt).Skip(200).Select(x => x.Id).ToHashSet();
                s.Outbox.RemoveAll(x => oldSent.Contains(x.Id));
            }, promptId);
            return complete;
        }
    }
    // Resolve the shortcut against authoritative session state under the same
    // lock as the save. Browser state can be one tick behind at the deadline.
    public (bool Queued, bool SessionCompleted) SaveOrSendReflection(Guid promptId, string text, string? reason = null, bool localOnly = false)
    {
        lock (gate) {
            var prompt = state.Prompts.SingleOrDefault(p => p.Id == promptId)
                ?? throw new ArgumentException("This reflection has already been saved or dismissed.");
            var attached = !prompt.IsTest && prompt.IsCheckIn && prompt.CheckInSessionId is { } id
                && id == state.Timer.SessionId && HasUnfinishedSession(state.Timer);
            if (attached && RemainingMilliseconds(state.Timer, Now) > 0) {
                SaveReflectionForLater(promptId, text, reason);
                return (false, false);
            }
            // Only an already-expired attached timer needs promotion before
            // sending. This cannot fast-forward a running or paused session.
            return (true, QueueReflection(promptId, text, reason, localOnly, endSession: attached));
        }
    }
    public OutboxItem? BeginUpload(bool supportsSafeRetry = false)
    {
        lock (gate)
        {
            var item = state.Outbox.FirstOrDefault(x => !x.LocalOnly && x.Status == DeliveryStatus.Pending && !(x.NextAttemptAt > Now));
            if (item is null) return null;
            if ((item.RetryProtected && !supportsSafeRetry) || (item.ReceiverUrl.Length > 0 && !ConnectionSetup.SameReceiver(item.ReceiverUrl, state.Connection.WebAppUrl))) {
                Change("upload.needsReview", s => s.Outbox = s.Outbox.Select(x => x.Id == item.Id
                    ? x with { Status = DeliveryStatus.NeedsReview, ErrorKind = "receiver_changed", NextAttemptAt = null } : x).ToList(), item.Id);
                return null;
            }
            Change("upload.started", s => s.Outbox = s.Outbox.Select(x => x.Id == item.Id
                ? x with { Status = DeliveryStatus.Sending, Attempts = x.Attempts + 1, ErrorKind = "", NextAttemptAt = null,
                    RetryProtected = x.RetryProtected || (x.Attempts == 0 && supportsSafeRetry),
                    ReceiverUrl = x.ReceiverUrl.Length > 0 ? x.ReceiverUrl : s.Connection.WebAppUrl } : x).ToList(), item.Id);
            return state.Outbox.Single(x => x.Id == item.Id);
        }
    }
    public void FinishUpload(Guid id, bool success, string errorKind = "", string tab = "", bool retryable = false) => Change(success ? "upload.sent" : "upload.needsReview", s =>
        s.Outbox = s.Outbox.Select(x => {
            if (x.Id != id) return x;
            var retry = !success && retryable && x.RetryProtected && x.Attempts < 8;
            return x with { Status = success ? DeliveryStatus.Sent : retry ? DeliveryStatus.Pending : DeliveryStatus.NeedsReview,
                NextAttemptAt = retry ? Now + Math.Min(300, 15 * (1 << Math.Clamp(x.Attempts - 1, 0, 5))) * 1000L : null,
                ErrorKind = success ? "" : SafeError(errorKind), SavedTab = success ? tab : "" };
        }).ToList(), id);
    public void RetryUpload(Guid id) => Change("upload.retryRequested", s => {
        if (s.Outbox.Any(x => x.Id == id && x.Status == DeliveryStatus.Sending)) throw new ArgumentException("This reflection is still sending.");
        s.Outbox = s.Outbox.Select(x => x.Id == id && x.Status != DeliveryStatus.Sent
            ? x with { Status = DeliveryStatus.Pending, ErrorKind = "", NextAttemptAt = null,
                // Explicit UI confirmation is required before retrying a partial write as new.
                Id = x.ErrorKind is "write_uncertain" or "id_conflict" ? Guid.NewGuid() : x.Id,
                Attempts = x.ErrorKind is "write_uncertain" or "id_conflict" ? 0 : x.Attempts } : x).ToList();
    }, id);
    public void MarkAlreadySent(Guid id) => Change("upload.confirmedByUser", s =>
        s.Outbox = s.Outbox.Select(x => x.Id == id && x.Status == DeliveryStatus.NeedsReview ? x with { Status = DeliveryStatus.Sent, ErrorKind = "" } : x).ToList(), id);
    public void SetFloatingTimer(bool visible) => Change("display.changed", s => s.ShowFloatingTimer = visible);
    public void SetAutoSendIncompleteReflections(bool enabled) => Change("settings.saved", s => s.AutoSendIncompleteReflections = enabled);
    public void SetReflectionSeparator(ReflectionSeparator separator)
    {
        if (!Enum.IsDefined(separator)) throw new ArgumentException("Choose a saved reflection separator from the list.");
        Change("settings.saved", s => s.ReflectionSeparator = separator);
    }
    public void SetAlwaysOnTop(bool compact, bool timeOnly, bool prompt) => Change("display.changed", s => {
        s.CompactAlwaysOnTop = compact; s.TimeOnlyAlwaysOnTop = timeOnly; s.PromptAlwaysOnTop = prompt;
    });
    public void SetFloatingTimerPosition(int left, int top) => Change("display.changed", s => {
        s.FloatingTimerLeft = left; s.FloatingTimerTop = top; s.FloatingPlacement = FloatingTimerPlacement.Custom;
    });
    public void SetFloatingTimerPlacement(FloatingTimerPlacement placement) => Change("display.changed", s => {
        if (!Enum.IsDefined(placement)) throw new ArgumentException("Choose a compact timer position from the list.");
        s.FloatingPlacement = placement;
    });
    public void SetPopupPosition(ReflectionPopupPosition position) => Change("display.changed", s => {
        if (!Enum.IsDefined(position)) throw new ArgumentException("Choose a reflection popup position from the list.");
        s.PopupPosition = position;
    }, value: (int)position);
    public void SetTheme(AppColorTheme theme) => Change("theme.changed", s => {
        if (!Enum.IsDefined(theme)) throw new ArgumentException("Choose a theme from the list.");
        s.Theme = theme;
    }, value: (int)theme);
    public void SetAlertSound(string path) => SetSound(SoundEvent.SessionEnd,
        AudioSettings.From(Snapshot).SessionEnd with { Mp3Path = path, Track = LibrarySound.Default });
    public void SetSound(SoundEvent kind, SoundSetting setting) => Change("sound.changed", s => {
        AudioSettings.Validate(setting);
        s.Audio = AudioSettings.From(s).With(kind, setting);
        if (kind == SoundEvent.SessionEnd) s.AlertSoundPath = setting.Mp3Path;
    }, value: (int)kind);
    public void SetLowTimeDefault(int seconds) => Change("sound.thresholdChanged", s => {
        ValidateDuration(seconds); s.Audio = AudioSettings.From(s) with { LowTimeThresholdSeconds = seconds };
    }, value: seconds);
    public void SetLowTime(LowTimeOptions options) => Change("timer.lowTimeOptions", s => {
        AudioSettings.Validate(options); s.Timer = s.Timer with { LowTime = options };
    });
    public void SaveSetupDraft(ConnectionSettings draft, bool? usesExistingReceiver = null) => Change("settings.saved", s => {
        s.SetupDraft = draft; s.SetupDraftUsesExistingReceiver = usesExistingReceiver;
    });
    public void CompleteSetup(ConnectionSettings connection, bool extensionDisabled) => Change("settings.saved", s => {
        var error = SheetsClient.Validate(connection);
        if (error is not null) throw new ArgumentException(error);
        if (!extensionDisabled) throw new ArgumentException("Confirm that the Chrome extension is off or not installed.");
        ProtectConnectionChange(s, connection);
        BindFirstConnection(s, connection);
        s.Connection = connection; s.SetupDraft = null; s.SetupDraftUsesExistingReceiver = null; s.ExtensionDisabledConfirmed = true;
    });
    public void SaveSettings(ConnectionSettings connection, bool logging, bool startAtLogin, bool extensionDisabled, int? lowTimeThresholdSeconds = null) => Change("settings.saved", s => {
        ProtectConnectionChange(s, connection);
        BindFirstConnection(s, connection);
        if (lowTimeThresholdSeconds is { } seconds) {
            ValidateDuration(seconds);
            s.Audio = AudioSettings.From(s) with { LowTimeThresholdSeconds = seconds };
        }
        if (s.Connection != connection) { s.SetupDraft = null; s.SetupDraftUsesExistingReceiver = null; }
        s.Connection = connection; s.LoggingEnabled = logging; s.StartAtLogin = startAtLogin; s.ExtensionDisabledConfirmed = extensionDisabled;
    });
    private static bool NeverAttempted(OutboxItem item) => item.Attempts == 0 && !item.RetryProtected && item.Status == DeliveryStatus.Pending;
    private static bool CanUseConnection(OutboxItem item, ConnectionSettings connection)
    {
        if (item.LocalOnly) return true;
        var sameSheet = ConnectionSetup.SameSpreadsheet(item.SheetUrl, connection.SheetUrl);
        var sameReceiver = ConnectionSetup.SameReceiver(item.ReceiverUrl, connection.WebAppUrl);
        return (sameSheet && sameReceiver) || (NeverAttempted(item)
            && (!ConnectionSetup.HasSpreadsheet(item.SheetUrl) || sameSheet)
            && (!ConnectionSetup.IsReceiverUrl(item.ReceiverUrl) || sameReceiver));
    }
    private static void ProtectConnectionChange(AppState s, ConnectionSettings connection)
    {
        var destinationChanged = !ConnectionSetup.SameSpreadsheet(s.Connection.SheetUrl, connection.SheetUrl)
            || !ConnectionSetup.SameReceiver(s.Connection.WebAppUrl, connection.WebAppUrl);
        var knownSheet = ConnectionSetup.HasSpreadsheet(s.Connection.SheetUrl);
        var knownReceiver = ConnectionSetup.IsReceiverUrl(s.Connection.WebAppUrl);
        var accountChanged = (knownSheet && !ConnectionSetup.SameSpreadsheet(s.Connection.SheetUrl, connection.SheetUrl))
            || (knownReceiver && !ConnectionSetup.SameReceiver(s.Connection.WebAppUrl, connection.WebAppUrl));
        var routeChanged = s.Connection.SheetMode != connection.SheetMode
            || (connection.SheetMode == "fixed" && s.Connection.SheetName != connection.SheetName);
        // Completing absent/invalid fields is not an account switch. Existing
        // usable destination pieces and all attempted entries remain protected.
        if ((destinationChanged && s.Outbox.Any(x => x.Status != DeliveryStatus.Sent && !CanUseConnection(x, connection)))
            || ((accountChanged || (routeChanged && (knownSheet || knownReceiver))) && (s.Timer.IsRunning || IsPaused(s.Timer) || s.Prompts.Count > 0)))
            throw new InvalidOperationException("Finish the active session and pending reflections in the current destination before switching spreadsheets, receivers, or tabs. Queued entries keep their original destination; finish delivery before changing accounts.");
    }
    private static void BindFirstConnection(AppState s, ConnectionSettings connection)
    {
        if (SheetsClient.Validate(connection) is not null) return;
        s.Outbox = s.Outbox.Select(x => {
            if (x.LocalOnly || !NeverAttempted(x) || !CanUseConnection(x, connection)) return x;
            var hasSheet = ConnectionSetup.HasSpreadsheet(x.SheetUrl); var hasReceiver = ConnectionSetup.IsReceiverUrl(x.ReceiverUrl);
            if (hasSheet && hasReceiver) return x;
            return x with {
                SheetUrl = hasSheet ? x.SheetUrl : connection.SheetUrl, ReceiverUrl = hasReceiver ? x.ReceiverUrl : connection.WebAppUrl,
                SheetMode = hasSheet || hasReceiver ? x.SheetMode : connection.SheetMode,
                SheetName = hasSheet || hasReceiver ? x.SheetName : connection.SheetName
            };
        }).ToList();
    }
    public static string SafeError(string kind) => new[] { "timeout", "network", "rejected", "invalid_response", "settings_required", "interrupted", "storage", "unknown", "write_uncertain", "id_conflict", "receiver_changed", "receiver_update_required" }.Contains(kind) ? kind : "unknown";
}
