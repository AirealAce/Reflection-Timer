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
                $"Ctrl+Enter saves content or skips empty fields without sending/ending: paused={paused}, repeat={repeat}, blank={blank}");
            check(blank ? saved.Prompts.Count==0 : saved.Prompts.Single() is { IsCheckIn: true, EndedEarly: false } prompt && prompt.Id == id
                && prompt.Draft == "Latest response" && prompt.EarlyEndReason == "Provisional reason",
                "Conditional command skips both-empty drafts and retains both fields when either has content");
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
            check(JsonSerializer.Serialize(session.Engine.Snapshot) == before, "A whitespace-only completed reflection is retained with a validation error, never skipped");
        }
        foreach(var kind in new[]{"natural","early","practice"}){
            var now=DateTimeOffset.Now;var session=new PreviewSession(new MemoryStore(),()=>now,true);
            session.Engine.Start(60,true,0);now=now.AddSeconds(kind=="early"?12:60);
            Guid id;
            if(kind=="practice")id=session.Engine.TestPrompt();
            else {if(kind=="early")session.Engine.EndEarly();else session.Tick();id=session.Engine.Snapshot.Prompts.Single().Id;}
            var timer=session.Engine.Snapshot.Timer;var skips=0;
            session.Engine.ActivityRecorded+=activity=>{if(activity.Event=="prompt.skipped")skips++;};
            var result=session.Execute("saveOrSendReflection",Data(id,"",""));
            check(result.Close&&!result.SessionCompleted&&session.Engine.Snapshot.Timer==timer&&session.Engine.Snapshot.Prompts.Count==0&&session.Engine.Snapshot.Outbox.Count==0&&skips==1,"Native both-empty conditional command uses Skip and preserves the current timer: "+kind);
        }
        foreach(var pair in new[]{("","Keep reason"),(" \n ","\t")}){
            var session=new PreviewSession(new MemoryStore());session.Engine.Start(60,false,0);var id=session.Engine.CheckIn();
            session.Execute("saveOrSendReflection",Data(id,pair.Item1,pair.Item2));
            check(session.Engine.Snapshot.Prompts.Single() is {} retained&&retained.Draft==pair.Item1&&retained.EarlyEndReason==pair.Item2,"Native conditional command does not skip a reason-only or whitespace-only draft");
        }
    }
}
