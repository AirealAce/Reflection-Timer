using System.Text.Json;
using ReflectionTimer.Accessible;
using ReflectionTimer.Core;

// Read the same clock the viewers present without committing any timer action.
static class TimerStatusShortcutTests
{
    internal static void Run(Action<bool, string> check)
    {
        var now = DateTimeOffset.Parse("2026-10-12T10:00:00-04:00");
        var store = new CountingStore { State = new() { LoggingEnabled = false, Csv = new() { Enabled = false },
            SessionEndPopups = false, Timer = new() { Volume = 0 } } };
        var session = new PreviewSession(store, () => now, isolatedProfile: true);
        var changes = 0; session.Engine.Changed += () => changes++;
        var draftChanges = 0; session.DurationDraftChanged += _ => draftChanges++;
        void Read(string expected, string name)
        {
            var before = JsonSerializer.Serialize(session.Engine.Snapshot);
            var input = JsonSerializer.SerializeToElement(session.View(), PreviewSession.Json).GetProperty("durationDraft").GetRawText();
            var writes = store.Saves; var events = changes; var inputs = draftChanges;
            check(session.StatusMessage() == expected, name + ": announces accurate mode, state and time");
            check(JsonSerializer.Serialize(session.Engine.Snapshot) == before && store.Saves == writes && changes == events
                && draftChanges == inputs && JsonSerializer.SerializeToElement(session.View(), PreviewSession.Json).GetProperty("durationDraft").GetRawText() == input,
                name + ": status reads neither write state nor change session, prompts, Outbox or duration inputs");
        }
        Read("Timer ready, 15 minutes set.", "Fresh ready timer");
        session.SetDurationDraft(["1", "2", "3"]);
        Read("Timer ready, 1 hour 2 minutes 3 seconds set.", "Ready timer with edited input");
        session.SetDurationDraft(["", "", ""]);
        Read("Timer ready, 0 seconds set.", "Ready timer with empty zero-valued inputs");
        session.SetDurationDraft(["invalid", "0", "0"]);
        Read("Timer ready, 15 minutes set.", "Ready timer with invalid unsaved input");
        session.SetDurationDraft(null); session.Engine.Start(125, false, 0);
        now = now.AddMilliseconds(60500);
        Read("Timer running, 1 minute 5 seconds remaining.", "Running timer between ordinary clock ticks");
        session.SetDurationDraft(["0", "0", "9"]);
        Read("Timer running, 1 minute 5 seconds remaining.", "Running timer ignores a different ready duration draft");
        session.SetDurationDraft(null); session.Engine.Pause(); now = now.AddMinutes(7);
        Read("Timer paused, 1 minute 5 seconds remaining.", "Paused timer retains frozen remaining time");
        session.SetDurationDraft(["0", "0", "37"]);
        Read("Timer paused, 37 seconds remaining.", "Paused timer reflects its edited duration preview consistently with viewers");
        session.SetDurationDraft(["invalid", "0", "0"]);
        Read("Timer paused, 1 minute 5 seconds remaining.", "Paused timer ignores invalid draft without resuming");
        session.SetDurationDraft(null); session.Engine.Resume(); now = now.AddMilliseconds(64500);
        Read("Timer running, 0 seconds remaining.", "Deadline status query does not complete or restart the timer itself");
        check(session.Engine.CurrentTimer.IsRunning && session.Engine.Snapshot.Prompts.Count == 0,
            "Status at the deadline leaves normal completion to the ordinary timer tick");
        session.Tick();
        Read("Timer finished, 2 minutes 5 seconds set.", "Finished timer announces its next duration preview");
        session.SetDurationDraft(["0", "3", "0"]);
        Read("Timer finished, 3 minutes set.", "Finished timer follows new input without changing completed accounting");
        check(session.Engine.Snapshot.Prompts.Single().ActualDurationSeconds == 125 && session.Engine.CurrentTimer.DurationSeconds == 125,
            "Reading a finished input preview preserves the original reflection duration");
        session.SetDurationDraft(null); session.Engine.SwitchMode(SessionMode.Stopwatch);
        Read("Stopwatch ready, 0 seconds elapsed.", "Ready stopwatch");
        session.Engine.StartStopwatch(); now = now.AddMilliseconds(61250);
        Read("Stopwatch running, 1 minute 1 second elapsed.", "Running stopwatch between ordinary ticks");
        session.Engine.Pause(); now = now.AddHours(1);
        Read("Stopwatch paused, 1 minute 1 second elapsed.", "Paused stopwatch excludes the pause duration");
        session.Engine.Resume(); now = now.AddMilliseconds(1750);
        Read("Stopwatch running, 1 minute 3 seconds elapsed.", "Resumed stopwatch includes all active segments");
        session.Engine.SetVoiceAnnouncements(false); store.Fail = true;
        Read("Stopwatch running, 1 minute 3 seconds elapsed.", "Status remains readable with app voice disabled and storage unavailable");
        store.Fail = false;

        var completedStore = new CountingStore { State = new() { Timer = new() { Mode = SessionMode.Stopwatch,
            SessionId = Guid.NewGuid(), ElapsedMilliseconds = 63000, StopwatchCompleted = true, Volume = 0 } } };
        var completed = new PreviewSession(completedStore, () => now, isolatedProfile: true);
        var completedBefore = JsonSerializer.Serialize(completed.Engine.Snapshot); var completedWrites = completedStore.Saves;
        check(completed.StatusMessage() == "Stopwatch finished, 0 seconds elapsed.",
            "Completed stopwatch announces the zero preview ready for the next session");
        check(JsonSerializer.Serialize(completed.Engine.Snapshot) == completedBefore && completedStore.Saves == completedWrites
            && completed.Engine.CurrentTimer.ElapsedMilliseconds == 63000,
            "Completed stopwatch status preserves its historical elapsed time without writing or resetting");
    }
    private sealed class CountingStore : IStateStore
    {
        internal AppState State = new(); internal int Saves; internal bool Fail;
        public AppState Load() => DataJson.Clone(State);
        public void Save(AppState state) { if (Fail) throw new IOException("Synthetic unavailable storage"); State = DataJson.Clone(state); Saves++; }
    }
}
