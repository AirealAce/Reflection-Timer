using System.Text.Json;
using ReflectionTimer.Accessible;
using ReflectionTimer.Core;

static class SessionEndPopupTests
{
    internal static async Task Run(Action<bool,string> check)
    {
        check(new AppState().SessionEndPopups&&JsonSerializer.Deserialize<AppState>("{}",DataJson.Options)!.SessionEndPopups,
            "Automatic session-end popups default on for fresh and older profiles");
        var now=DateTimeOffset.Now;var store=new MemoryStore();
        var session=new PreviewSession(store,()=>now,isolatedProfile:true);
        session.Engine.SetSessionEndPopups(false);session.Engine.SetAutoSendIncompleteReflections(false);
        check(!new PreviewSession(store).Engine.SettingsSnapshot.SessionEndPopups,"Popup opt-out persists across app reopen");
        store.Fail=true;
        try{session.Engine.SetSessionEndPopups(true);throw new Exception("Expected write failure");}catch(IOException){}
        check(!session.Engine.SettingsSnapshot.SessionEndPopups,"Failed popup-setting save preserves the previous choice");store.Fail=false;
        var shown=new List<Guid>();var windows=new List<Window>();var queued=0;
        var coordinator=new ReflectionPromptCoordinator(session,()=>windows.Cast<IReflectionPromptWindow>().ToArray(),
            (id,_)=>{shown.Add(id);return Task.CompletedTask;},()=>queued++);
        session.Engine.Start(3,true,0);now=now.AddSeconds(3);
        var first=session.Tick().Single();
        await coordinator.OpenAsync(first.Id,false,sessionCompleted:true,automaticCompletion:true);
        check(shown.Count==0&&session.Engine.Snapshot.Prompts.Single().Id==first.Id,"Disabled automatic completion does not show a window or discard the reflection");
        check(session.Engine.CurrentTimer.IsRunning&&session.Engine.CurrentTimer.AutoRestart,"Suppressing a popup does not interrupt auto-start");
        session.Engine.SaveDraft(first.Id,"Saved locally");
        var visible=new Window(first.Id);windows.Add(visible);
        now=now.AddSeconds(3);var second=session.Tick().Single();
        await coordinator.OpenAsync(second.Id,false,sessionCompleted:true,automaticCompletion:true);
        check(shown.Count==0&&visible.Prepared==0&&session.Engine.Snapshot.Prompts.Count==2,"Popup opt-out leaves an existing editor alone and retains older drafts when auto-send is off");
        await coordinator.OpenAsync(second.Id,true);
        check(shown.Last()==second.Id,"Pending reflections can still be opened manually with automatic popups off");
        await coordinator.NavigateAsync(second.Id,-1);check(shown.Last()==first.Id,"Prev/Next browsing still works with automatic popups off");
        session.Engine.EndEarly();var early=session.Engine.Snapshot.Prompts.Last();
        await coordinator.OpenAsync(early.Id,true,sessionCompleted:true);
        check(shown.Last()==early.Id,"An explicit end-early request still opens its reflection");
        var practice=session.Engine.TestPrompt();await coordinator.OpenAsync(practice,true);
        check(shown.Last()==practice,"Test reflection remains available while automatic popups are off");
        session.Engine.SwitchMode(SessionMode.Stopwatch);session.Engine.StartStopwatch();now=now.AddSeconds(7);
        var review=session.Engine.ReviewStopwatch();await coordinator.OpenAsync(review,true);
        check(shown.Last()==review&&!session.Engine.CurrentTimer.IsRunning,"Explicit stopwatch pause-and-reflect still opens its editor");
        session.Engine.SetSessionEndPopups(true);session.Engine.SwitchMode(SessionMode.Timer);session.Engine.Reset(2);session.Engine.Start(2,false,0);
        now=now.AddSeconds(2);var enabled=session.Tick().Single();
        await coordinator.OpenAsync(enabled.Id,false,sessionCompleted:true,automaticCompletion:true);
        check(shown.Last()==enabled.Id,"Re-enabling automatic popups restores the next completion window");

        // Separate auto-send policy still applies, including flushing a visible
        // older draft. Use its await to exercise a setting change while queued.
        var other=new PreviewSession(new MemoryStore(),()=>now,isolatedProfile:true);other.Engine.Start(2,false,0);now=now.AddSeconds(2);
        var prior=other.Tick().Single();other.Engine.Start(2,false,0);now=now.AddSeconds(2);var latest=other.Tick().Single();
        var release=new TaskCompletionSource();var waiting=new Window(prior.Id){Flush=()=>release.Task};var displays=0;var sends=0;
        var pending=new ReflectionPromptCoordinator(other,()=>[waiting],(_,_)=>{displays++;return Task.CompletedTask;},()=>sends++);
        var arrival=pending.OpenAsync(latest.Id,false,sessionCompleted:true,automaticCompletion:true);
        other.Engine.SetSessionEndPopups(false);other.Engine.SaveDraft(prior.Id,"Latest draft before suppressed popup");release.SetResult();await arrival;
        check(displays==0&&sends==1&&waiting.Closed,"Changing popup preference during completion is respected without disabling the separate auto-send policy");
        check(other.Engine.Snapshot.Outbox.Single().Message=="Latest draft before suppressed popup"&&other.Engine.Snapshot.Outbox.Single().AutoSent,
            "Auto-send retains the newest draft and metadata when the next popup is suppressed");
        check(other.Engine.Snapshot.Prompts.Single().Id==latest.Id,"Suppressing and auto-sending never submits the newly completed reflection");
        using var services=new PreviewServices(other.Engine,Path.Combine(Path.GetTempPath(),"ReflectionTimer-Popup-"+Guid.NewGuid().ToString("N")),speech:new SilentSpeech());
        check(!JsonSerializer.SerializeToElement(services.Settings(),PreviewSession.Json).GetProperty("sessionEndPopups").GetBoolean(),"Settings bridge exposes the saved popup preference");
    }
    private sealed class Window(Guid id):IReflectionPromptWindow
    {
        public Guid ReflectionId=>id;
        internal int Prepared;internal bool Closed;internal Func<Task>? Flush;
        public Task PrepareHandoffAsync(){Prepared++;return Flush?.Invoke()??Task.CompletedTask;}
        public void ResumeEditing(){}
        public void CloseAfterSave()=>Closed=true;
    }
}
