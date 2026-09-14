using System.Text.Json;
using ReflectionTimer.Accessible;
using ReflectionTimer.Core;

static class TimerToggleShortcutTests
{
    internal static void Run(Action<bool,string> check)
    {
        var now=DateTimeOffset.Now;
        var cutoff=now.AddHours(1).ToUnixTimeMilliseconds();
        var store=new MemoryStore {State=new AppState {Timer=new() {
            DurationSeconds=900,RemainingSeconds=900,Volume=37,AutoRestart=true,AutoRestartUntil=cutoff,
            LowTime=new(){Enabled=false,ThresholdSeconds=23,Track=LibrarySound.PokemonHealed}
        }}};
        var session=new PreviewSession(store,()=>now);
        session.SetDurationDraft(["","2","3"]);
        var result=session.ToggleTimerFromShortcut();
        var started=session.Engine.Snapshot.Timer;
        check(started.IsRunning&&started.DurationSeconds==123&&result.OpenReflection is null,"Global toggle starts the shared duration draft, treating an empty field as zero");
        check(started.Volume==37&&started.AutoRestart&&started.AutoRestartUntil==cutoff&&started.LowTime is {Enabled:false,ThresholdSeconds:23,Track:LibrarySound.PokemonHealed},"Global start retains Auto-start, cutoff, volume, and individual low-time settings");
        check(JsonSerializer.SerializeToElement(session.View(),PreviewSession.Json).GetProperty("durationDraft").ValueKind==JsonValueKind.Null,"Successful global start clears the shared duration draft like Compact");
        var id=session.Engine.CheckIn();session.Engine.SaveDraft(id,"Keep this reflection","Keep this reason");
        var reflections=JsonSerializer.Serialize(session.Engine.Snapshot.Prompts);
        now=now.AddMilliseconds(4321);
        session.SetDurationDraft(["invalid","",""]);
        result=session.ToggleTimerFromShortcut();
        var paused=session.Engine.Snapshot.Timer;
        check(!paused.IsRunning&&paused.PausedRemainingMilliseconds==118679&&paused.SessionId==started.SessionId&&result.OpenReflection is null,"Global toggle pauses with millisecond precision even if a duration draft is invalid");
        check(JsonSerializer.Serialize(session.Engine.Snapshot.Prompts)==reflections&&session.Engine.Snapshot.Outbox.Count==0,"Global pause neither sends nor changes an open reflection and its reason");
        foreach(var parts in new[]{new[]{"","",""},new[]{"-1","0","0"},new[]{"0","1.5","0"},new[]{"99999999999999999999","0","0"}}){
            session.SetDurationDraft(parts);
            var before=JsonSerializer.Serialize(session.Engine.Snapshot);
            try {session.ToggleTimerFromShortcut();throw new Exception("Invalid draft accepted");}catch(ArgumentException){}
            check(JsonSerializer.Serialize(session.Engine.Snapshot)==before,"Invalid or zero global start input preserves the paused session: "+string.Join('/',parts));
        }
        session.SetDurationDraft(["0","2","3"]);now=now.AddMinutes(5);
        session.ToggleTimerFromShortcut();
        check(session.Engine.Snapshot.Timer.SessionId==started.SessionId&&session.Engine.Snapshot.Timer.EndTime==now.ToUnixTimeMilliseconds()+118679,"Global resume retains the same session and exact remaining time after a long pause");
        session.ToggleTimerFromShortcut();session.SetDurationDraft(["0","0","47"]);
        session.ToggleTimerFromShortcut();
        check(session.Engine.Snapshot.Timer is {IsRunning:true,DurationSeconds:47}&&session.Engine.Snapshot.Timer.SessionId!=started.SessionId,"Editing a paused duration starts the replacement duration, matching Compact");
        check(session.Engine.Snapshot.Prompts.Single() is {Draft:"Keep this reflection",EarlyEndReason:"Keep this reason",CheckInSessionId:null,ActualDurationSeconds:4} retained&&retained.Id==id&&session.Engine.Snapshot.Outbox.Count==0,"Starting an edited duration retains the older draft and freezes its elapsed time without submitting it");
        foreach(var running in new[]{true,false}){
            if(!running)session.ToggleTimerFromShortcut();
            var before=JsonSerializer.Serialize(session.Engine.Snapshot);store.Fail=true;
            try {session.ToggleTimerFromShortcut();throw new Exception("Failed persistence ignored");}catch(IOException){}
            check(JsonSerializer.Serialize(session.Engine.Snapshot)==before,"Failed global "+(running?"pause":"resume")+" retains the timer and all drafts");
            store.Fail=false;
        }
        var completed=new PreviewSession(new MemoryStore(),()=>now);
        completed.Engine.Start(10,false,0);now=now.AddSeconds(10);
        result=completed.ToggleTimerFromShortcut();
        check(result.SessionCompleted&&result.OpenReflection==completed.Engine.Snapshot.Prompts.Single().Id&&!completed.Engine.Snapshot.Prompts.Single().EndedEarly,"Ctrl+Space at the exact deadline follows normal completion, not early ending");
        completed.SetDurationDraft(["0","1","30"]);result=completed.ToggleTimerFromShortcut();
        check(completed.Engine.Snapshot.Timer is {IsRunning:true,DurationSeconds:90}&&completed.Engine.Snapshot.Prompts.Count==1&&result.OpenReflection is null,"Global toggle restarts a finished timer using its new input and retains the pending reflection");
        var idle=new PreviewSession(new MemoryStore(),()=>now);idle.ToggleTimerFromShortcut();
        check(idle.Engine.Snapshot.Timer is {IsRunning:true,DurationSeconds:900},"Global toggle uses the saved duration when no viewer has an edited draft");
    }
}
