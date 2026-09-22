using System.Text.Json;
using ReflectionTimer.Accessible;
using ReflectionTimer.Core;
using ReflectionTimer.Desktop;

static class ClockAccuracyTests
{
    internal static void Run(Action<bool, string> check)
    {
        foreach (var mode in Enum.GetValues<SessionMode>()) {
            using var test = new Fixture(); test.Start(mode);
            test.Clock.Advance(30250);
            foreach (var correction in new[] { -7200, 14400, -86400 }) {
                test.Clock.Shift(correction); test.Engine.Advance();
                check(test.Spent == 30250 && test.Engine.Snapshot.Prompts.Count == 0,
                    $"Calendar correction {correction}s cannot change active {mode} elapsed time or finish it");
                check(test.Seconds == (mode == SessionMode.Timer ? 270 : 30), "Visual and spoken clocks follow elapsed time after correction: " + mode);
            }
            test.Clock.Advance(875);
            check(test.Spent == 31125, "Elapsed progress continues with millisecond precision after clock corrections: " + mode);
            test.Engine.SetAppVolume(0);
            check(test.Spent == 31125 && test.Store.State.Timer.ClockSavedAt == test.Clock.Wall.ToUnixTimeMilliseconds(),
                "Ordinary settings saves checkpoint timing without resetting elapsed work: " + mode);
        }

        foreach (var mode in Enum.GetValues<SessionMode>()) {
            using var test = new Fixture(); test.Start(mode, 100);
            test.Clock.Advance(20125); var inputAt = test.Engine.ElapsedNow;
            test.Clock.Advance(7000); test.Clock.Shift(-3600);
            test.Store.OnSave = () => { test.Clock.Advance(9000); test.Clock.Shift(7200); };
            test.Session.Execute("toggle", Data(new { }), inputAt);
            check(test.Spent == 20125 && TimerEngine.IsPaused(test.Engine.Snapshot.Timer),
                "Pause excludes input queue, storage delay and intervening clock corrections: " + mode);
            test.Store.OnSave = null; test.Clock.Advance(100000); test.Engine.Resume();
            test.Clock.Advance(10500); test.Engine.Pause();
            check(test.Spent == 30625, "Pause/resume preserves fractional seconds and excludes paused time: " + mode);
            test.Engine.Resume(); var freshStart = test.Engine.ElapsedNow;
            test.Clock.Advance(3000); test.Engine.Pause(freshStart - 100000);
            check(test.Spent == 30625, "A stale pause timestamp cannot remove time accrued before this running segment: " + mode);
        }

        using (var test = new Fixture()) {
            test.Start(SessionMode.Stopwatch); test.Clock.Advance(75375);
            var intent = test.Engine.ElapsedNow; var expectedDate = test.Engine.Now;
            test.Clock.Advance(4000); test.Store.OnSave = () => test.Clock.Advance(6000);
            var prompt = test.Engine.ReviewStopwatch(intent);
            check(test.Spent == 75375 && test.Engine.Snapshot.Prompts.Single().ActualDurationSeconds == 75,
                "Stopwatch review freezes before popup, storage or UI-queue work can add time");
            check(test.Engine.Snapshot.Prompts.Single().CompletedAt == expectedDate, "Review keeps the calendar date corresponding to the captured stop intent");
            test.Store.OnSave = null; test.Clock.Advance(180000); test.Clock.Shift(-86400);
            test.Engine.SaveReflectionForLater(prompt, "Synthetic saved work"); test.Clock.Advance(625);
            check(test.Spent == 76000 && test.Engine.Snapshot.Timer.IsRunning, "Saving the current stopwatch draft resumes it without counting reflection-writing time");
            test.Engine.ReviewStopwatch(); test.Engine.QueueReflection(prompt, "Synthetic final work", localOnly: true);
            check(test.Engine.Snapshot.Outbox.Single().ActualDurationSeconds == 76 && test.Engine.Snapshot.Timer.StopwatchCompleted,
                "Stopwatch delivery retains corrected elapsed time after draft save/resume");
        }

        using (var test = new Fixture()) {
            test.Start(SessionMode.Stopwatch); test.Clock.Advance(20125);
            var intent = test.Engine.ElapsedNow; test.Clock.Advance(5000);
            test.Engine.SwitchMode(SessionMode.Timer, intent);
            test.Engine.Start(100, false, 0); test.Clock.Advance(15375); test.Clock.Shift(3600);
            var timerIntent = test.Engine.ElapsedNow; test.Clock.Advance(3000);
            test.Engine.SwitchMode(SessionMode.Stopwatch, timerIntent);
            check(test.Spent == 20125 && !test.Engine.Snapshot.Timer.IsRunning && !test.Engine.Snapshot.ParkedTimer!.IsRunning,
                "Mode switches preserve the paused stopwatch and never run both modes");
            check(test.Engine.Snapshot.ParkedTimer!.PausedRemainingMilliseconds == 84625,
                "Switching away from a countdown also excludes queued-input delay");
            test.Engine.Resume(); test.Clock.Advance(875); var prompt = test.Engine.ReviewStopwatch();
            test.Engine.SwitchMode(SessionMode.Timer); test.Engine.Resume(); test.Clock.Advance(4000);
            test.Engine.SaveReflectionForLater(prompt, "Synthetic parked draft");
            check(!test.Engine.Snapshot.ParkedTimer!.IsRunning && test.Engine.Snapshot.Timer.IsRunning,
                "Saving a parked stopwatch draft cannot resume a second clock");
            test.Engine.QueueReflection(prompt, "Synthetic parked completion", localOnly: true);
            check(test.Engine.Snapshot.Outbox.Single().ActualDurationSeconds == 21 && test.Engine.Snapshot.Timer.IsRunning,
                "A parked stopwatch submission cannot borrow time from the running countdown");
        }

        foreach (var action in new[] { "end", "queue" }) {
            using var test = new Fixture(); test.Engine.Start(100, true, 0);
            var prompt = test.Engine.CheckIn(); test.Clock.Advance(23125); var intent = test.Engine.ElapsedNow;
            test.Clock.Advance(6000); test.Store.OnSave = () => test.Clock.Advance(8000);
            test.Session.Execute(action, Data(new { id = prompt, text = "Synthetic early completion", reason = "Synthetic reason", endSession = true }), intent);
            check((action == "end" ? test.Engine.Snapshot.Prompts.Single().ActualDurationSeconds : test.Engine.Snapshot.Outbox.Single().ActualDurationSeconds) == 23,
                "Countdown completion records input-intent time before queue/storage delay: " + action);
            check(test.Engine.Snapshot.Timer.IsRunning && test.Engine.Snapshot.Timer.SessionId != test.Engine.Snapshot.Outbox.FirstOrDefault()?.SessionId,
                "Early completion retains normal auto-start behavior: " + action);
            if (action == "queue") check(test.Engine.Snapshot.Prompts.Count == 0, "Sending early still suppresses a duplicate completion popup");
        }

        foreach (var mode in Enum.GetValues<SessionMode>()) {
            using var test = new Fixture(); test.Start(mode, 1200);
            test.Engine.SaveSchedule(null, test.Clock.Wall.AddMinutes(5), 120, false, 0);
            test.Clock.Advance(17000); test.Clock.Shift(600); test.Engine.Advance();
            check(test.Engine.Snapshot.Prompts.Single().ActualDurationSeconds == 17 && test.Engine.Snapshot.Timer.Mode == SessionMode.Timer
                && test.Engine.Snapshot.Timer.DurationSeconds == 120 && test.Spent == 0,
                "Calendar-based scheduling interrupts only real elapsed work after a clock correction: " + mode);
            check(test.Engine.Snapshot.Prompts.Single().CompletedAt == test.Engine.Now, "Schedule reflection dates remain calendar dates: " + mode);
        }

        using (var test = new Fixture()) {
            test.Engine.Start(300, true, 0, test.Engine.Now + 120000);
            test.Clock.Advance(10000); test.Clock.Shift(3600); test.Engine.Advance();
            check(!test.Engine.Snapshot.Timer.AutoRestart && test.Engine.Snapshot.Timer.IsRunning && test.Spent == 10000,
                "A calendar cutoff disables auto-start without fast-forwarding the countdown");
            test.Clock.Advance(290000); test.Engine.Advance();
            check(test.Engine.Snapshot.Prompts.Count == 1 && !test.Engine.Snapshot.Timer.IsRunning,
                "The cutoff prevents a repeat at the real elapsed deadline");
        }

        using (var test = new Fixture()) {
            var warnings = 0; test.Engine.LowTimeReached += _ => warnings++;
            test.Engine.Start(120, false, 0, lowTime: new() { Enabled = true, ThresholdSeconds = 30 });
            test.Clock.Advance(89000); test.Clock.Shift(7200); test.Engine.Advance();
            check(warnings == 0 && test.Engine.Snapshot.Prompts.Count == 0, "A forward clock change cannot prematurely trigger low-time audio or completion");
            test.Clock.Advance(1000); test.Engine.Advance(); test.Clock.Shift(-14400); test.Engine.Advance();
            check(warnings == 1 && test.Seconds == 30, "Low-time threshold uses elapsed time and sounds only once across clock corrections");
            test.Clock.Advance(30000); test.Engine.Advance(); test.Engine.Advance();
            check(test.Engine.Snapshot.Prompts.Single().ActualDurationSeconds == 120 && !test.Engine.Snapshot.Prompts.Single().EndedEarly,
                "Natural countdown completion records the full allotted duration exactly once");
        }

        using (var test = new Fixture()) {
            var warnings = 0; test.Engine.TimeReached += _ => warnings++;
            test.Start(SessionMode.Stopwatch); test.Engine.SetTimeReached(true, 60);
            test.Clock.Advance(59999); test.Clock.Shift(86400); test.Engine.Advance();
            check(warnings == 0, "Stopwatch time-reached audio waits for its elapsed threshold");
            test.Clock.Advance(1); test.Engine.Advance(); test.Clock.Shift(-172800); test.Engine.Advance();
            check(warnings == 1, "Stopwatch time-reached audio fires once at the exact elapsed threshold");
        }

        using (var test = new Fixture()) {
            test.Clock.Wall = new(2026, 9, 22, 23, 59, 50, TimeSpan.FromHours(-4));
            test.Engine.Start(100, false, 0); var prompt = test.Engine.CheckIn();
            test.Clock.Advance(15500); test.Clock.Shift(86400);
            var activities = new List<Activity>(); test.Engine.ActivityRecorded += activities.Add;
            test.Engine.QueueReflection(prompt, "Synthetic dated reflection", localOnly: true, endSession: true);
            var entry = test.Engine.Snapshot.Outbox.Single();
            check(entry.ActualDurationSeconds == 15 && entry.SubmittedAt == test.Clock.Wall && entry.SubmittedAt.Offset == TimeSpan.FromHours(-4),
                "Sheet submission keeps current local date/offset independently of elapsed accounting");
            check(activities.Last().At == test.Clock.Wall.ToUnixTimeMilliseconds(), "Diagnostic events retain calendar timestamps, not process-local elapsed coordinates");
        }

        foreach (var mode in Enum.GetValues<SessionMode>()) {
            using var test = new Fixture(); test.Start(mode, 900); test.Clock.Advance(42125); test.Clock.Shift(-3600);
            test.Engine.Checkpoint(); var saved = test.Store.State.Timer;
            check(saved.ClockSavedAt == test.Clock.Wall.ToUnixTimeMilliseconds()
                && (mode == SessionMode.Stopwatch ? saved.ElapsedMilliseconds == 42125 && saved.RunningSince == saved.ClockSavedAt
                    : saved.RemainingMillisecondsAtSave == 857875 && saved.EndTime == saved.ClockSavedAt + 857875),
                "Saved timing is a portable calendar checkpoint with precise accumulated duration: " + mode);
            test.Clock.Advance(600000);
            var restarted = new Fixture(test.Store, new Clock { Wall = test.Clock.Wall, Ticks = 987654321 });
            using (restarted) {
                check(restarted.Spent == 642125 && restarted.Engine.ClockRecoveryNotice!.Contains("cannot be verified"),
                    "Restart includes documented downtime without reusing a process timestamp, and reports its estimate: " + mode);
                restarted.Engine.Pause(); restarted.Clock.Advance(300000);
                using var pausedRestart = new Fixture(test.Store, new Clock { Wall = restarted.Clock.Wall });
                check(pausedRestart.Spent == 642125 && pausedRestart.Engine.ClockRecoveryNotice is null,
                    "Paused sessions stay precise across downtime/restart without a running-time recovery warning: " + mode);
            }
        }

        foreach (var mode in Enum.GetValues<SessionMode>()) {
            using var test = new Fixture(); test.Start(mode, 100); test.Clock.Advance(24125); test.Engine.Checkpoint();
            var clock = new Clock { Wall = test.Clock.Wall.AddHours(-2) };
            using var restarted = new Fixture(test.Store, clock);
            check(restarted.Spent == 24125 && restarted.Engine.ClockRecoveryNotice!.Contains("earlier than"),
                "Backward-clock recovery retains saved work instead of inventing downtime: " + mode);
            clock.Advance(1000); var intent = restarted.Engine.ElapsedNow; clock.Advance(2000); restarted.Engine.Pause(intent);
            check(restarted.Spent == 25125, "Recovered running segments still honor captured pause intent: " + mode);
        }

        using (var test = new Fixture()) {
            test.Engine.Start(60, true, 0); var originalDeadline = test.Engine.Now + 60000;
            test.Clock.Advance(120000); test.Engine.Checkpoint(); test.Clock.Advance(3600000);
            using var restarted = new Fixture(test.Store, new Clock { Wall = test.Clock.Wall });
            restarted.Engine.Advance(); restarted.Engine.Advance();
            check(restarted.Engine.Snapshot.Prompts.Count == 1 && restarted.Engine.Snapshot.Prompts.Single().CompletedAt == originalDeadline,
                "An overdue checkpoint preserves the original completion date without creating missed-repeat reflections");
            check(restarted.Engine.Snapshot.Timer.IsRunning && restarted.Seconds == 60, "Recovery starts one fresh repeat rather than replaying every missed cycle");
        }

        foreach (var mode in Enum.GetValues<SessionMode>()) {
            using var test = new Fixture(); test.Start(mode, 900); test.Clock.Advance(20125);
            test.Clock.Shift(3600); var before = JsonSerializer.Serialize(test.Engine.Snapshot);
            test.Store.Fail = true;
            try { test.Engine.Advance(); throw new Exception("Failed clock checkpoint accepted"); } catch (IOException) { }
            check(JsonSerializer.Serialize(test.Engine.Snapshot) == before && test.Spent == 20125,
                "A failed clock-adjustment checkpoint leaves session state and accrued time intact: " + mode);
            test.Store.Fail = false; test.Clock.Advance(875); test.Engine.Advance();
            check(test.Spent == 21000 && test.Store.State.Timer.ClockSavedAt == test.Clock.Wall.ToUnixTimeMilliseconds(),
                "Clock checkpoint retries normally after storage recovers: " + mode);
            var writes = test.Store.Saves; test.Engine.Advance(); test.Engine.Advance();
            check(test.Store.Saves == writes, "Stable elapsed ticks do not add new per-second checkpoint writes: " + mode);
        }

        LegacyAndBackup(check);
        foreach (var mode in Enum.GetValues<SessionMode>()) {
            using var test = new Fixture(); test.Start(mode, 60); test.Clock.Advance(10000);
            test.Clock.Advance(3600000); // Simulate sleep: elapsed and calendar advance, but no application ticks execute.
            test.Engine.Advance(); test.Engine.Advance();
            check(mode == SessionMode.Stopwatch ? test.Spent == 3610000 && test.Engine.Snapshot.Timer.IsRunning
                : test.Engine.Snapshot.Prompts.Count == 1 && test.Engine.Snapshot.Prompts.Single().ActualDurationSeconds == 60,
                "Sleep/resume retains the existing running-session policy without duplicate completion: " + mode);
        }
        using (var test = new Fixture()) {
            test.Engine.Start(300, false, 0); test.Clock.Advance(12000); test.Clock.Shift(-3600); test.Engine.Advance();
            var view = JsonSerializer.SerializeToElement(test.Session.View(), PreviewSession.Json);
            check(view.GetProperty("clock").GetProperty("seconds").GetInt32() == 288
                && view.GetProperty("timer").GetProperty("endTime").GetInt64() == test.Engine.Now + 288000,
                "The desktop bridge shows elapsed countdown time and a correctly converted calendar end time");
            using var log = new DiagnosticLog(Path.Combine(Path.GetTempPath(), "ReflectionTimer-ClockReport-" + Guid.NewGuid().ToString("N"))) { Enabled = false };
            var report = JsonSerializer.SerializeToElement(log.Report(test.Engine.Snapshot, test.Engine.ElapsedNow, test.Engine.Now), PreviewSession.Json);
            check(report.GetProperty("timerClock").GetProperty("elapsedAt").GetInt64() == test.Engine.ElapsedNow
                && report.GetProperty("timerClock").GetProperty("calendarAt").GetInt64() == test.Engine.Now,
                "Diagnostic reports explicitly pair runtime timer coordinates with elapsed and calendar reference times");
        }
        var provider = new Clock(); var defaultEngine = new TimerEngine(new Store(), timeProvider: provider);
        defaultEngine.SwitchMode(SessionMode.Stopwatch); defaultEngine.StartStopwatch(); provider.Advance(3000); provider.Shift(7200);
        check(TimerEngine.ActualSeconds(defaultEngine.Snapshot.Timer, defaultEngine.ElapsedNow) == 3,
            "The production constructor path uses independent TimeProvider elapsed time without a calendar delegate");
    }

    private static void LegacyAndBackup(Action<bool, string> check)
    {
        check(!TimerEngine.IsPaused(TimerEngine.PauseRecoveredTimer(new TimerState(), DateTimeOffset.UtcNow)),
            "Backup recovery must not turn an idle countdown into a paused session");
        foreach (var mode in Enum.GetValues<SessionMode>()) {
            var clock = new Clock(); var wall = clock.Wall.ToUnixTimeMilliseconds();
            var legacy = new TimerState { Mode = mode, SessionId = Guid.NewGuid(), IsRunning = true, DurationSeconds = 100,
                RemainingSeconds = 100, EndTime = mode == SessionMode.Timer ? wall + 70000 : null,
                RunningSince = mode == SessionMode.Stopwatch ? wall - 30000 : null, ElapsedMilliseconds = mode == SessionMode.Stopwatch ? 125 : 0 };
            using var test = new Fixture(new Store(new() { Timer = legacy, Outbox = [new() { Message = "Synthetic historic entry", ActualDurationSeconds = 7 }] }), clock);
            check(test.Spent == (mode == SessionMode.Stopwatch ? 30125 : 30000) && test.Engine.Snapshot.FormatVersion == 1,
                "Legacy version-1 timing loads without resetting its session: " + mode);
            test.Engine.Checkpoint();
            check(test.Store.State.Outbox.Single().ActualDurationSeconds == 7 && test.Store.State.Outbox.Single().Message == "Synthetic historic entry",
                "Timing migration never recalculates old reflection totals or changes their content: " + mode);
            var recovered = TimerEngine.PauseRecoveredTimer(test.Store.State.Timer, clock.Wall.AddSeconds(2));
            check(!recovered.IsRunning && (mode == SessionMode.Stopwatch ? recovered.ElapsedMilliseconds == 32125 : recovered.PausedRemainingMilliseconds == 68000),
                "Backup recovery pauses portable timing while retaining precise work: " + mode);
        }
        var directory = Path.Combine(Path.GetTempPath(), "ReflectionTimer-Clock-" + Guid.NewGuid().ToString("N"));
        try {
            var store = new EncryptedStore(directory); var clock = new Clock(); var engine = new TimerEngine(store, () => clock.Wall, clock);
            engine.SwitchMode(SessionMode.Stopwatch); engine.StartStopwatch(); clock.Advance(12345); engine.Checkpoint();
            var saved = store.Load();
            check(saved.Timer.ClockSavedAt == clock.Wall.ToUnixTimeMilliseconds() && saved.Timer.ElapsedMilliseconds == 12345,
                "Encrypted profile round-trip retains the new portable timing metadata");
            check(EncryptedStore.Read<AppState>(Path.Combine(directory, "state.dat.bak")).Timer.IsRunning,
                "The existing encrypted backup transaction protects the preceding timing state");
            clock.Advance(2000); var reopened = new TimerEngine(store, () => clock.Wall, clock);
            check(TimerEngine.StopwatchMilliseconds(reopened.Snapshot.Timer, reopened.ElapsedNow) == 14345,
                "Reopening an encrypted profile includes documented downtime without losing fractional elapsed time");
        }
        finally {
            File.Delete(Path.Combine(directory, "state.dat")); File.Delete(Path.Combine(directory, "state.dat.bak"));
            if (Directory.Exists(directory)) Directory.Delete(directory);
        }
    }
    private static JsonElement Data(object value) => JsonSerializer.SerializeToElement(value, PreviewSession.Json);
    private sealed class Fixture : IDisposable
    {
        public readonly Clock Clock;
        public readonly Store Store;
        public readonly PreviewSession Session;
        public TimerEngine Engine => Session.Engine;
        public int Seconds => JsonSerializer.SerializeToElement(Session.Clock(), PreviewSession.Json).GetProperty("seconds").GetInt32();
        public long Spent {
            get {
                var timer = Engine.Snapshot.Timer;
                return timer.Mode == SessionMode.Stopwatch ? TimerEngine.StopwatchMilliseconds(timer, Engine.ElapsedNow)
                    : timer.DurationSeconds * 1000L - Math.Clamp(timer.IsRunning ? timer.EndTime!.Value - Engine.ElapsedNow
                        : timer.PausedRemainingMilliseconds ?? timer.RemainingSeconds * 1000L, 0, timer.DurationSeconds * 1000L);
            }
        }
        public Fixture(Store? store = null, Clock? clock = null)
        {
            Clock = clock ?? new(); Store = store ?? new(); Session = new(Store, () => Clock.Wall, true, Clock);
        }
        public void Start(SessionMode mode, int seconds = 300) { Engine.SwitchMode(mode); Engine.Start(seconds, false, 0); }
        public void Dispose() { } // Synthetic in-memory profile; no desktop windows or real audio backend.
    }
    private sealed class Store(AppState? state = null) : IStateStore
    {
        public AppState State = state ?? new() { LoggingEnabled = false };
        public bool Fail;
        public int Saves;
        public Action? OnSave;
        public AppState Load() => DataJson.Clone(State);
        public void Save(AppState value) { OnSave?.Invoke(); if (Fail) throw new IOException("Synthetic timing storage failure"); State = DataJson.Clone(value); Saves++; }
    }
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Wall = new(2026, 9, 22, 12, 0, 0, TimeSpan.Zero);
        public long Ticks;
        public override long GetTimestamp() => Ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override DateTimeOffset GetUtcNow() => Wall.ToUniversalTime();
        public void Advance(long milliseconds) { Ticks += milliseconds * TimeSpan.TicksPerMillisecond; Wall = Wall.AddMilliseconds(milliseconds); }
        public void Shift(int seconds) => Wall = Wall.AddSeconds(seconds);
    }
}
