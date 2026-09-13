using System.Collections.Concurrent;
using ReflectionTimer.Accessible;
using ReflectionTimer.Core;
using ReflectionTimer.Desktop;

static class QuietCompletionTests
{
    internal static async Task Run(Action<bool,string> check)
    {
        foreach(var paused in new[]{false,true})foreach(var repeat in new[]{false,true})foreach(var behavior in Enum.GetValues<SoundBehavior>()) {
            var directory=Path.Combine(Path.GetTempPath(),"ReflectionTimer-QuietCompletion-"+Guid.NewGuid().ToString("N"));
            var now=DateTimeOffset.Now;
            var engine=new TimerEngine(new MemoryStore{State=new(){Audio=new(){
                SessionEnd=new(){Track=LibrarySound.ObtainedItem,Behavior=behavior},
                Success=new(){Track=LibrarySound.LevelUp,Behavior=SoundBehavior.Polite}
            }}},()=>now);
            var audio=new RecordingAudio();
            using var services=new PreviewServices(engine,directory,audio:audio);
            try {
                engine.Start(60,repeat,50,lowTime:new(){Enabled=false});now=now.AddSeconds(10);
                var id=engine.CheckIn();if(paused)engine.Pause();
                engine.QueueReflection(id,"Synthetic early response","Synthetic reason",endSession:true);
                // A real backend is asynchronous; leave queued playback time to
                // reach the spy before checking absence of completion audio.
                await services.Play(SoundEvent.Success);await Task.Delay(100);
                var endPath=SoundLibrary.Resolve(SoundEvent.SessionEnd,AudioSettings.From(engine.Snapshot).SessionEnd);
                check(!audio.Paths.Contains(endPath!)&&audio.Paths.Count==1,
                    $"End-and-send suppresses completion audio, retaining success feedback: paused={paused}, repeat={repeat}, {behavior}");
                check(engine.Snapshot.Prompts.Count==0&&engine.Snapshot.Timer.IsRunning==repeat,
                    "Quiet completion leaves no pending popup and preserves auto-start");
                check(services.Log.Recent().Count(e=>e.Event=="reflection.endedAndQueued"&&e.ItemId==id)==1,
                    "Quiet end-and-send is recorded once in privacy-safe diagnostics");
                if(!repeat)engine.Start(60,false,50,lowTime:new(){Enabled=false});
                else engine.SetPreferences(false,50);
                now=now.AddSeconds(50);engine.Advance();
                check(engine.Snapshot.Prompts.Count==0&&!audio.Paths.Contains(endPath!),
                    "Crossing the submitted session's original deadline cannot create another completion");
                now=now.AddSeconds(10);engine.Advance();
                using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(5));
                while(!audio.Paths.Contains(endPath!))await Task.Delay(10,timeout.Token);
                engine.Advance();
                check(engine.Snapshot.Prompts.Count==1&&audio.Paths.Count(p=>p==endPath)==1,
                    "The next session's genuine completion still creates one prompt and one selected completion sound");
            } finally {
                services.Dispose();
                if(Directory.Exists(directory)){
                    foreach(var name in new[]{"diagnostics.dat","diagnostics.dat.bak"})File.Delete(Path.Combine(directory,name));
                    Directory.Delete(directory);
                }
            }
        }
    }
    private sealed class RecordingAudio:IAlertAudioBackend
    {
        internal ConcurrentQueue<string> Paths {get;}=new();
        public Task PlayAsync(string path,AudioLevel level,CancellationToken cancellationToken)
        {Paths.Enqueue(path);return Task.CompletedTask;}
    }
}
