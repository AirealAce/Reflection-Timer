using System.Text.Json;
using ReflectionTimer.Accessible;
using ReflectionTimer.Core;

static class ViewerAutoHideTests
{
    internal static void Run(Action<bool,string> check)
    {
        foreach(var state in new[]{AppState.CreateDefault(),JsonSerializer.Deserialize<AppState>("{}",DataJson.Options)!})
            check(!state.ViewerAutoHide&&state.ViewerAutoHideSeconds==3,"New and legacy profiles default to auto-hide disabled at 3 seconds");
        var store=new MemoryStore();var engine=new TimerEngine(store);var writes=0;engine.Changed+=()=>writes++;
        engine.SetViewerAutoHide(false,3);
        check(writes==0,"Unchanged auto-hide preferences do not write storage");
        engine.SetViewerAutoHide(true,7);
        var reopened=new TimerEngine(store);
        check(reopened.SettingsSnapshot.ViewerAutoHide&&reopened.SettingsSnapshot.ViewerAutoHideSeconds==7,"Auto-hide preference and delay survive reload");
        var before=JsonSerializer.Serialize(engine.Snapshot,DataJson.Options);
        foreach(var seconds in new[]{0,-1,TimerEngine.MaxDuration+1}) {
            try{engine.SetViewerAutoHide(false,seconds);throw new Exception("Expected invalid delay");}catch(ArgumentException){}
        }
        check(JsonSerializer.Serialize(engine.Snapshot,DataJson.Options)==before,"Invalid delays cannot partially change saved preferences");
        store.Fail=true;
        try{engine.SetViewerAutoHide(false,2);throw new Exception("Expected failed save");}catch(IOException){}
        check(JsonSerializer.Serialize(engine.Snapshot,DataJson.Options)==before,"Failed auto-hide saves preserve the prior in-memory state");
        store.Fail=false;
        engine.SetViewerAutoHide(false,7);
        check(!engine.SettingsSnapshot.ViewerAutoHide&&engine.SettingsSnapshot.ViewerAutoHideSeconds==7,"Disabling retains the user's chosen delay");
        foreach(var mode in Enum.GetValues<SessionMode>()) {
            engine.SwitchMode(mode);
            if(mode==SessionMode.Timer)engine.Start(600,false,0);else engine.StartStopwatch();
            var timer=engine.CurrentTimer;var prompts=engine.Snapshot.Prompts.Count;
            engine.SetViewerAutoHide(true,3);engine.SetFloatingTimer(false);
            check(engine.CurrentTimer.IsRunning&&engine.CurrentTimer.SessionId==timer.SessionId&&engine.CurrentTimer.EndTime==timer.EndTime
                &&engine.Snapshot.Prompts.Count==prompts,"Auto-hide settings/visibility cannot pause or finish the session: "+mode);
            engine.Pause();
        }
        long now=0;var deadline=new ViewerAutoHideDeadline(()=>now);
        check(deadline.RemainingMilliseconds is null,"No countdown exists before activation");
        deadline.Restart(3);now=2999;
        check(deadline.RemainingMilliseconds==1,"Auto-hide waits the complete configured delay");
        deadline.Restart(3);now=3000;
        check(deadline.RemainingMilliseconds==2999,"Reactivation replaces, rather than queues, the old deadline");
        now=5999;check(deadline.RemainingMilliseconds==0,"The replacement deadline expires at the expected elapsed time");
        deadline.Cancel();now+=10000;
        check(deadline.RemainingMilliseconds is null,"Cancel prevents stale expiry after hiding or disabling");
        deadline.Restart(TimerEngine.MaxDuration);
        check(deadline.RemainingMilliseconds==int.MaxValue,"Long delays safely fit native timer interval limits");
        now+=(long)TimerEngine.MaxDuration*1000;
        check(deadline.RemainingMilliseconds==0,"Long delays retain their complete duration without integer overflow");
    }
}
