using System.Net;
using System.Text.Json;
using System.Threading.Channels;
using NAudio.Wave;
using ReflectionTimer.Accessible;
using ReflectionTimer.Core;
using ReflectionTimer.Desktop;

static class MessageSentFadeTests
{
    internal static async Task Run(Action<bool,string> check)
    {
        var legacy=JsonSerializer.Deserialize<SoundSetting>("{}")!;
        check(!legacy.FadeOutAfterMessageSent&&legacy.MessageSentFadeSeconds==3,"Existing audio settings default to message-sent fading off with a three-second duration");
        var memory=new MemoryStore();var engine=new TimerEngine(memory);
        engine.SetSound(SoundEvent.LowTime,new(){FadeOutAfterMessageSent=true,MessageSentFadeSeconds=7});
        check(AudioSettings.From(new TimerEngine(memory).Snapshot).LowTime is {FadeOutAfterMessageSent:true,MessageSentFadeSeconds:7},"Message-sent fade preference and duration survive a saved-state reload");
        foreach(var seconds in new[]{0,-1,TimerEngine.MaxDuration+1}){
            try{engine.SetSound(SoundEvent.LowTime,new(){MessageSentFadeSeconds=seconds});throw new Exception("Invalid fade accepted");}catch(ArgumentException){}
        }
        check(AudioSettings.From(engine.Snapshot).LowTime.MessageSentFadeSeconds==7,"Invalid message-sent fade durations cannot replace saved audio settings");
        Envelope(check);
        await Scope(check);
        await NonDelivery(check);
        foreach(var ending in new[]{"check-in","natural","early","scheduled","end-and-send"})await Delivery(check,ending);
    }
    private static void Envelope(Action<bool,string> check)
    {
        var level=new AudioLevel(80);var provider=new LiveGainProvider(new Ones(),level);
        var before=new float[2000];check(provider.Read(before,0,before.Length)==2000&&before.All(x=>x==.8f),"Playing audio stays at its normal level before a message-sent fade request");
        level.RequestFadeOut(3);var first=new float[3000];provider.Read(first,0,first.Length);
        check(first[0]==.8f&&first[1]==.8f&&Math.Abs(first[2000]-.8f*2/3)<.001f,"Requested fade begins at the current frame and scales both stereo channels equally");
        level.RequestFadeOut(9);var last=new float[4000];var count=provider.Read(last,0,last.Length);
        check(count==3000&&Math.Abs(last[0]-.4f)<.001f&&last[count-1]<.001f&&provider.Read(last,0,last.Length)==0,"Fade reaches silence and ends after exactly the selected three seconds; repeated requests cannot extend it");
        level=new AudioLevel(100,2);provider=new LiveGainProvider(new Ones(),level);
        provider.Read(new float[5000],0,5000);level.RequestFadeOut(5);
        var tail=new float[10000];count=provider.Read(tail,0,tail.Length);
        check(count==1000&&Math.Abs(tail[0]-.5f)<.001f&&provider.Read(tail,0,1)==0,"An existing timed fade can finish sooner; message-sent fading never restores volume or extends playback");
        level=new AudioLevel(80,soundVolume:50);level.Duck(true);level.RequestFadeOut(2);provider=new LiveGainProvider(new Ones(),level);
        var sample=new float[2000];provider.Read(sample,0,sample.Length);
        check(Math.Abs(sample[0]-.1f)<.001f,"Requested fading preserves App sound, per-track volume, and Assertive ducking");
        level.SetVolumes(40,50);level.Duck(false);provider.Read(sample,0,sample.Length);
        check(Math.Abs(sample[0]-.1f)<.001f&&provider.Read(sample,0,1)==0,"Volume and ducking changes cannot remove an active fade or restart the sound");
    }
    private static async Task Scope(Action<bool,string> check)
    {
        var backend=new HoldingAudio();using var player=new AlertSoundPlayer(backend);
        var firstId=Guid.NewGuid();var nextId=Guid.NewGuid();
        _=player.PlayAsync("first",80,SoundBehavior.Polite,SoundEvent.LowTime,sessionId:firstId);var first=await backend.Next();
        _=player.PlayAsync("next",80,SoundBehavior.Polite,SoundEvent.LowTime,sessionId:nextId);var next=await backend.Next();
        _=player.PlayAsync("preview",80,SoundBehavior.Polite,SoundEvent.LowTime,preview:true,sessionId:firstId);var preview=await backend.Next();
        _=player.PlayAsync("end",80,SoundBehavior.Assertive,SoundEvent.SessionEnd,sessionId:firstId);var end=await backend.Next();
        try{
            player.FadeOut(SoundEvent.LowTime,firstId,4);
            check(first.Level.RequestedFadeSeconds==4&&!first.Token.IsCancellationRequested&&first.Level.Gain==.2f,"A session-scoped fade requests gradual attenuation instead of abrupt cancellation");
            check(next.Level.RequestedFadeSeconds==0&&preview.Level.RequestedFadeSeconds==0&&end.Level.RequestedFadeSeconds==0,"Sending an older reflection cannot fade the next session, a preview, or session-end audio");
            player.FadeOut(SoundEvent.LowTime,firstId,8);
            check(first.Level.RequestedFadeSeconds==4,"Repeated send requests cannot restart or lengthen a fade");
            _=player.PlayAsync("success",80,SoundBehavior.Disruptive,SoundEvent.Success);await backend.Next();
            check(first.Token.IsCancellationRequested&&next.Token.IsCancellationRequested,"Disruptive audio retains its explicit immediate-stop behavior during a fade");
        }finally{player.Dispose();await backend.FinishAll();}
    }
    private static async Task Delivery(Action<bool,string> check,string ending)
    {
        var directory=Path.Combine(Path.GetTempPath(),"ReflectionTimer-SentFade-"+Guid.NewGuid().ToString("N"));
        var now=DateTimeOffset.Now;var memory=new MemoryStore{State=new(){LoggingEnabled=false,ExtensionDisabledConfirmed=true,
            Connection=new(){SheetUrl="https://docs.google.com/spreadsheets/d/abcdefghijklmnopqrstuvwxyz/edit",WebAppUrl="https://script.google.com/macros/s/syntheticReceiver/exec",ApiToken=new string('a',64)},
            Audio=new(){LowTime=new(){Behavior=SoundBehavior.Polite,FadeOutAfterMessageSent=true,MessageSentFadeSeconds=4},SessionEnd=new(){Track=LibrarySound.None},Success=new(){Track=LibrarySound.None},Failure=new(){Track=LibrarySound.None}}}};
        var engine=new TimerEngine(memory,()=>now);var backend=new HoldingAudio();var receiver=new Receiver();
        using var services=new PreviewServices(engine,directory,new SheetsClient(receiver),backend);
        try{
            engine.Start(20,ending=="natural",80,lowTime:new(){ThresholdSeconds=10});var sessionId=engine.Snapshot.Timer.SessionId;
            var prompt=engine.CheckIn();engine.SaveReflectionForLater(prompt,"Saved locally");
            if(ending=="scheduled")engine.SaveSchedule(null,now.AddSeconds(18),20,false,80,lowTime:new(){ThresholdSeconds=20});
            now=now.AddSeconds(10);engine.Advance();var low=await backend.Next();
            if(ending=="natural"){now=now.AddSeconds(10);engine.Advance();}
            else if(ending=="early")engine.EndEarly();
            else if(ending=="scheduled"){now=now.AddSeconds(8);engine.Advance();}
            check(engine.Snapshot.Prompts.Single(p=>p.Id==prompt).SessionId==sessionId,"Reflection retains its audio session identity through "+ending);
            engine.SaveReflectionForLater(prompt,"Ready to send");
            check(low.Level.RequestedFadeSeconds==0,"Saving a "+ending+" draft does not fade low-time audio");
            Playback? newer=null;
            if(ending is "natural" or "scheduled"){
                now=now.AddSeconds(10);engine.Advance();newer=await backend.Next();
            }
            var before=JsonSerializer.Serialize(engine.Snapshot);
            services.ReflectionSendStarted(prompt);
            check(low.Level.RequestedFadeSeconds==4&&!low.Token.IsCancellationRequested&&JsonSerializer.Serialize(engine.Snapshot)==before,
                "Send immediately starts the configured fade before validation, storage, or delivery, without changing state: "+ending);
            check(newer is null||newer.Level.RequestedFadeSeconds==0,"Sending an older response leaves the next session's warning unchanged: "+ending);
            try{engine.QueueReflection(prompt,"",endSession:ending=="end-and-send");throw new Exception("Invalid response accepted");}catch(ArgumentException){}
            memory.Fail=true;
            try{engine.SaveDraft(prompt,"Uncommitted final keystrokes");throw new Exception("Failed save accepted");}catch(IOException){}
            memory.Fail=false;
            check(JsonSerializer.Serialize(engine.Snapshot)==before&&low.Level.RequestedFadeSeconds==4,
                "Validation and draft-storage failures preserve the response and leave the already-started fade active: "+ending);
            services.ReflectionSendStarted(prompt);
            engine.QueueReflection(prompt,"A synthetic reflection",endSession:ending=="end-and-send");
            check(engine.Snapshot.Outbox.Single().SessionId==sessionId&&low.Level.RequestedFadeSeconds==4,"Retry and queue retain the original fade and audio session identity: "+ending);
            receiver.Fail=true;await services.Sync();
            check(engine.Snapshot.Outbox.Single().Status==DeliveryStatus.NeedsReview&&low.Level.RequestedFadeSeconds==4,"Failed "+ending+" delivery leaves the requested fade untouched");
            engine.RetryUpload(prompt);receiver.Fail=false;receiver.OnAppend=()=>memory.Fail=true;
            await services.Sync();memory.Fail=false;receiver.OnAppend=null;
            check(engine.Snapshot.Outbox.Single().Status==DeliveryStatus.Sending&&low.Level.RequestedFadeSeconds==4,"A failed delivery-state save cannot restart the "+ending+" fade");
            // Resolve the synthetic interrupted upload as a failure before its retry.
            engine.FinishUpload(prompt,false,"test_failure");engine.RetryUpload(prompt);await services.Sync();
            check(engine.Snapshot.Outbox.Single().Status==DeliveryStatus.Sent&&low.Level.RequestedFadeSeconds==4&&!low.Token.IsCancellationRequested,"Confirmed "+ending+" delivery leaves the original fade unchanged");
            check(newer is null||newer.Level.RequestedFadeSeconds==0,"Delayed "+ending+" delivery leaves newer low-time audio playing");
            check(!receiver.LastBody.Contains("sessionId",StringComparison.OrdinalIgnoreCase),"Local audio session identifiers are not added to the Sheets protocol: "+ending);
            var settings=JsonSerializer.SerializeToElement(services.Settings(),PreviewSession.Json).GetProperty("sounds").EnumerateArray().Single(x=>x.GetProperty("kind").GetInt32()==3);
            check(settings.GetProperty("fadeOutAfterMessageSent").GetBoolean()&&settings.GetProperty("messageSentFadeSeconds").GetInt32()==4,"Settings expose both message-sent fade values: "+ending);
        }finally{services.Dispose();await backend.FinishAll();CleanDiagnostics(directory);}
    }
    private static async Task NonDelivery(Action<bool,string> check)
    {
        foreach(var mode in new[]{"disabled","local simulation","practice","manual confirmation","unknown session","different session","missing prompt","skip","background delivery","automatic send"}){
            var directory=Path.Combine(Path.GetTempPath(),"ReflectionTimer-NoSentFade-"+Guid.NewGuid().ToString("N"));
            var now=DateTimeOffset.Now;var id=Guid.NewGuid();var itemId=Guid.NewGuid();
            var engine=new TimerEngine(new MemoryStore{State=new(){LoggingEnabled=false,
                Timer=new(){SessionId=id,IsRunning=true,DurationSeconds=20,RemainingSeconds=20,EndTime=now.AddSeconds(10).ToUnixTimeMilliseconds(),LowTime=new(){ThresholdSeconds=10}},
                Audio=new(){LowTime=new(){Behavior=SoundBehavior.Polite,FadeOutAfterMessageSent=mode!="disabled"},Success=new(){Track=LibrarySound.None},SessionEnd=new(){Track=LibrarySound.None}},
                Outbox=mode=="automatic send"?[]:[new(){Id=itemId,SessionId=id,LocalOnly=mode=="local simulation",IsTest=mode=="practice",Status=DeliveryStatus.NeedsReview}],
                Prompts=[new(itemId,now.ToUnixTimeMilliseconds(),20,80,mode=="practice"){
                    SessionId=mode=="unknown session"?null:mode=="different session"?Guid.NewGuid():id}]
            }},()=>now);
            var backend=new HoldingAudio();using var services=new PreviewServices(engine,directory,audio:backend);
            try{
                engine.Advance();var low=await backend.Next();
                if(mode=="manual confirmation")engine.MarkAlreadySent(itemId);
                else if(mode=="skip")engine.SkipPrompt(itemId);
                else if(mode=="automatic send")engine.AutoSendReflection(itemId);
                else if(mode is "local simulation" or "background delivery")engine.FinishUpload(itemId,true);
                else services.ReflectionSendStarted(mode=="missing prompt"?Guid.NewGuid():itemId);
                check(low.Level.RequestedFadeSeconds==0&&!low.Token.IsCancellationRequested,mode+" does not start the low-time message-sent fade");
            }finally{services.Dispose();await backend.FinishAll();CleanDiagnostics(directory);}
        }
    }
    private static void CleanDiagnostics(string directory)
    {
        if(!Directory.Exists(directory))return;
        foreach(var name in new[]{"diagnostics.dat","diagnostics.dat.bak"})File.Delete(Path.Combine(directory,name));
        Directory.Delete(directory);
    }
    private sealed class Ones:ISampleProvider
    {
        public WaveFormat WaveFormat{get;}=WaveFormat.CreateIeeeFloatWaveFormat(1000,2);
        public int Read(float[] buffer,int offset,int count){Array.Fill(buffer,1f,offset,count);return count;}
        public int Read(Span<float> buffer){buffer.Fill(1);return buffer.Length;}
    }
    private sealed record Playback(AudioLevel Level,CancellationToken Token)
    {
        public TaskCompletionSource Complete=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Done=new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private sealed class HoldingAudio:IAlertAudioBackend
    {
        private readonly Channel<Playback> started=Channel.CreateUnbounded<Playback>();private readonly List<Playback> all=[];
        public async Task PlayAsync(string path,AudioLevel level,CancellationToken cancellationToken)
        {
            var playback=new Playback(level,cancellationToken);lock(all)all.Add(playback);started.Writer.TryWrite(playback);
            try{await playback.Complete.Task.WaitAsync(cancellationToken);}finally{playback.Done.TrySetResult();}
        }
        public async Task<Playback> Next()=>await started.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(4));
        public async Task FinishAll(){Playback[] voices;lock(all)voices=all.ToArray();foreach(var voice in voices)voice.Complete.TrySetResult();await Task.WhenAll(voices.Select(v=>v.Done.Task)).WaitAsync(TimeSpan.FromSeconds(4));}
    }
    private sealed class Receiver:HttpMessageHandler
    {
        public bool Fail;public Action? OnAppend;public string LastBody="";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {
            LastBody=await request.Content!.ReadAsStringAsync(token);using var data=JsonDocument.Parse(LastBody);
            var append=data.RootElement.GetProperty("action").GetString()=="appendReflection";if(append)OnAppend?.Invoke();
            return new(HttpStatusCode.OK){Content=new StringContent(append&&Fail?"{\"success\":false,\"code\":\"test_failure\"}":JsonSerializer.Serialize(new{success=true,target="Synthetic destination",sheet="QA only",deliveryProtocol=SheetsClient.DeliveryProtocol,supportsCheckIns=true,supportsAutoSent=true}))};
        }
    }
}
