using System.Text.Json;
using ReflectionTimer.Accessible;
using ReflectionTimer.Core;

static class SessionDraftTests
{
    internal static void Run(Action<bool,string> check)
    {
        foreach(var ending in new[]{"deadline","early","pause-at-deadline","scheduled","schedule-choice"}) {
            var now=DateTimeOffset.Now;var store=new MemoryStore();var session=new PreviewSession(store,()=>now,isolatedProfile:true);
            session.Engine.Start(60,ending=="deadline",50);
            now=now.AddSeconds(10);var id=session.Engine.CheckIn();
            session.Engine.SaveDraft(id,"Same-session saved response","Possible interruption");
            check(Reason(session,id)&&new PreviewSession(store,()=>now).Engine.Snapshot.Prompts.Single().EarlyEndReason=="Possible interruption","A pre-completion check-in shows and retains its reason across restart: "+ending);
            Guid? opened=null;
            if(ending=="scheduled"||ending=="schedule-choice") {
                session.Engine.SetScheduleOverlap(ending=="scheduled"?ScheduleOverlapPolicy.EndWithReflection:ScheduleOverlapPolicy.Ask);
                var schedule=session.Engine.SaveSchedule(null,now.AddSeconds(5),30,false,50);
                now=now.AddSeconds(5);session.Tick();
                if(ending=="schedule-choice"){
                    var result=session.Execute("resolveSchedule",JsonSerializer.SerializeToElement(new{id=schedule,decision=(int)ScheduleDecision.StartNow}));
                    opened=result.OpenReflection;
                    check(result.SessionCompleted,"Starting a due schedule through the UI reports completion for the reflection policy");
                }
            } else if(ending=="early") {
                now=now.AddSeconds(10);opened=session.Execute("end",JsonSerializer.SerializeToElement(new{})).OpenReflection;
            } else {
                now=now.AddSeconds(50);
                if(ending=="pause-at-deadline")opened=session.Execute("toggle",JsonSerializer.SerializeToElement(new{})).OpenReflection;else session.Tick();
            }
            var prompt=session.Engine.Snapshot.Prompts.Single();
            var early=ending is "early" or "scheduled" or "schedule-choice";
            check(prompt.Id==id&&!prompt.IsCheckIn&&prompt.CheckInSessionId is null&&prompt.Draft=="Same-session saved response","Completion promotes the same draft ID instead of making a blank or duplicate prompt: "+ending);
            check(prompt.EndedEarly==early&&Reason(session,id)==early,"Reason visibility follows this session's completion, not a restarted or scheduled timer: "+ending);
            if(ending is "early" or "pause-at-deadline" or "schedule-choice")check(opened==id,"Completion command opens the promoted reflection ID: "+ending);
            session.Engine.SaveDraft(id,"Last keystroke after completion","Updated reason");
            check(session.Engine.Snapshot.Prompts.Single().Draft=="Last keystroke after completion","In-flight draft saves still address the same reflection after completion: "+ending);
            var actual=prompt.ActualDurationSeconds;
            session.Engine.QueueReflection(id,"Final response","Updated reason",localOnly:true);
            var entry=session.Engine.Snapshot.Outbox.Single();
            check(entry.ActualDurationSeconds==actual&&!entry.IsCheckIn&&entry.EndedEarly==early&&entry.EarlyEndReason==(early?"Updated reason":""),"Submitted completion retains correct actual time/status and excludes reasons for natural completion: "+ending);
        }
        var clock=DateTimeOffset.Now;var memory=new MemoryStore();var current=new PreviewSession(memory,()=>clock);
        current.Engine.Start(60,false,50);clock=clock.AddSeconds(5);
        var submitted=current.Engine.CheckIn();current.Engine.QueueReflection(submitted,"Already sent",localOnly:true);
        clock=clock.AddSeconds(55);current.Tick();
        check(current.Engine.Snapshot.Prompts.Single() is {Draft:""} p&&p.Id!=submitted&&current.Engine.Snapshot.Outbox.Count==1,"An already-submitted check-in is never copied into a new completion or submitted twice");
        current.Engine.SkipPrompt(current.Engine.Snapshot.Prompts.Single().Id);
        current.Engine.Start(60,false,50);var previous=current.Engine.CheckIn();current.Engine.SaveDraft(previous,"Earlier session","Earlier reason");
        current.Engine.Reset();
        check(!Reason(current,previous),"An abandoned check-in loses the reason field after its session is reset");
        current.Engine.Start(60,false,50);var before=JsonSerializer.Serialize(current.Engine.Snapshot);
        memory.Fail=true;clock=clock.AddSeconds(60);
        try{current.Tick();throw new Exception("Failed completion accepted");}catch(IOException){}
        check(JsonSerializer.Serialize(current.Engine.Snapshot)==before,"Failed completion storage preserves all existing drafts and the timer atomically");
        memory.Fail=false;current.Tick();
        check(current.Engine.Snapshot.Prompts.Count==2&&current.Engine.Snapshot.Prompts.Last().Draft==""&&current.Engine.Snapshot.Prompts.First().Draft=="Earlier session","A different session never inherits an older session's reflection text");
        var pausedStore=new MemoryStore();var pausedSession=new PreviewSession(pausedStore,()=>clock);
        pausedSession.Engine.Start(20,false,50);clock=clock.AddMilliseconds(8123);pausedSession.Engine.Pause();
        var pausedTimer=pausedSession.Engine.Snapshot.Timer;
        clock=clock.AddDays(1);var afterRestart=new PreviewSession(pausedStore,()=>clock);afterRestart.Tick();
        check(JsonSerializer.Serialize(afterRestart.Engine.Snapshot.Timer)==JsonSerializer.Serialize(pausedTimer)&&TimerEngine.IsPaused(afterRestart.Engine.Snapshot.Timer),"A paused session survives reopening and later ticks with its precise remaining time unchanged");
        check(afterRestart.Engine.Snapshot.Prompts.Count==0&&afterRestart.Engine.Snapshot.Outbox.Count==0,"Reopening a paused session does not finish it or create/send a reflection");
    }
    private static bool Reason(PreviewSession session,Guid id)=>JsonSerializer.SerializeToElement(session.View(),PreviewSession.Json)
        .GetProperty("prompts").EnumerateArray().Single(p=>p.GetProperty("id").GetGuid()==id).GetProperty("showEarlyEndReason").GetBoolean();
}
