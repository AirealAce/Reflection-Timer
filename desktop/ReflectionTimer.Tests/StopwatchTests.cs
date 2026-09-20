using System.Net;
using System.Text.Json;
using ReflectionTimer.Accessible;
using ReflectionTimer.Core;
using ReflectionTimer.Desktop;

static class StopwatchTests
{
    internal static async Task Run(Action<bool,string> check)
    {
        ModeShortcut(check);
        StopTiming(check);
        var now=DateTimeOffset.Now;
        var store=new MemoryStore();var session=new PreviewSession(store,()=>now,isolatedProfile:true);var engine=session.Engine;
        JsonElement Data(object value)=>JsonSerializer.SerializeToElement(value,PreviewSession.Json);
        int Elapsed()=>TimerEngine.ActualSeconds(engine.Snapshot.Timer,engine.Now);
        void Step(double seconds)=>now=now.AddSeconds(seconds);
        check(engine.Snapshot.Timer.Mode==SessionMode.Timer&&engine.Snapshot.ParkedTimer is null,"Legacy profile defaults to Timer without an extra session");
        var audio=AudioSettings.From(engine.Snapshot);
        check(audio.TimeReachedEnabled&&audio.TimeReachedSeconds==300&&audio.For(SoundEvent.TimeReached)==audio.LowTime,
            "Stopwatch alert defaults to five minutes and inherits the low-time sound and behavior");
        engine.Start(120,true,55);Step(12.25);
        session.Execute("switchMode",Data(new{mode=1}));
        var parked=engine.Snapshot.ParkedTimer!;
        check(!parked.IsRunning&&parked.PausedRemainingMilliseconds==107750&&engine.Snapshot.Timer.Mode==SessionMode.Stopwatch,
            "Mode switch precisely pauses and parks the countdown");
        session.SetDurationDraft(["invalid","",""]);
        session.ToggleTimerFromShortcut();Step(.25);engine.Pause();Step(500);
        check(TimerEngine.IsPaused(engine.Snapshot.Timer)&&Elapsed()==0,"Even a sub-second stopwatch can pause and resume without counting a break");
        engine.Resume();Step(.75);
        check(Elapsed()==1,"Sub-second portions accumulate across stopwatch pauses");
        engine.SwitchMode(SessionMode.Timer);Step(500);
        check(!engine.Snapshot.Timer.IsRunning&&engine.Snapshot.Timer.PausedRemainingMilliseconds==107750
            &&engine.Snapshot.ParkedTimer is {IsRunning:false,ElapsedMilliseconds:1000},"Both unfinished modes remain paused after switching back");
        engine.SwitchMode(SessionMode.Stopwatch);
        check(TimerEngine.IsPaused(engine.Snapshot.Timer)&&Elapsed()==1,"Returning to Stopwatch restores elapsed time without auto-starting");
        session.ToggleTimerFromShortcut();Step(19);
        var reviews=0;engine.ActivityRecorded+=a=>{if(a.Event=="stopwatch.reviewOpened")reviews++;};
        var prompt=engine.ReviewStopwatch();Step(300);
        var reflection=engine.Snapshot.Prompts.Single();
        check(Elapsed()==20&&!engine.Snapshot.Timer.IsRunning&&reflection.Mode==SessionMode.Stopwatch
            &&reflection.DurationSeconds==0&&!TimerEngine.ShowEarlyEndReason(reflection,engine.Snapshot.Timer,engine.Now),
            "Review pauses work time and has neither allotted time nor an early-ending reason");
        check(engine.ReviewStopwatch()==prompt&&reviews==1,"Reopening an already-paused stopwatch reflection reuses its ID without replaying completion audio");
        store.Fail=true;
        try{engine.SaveReflectionForLater(prompt,"Important draft");throw new Exception("Failed write accepted");}catch(IOException){}
        check(!engine.Snapshot.Timer.IsRunning&&engine.Snapshot.Prompts.Single().Draft=="","A failed Save neither resumes the stopwatch nor discards the existing draft");
        store.Fail=false;engine.SaveReflectionForLater(prompt,"Important draft");Step(10);
        check(engine.Snapshot.Timer.IsRunning&&Elapsed()==30&&engine.Snapshot.Prompts.Single().Draft=="Important draft","Successful Save resumes the same stopwatch and retains its reflection");
        check(engine.ReviewStopwatch()==prompt&&engine.Snapshot.Prompts.Single().Draft=="Important draft","Next stopwatch review restores the saved response");
        engine.Resume();engine.Pause();engine.SaveReflectionForLater(prompt,"Edited after manual pause");
        check(!engine.Snapshot.Timer.IsRunning,"Saving an older draft cannot undo an explicit later pause");
        engine.ReviewStopwatch();engine.SwitchMode(SessionMode.Timer);engine.Resume();Step(4);
        engine.SaveReflectionForLater(prompt,"Parked work");
        check(engine.Snapshot.Timer.IsRunning&&engine.Snapshot.ParkedTimer is {IsRunning:false},"Saving a parked stopwatch never starts a second clock");
        var countdownEnd=engine.Snapshot.Timer.EndTime;
        check(engine.QueueReflection(prompt,"Stopwatch work",localOnly:true),"Sending a parked stopwatch reflection finalizes its own session");
        var item=engine.Snapshot.Outbox.Single();
        check(item.Mode==SessionMode.Stopwatch&&item.ActualDurationSeconds==30&&item.DurationSeconds==0&&!item.EndedEarly&&!item.IsCheckIn&&item.EarlyEndReason==""
            &&engine.Snapshot.Timer.EndTime==countdownEnd&&engine.Snapshot.Timer.IsRunning,"Stopwatch delivery records work time only and cannot end the other mode");
        engine.SwitchMode(SessionMode.Stopwatch);session.ResetTimerFromShortcut();session.ToggleTimerFromShortcut();
        var alerts=0;engine.TimeReached+=_=>alerts++;engine.SetTimeReached(true,5);
        Step(4);engine.Advance();engine.Pause();Step(600);engine.Advance();engine.Resume();Step(1);engine.Advance();engine.Advance();
        check(alerts==1&&engine.Snapshot.Timer.IsRunning,"Time-reached alert fires once at active-time threshold without stopping Stopwatch");
        Step(20);engine.Advance();check(alerts==1,"Subsequent ticks do not repeat the stopwatch threshold alert");
        prompt=engine.ReviewStopwatch();engine.SaveOrSendReflection(prompt,"Continue working");Step(3);
        check(engine.Snapshot.Timer.IsRunning,"Ctrl+Enter Save resumes the reviewed active stopwatch");
        engine.ReviewStopwatch();var beforeSend=Elapsed();Step(60);engine.QueueReflection(prompt,"Finished work",localOnly:true);
        check(engine.Snapshot.Timer.StopwatchCompleted&&!engine.Snapshot.Timer.IsRunning&&engine.Snapshot.Outbox.Last().ActualDurationSeconds==beforeSend,"Send ends Stopwatch, excluding reflection-writing time");
        Step(600);engine.Advance();check(engine.Snapshot.Prompts.Count==0,"A finished stopwatch never creates a later deadline popup");
        engine.StartStopwatch();Step(5);engine.Advance();check(alerts==2,"Starting a fresh stopwatch rearms its threshold alert");
        var restored=new TimerEngine(store,()=>now);
        check(restored.Snapshot.Timer.Mode==SessionMode.Stopwatch&&restored.Snapshot.ParkedTimer?.Mode==SessionMode.Timer,"Selected mode and paused other mode survive a saved-state reload");
        var clock=Data(session.Clock());check(clock.GetProperty("stopwatch").GetBoolean()&&clock.GetProperty("seconds").GetInt32()==5,"Stopwatch display ignores stale or invalid countdown input drafts");
        foreach(var policy in Enum.GetValues<ScheduleOverlapPolicy>()){
            var e=new TimerEngine(new MemoryStore(),()=>now);e.SwitchMode(SessionMode.Stopwatch);e.StartStopwatch();e.SetScheduleOverlap(policy);
            e.SaveSchedule(null,now.AddSeconds(10),60,false,55);Step(10);e.Advance();
            if(policy==ScheduleOverlapPolicy.EndWithReflection)check(e.Snapshot.Timer is {Mode:SessionMode.Timer,IsRunning:true}&&e.Snapshot.ParkedTimer is {IsRunning:false,StopwatchCompleted:true}&&e.Snapshot.Prompts.Single().Mode==SessionMode.Stopwatch,"Scheduled handoff ends Stopwatch once and starts a countdown");
            else check(e.Snapshot.Timer is {Mode:SessionMode.Stopwatch,IsRunning:true}&&e.Snapshot.Schedules.Single() is {} s&&(s.AwaitingDecision||s.WaitingForCurrentSession),"Schedule overlap policy also protects Stopwatch: "+policy);
        }
        var settings=new ConnectionSettings{SheetUrl="https://docs.google.com/spreadsheets/d/abcdefghijklmnopqrstuvwxyz/edit",WebAppUrl="https://script.google.com/macros/s/syntheticReceiver/exec",ApiToken=new string('a',64)};
        var handoff=new TimerEngine(new MemoryStore(),()=>now);handoff.Start(120,false,55);Step(7);handoff.SwitchMode(SessionMode.Stopwatch);handoff.StartStopwatch();
        handoff.SaveSchedule(null,now.AddSeconds(10),60,false,55);Step(10);handoff.Advance();
        check(handoff.Snapshot.Prompts.Count==2&&handoff.Snapshot.Prompts.Any(p=>p.Mode==SessionMode.Timer&&p.ActualDurationSeconds==7)
            &&handoff.Snapshot.Prompts.Any(p=>p.Mode==SessionMode.Stopwatch&&p.ActualDurationSeconds==10),"Scheduled replacement retains reflections for both interrupted stopwatch and parked countdown");
        var handler=new Receiver();using var client=new SheetsClient(handler);
        var upload=item with{LocalOnly=false,SheetUrl=settings.SheetUrl,ReceiverUrl=settings.WebAppUrl};
        var reply=await client.Upload(settings,upload);
        check(!reply.Success&&reply.ErrorKind=="receiver_update_required"&&handler.Writes==0,"Old Sheets receiver cannot receive mislabeled stopwatch data");
        handler.Stopwatch=true;reply=await client.Upload(settings,upload);
        check(reply.Success&&handler.Writes==1&&handler.Last.GetProperty("durationSeconds").ValueKind==JsonValueKind.Null
            &&handler.Last.GetProperty("actualDurationSeconds").GetInt32()==30&&handler.Last.GetProperty("sessionMode").GetString()=="stopwatch", "Upgraded receiver gets active duration, explicit stopwatch mode, and no allotted duration");
        var protectedStore=new MemoryStore{State=new AppState{Connection=settings}};var protectedEngine=new TimerEngine(protectedStore,()=>now);
        protectedEngine.Start(20,false,55);protectedEngine.SwitchMode(SessionMode.Stopwatch);
        try{protectedEngine.SaveSettings(settings with{SheetName="Different",SheetMode="fixed"},true,false,false);throw new Exception("Account switch with parked work accepted");}catch(InvalidOperationException){}
        check(true,"Destination changes remain blocked while an unfinished session is parked");
    }
    private static void ModeShortcut(Action<bool,string> check)
    {
        var now=DateTimeOffset.Now;
        var store=new MemoryStore();
        var session=new PreviewSession(store,()=>now,isolatedProfile:true);
        session.SetDurationDraft(["0","2","15"]);
        var result=session.ToggleModeFromShortcut();
        check(session.Engine.Snapshot.Timer is {Mode:SessionMode.Stopwatch,IsRunning:false,SessionId:null}&&result.OpenReflection is null,
            "Mode shortcut selects an idle stopwatch without starting or opening a reflection");
        session.ToggleModeFromShortcut();session.ToggleTimerFromShortcut();
        var timerId=session.Engine.Snapshot.Timer.SessionId;
        now=now.AddMilliseconds(4250);
        var prompt=session.Engine.CheckIn();session.Engine.SaveDraft(prompt,"Retain this draft","Retain this reason");
        session.SetDurationDraft(["invalid","",""]);
        var prompts=JsonSerializer.Serialize(session.Engine.Snapshot.Prompts);
        result=session.ToggleModeFromShortcut();
        check(session.Engine.Snapshot.ParkedTimer is {IsRunning:false,PausedRemainingMilliseconds:130750} parked&&parked.SessionId==timerId
            &&JsonSerializer.Serialize(session.Engine.Snapshot.Prompts)==prompts&&result.OpenReflection is null,
            "Mode shortcut parks the precise countdown and its draft, even with invalid duration input");
        session.ToggleTimerFromShortcut();now=now.AddMilliseconds(7250);
        var stopwatchId=session.Engine.Snapshot.Timer.SessionId;
        session.ToggleModeFromShortcut();now=now.AddMinutes(10);
        check(session.Engine.Snapshot.Timer.SessionId==timerId&&!session.Engine.Snapshot.Timer.IsRunning
            &&session.Engine.Snapshot.ParkedTimer is {IsRunning:false,ElapsedMilliseconds:7250},
            "Switching away from running Stopwatch pauses both modes and excludes the following break");
        session.ToggleModeFromShortcut();
        check(session.Engine.Snapshot.Timer.SessionId==stopwatchId&&TimerEngine.ActualSeconds(session.Engine.Snapshot.Timer,session.Engine.Now)==7
            &&!session.Engine.Snapshot.Timer.IsRunning&&session.Engine.Snapshot.Outbox.Count==0,
            "Switching back restores the same paused stopwatch without submitting or auto-starting");
        var restored=new PreviewSession(store,()=>now,isolatedProfile:true).Engine.Snapshot;
        check(restored.Timer.Mode==SessionMode.Stopwatch&&restored.Timer.ElapsedMilliseconds==7250&&restored.ParkedTimer?.SessionId==timerId,
            "Shortcut-selected mode and both retained sessions survive reload");
        foreach(var mode in new[]{SessionMode.Stopwatch,SessionMode.Timer}){
            if(session.Engine.Snapshot.Timer.Mode!=mode)session.ToggleModeFromShortcut();
            session.Engine.Resume();
            var before=JsonSerializer.Serialize(session.Engine.Snapshot);store.Fail=true;
            try{session.ToggleModeFromShortcut();throw new Exception("Failed mode write accepted");}catch(IOException){}
            check(JsonSerializer.Serialize(session.Engine.Snapshot)==before,"Failed mode shortcut preserves all running state and drafts: "+mode);
            store.Fail=false;
        }
        var deadline=new PreviewSession(new MemoryStore(),()=>now,isolatedProfile:true);
        deadline.Engine.Start(5,false,0);now=now.AddSeconds(5);
        result=deadline.ToggleModeFromShortcut();
        check(result.SessionCompleted&&result.OpenReflection==deadline.Engine.Snapshot.Prompts.Single().Id
            &&!deadline.Engine.Snapshot.Prompts.Single().EndedEarly&&deadline.Engine.Snapshot.Timer.Mode==SessionMode.Stopwatch,
            "Mode shortcut at the countdown deadline retains normal completion and its reflection");
    }
    private static void StopTiming(Action<bool,string> check)
    {
        foreach(var action in new[]{"mode","reflection","toggle","end","checkIn"}){
            var now=DateTimeOffset.Now;
            var store=new SlowStore();
            var session=new PreviewSession(store,()=>now,isolatedProfile:true);
            var engine=session.Engine;
            engine.SwitchMode(SessionMode.Stopwatch);engine.StartStopwatch();
            now=now.AddMilliseconds(12999);
            var pressedAt=now.ToUnixTimeMilliseconds();
            now=now.AddSeconds(3); // Input waited while the UI thread was busy.
            store.BeforeSave=()=>now=now.AddSeconds(5);
            engine.ActivityRecorded+=_=>now=now.AddSeconds(4); // Slow diagnostics/audio.
            switch(action){
                case "mode":session.ToggleModeFromShortcut(pressedAt);break;
                case "reflection":session.ReflectionForShortcut(pressedAt);break;
                case "toggle":session.ToggleTimerFromShortcut(pressedAt);break;
                default:session.Execute(action,JsonSerializer.SerializeToElement(new{}),pressedAt);break;
            }
            now=now.AddSeconds(20); // A cold reflection WebView is still loading.
            var timer=action=="mode"?engine.Snapshot.ParkedTimer!:engine.Snapshot.Timer;
            check(!timer.IsRunning&&timer.ElapsedMilliseconds==12999,
                "Stopwatch freezes at input time, excluding queue, save, audio and popup delay: "+action);
            var prompt=engine.Snapshot.Prompts.SingleOrDefault();
            if(prompt is not null){
                check(prompt.ActualDurationSeconds==12&&prompt.CompletedAt==pressedAt,
                    "Reflection uses the captured stop time and actual seconds: "+action);
                engine.QueueReflection(prompt.Id,"Synthetic work",localOnly:true);
                check(engine.Snapshot.Outbox.Single().ActualDurationSeconds==12,
                    "Sheet-bound actual time excludes reflection-loading and submission delay: "+action);
            }
        }
        var clock=DateTimeOffset.Now;
        var memory=new MemoryStore();
        var precise=new TimerEngine(memory,()=>clock);
        precise.SwitchMode(SessionMode.Stopwatch);precise.StartStopwatch();clock=clock.AddMilliseconds(1750);
        precise.Pause();clock=clock.AddMinutes(1);precise.Resume();
        precise.Pause(clock.AddMinutes(-2).ToUnixTimeMilliseconds());
        check(precise.Snapshot.Timer.ElapsedMilliseconds==1750,
            "A delayed stop cannot subtract active time accumulated before the latest resume");
        precise.Resume();clock=clock.AddSeconds(2);precise.Pause(clock.AddHours(1).ToUnixTimeMilliseconds());
        check(precise.Snapshot.Timer.ElapsedMilliseconds==3750,"A future stop timestamp cannot add time to Stopwatch");
        precise.Resume();clock=clock.AddSeconds(1);var before=JsonSerializer.Serialize(precise.Snapshot);memory.Fail=true;
        try{precise.ReviewStopwatch(clock.ToUnixTimeMilliseconds());throw new Exception("Failed timed stop accepted");}catch(IOException){}
        check(JsonSerializer.Serialize(precise.Snapshot)==before,
            "Failed pause persistence keeps the running stopwatch and creates no uncommitted reflection");
    }
    private sealed class SlowStore:IStateStore
    {
        private AppState saved=new();
        internal Action? BeforeSave;
        public AppState Load()=>DataJson.Clone(saved);
        public void Save(AppState state){BeforeSave?.Invoke();saved=DataJson.Clone(state);}
    }
    private sealed class Receiver:HttpMessageHandler
    {
        public bool Stopwatch;public int Writes;public JsonElement Last;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellation)
        {
            Last=JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellation)).RootElement.Clone();
            var ping=Last.GetProperty("action").GetString()=="ping";if(!ping)Writes++;
            return new(HttpStatusCode.OK){Content=new StringContent(ping?JsonSerializer.Serialize(new{success=true,target="Synthetic",supportsStopwatch=Stopwatch}):JsonSerializer.Serialize(new{success=true,requestId=Last.GetProperty("requestId").GetString(),sheet="test"}))};
        }
    }
}
