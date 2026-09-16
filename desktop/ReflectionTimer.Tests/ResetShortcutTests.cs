using System.Text.Json;
using ReflectionTimer.Accessible;
using ReflectionTimer.Core;

static class ResetShortcutTests
{
    internal static void Run(Action<bool,string> check)
    {
        var now=DateTimeOffset.Now;
        foreach(var status in new[]{"ready","running","paused","finished"}){
            var store=new MemoryStore();var session=new PreviewSession(store,()=>now);
            session.Engine.Start(900,false,37,lowTime:new(){Enabled=true,ThresholdSeconds=23});
            var id=session.Engine.CheckIn();session.Engine.SaveDraft(id,"Retain reflection","Retain reason");
            if(status=="ready")session.Engine.Reset();
            if(status=="paused"){now=now.AddSeconds(123);session.Engine.Pause();}
            if(status=="finished"){now=now.AddSeconds(900);session.Tick();}
            string Drafts()=>JsonSerializer.Serialize(session.Engine.Snapshot.Prompts.Select(p=>new{p.Id,p.Draft,p.EarlyEndReason}));
            var reflections=Drafts();
            session.SetDurationDraft(["","2","3"]);
            session.ResetTimerFromShortcut();
            check(session.Engine.Snapshot.Timer is {IsRunning:false,DurationSeconds:123,RemainingSeconds:123,PausedRemainingMilliseconds:null,EndTime:null,SessionId:null,Volume:37,LowTime.ThresholdSeconds:23},
                "Ctrl+R uses edited input duration and normal Reset semantics from "+status);
            check(Drafts()==reflections&&session.Engine.Snapshot.Outbox.Count==0,
                "Reset from "+status+" retains reflections without sending or generating another prompt");
            check(JsonSerializer.SerializeToElement(session.View(),PreviewSession.Json).GetProperty("durationDraft").ValueKind==JsonValueKind.Null,
                "Reset clears the shared input draft from "+status);
            session.Engine.Start(123,false,37);now=now.AddSeconds(5);session.Engine.Pause();
            session.ResetTimerFromShortcut();
            check(session.Engine.Snapshot.Timer.RemainingSeconds==123,"Ctrl+R restores saved input duration when no fields were edited from "+status);
            session.SetDurationDraft(["0","1","30"]);store.Fail=true;
            var before=JsonSerializer.Serialize(session.Engine.Snapshot);
            try{session.ResetTimerFromShortcut();throw new Exception("Failed save accepted");}catch(IOException){}
            check(JsonSerializer.Serialize(session.Engine.Snapshot)==before&&JsonSerializer.SerializeToElement(session.View(),PreviewSession.Json).GetProperty("durationDraft")[1].GetString()=="1",
                "Failed reset retains the timer and duration draft from "+status);
            store.Fail=false;
            foreach(var parts in new[]{new[]{"","",""},new[]{"-1","0","0"},new[]{"0","1.5","0"},new[]{"99999999999999999999","0","0"}}){
                session.SetDurationDraft(parts);
                try{session.ResetTimerFromShortcut();throw new Exception("Invalid reset accepted");}catch(ArgumentException){}
                check(JsonSerializer.Serialize(session.Engine.Snapshot)==before,"Invalid reset input preserves the timer from "+status+": "+string.Join('/',parts));
            }
        }
    }
}
