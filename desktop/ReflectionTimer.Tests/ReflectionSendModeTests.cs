using System.Text.Json;
using ReflectionTimer.Accessible;
using ReflectionTimer.Core;

static class ReflectionSendModeTests
{
    private static JsonElement Data(object value)=>JsonSerializer.SerializeToElement(value,PreviewSession.Json);
    internal static async Task Run(Action<bool,string> check)
    {
        foreach(var paused in new[]{false,true})foreach(var repeat in new[]{false,true}) {
            var now=DateTimeOffset.Now;var store=new MemoryStore();var session=new PreviewSession(store,()=>now,true);
            session.Engine.Start(100,repeat,37);var original=session.Engine.Snapshot.Timer.SessionId;
            now=now.AddSeconds(17);var id=session.Engine.CheckIn();
            if(paused){session.Engine.Pause();now=now.AddMinutes(2);}
            session.Engine.SaveDraft(id,"Saved draft","Saved reason");
            var before=JsonSerializer.Serialize(session.Engine.Snapshot);store.Fail=true;
            try{session.Execute("queue",Data(new{id,text="Latest response",reason="Latest reason",endSession=true}));throw new Exception("Failed storage accepted");}catch(IOException){}
            check(JsonSerializer.Serialize(session.Engine.Snapshot)==before,"End-and-send storage failure retains the entire "+(paused?"paused":"running")+" session, draft, and Outbox");
            store.Fail=false;var notifications=0;session.Engine.Changed+=()=>notifications++;
            var result=session.Execute("queue",Data(new{id,text="Latest response",reason="Latest reason",endSession=true}));
            var state=session.Engine.Snapshot;var sent=state.Outbox.Single();
            check(result.Close&&result.SessionCompleted&&notifications==1&&state.Prompts.Count==0,"End-and-send atomically completes and queues once without leaving a popup to reopen");
            check(sent is {IsCheckIn:false,EndedEarly:true,ActualDurationSeconds:17,DurationSeconds:100,EarlyEndReason:"Latest reason",Message:"Latest response",LocalOnly:true}&&sent.SessionId==original,"End-and-send records actual/allotted time, early status, reason, and original audio session identity");
            check(state.Timer.IsRunning==repeat&&state.Timer.AutoRestart==repeat&&state.Timer.Volume==37&&(!repeat||state.Timer.SessionId!=original),"End-and-send preserves auto-start and volume for "+(paused?"paused":"running")+" sessions");
            var after=JsonSerializer.Serialize(state);
            try{session.Execute("queue",Data(new{id,text="Duplicate",reason="",endSession=true}));throw new Exception("Duplicate accepted");}catch(ArgumentException){}
            check(JsonSerializer.Serialize(session.Engine.Snapshot)==after,"A duplicate end-and-send cannot end the next auto-started session");
            check(new PreviewSession(store).Engine.Snapshot.Outbox.Single()==sent,"End-and-send accounting survives a saved-state reload");
        }
        CheckInAndBoundaries(check);
        await CompletionPolicy(check);
    }
    private static void CheckInAndBoundaries(Action<bool,string> check)
    {
        var now=DateTimeOffset.Now;var session=new PreviewSession(new MemoryStore(),()=>now,true);
        session.Engine.Start(100,true,50);now=now.AddSeconds(23);var id=session.Engine.CheckIn();
        var timer=session.Engine.Snapshot.Timer;
        var result=session.Execute("queue",Data(new{id,text="Check-in",reason="Provisional reason",endSession=false}));
        check(!result.SessionCompleted&&result.Close&&session.Engine.Snapshot.Timer==timer&&session.Engine.Snapshot.Outbox.Single() is {IsCheckIn:true,EndedEarly:false,ActualDurationSeconds:23,EarlyEndReason:""},"Alt+Enter check-in sends actual elapsed time without ending, restarting, or changing the timer");
        id=session.Engine.CheckIn();
        foreach(var text in new[]{"",new string('x',5001)}){
            try{session.Engine.QueueReflection(id,text,endSession:true);throw new Exception("Invalid reflection accepted");}catch(ArgumentException){}
        }
        check(session.Engine.Snapshot.Timer==timer&&session.Engine.Snapshot.Prompts.Any(p=>p.Id==id),"Invalid responses cannot stop the session");
        session.Engine.Reset();session.Engine.Start(50,false,50);timer=session.Engine.Snapshot.Timer;
        result=session.Execute("queue",Data(new{id,text="Old check-in",reason="",endSession=true}));
        check(!result.SessionCompleted&&session.Engine.Snapshot.Timer==timer&&session.Engine.Snapshot.Outbox.Last() is {IsCheckIn:true,ActualDurationSeconds:23},"A detached old check-in never ends or borrows elapsed time from a newer session");
        id=session.Engine.CheckIn();now=now.AddSeconds(50); // Deadline passed before the UI tick.
        result=session.Execute("queue",Data(new{id,text="Finished",reason="Must not be logged",endSession=true}));
        check(result.SessionCompleted&&session.Engine.Snapshot.Outbox.Last() is {IsCheckIn:false,EndedEarly:false,ActualDurationSeconds:50,EarlyEndReason:""},"Ctrl+Enter at the deadline records natural completion, not a false early ending");
        session.Engine.Start(60,true,50);id=session.Engine.CheckIn();now=now.AddSeconds(60);session.Tick();timer=session.Engine.Snapshot.Timer;
        result=session.Execute("queue",Data(new{id,text="Previously completed",reason="",endSession=true}));
        check(!result.SessionCompleted&&session.Engine.Snapshot.Timer==timer&&session.Engine.Snapshot.Outbox.Last() is {IsCheckIn:false,EndedEarly:false,ActualDurationSeconds:60},"Submitting a naturally promoted reflection cannot end the next auto-started session");
        id=session.Engine.TestPrompt();timer=session.Engine.Snapshot.Timer;
        session.Execute("queue",Data(new{id,text="Practice",reason="",endSession=true}));
        check(session.Engine.Snapshot.Timer==timer&&session.Engine.Snapshot.Outbox.Last().IsTest,"Practice submissions never end the real timer");
        session.Engine.Start(100,true,42,now.AddSeconds(10).ToUnixTimeMilliseconds());id=session.Engine.CheckIn();now=now.AddSeconds(11);
        session.Execute("queue",Data(new{id,text="Cutoff",reason="",endSession=true}));
        check(!session.Engine.Snapshot.Timer.IsRunning&&!session.Engine.Snapshot.Timer.AutoRestart,"End-and-send respects an expired auto-start cutoff");
        session.Engine.Start(100,true,50);id=session.Engine.CheckIn();session.Engine.SetScheduleOverlap(ScheduleOverlapPolicy.Wait);
        var scheduled=session.Engine.SaveSchedule(null,now.AddSeconds(5),75,false,29);
        now=now.AddSeconds(5);session.Tick();now=now.AddSeconds(3);
        session.Execute("queue",Data(new{id,text="Handoff",reason="Next session",endSession=true}));
        check(session.Engine.Snapshot.Timer is {IsRunning:true,DurationSeconds:75,AutoRestart:false,Volume:29}&&session.Engine.Snapshot.Schedules.All(s=>s.Id!=scheduled),"End-and-send starts the waiting scheduled session instead of an unwanted auto-repeat");
    }
    private static async Task CompletionPolicy(Action<bool,string> check)
    {
        foreach(var autoSend in new[]{false,true}){
            var now=DateTimeOffset.Now;var store=new MemoryStore();var session=new PreviewSession(store,()=>now,true);
            session.Engine.SetAutoSendIncompleteReflections(autoSend);
            session.Engine.Start(10,false,50);now=now.AddSeconds(10);session.Tick();var old=session.Engine.Snapshot.Prompts.Single().Id;
            var practice=session.Engine.TestPrompt();
            session.Engine.Start(100,true,50);now=now.AddSeconds(17);var id=session.Engine.CheckIn();
            session.Execute("queue",Data(new{id,text="Current complete",reason="Early",endSession=true}));
            var active=session.Engine.CheckIn();
            // A later completed reflection must not be included in this completion.
            now=now.AddSeconds(1);session.Engine.EndEarly();var future=active;
            var window=new FakeWindow(old,()=>session.Engine.SaveDraft(old,"Final older draft",""));var queued=0;var shown=0;
            var coordinator=new ReflectionPromptCoordinator(session,()=>window.Closed?[]:[window],(_,_)=>{shown++;return Task.CompletedTask;},()=>queued++);
            if(autoSend){
                window.Fail=true;
                try{await coordinator.CompleteSubmittedAsync(id);throw new Exception("Failed flush accepted");}catch(IOException){}
                check(!window.Closed&&window.Resumed==1&&session.Engine.Snapshot.Prompts.Any(p=>p.Id==old),"Failed older-draft flush preserves it for retry without reopening the submitted prompt");
                window.Fail=false;
            }
            await coordinator.CompleteSubmittedAsync(id);
            var state=session.Engine.Snapshot;
            check(shown==0&&queued==(autoSend?1:0)&&state.Prompts.Any(p=>p.Id==old)!=autoSend,"End-and-send honors the existing auto-send preference without showing another popup");
            check(state.Prompts.Any(p=>p.Id==practice)&&state.Prompts.Any(p=>p.Id==future)&&state.Outbox.Single(o=>o.Id==id).AutoSent==false,"Completion policy preserves practice/future prompts and the manual submission's classification");
            if(autoSend)check(state.Outbox.Single(o=>o.Id==old) is {AutoSent:true,Message:"Final older draft"}&&window.Closed,"Older auto-sent reflections retain the latest durable editor text");
        }
    }
    private sealed class FakeWindow(Guid id,Action save):IReflectionPromptWindow
    {
        public Guid ReflectionId=>id;
        public bool Closed,Fail;public int Resumed;
        public Task PrepareHandoffAsync(){if(Fail)throw new IOException("Synthetic flush failure");save();return Task.CompletedTask;}
        public void ResumeEditing()=>Resumed++;
        public void CloseAfterSave()=>Closed=true;
    }
}
