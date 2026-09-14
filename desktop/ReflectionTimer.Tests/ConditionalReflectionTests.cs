using System.Text.Json;
using ReflectionTimer.Accessible;
using ReflectionTimer.Core;

static class ConditionalReflectionTests
{
    private static JsonElement Data(Guid id, string text, string reason = "") => JsonSerializer.SerializeToElement(new { id, text, reason });
    internal static void Run(Action<bool, string> check)
    {
        foreach (var paused in new[] { false, true }) foreach (var repeat in new[] { false, true }) foreach (var blank in new[] { false, true }) {
            var now = DateTimeOffset.Now; var store = new MemoryStore(); var session = new PreviewSession(store, () => now, true);
            session.Engine.Start(100, repeat, 0); now = now.AddSeconds(17); var id = session.Engine.CheckIn();
            if (paused) { session.Engine.Pause(); now = now.AddHours(1); }
            var timer = session.Engine.Snapshot.Timer; var before = JsonSerializer.Serialize(session.Engine.Snapshot);
            var data = Data(id, blank ? "" : "Latest response", blank ? "" : "Provisional reason");
            store.Fail = true;
            try { session.Execute("saveOrSendReflection", data); throw new Exception("Failed save was accepted"); } catch (IOException) { }
            check(JsonSerializer.Serialize(session.Engine.Snapshot) == before, "Conditional save failure preserves timer, draft and Outbox");
            store.Fail = false;
            var result = session.Execute("saveOrSendReflection", data); var saved = session.Engine.Snapshot;
            check(result.Close && !result.SessionCompleted && saved.Timer == timer && saved.Outbox.Count == 0,
                $"Ctrl+Enter saves without sending/ending: paused={paused}, repeat={repeat}, blank={blank}");
            check(saved.Prompts.Single() is { IsCheckIn: true, EndedEarly: false } prompt && prompt.Id == id
                && prompt.Draft == (blank ? "" : "Latest response") && prompt.EarlyEndReason == (blank ? "" : "Provisional reason"),
                "Conditional save retains both fields and the session link, including empty drafts");
            check(JsonSerializer.Serialize(new PreviewSession(store).Engine.Snapshot) == JsonSerializer.Serialize(saved), "Conditional saved draft survives reopening");
        }
        foreach (var early in new[] { false, true }) {
            var now = DateTimeOffset.Now; var session = new PreviewSession(new MemoryStore(), () => now, true);
            session.Engine.Start(100, true, 0); now = now.AddSeconds(early ? 23 : 100);
            if (early) session.Engine.EndEarly(); else session.Tick();
            var id = session.Engine.Snapshot.Prompts.Single().Id; var timer = session.Engine.Snapshot.Timer;
            var result = session.Execute("saveOrSendReflection", Data(id, "Completed response", "Early reason"));
            var sent = session.Engine.Snapshot.Outbox.Single();
            check(result.Close && !result.SessionCompleted && session.Engine.Snapshot.Timer == timer && sent.EndedEarly == early
                && sent.ActualDurationSeconds == (early ? 23 : 100) && sent.EarlyEndReason == (early ? "Early reason" : ""),
                "Ctrl+Enter sends a completed reflection without touching the newer auto-started session");
            try { session.Execute("saveOrSendReflection", Data(id, "Duplicate")); throw new Exception("Duplicate accepted"); } catch (ArgumentException) { }
            check(session.Engine.Snapshot.Outbox.Count == 1 && session.Engine.Snapshot.Timer == timer, "Repeated completed submission cannot duplicate a send or affect the next timer");
        }
        {
            var now = DateTimeOffset.Now; var store = new MemoryStore(); var session = new PreviewSession(store, () => now, true);
            session.Engine.Start(60, true, 0); var id = session.Engine.CheckIn(); now = now.AddSeconds(60);
            var before = JsonSerializer.Serialize(session.Engine.Snapshot); store.Fail = true;
            try { session.Execute("saveOrSendReflection", Data(id, "At deadline")); throw new Exception("Failed deadline send accepted"); } catch (IOException) { }
            check(JsonSerializer.Serialize(session.Engine.Snapshot) == before, "Deadline send is atomic on storage failure");
            store.Fail = false; var result = session.Execute("saveOrSendReflection", Data(id, "At deadline", "Not an early ending"));
            check(result.SessionCompleted && session.Engine.Snapshot.Prompts.Count == 0 && session.Engine.Snapshot.Outbox.Single() is {
                IsCheckIn: false, EndedEarly: false, ActualDurationSeconds: 60, EarlyEndReason: "" },
                "Ctrl+Enter resolves a deadline before the UI tick as a natural completion, without another popup");
            check(session.Engine.Snapshot.Timer is { IsRunning: true, AutoRestart: true }, "Deadline submission preserves auto-start");
        }
        {
            var now = DateTimeOffset.Now; var session = new PreviewSession(new MemoryStore(), () => now, true);
            session.Engine.Start(100, false, 0); now = now.AddSeconds(12); var id = session.Engine.CheckIn();
            session.Engine.Reset(); session.Engine.Start(60, false, 0); var timer = session.Engine.Snapshot.Timer;
            session.Execute("saveOrSendReflection", Data(id, "Old session"));
            check(session.Engine.Snapshot.Timer == timer && session.Engine.Snapshot.Outbox.Single().ActualDurationSeconds == 12,
                "A detached reflection is sent using its own elapsed time, not saved against a newer session");
            id = session.Engine.TestPrompt();
            session.Execute("saveOrSendReflection", Data(id, "Practice"));
            check(session.Engine.Snapshot.Timer == timer && session.Engine.Snapshot.Outbox.Last().IsTest, "Practice Ctrl+Enter sends only the practice reflection");
            id = session.Engine.TestPrompt(); var before = JsonSerializer.Serialize(session.Engine.Snapshot);
            try { session.Execute("saveOrSendReflection", Data(id, "   ")); throw new Exception("Empty completed reflection accepted"); } catch (ArgumentException) { }
            check(JsonSerializer.Serialize(session.Engine.Snapshot) == before, "An empty completed reflection is retained with a validation error, never skipped");
        }
    }
}
