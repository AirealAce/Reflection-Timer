using System.Text.Json;
using ReflectionTimer.Accessible;
using ReflectionTimer.Core;

static class EarlyEndGraceTests
{
    private static JsonElement Data(object value)=>JsonSerializer.SerializeToElement(value,PreviewSession.Json);
    internal static void Run(Action<bool,string> check)
    {
        // Millisecond boundaries also cover fractional grace periods and the
        // maximum duration, where multiplying by 100 in an int would overflow.
        var cases=new (int Duration,bool Enabled,int Default,int? Override,long Grace)[]{
            (900,true,15,null,15000),(900,true,40,null,40000),(900,true,40,8,8000),
            (900,false,40,8,15000),(60,true,40,null,6000),(60,false,5,null,6000),
            (5,true,15,null,500),(1,false,15,null,100),
            (TimerEngine.MaxDuration,true,TimerEngine.MaxDuration,null,TimerEngine.MaxDuration*100L)
        };
        foreach(var sample in cases)foreach(var paused in new[]{false,true})foreach(var offset in new[]{-1,0,1}){
            var now=new DateTimeOffset(2030,1,1,0,0,0,TimeSpan.Zero);var store=new MemoryStore();
            var session=new PreviewSession(store,()=>now,true);session.Engine.SetLowTimeDefault(sample.Default);
            session.Engine.Start(sample.Duration,false,0,lowTime:new(){Enabled=sample.Enabled,ThresholdSeconds=sample.Override});
            var id=session.Engine.CheckIn();var originalSession=session.Engine.Snapshot.Timer.SessionId;
            var remaining=sample.Grace+offset;now=now.AddMilliseconds(sample.Duration*1000L-remaining);
            if(paused){session.Engine.Pause();now=now.AddMinutes(5);}
            var result=session.Execute("queue",Data(new{id,text="Synthetic response",reason="Synthetic reason",endSession=true}));
            var entry=session.Engine.Snapshot.Outbox.Single();var early=offset>0;
            check(result.Close&&result.SessionCompleted&&!entry.IsCheckIn&&entry.EndedEarly==early
                &&entry.ActualDurationSeconds==(sample.Duration*1000L-remaining)/1000&&entry.DurationSeconds==sample.Duration
                &&entry.SessionId==originalSession&&entry.EarlyEndReason==(early?"Synthetic reason":""),
                $"Grace boundary uses effective low-time options and precise elapsed time: {sample}, paused={paused}, offset={offset}ms");
            check(!session.Engine.Snapshot.Timer.IsRunning&&session.Engine.Snapshot.Prompts.Count==0
                &&new PreviewSession(store,()=>now,true).Engine.Snapshot.Outbox.Single()==entry,
                "Grace classification and elapsed time survive reload without leaving an unfinished prompt");
        }
        CompletionRoutes(check);
        LiveSettingsAndRetry(check);
    }
    private static void CompletionRoutes(Action<bool,string> check)
    {
        foreach(var route in new[]{"button","send","scheduled","schedule-choice"})foreach(var remaining in new[]{30,31}){
            var now=new DateTimeOffset(2030,1,1,0,0,0,TimeSpan.Zero);var session=new PreviewSession(new MemoryStore(),()=>now,true);
            session.Engine.Start(900,true,0,lowTime:new(){ThresholdSeconds=30});
            var id=session.Engine.CheckIn();session.Engine.SaveDraft(id,"Saved response","Saved reason");
            var completion=now.AddSeconds(900-remaining);Guid? scheduled=null;
            if(route is "scheduled" or "schedule-choice"){
                session.Engine.SetScheduleOverlap(route=="scheduled"?ScheduleOverlapPolicy.EndWithReflection:ScheduleOverlapPolicy.Ask);
                scheduled=session.Engine.SaveSchedule(null,completion,60,false,0,lowTime:new(){ThresholdSeconds=1});
            }
            now=completion;
            if(route=="send")session.Execute("queue",Data(new{id,text="Saved response",reason="Saved reason",endSession=true}));
            else if(route=="button")session.Execute("end",Data(new{}));
            else {
                session.Tick();
                if(route=="schedule-choice")session.Execute("resolveSchedule",Data(new{id=scheduled!.Value,decision=(int)ScheduleDecision.StartNow}));
            }
            var early=remaining>30;
            if(route!="send"){
                var prompt=session.Engine.Snapshot.Prompts.Single();
                check(prompt.Id==id&&prompt.EndedEarly==early&&prompt.ActualDurationSeconds==900-remaining
                    &&TimerEngine.ShowEarlyEndReason(prompt,session.Engine.Snapshot.Timer,session.Engine.Now)==early,
                    "Completion and reason visibility use the ending session's threshold: "+route+", remaining="+remaining);
                session.Engine.SetLowTime(new(){ThresholdSeconds=1});session.Engine.SetLowTimeDefault(1);
                var next=session.Engine.Snapshot.Timer;
                session.Execute("queue",Data(new{id,text="Saved response",reason="Saved reason",endSession=true}));
                check(session.Engine.Snapshot.Timer==next,"Sending an older grace-classified response cannot end the next timer: "+route);
            }
            var entry=session.Engine.Snapshot.Outbox.Single();
            check(entry.EndedEarly==early&&!entry.IsCheckIn&&entry.ActualDurationSeconds==900-remaining
                &&entry.EarlyEndReason==(early?"Saved reason":"")&&session.Engine.Snapshot.Timer.IsRunning,
                "All completion paths preserve actual time, stored classification, and the next session: "+route+", remaining="+remaining);
        }
    }
    private static void LiveSettingsAndRetry(Action<bool,string> check)
    {
        foreach(var mode in new[]{"changed-default","changed-override","unchecked","override-retained"}){
            var now=new DateTimeOffset(2030,1,1,0,0,0,TimeSpan.Zero);var session=new PreviewSession(new MemoryStore(),()=>now,true);
            session.Engine.SetLowTimeDefault(5);session.Engine.Start(900,false,0,lowTime:new(){ThresholdSeconds=mode=="override-retained"?5:null});
            var id=session.Engine.CheckIn();now=now.AddSeconds(880);
            session.Engine.SetLowTimeDefault(30);
            if(mode=="changed-override")session.Engine.SetLowTime(new(){ThresholdSeconds=30});
            if(mode=="unchecked")session.Engine.SetLowTime(new(){Enabled=false,ThresholdSeconds=90});
            session.Execute("queue",Data(new{id,text="Synthetic response",reason="",endSession=true}));
            check(session.Engine.Snapshot.Outbox.Single().EndedEarly==(mode is "unchecked" or "override-retained"),
                "Classification uses the options in effect at submission: "+mode);
        }
        var clock=new DateTimeOffset(2030,1,1,0,0,0,TimeSpan.Zero);var store=new MemoryStore();var retry=new PreviewSession(store,()=>clock,true);
        retry.Engine.Start(900,true,0,lowTime:new(){ThresholdSeconds=30});var prompt=retry.Engine.CheckIn();
        retry.Engine.SaveDraft(prompt,"Retry response","Retry reason");clock=clock.AddSeconds(869);
        var before=JsonSerializer.Serialize(retry.Engine.Snapshot);store.Fail=true;
        try{retry.Execute("queue",Data(new{id=prompt,text="Retry response",reason="Retry reason",endSession=true}));throw new Exception("Failed send accepted");}catch(IOException){}
        check(JsonSerializer.Serialize(retry.Engine.Snapshot)==before,"A failed grace-period submission retains the timer, draft, and Outbox atomically");
        store.Fail=false;clock=clock.AddSeconds(1);
        retry.Execute("queue",Data(new{id=prompt,text="Retry response",reason="Retry reason",endSession=true}));
        check(retry.Engine.Snapshot.Outbox.Single() is {EndedEarly:false,ActualDurationSeconds:870,EarlyEndReason:""}
            &&retry.Engine.Snapshot.Timer.IsRunning&&retry.Engine.Snapshot.Prompts.Count==0,
            "Retry at the grace boundary uses successful submission time and preserves auto-start");
    }
}
