using System.Text.Json;
using System.Windows.Forms.Automation;
using ReflectionTimer.Accessible;
using ReflectionTimer.Core;

// Test message generation/routing, not a screen-reader process or accessibility
// tree. Native speech is replaced; no production profile or Sheets is accessed.
static class ScreenReaderFeedbackTests
{
    internal static void Run(Action<bool,string> check)
    {
        FocusAndShortcutFeedback(check);
        var now=DateTimeOffset.Now;
        var store=new MemoryStore{State=new(){ShowFloatingTimer=true,FloatingTimeOnly=false,ShowAppView=false}};
        var engine=new TimerEngine(store,()=>now);
        var reader=new List<(string Text,bool Supplementary)>();var speech=new Speech();
        using var feedback=new SessionVoice(engine,speech,(text,extra)=>reader.Add((text,extra)));
        check(reader.Count==0&&!engine.SettingsSnapshot.VoiceAnnouncements,"New profiles start with app voice off and no fabricated startup announcement");
        engine.Start(3723,false,0);
        check(reader.Single()==("Timer started with 1 hour 2 minutes 3 seconds remaining.",false),"Screen-reader start includes the same full duration even with app voice off and App sound muted");
        now=now.AddSeconds(1);engine.Pause();
        check(reader.Last().Text=="Timer paused with 1 hour 2 minutes 2 seconds remaining.","Screen-reader pause reports committed remaining time");
        engine.Resume();check(reader.Last().Text=="Timer resumed with 1 hour 2 minutes 2 seconds remaining.","Screen-reader resume has the same wording as app voice");
        engine.Reset();check(reader.Last().Text=="Timer reset to 1 hour 2 minutes 3 seconds.","Reset duration is accessible without optional voice");
        engine.SwitchMode(SessionMode.Stopwatch);check(reader.Last().Text=="Stopwatch mode.","Timer-to-Stopwatch destination is accessible without optional voice");
        engine.StartStopwatch();check(reader.Last().Text=="Stopwatch started.","A zero stopwatch omits only its zero time");
        now=now.AddMilliseconds(3250);engine.Pause();check(reader.Last().Text=="Stopwatch paused at 3 seconds elapsed.","Stopwatch screen-reader feedback uses elapsed time rather than remaining time");
        engine.Resume();check(reader.Last().Text=="Stopwatch resumed at 3 seconds elapsed.","Stopwatch resume includes accumulated active time");
        engine.SwitchMode(SessionMode.Timer);
        check(reader.Last().Text=="Stopwatch paused at 3 seconds elapsed. Timer mode.","Mode-switch pause and destination use one accessible message");
        engine.SetFloatingTimeOnly(true);check(reader.Last()==("Time-only view.",true),"View feedback is marked supplementary for screen-reader queuing");
        engine.SetFloatingTimer(false);check(reader.Last().Text=="Floating timer hidden.","Hiding a viewer remains announceable when no timer window has focus");
        var count=reader.Count;engine.SetFloatingTimeOnly(false);check(reader.Count==count,"Hidden layout edits do not announce an unseen view");
        engine.SetFloatingTimer(true);check(reader.Last().Text=="Compact view.","Showing Compact has accessible view feedback");
        engine.SetAppViewVisibility(true);check(reader.Last().Text=="App view.","App visibility has accessible feedback");
        engine.SetAppViewVisibility(false);check(reader.Last().Text=="App view hidden.","Hidden App view has distinct accessible feedback");
        count=reader.Count;engine.SetTheme(AppColorTheme.Glamour);engine.Checkpoint();engine.SetFloatingTimer(true);engine.SwitchMode(SessionMode.Timer);
        check(reader.Count==count&&speech.Messages.Count==0,"Checkpoints, unrelated settings and unchanged modes neither repeat status nor synthesize disabled speech");
        engine.SetVoiceAnnouncements(true);engine.SetAppVolume(55);
        check(reader.Count==count,"Enabling voice and unmuting do not invent a timer transition");
        engine.Start(65,false,55);check(reader.Last().Text==speech.Messages.Last(),"Enabled voice and screen-reader channels receive identical action text");
        engine.SetFloatingTimeOnly(true);check(reader.Last().Text==speech.Messages.Last(),"Enabled voice and screen-reader channels receive identical view text");
        engine.SetAppVolume(0);var spoken=speech.Messages.Count;engine.Pause();
        check(reader.Last().Text=="Timer paused with 1 minute 5 seconds remaining."&&speech.Messages.Count==spoken,"Muting app sound never mutes screen-reader status");
        engine.SetVoiceAnnouncements(false);engine.Resume();
        check(reader.Last().Text=="Timer resumed with 1 minute 5 seconds remaining."&&speech.Stops>0,"Disabling app voice cancels it but leaves screen-reader status available");
        count=reader.Count;store.Fail=true;
        try{engine.Pause();throw new Exception("Expected save failure");}catch(IOException){}
        check(reader.Count==count&&engine.CurrentTimer.IsRunning,"Failed state saves cannot announce an uncommitted pause through either channel");
        store.Fail=false;engine.SetAppVolume(55);engine.SetVoiceAnnouncements(true);speech.Throw=true;engine.Pause();
        check(reader.Last().Text=="Timer paused with 1 minute 5 seconds remaining."&&feedback.PollFailure() is not null,"Optional voice failure does not suppress accessible status");
        speech.Throw=false;feedback.Dispose();count=reader.Count;engine.Resume();
        check(reader.Count==count,"Disposal detaches accessible feedback alongside voice");

        var saved=JsonSerializer.Serialize(new AppState{VoiceAnnouncements=true},DataJson.Options);
        check(JsonSerializer.Deserialize<AppState>(saved,DataJson.Options)!.VoiceAnnouncements,"Existing explicit voice-on preferences survive the new off default");
        check(!JsonSerializer.Deserialize<AppState>("{}",DataJson.Options)!.VoiceAnnouncements,"Profiles lacking a voice preference receive the off default");
        var failingReaderSpeech=new Speech();
        using(var failingReader=new SessionVoice(engine,failingReaderSpeech,(_,_)=>throw new InvalidOperationException("Synthetic notification failure"))) {
            engine.Pause();check(failingReaderSpeech.Messages.Count==1&&!engine.CurrentTimer.IsRunning,"A screen-reader delivery failure cannot suppress app voice or undo a pause");
        }
        check(ScreenReaderAnnouncements.Processing(false)==AutomationNotificationProcessing.ImportantMostRecent&&
            ScreenReaderAnnouncements.Processing(true)==AutomationNotificationProcessing.CurrentThenMostRecent,
            "Native routing prioritizes action status and preserves the current utterance for supplementary views");
        check(!ScreenReaderAnnouncements.TryAnnounce(null,"Synthetic message",false),"An unavailable native provider permits live-region fallback");

        var session=new PreviewSession(new MemoryStore(),()=>now,isolatedProfile:true);
        using var services=new PreviewServices(session.Engine,Path.Combine(Path.GetTempPath(),"ReflectionTimer-Reader-"+Guid.NewGuid().ToString("N")),speech:new Speech());
        var serviceMessages=new List<string>();services.SessionAnnouncement+=(text,_)=>serviceMessages.Add(text);
        var start=session.ToggleTimerFromShortcut();
        check(start.HasSessionFeedback&&serviceMessages.Count==1&&serviceMessages[0].StartsWith("Timer started with "),"Service forwards detailed status and marks the terse command reply as redundant");
        var pause=session.ToggleTimerFromShortcut();var reset=session.ResetTimerFromShortcut();var mode=session.ToggleModeFromShortcut();
        check(pause.HasSessionFeedback&&reset.HasSessionFeedback&&mode.HasSessionFeedback,"Pause, reset and mode commands do not add duplicate terse announcements");
        var settings=session.Execute("repeat",JsonSerializer.SerializeToElement(new{enabled=false}));
        check(!settings.HasSessionFeedback,"Ordinary settings confirmations keep their existing live feedback");
    }
    private static void FocusAndShortcutFeedback(Action<bool,string> check)
    {
        var store=new MemoryStore{State=new(){VoiceAnnouncements=true,Timer=new(){Volume=55},FocusMode=new(){IdleEnabled=true}}};
        var engine=new TimerEngine(store);var speech=new Speech();var reader=new List<(string Text,bool Supplementary)>();
        using var feedback=new SessionVoice(engine,speech,(text,extra)=>reader.Add((text,extra)));
        foreach(var mode in new[]{SessionMode.Timer,SessionMode.Stopwatch}){
            engine.SwitchMode(mode);reader.Clear();speech.Messages.Clear();
            var options=engine.SettingsSnapshot.FocusMode;
            engine.SetFocusMode(options with{Enabled=true});
            check(reader.SequenceEqual(new[]{("Focus mode on.",false)})&&speech.Messages.SequenceEqual(new[]{"Focus mode on."}),mode+": committed Focus on reaches app voice and screen-reader feedback once");
            engine.SetFocusMode(engine.SettingsSnapshot.FocusMode with{DelaySeconds=9});engine.Checkpoint();
            engine.SetFocusMode(engine.SettingsSnapshot.FocusMode);
            check(reader.Count==1&&speech.Messages.Count==1,"Focus delay edits, unchanged saves and checkpoints do not repeat announcements");
            engine.SetFocusMode(engine.SettingsSnapshot.FocusMode with{Enabled=false});
            check(reader.Last()==("Focus mode off.",false)&&reader.Count==2&&speech.Messages.SequenceEqual(new[]{"Focus mode on.","Focus mode off."}),mode+": committed Focus off uses the same feedback once");
        }
        engine.SetVoiceAnnouncements(false);reader.Clear();speech.Messages.Clear();
        engine.SetFocusMode(engine.SettingsSnapshot.FocusMode with{Enabled=true});
        check(reader.Single().Text=="Focus mode on."&&speech.Messages.Count==0,"Focus remains accessible with optional app voice disabled");
        engine.SetVoiceAnnouncements(true);engine.SetAppVolume(0);
        engine.SetFocusMode(engine.SettingsSnapshot.FocusMode with{Enabled=false});
        check(reader.Last().Text=="Focus mode off."&&speech.Messages.Count==0,"Master mute silences Focus speech without suppressing reader feedback");
        engine.SetAppVolume(55);store.Fail=true;var count=reader.Count;
        try{engine.SetFocusMode(engine.SettingsSnapshot.FocusMode with{Enabled=true});throw new Exception("Expected failed Focus save");}catch(IOException){}
        check(reader.Count==count&&speech.Messages.Count==0&&!engine.SettingsSnapshot.FocusMode.Enabled,"Failed Focus saves announce no success and retain the saved state");
        store.Fail=false;
        feedback.Feedback("Choose a Focus target.");
        check(reader.Last()==("Choose a Focus target.",false)&&speech.Messages.Single()=="Choose a Focus target.","Explicit shortcut guidance uses both feedback channels without inventing a Focus transition");
        engine.SetVoiceAnnouncements(false);count=speech.Messages.Count;
        feedback.Feedback("No pending reflections.");
        check(reader.Last().Text=="No pending reflections."&&speech.Messages.Count==count,"No-op shortcut feedback respects voice opt-out but stays accessible");
        engine.SetVoiceAnnouncements(true);engine.SetAppVolume(0);
        feedback.Feedback("That action is unavailable.");
        check(reader.Last().Text=="That action is unavailable."&&speech.Messages.Count==count,"Shortcut error feedback respects master mute independently of the reader");
        engine.SetAppVolume(55);speech.Throw=true;feedback.Feedback("Synthetic shortcut failure.");
        check(reader.Last().Text=="Synthetic shortcut failure."&&feedback.PollFailure() is not null,"Failed app speech cannot suppress shortcut reader feedback");
        speech.Throw=false;count=reader.Count;feedback.Feedback(" ");
        feedback.Dispose();feedback.Feedback("After disposal.");
        check(reader.Count==count&&speech.Messages.Count==1,"Blank feedback and disposed feedback cannot queue announcements");
        var restoredMessages=new List<string>();var restoredSpeech=new Speech();
        using var restored=new SessionVoice(new TimerEngine(store),restoredSpeech,(text,_)=>restoredMessages.Add(text));
        check(restoredMessages.Count==0&&restoredSpeech.Messages.Count==0,"Restoring Focus settings does not announce a fabricated transition");
    }
    private sealed class Speech : IVoiceOutput
    {
        internal readonly List<string> Messages=[];internal int Stops;internal bool Throw;
        public void Speak(string text,int volume){if(Throw)throw new IOException("Synthetic voice failure");Messages.Add(text);}
        public void SetVolume(int volume){}
        public void Stop()=>Stops++;
        public bool TakeFailure()=>false;
        public void Dispose(){}
    }
}
