using System.Collections.Concurrent;
using System.Text.Json;
using ReflectionTimer.Accessible;
using ReflectionTimer.Core;
using ReflectionTimer.Desktop;

static class SuccessAudioTests
{
    internal static async Task Run(Action<bool,string> check)
    {
        var directory=Path.Combine(Path.GetTempPath(),"ReflectionTimer-SuccessAudio-"+Guid.NewGuid().ToString("N"));
        var memory=new MemoryStore {State=new() {LoggingEnabled=false,Audio=new() {
            SessionEnd=new(){Track=LibrarySound.None},Success=new(){Track=LibrarySound.LevelUp,Volume=40,Behavior=SoundBehavior.Polite}
        }}};
        var session=new PreviewSession(memory,isolatedProfile:true);var audio=new RecordingAudio();
        using var services=new PreviewServices(session.Engine,directory,audio:audio,speech:new SilentSpeech());
        try {
            var prompt=session.Engine.TestPrompt();
            session.Execute("skip",JsonSerializer.SerializeToElement(new{id=prompt}));
            using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(4));
            while(audio.Calls.IsEmpty)await Task.Delay(10,timeout.Token);
            var played=audio.Calls.Single();
            check(session.Engine.Snapshot.Prompts.Count==0&&session.Engine.Snapshot.Outbox.Count==0&&Path.GetFileName(played.Path)=="pokemon-level-up.mp3",
                "Successful Skip plays the selected success track once without queuing a reflection");
            check(Math.Abs(played.Gain-.2f)<.001f,"Skip success audio respects master volume and the success sound's relative volume");
            var pending=session.Engine.TestPrompt();memory.Fail=true;
            try{session.Execute("skip",JsonSerializer.SerializeToElement(new{id=pending}));throw new Exception("Failed Skip accepted");}catch(IOException){}
            check(audio.Calls.Count==1&&session.Engine.Snapshot.Prompts.Any(p=>p.Id==pending),"A failed Skip preserves the reflection and never plays success");
            memory.Fail=false;session.Engine.SetSound(SoundEvent.Success,new(){Track=LibrarySound.None});
            session.Execute("skip",JsonSerializer.SerializeToElement(new{id=pending}));
            check(audio.Calls.Count==1,"Skip respects a success track set to None");
            session.Engine.SetSound(SoundEvent.Success,new(){Track=LibrarySound.LevelUp,Volume=0});
            pending=session.Engine.TestPrompt();session.Execute("skip",JsonSerializer.SerializeToElement(new{id=pending}));
            check(audio.Calls.Count==1,"Skip respects a muted success sound");
        }finally{
            services.Dispose();
            if(Directory.Exists(directory)){
                foreach(var name in new[]{"diagnostics.dat","diagnostics.dat.bak"})File.Delete(Path.Combine(directory,name));
                Directory.Delete(directory);
            }
        }
    }
    private sealed class RecordingAudio:IAlertAudioBackend
    {
        internal readonly ConcurrentQueue<(string Path,float Gain)> Calls=new();
        public Task PlayAsync(string path,AudioLevel level,CancellationToken cancellationToken){Calls.Enqueue((path,level.Gain));return Task.CompletedTask;}
    }
}
