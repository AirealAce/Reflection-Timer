using System.Text.Json;
using System.Threading.Channels;
using ReflectionTimer.Accessible;
using ReflectionTimer.Core;
using ReflectionTimer.Desktop;

internal static class FocusModeTests
{
    internal static async Task Run(Action<bool,string> check)
    {
        var target=new FocusTarget(Guid.NewGuid(),FocusTargetKind.Window,"Synthetic focus target","synthetic",123,456,789);
        var legacy=JsonSerializer.Deserialize<AppState>("{}")!;
        check(!legacy.FocusMode.Enabled&&legacy.FocusMode.DelaySeconds==5,"Existing profiles gain disabled focus mode with a five-second delay");
        check(AudioSettings.From(legacy).FocusLost is {Track:LibrarySound.TrainerBattle,Behavior:SoundBehavior.Polite},"Focus audio defaults to the existing Trainer Battle MP3 without interrupting other sounds");
        var store=new MemoryStore();var engine=new TimerEngine(store);
        var beforeAudio=engine.Snapshot.Audio;var beforeConnection=engine.Snapshot.Connection;
        engine.SetFocusMode(true,7,target);
        var restored=new TimerEngine(store).Snapshot;
        check(restored.FocusMode is {Enabled:true,DelaySeconds:7}&&restored.FocusMode.Target==target,"Focus choice and delay survive a state reload");
        check(restored.Audio==beforeAudio&&restored.Connection==beforeConnection,"Saving focus mode preserves audio and Sheets preferences");
        foreach(var invalid in new[]{-1,TimerEngine.MaxDuration+1}){
            try{engine.SetFocusMode(true,invalid,target);throw new Exception("Invalid delay accepted");}catch(ArgumentException){}
        }
        try{engine.SetFocusMode(true,5,null);throw new Exception("Missing target accepted");}catch(ArgumentException){}
        check(engine.Snapshot.FocusMode.DelaySeconds==7&&engine.Snapshot.FocusMode.Target==target,"Invalid focus settings cannot replace a saved target");
        foreach(var mode in Enum.GetValues<SessionMode>()){
            var gate=new FocusModeGate();var options=new FocusModeSettings{Enabled=true,DelaySeconds=5,Target=target};
            var timer=new TimerState{Mode=mode,IsRunning=true,SessionId=Guid.NewGuid()};
            check(!gate.Evaluate(options,timer,FocusPresence.Focused,1000).Alert,mode+": no alert while the target is focused");
            check(!gate.Evaluate(options,timer,FocusPresence.Away,2000).Alert&&!gate.Evaluate(options,timer,FocusPresence.Away,6999).Alert,mode+": short switches and the full configured grace period remain silent");
            check(gate.Evaluate(options,timer,FocusPresence.Away,7000).Alert,mode+": alert begins at the delay boundary");
            check(!gate.Evaluate(options,timer,FocusPresence.Focused,7001).Alert,mode+": returning stops immediately");
            check(!gate.Evaluate(options,timer,FocusPresence.Away,8000).Alert&&gate.Evaluate(options,timer,FocusPresence.Away,13000).Alert,mode+": the next switch gets a fresh delay");
            check(!gate.Evaluate(options,timer with{IsRunning=false},FocusPresence.Away,13001).Alert,mode+": pausing stops the alert");
            check(!gate.Evaluate(options,timer,FocusPresence.Away,16000).Alert,mode+": resume starts a fresh delay");
            check(!gate.Evaluate(options,timer,FocusPresence.Unavailable,17000).Alert&&!gate.Evaluate(options,timer,FocusPresence.Unknown,18000).Alert,mode+": closed or unreadable targets stop instead of generating false alarms");
            check(!gate.Evaluate(options,timer,FocusPresence.Away,19000).Alert,mode+": target recovery gets a fresh delay");
            check(!gate.Evaluate(options with{Target=target with{Id=Guid.NewGuid()}},timer,FocusPresence.Away,24000).Alert,mode+": selecting another target resets the grace period");
            check(!gate.Evaluate(options,timer with{SessionId=Guid.NewGuid()},FocusPresence.Away,30000).Alert,mode+": a new session cannot inherit an old away period");
            check(!gate.Evaluate(options with{Enabled=false},timer,FocusPresence.Away,50000).Alert,mode+": turning focus mode off stops monitoring");
            check(gate.Evaluate(options with{DelaySeconds=0},timer,FocusPresence.Away,50001).Alert,mode+": zero delay alerts on the first away sample");
        }
        await Monitor(check,target);
        await Audio(check,target);
    }
    private static async Task Monitor(Action<bool,string> check,FocusTarget target)
    {
        var now=DateTimeOffset.Now;var store=new MemoryStore();var engine=new TimerEngine(store,()=>now);
        engine.SetFocusMode(true,0,target);var source=new FakeSource();var transitions=new List<bool>();
        using var monitor=new FocusModeMonitor(engine,source,transitions.Add);
        monitor.Poll();check(source.Reads==0,"Inactive sessions never query another application's focus");
        engine.Start(900,false,50);source.Presence=FocusPresence.Away;monitor.Poll();
        check(transitions.SequenceEqual(new[]{true}),"Monitor starts exactly one alert on focus loss");
        monitor.Poll();check(transitions.Count==1,"Repeated away samples do not create overlapping alerts");
        source.Presence=FocusPresence.Focused;monitor.Poll();check(transitions.SequenceEqual(new[]{true,false}),"Returning stops the alert once");
        source.Presence=FocusPresence.Away;monitor.Poll();engine.Pause();check(transitions.Last()==false,"Pause stops audio synchronously without waiting for a focus probe");
        engine.Resume();source.Pending=new(TaskCreationOptions.RunContinuationsAsynchronously);monitor.Poll();
        now=now.AddSeconds(3);monitor.Poll();monitor.Poll();check(source.Reads==5,"A hung provider has only one request in flight");
        check(!transitions.Last()&&monitor.Status.Contains("could not be checked"),"A stalled focus provider fails quietly and exposes a readable status");
        source.Pending.TrySetResult(FocusPresence.Away);monitor.Poll();engine.SetFocusMode(false,0,target);check(!transitions.Last(),"Disable cancels an alert even after an asynchronous result");
        await Task.CompletedTask;
    }
    private static async Task Audio(Action<bool,string> check,FocusTarget target)
    {
        var directory=Path.Combine(Path.GetTempPath(),"ReflectionTimer-FocusAudio-"+Guid.NewGuid().ToString("N"));
        var store=new MemoryStore{State=new(){LoggingEnabled=false,Audio=new(){Success=new(){Behavior=SoundBehavior.Polite},SessionEnd=new(){Track=LibrarySound.None}}}};
        var engine=new TimerEngine(store);engine.SetFocusMode(true,0,target);engine.Start(900,false,80);
        var backend=new HoldingAudio();using var services=new PreviewServices(engine,directory,audio:backend,speech:new SilentVoice());
        try{
            services.SetFocusAlert(true);var focus=await backend.Next();
            check(Path.GetFileName(focus.Path)=="pokemon-battle-trainer.mp3"&&focus.Level.Volume==80,"Focus alerts use the bundled Pokémon battle track and App sound volume");
            _=services.Play(SoundEvent.Success);var success=await backend.Next();
            services.SetFocusAlert(false);
            check(focus.Token.IsCancellationRequested&&!success.Token.IsCancellationRequested,"Returning cancels only focus audio, leaving unrelated audio playing");
            services.SetFocusAlert(true);var next=await backend.Next();
            engine.SetAppVolume(40);engine.SetSound(SoundEvent.FocusLost,AudioSettings.From(engine.Snapshot).FocusLost with{Volume=25});
            check(next.Level.Volume==40&&next.Level.SoundVolume==25,"Both volume controls update a playing focus alert");
            engine.SetSound(SoundEvent.Success,AudioSettings.From(engine.Snapshot).Success with{Behavior=SoundBehavior.Disruptive});
            _=services.Play(SoundEvent.Success);var interrupt=await backend.Next();
            check(next.Token.IsCancellationRequested,"A disruptive alert keeps its original ability to interrupt focus audio");
            interrupt.Finish.TrySetResult();var resumed=await backend.Next();
            check(Path.GetFileName(resumed.Path)=="pokemon-battle-trainer.mp3"&&!resumed.Token.IsCancellationRequested,"Focus audio resumes after an interruption when the target remains unfocused");
            _=services.Play(SoundEvent.Success);var finalInterrupt=await backend.Next();services.SetFocusAlert(false);
            finalInterrupt.Finish.TrySetResult();await Task.Delay(300);
            check(!backend.HasPending,"Returning during another alert prevents a delayed focus-audio restart");
            services.SetFocusAlert(true);var last=await backend.Next();
            services.StopAudio();check(last.Token.IsCancellationRequested,"Stop all app audio also cancels the focus loop");
        }finally{
            services.Dispose();await Task.Delay(50);
            if(Directory.Exists(directory))Directory.Delete(directory,true);
        }
    }
    private sealed class FakeSource:IFocusTargetSource
    {
        internal FocusPresence Presence;internal int Reads;internal TaskCompletionSource<FocusPresence>? Pending;
        public Task<IReadOnlyList<FocusTarget>> ListAsync(FocusTargetKind kind)=>Task.FromResult<IReadOnlyList<FocusTarget>>([]);
        public Task<FocusPresence> CheckAsync(FocusTarget target){Reads++;return Pending?.Task??Task.FromResult(Presence);}
        public void Dispose(){}
    }
    private sealed record Playback(string Path,AudioLevel Level,CancellationToken Token)
    {
        internal TaskCompletionSource Finish { get; }=new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private sealed class HoldingAudio:IAlertAudioBackend
    {
        private readonly Channel<Playback> events=Channel.CreateUnbounded<Playback>();
        internal bool HasPending=>events.Reader.TryPeek(out _);
        public Task<Playback> Next()=>events.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        public async Task PlayAsync(string path,AudioLevel level,CancellationToken token){var playback=new Playback(path,level,token);await events.Writer.WriteAsync(playback);await playback.Finish.Task.WaitAsync(token);}
    }
    private sealed class SilentVoice:IVoiceOutput
    {
        public void Speak(string text,int volume){}public void SetVolume(int volume){}public bool TakeFailure()=>false;
        public void Stop(){}public void Dispose(){}
    }
}
