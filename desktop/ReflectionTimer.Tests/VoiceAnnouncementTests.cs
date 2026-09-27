using System.Collections.Concurrent;
using System.Text.Json;
using System.Xml.Linq;
using ReflectionTimer.Accessible;
using ReflectionTimer.Core;

static class VoiceAnnouncementTests
{
    internal static async Task Run(Action<bool,string> check)
    {
        Reset(check);Preset(check);Modes(check);
        var now=DateTimeOffset.Now;var store=new MemoryStore();var engine=new TimerEngine(store,()=>now);
        check(!engine.SettingsSnapshot.VoiceAnnouncements&&!JsonSerializer.Deserialize<AppState>("{}",DataJson.Options)!.VoiceAnnouncements,"New and missing-preference profiles leave optional voice off by default");
        engine.SetVoiceAnnouncements(false);
        var speech=new RecordingSpeech();using var voice=new SessionVoice(engine,speech);
        engine.Start(3723,false,55);engine.Pause();engine.Resume();
        check(speech.Messages.Count==0&&!new TimerEngine(store,()=>now).SettingsSnapshot.VoiceAnnouncements,"Explicitly disabled voice stays off across reloads");
        engine.SetVoiceAnnouncements(true);
        check(new TimerEngine(store,()=>now).SettingsSnapshot.VoiceAnnouncements&&speech.Messages.Count==0,"Voice preference persists without announcing a fake start when enabled");
        now=now.AddSeconds(1);engine.Pause();
        check(speech.Messages.Last()==("Timer paused with 1 hour 2 minutes 2 seconds remaining.",55),"Timer pause speaks remaining time and follows App sound volume");
        var count=speech.Messages.Count;engine.Pause();engine.SetTheme(AppColorTheme.Glamour);engine.Checkpoint();
        check(speech.Messages.Count==count,"Repeated pauses, settings saves and checkpoints do not repeat speech");
        engine.Resume();check(speech.Messages.Last().Text=="Timer resumed with 1 hour 2 minutes 2 seconds remaining.","Timer resume announces current remaining time");
        count=speech.Messages.Count;engine.Resume();check(speech.Messages.Count==count,"Repeated resume does not announce another transition");
        engine.Reset();engine.Start(62,false,40);
        check(speech.Messages.Last()==("Timer started with 1 minute 2 seconds remaining.",40),"Timer start speaks its full duration with singular/plural units");
        engine.SwitchMode(SessionMode.Stopwatch);
        check(speech.Messages.Last().Text=="Timer paused with 1 minute 2 seconds remaining. Stopwatch mode.","Switching modes announces the paused outgoing clock and destination together");
        engine.StartStopwatch();
        check(speech.Messages.Last().Text=="Stopwatch started.","Starting a zero stopwatch omits the zero time only");
        now=now.AddMilliseconds(3456);engine.Pause();
        check(speech.Messages.Last().Text=="Stopwatch paused at 3 seconds elapsed.","Stopwatch pause speaks active elapsed seconds");
        engine.Resume();check(speech.Messages.Last().Text=="Stopwatch resumed at 3 seconds elapsed.","Stopwatch resume uses elapsed, not remaining, time");
        now=now.AddSeconds(2);var prompt=engine.ReviewStopwatch();
        check(speech.Messages.Last().Text=="Stopwatch paused at 5 seconds elapsed.","Opening the stopwatch reflection announces its committed pause");
        engine.SaveReflectionForLater(prompt,"Synthetic draft");
        check(speech.Messages.Last().Text=="Stopwatch resumed at 5 seconds elapsed.","Saving a stopwatch reflection announces its automatic resume");
        engine.SetAppVolume(0);count=speech.Messages.Count;engine.Pause();engine.Resume();
        check(speech.Messages.Count==count&&speech.Volume==0,"Mute applies to voice and does not queue muted transitions");
        engine.SetAppVolume(55);check(speech.Messages.Count==count,"Unmuting does not replay missed announcements");
        engine.SetVoiceAnnouncements(false);check(speech.Stops>0,"Turning voice off stops current speech");
        count=speech.Messages.Count;engine.Pause();engine.Resume();check(speech.Messages.Count==count,"Disabled voice remains silent for real state transitions");
        engine.SetVoiceAnnouncements(true);store.Fail=true;
        try{engine.Pause();throw new Exception("Expected failed save");}catch(IOException){}
        check(engine.CurrentTimer.IsRunning&&speech.Messages.Count==count,"Failed state saves never announce an uncommitted pause");
        store.Fail=false;speech.Throw=true;engine.Pause();
        check(!engine.CurrentTimer.IsRunning&&voice.PollFailure() is not null&&voice.PollFailure() is null,"Speech failure cannot undo a saved pause and produces one readable error");
        speech.Throw=false;engine.Reset();engine.SwitchMode(SessionMode.Timer);engine.Reset(3);engine.Start(3,true,55);
        count=speech.Messages.Count;now=now.AddSeconds(3);engine.Advance();
        check(speech.Messages.Count==count+1&&speech.Messages.Last().Text=="Timer started with 3 seconds remaining.","Automatic timer repeats announce each genuine new start once");
        engine.Reset();engine.SetPreferences(false,55);engine.SaveSchedule(null,now.AddSeconds(1),120,false,55);
        count=speech.Messages.Count;now=now.AddSeconds(1);engine.Advance();
        check(speech.Messages.Count==count+1&&speech.Messages.Last().Text=="Timer started with 2 minutes remaining.","Scheduled timer starts use the same voice feedback");
        count=speech.Messages.Count;now=now.AddSeconds(120);engine.Advance();
        check(speech.Messages.Count==count,"Natural completion keeps existing end-audio behavior without extra voice or a zero-time readout");
        check(SessionVoice.Message(new(){Mode=SessionMode.Timer,RemainingSeconds=0},"paused",0)=="Timer paused.","Zero countdown time is not read aloud");
        voice.Dispose();count=speech.Messages.Count;engine.Start(60,false,55);
        check(speech.Disposed&&speech.Messages.Count==count,"Disposal detaches voice observers without affecting the timer");
        using(var reopened=new SessionVoice(engine,speech=new RecordingSpeech()))check(speech.Messages.Count==0,"Restoring an already-running profile never announces a fake start");
        var directory=Path.Combine(Path.GetTempPath(),"ReflectionTimer-VoiceService-"+Guid.NewGuid().ToString("N"));
        using(var services=new PreviewServices(engine,directory,speech:speech=new RecordingSpeech())){
            check(JsonSerializer.SerializeToElement(services.Settings(),PreviewSession.Json).GetProperty("voiceAnnouncements").GetBoolean(),"Settings bridge exposes the persisted voice preference");
            services.PreviewVoice();check(speech.Messages.Last().Text=="Timer paused with 5 minutes remaining.","Voice preview uses the same short pause without changing the timer");
            services.StopAudio();check(speech.Stops==1,"Stop all app audio also cancels speech");
        }
        await Worker(check);
    }
    private static void Reset(Action<bool,string> check)
    {
        var now=DateTimeOffset.Now;var store=new MemoryStore();var engine=new TimerEngine(store,()=>now);
        engine.SetVoiceAnnouncements(true);
        var speech=new RecordingSpeech();using var voice=new SessionVoice(engine,speech);
        engine.Start(90,false,55);now=now.AddSeconds(30);
        var count=speech.Messages.Count;engine.Reset();
        check(speech.Messages.Count==count+1&&speech.Messages.Last()==("Timer reset to 1 minute 30 seconds.",55),"Running timer reset speaks the reset duration once, not the old remaining time or a pause");
        engine.Reset(3723);
        check(speech.Messages.Last().Text=="Timer reset to 1 hour 2 minutes 3 seconds.","A reset to edited duration speaks hours, minutes and seconds");
        engine.Start(62,false,55);engine.Pause();engine.Reset();
        check(speech.Messages.Last().Text=="Timer reset to 1 minute 2 seconds.","Paused timer reset announces its restored duration");
        count=speech.Messages.Count;engine.Reset();
        check(speech.Messages.Count==count+1,"An explicitly repeated idle reset still gives feedback once");
        engine.SwitchMode(SessionMode.Stopwatch);engine.StartStopwatch();now=now.AddSeconds(17);
        count=speech.Messages.Count;engine.Reset();
        check(speech.Messages.Count==count+1&&speech.Messages.Last().Text=="Stopwatch reset.","Running stopwatch reset omits zero rather than speaking its previous elapsed time");
        engine.StartStopwatch();now=now.AddSeconds(3);engine.Pause();engine.Reset();
        check(speech.Messages.Last().Text=="Stopwatch reset.","Paused stopwatch reset uses the same feedback");
        check(SessionVoice.Message(new(){Mode=SessionMode.Timer,RemainingSeconds=0},"reset",0)=="Timer reset.","Zero reset time is omitted from speech");
        engine.StartStopwatch();count=speech.Messages.Count;store.Fail=true;
        try{engine.Reset();throw new Exception("Expected failed reset");}catch(IOException){}
        check(speech.Messages.Count==count&&engine.CurrentTimer.IsRunning,"Failed reset saves never announce success or alter the clock");
        store.Fail=false;engine.SetVoiceAnnouncements(false);engine.Reset();
        check(speech.Messages.Count==count,"Reset respects the voice opt-out");
        engine.SetVoiceAnnouncements(true);engine.SetAppVolume(0);engine.Reset();
        check(speech.Messages.Count==count,"Reset respects master mute");
    }
    private static void Modes(Action<bool,string> check)
    {
        var store=new MemoryStore{State=new(){VoiceAnnouncements=true,ShowFloatingTimer=true,FloatingTimeOnly=false,ShowAppView=false}};
        var engine=new TimerEngine(store);var speech=new RecordingSpeech();using var voice=new SessionVoice(engine,speech);
        engine.SwitchMode(SessionMode.Stopwatch);
        check(speech.Messages.Last().Text=="Stopwatch mode.","Idle Timer-to-Stopwatch switch speaks the new mode");
        engine.SwitchMode(SessionMode.Timer);
        check(speech.Messages.Last().Text=="Timer mode.","Switching back speaks Timer mode");
        var count=speech.Messages.Count;engine.SwitchMode(SessionMode.Timer);engine.SetFloatingTimeOnly(false);
        check(speech.Messages.Count==count,"Selecting the current mode/view does not repeat speech");
        engine.SetFloatingTimeOnly(true);check(speech.Messages.Last().Text=="Time-only view.","Shrinking speaks Time-only view");
        engine.SetFloatingTimer(false);check(speech.Messages.Last().Text=="Floating timer hidden.","Hiding speaks floating timer hidden");
        count=speech.Messages.Count;engine.SetFloatingTimeOnly(false);check(speech.Messages.Count==count,"Changing a hidden layout does not announce an unseen view");
        engine.SetFloatingTimer(true);check(speech.Messages.Last().Text=="Compact view.","Showing speaks the actual saved floating mode");
        engine.SetAppViewVisibility(true);check(speech.Messages.Last().Text=="App view.","Opening App view speaks its name");
        engine.SetAppViewVisibility(false);check(speech.Messages.Last().Text=="App view hidden.","Hiding App view has distinct feedback");
        engine.SetVoiceAnnouncements(false);count=speech.Messages.Count;engine.SwitchMode(SessionMode.Stopwatch);engine.SetFloatingTimeOnly(true);
        check(speech.Messages.Count==count,"The existing voice opt-out silences mode and view changes");
        engine.SetVoiceAnnouncements(true);engine.SetAppVolume(0);engine.SwitchMode(SessionMode.Timer);engine.SetFloatingTimeOnly(false);
        check(speech.Messages.Count==count,"Mode and view announcements respect master mute");
        engine.SetAppVolume(50);store.Fail=true;
        try{engine.SetFloatingTimeOnly(true);throw new Exception("Expected failed view save");}catch(IOException){}
        check(speech.Messages.Count==count,"Failed view saves never announce a successful switch");
    }
    private static void Preset(Action<bool,string> check)
    {
        foreach(var mode in new[]{SessionMode.Timer,SessionMode.Stopwatch})
        foreach(var action in new[]{"started","paused","resumed"}) {
            var message=SessionVoice.Message(new(){Mode=mode,RemainingSeconds=300,ElapsedMilliseconds=300000},action,0);
            check(message.Count(c=>c=='.')==1&&message.EndsWith('.'),$"{mode} {action} and time form a single uninterrupted phrase");
        }
        InstalledVoice hazel=new("Microsoft Hazel Desktop","Female","809"),british=new("Other British","Female","0809;409"),
            american=new("Other American","Female","409"),japanese=new("Other Japanese","Female","411"),male=new("Default male","Male","409");
        check(VoicePreset.Select([male,american,british,hazel])==3,"Shared voice default prefers the exact Hazel name over other female voices");
        check(VoicePreset.Select([american,japanese,british])==2,"Missing Hazel falls back to British English female, including multi-language tokens");
        check(VoicePreset.Select([japanese,american])==1,"Missing British voices falls back to American English female");
        check(VoicePreset.Select([male,japanese])==1,"Other installed female voices are the next fallback");
        check(VoicePreset.Select([male])==-1&&VoicePreset.Select([])==-1,"No preferred candidate retains the Windows default without requiring a download");
        check(VoicePreset.Select([hazel with{Name="microsoft hazel desktop"}])==0,"Voice names are matched case-insensitively");
        check(VoicePreset.Culture("bad;0809;409")=="en-GB"&&VoicePreset.Culture("")=="en-GB","Voice culture uses the installed token and safely defaults when metadata is missing");
        var text="Timer <reset> & \"quoted\". <audio src='https://example.invalid'/>";
        var xml=XElement.Parse(VoicePreset.Ssml(text,"en-GB"));XNamespace ns="http://www.w3.org/2001/10/synthesis";
        check(VoicePreset.Rate==3&&xml.Element(ns+"prosody")?.Attribute("pitch")?.Value=="+20%","Shared speed and pitch match the hotkey announcement preset");
        check(xml.Descendants().Count()==1&&xml.Value==text,"Speech text is escaped and cannot inject audio, file paths or SSML elements");
    }
    private static async Task Worker(Action<bool,string> check)
    {
        var started=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release=new ManualResetEventSlim(false);
        var native=new RecordingSystemVoice();ApartmentState apartment=ApartmentState.Unknown;
        using var output=new WindowsVoiceOutput(()=>{apartment=Thread.CurrentThread.GetApartmentState();started.SetResult();release.Wait();return native;});
        output.Speak("old start",50);await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        output.Speak("new pause",30);output.SetVolume(70);release.Set();
        await Until(()=>native.Messages.Count==1);
        check(native.Messages.Single()=="new pause"&&native.VolumeValue==70&&apartment==ApartmentState.STA,"Slow voice initialization never blocks callers; newest speech wins on its own STA thread");
        output.Stop();await Until(()=>native.Messages.LastOrDefault()=="");
        check(true,"Stop cancels in-flight Windows speech");
        output.Speak("next",60);await Until(()=>native.Messages.LastOrDefault()=="next");
        output.SetVolume(0);await Until(()=>native.Messages.LastOrDefault()==""&&native.VolumeValue==0);
        check(true,"Changing master volume to zero cancels speech instead of leaving it queued");
        output.Dispose();await Until(()=>native.Disposed);
        check(true,"Voice shutdown releases the native voice on its owning thread");
        using var broken=new WindowsVoiceOutput(()=>throw new InvalidOperationException("Synthetic missing voice"));
        broken.Speak("sample",50);await Until(broken.TakeFailure);
        check(!broken.TakeFailure(),"Missing Windows voice is reported once without crashing its caller");
        var continuation=new RecordingSystemVoice{Speaking=true};
        using var queued=new WindowsVoiceOutput(()=>continuation);
        queued.Speak("Timer started with 5 minutes remaining.",50);await Until(()=>continuation.Messages.Count==1);
        queued.SpeakSupplement("Compact view.",50);queued.SpeakSupplement("Time-only view.",50);
        await Task.Delay(150);
        check(continuation.Messages.Count==1,"View announcements never cut off the current action/time utterance");
        continuation.Speaking=false;await Until(()=>continuation.Messages.Count==2);
        check(continuation.Messages.Last()=="Time-only view.","Only the latest waiting view is announced after the action finishes");
        continuation.Speaking=true;queued.SpeakSupplement("Floating timer hidden.",50);await Until(()=>continuation.Messages.Last()=="Floating timer hidden.");
        check(true,"A newer view replaces an obsolete view announcement immediately");
        queued.Speak("Stopwatch mode.",50);await Until(()=>continuation.Messages.Last()=="Stopwatch mode.");
        queued.SpeakSupplement("Compact view.",50);queued.Stop();await Until(()=>continuation.Messages.Last()=="");
        continuation.Speaking=false;await Task.Delay(100);
        check(continuation.Messages.Last()=="","Stop cancels both current speech and a waiting view announcement");
    }
    private static async Task Until(Func<bool> ready){using var limit=new CancellationTokenSource(TimeSpan.FromSeconds(5));while(!ready())await Task.Delay(10,limit.Token);}
    private sealed class RecordingSpeech : IVoiceOutput
    {
        internal List<(string Text,int Volume)> Messages=[];internal bool Throw,Disposed;internal int Volume,Stops;
        public void Speak(string text,int volume){if(Throw)throw new IOException("Synthetic speech failure");Messages.Add((text,volume));}
        public void SetVolume(int volume)=>Volume=volume;
        public void Stop()=>Stops++;
        public bool TakeFailure()=>false;
        public void Dispose()=>Disposed=true;
    }
    private sealed class RecordingSystemVoice : ISystemVoice
    {
        internal readonly ConcurrentQueue<string> Messages=new();internal volatile int VolumeValue;internal volatile bool Disposed;
        internal volatile bool Speaking;
        public bool IsSpeaking=>Speaking;
        public int Volume{set=>VolumeValue=value;}
        public void Speak(string text)=>Messages.Enqueue(text);
        public void Dispose()=>Disposed=true;
    }
}
