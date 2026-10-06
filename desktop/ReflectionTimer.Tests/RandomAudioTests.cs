using System.Text.Json;
using System.Collections.Immutable;
using ReflectionTimer.Accessible;
using ReflectionTimer.Core;
using ReflectionTimer.Desktop;

static class RandomAudioTests
{
    internal static async Task Run(Action<bool,string> check)
    {
        var tracks=SoundLibrary.Tracks.ToArray();
        check((int)LibrarySound.TrainerBattle==6&&(int)LibrarySound.ChampionBattle==7&&(int)LibrarySound.None==9,"Adding Random and new songs preserves every previous saved track ID");
        check((int)LibrarySound.RegiBattle==16&&(int)LibrarySound.RgbyFinalRival==17&&(int)LibrarySound.FrlgDeoxys==33,
            "The expanded music library appends new IDs without changing previously saved tracks");
        check(tracks.Length==31&&tracks.Count(RandomAudio.IsSong)==25,"The library distinguishes 25 songs from six notification sounds");
        foreach(var kind in Enum.GetValues<SoundEvent>()) {
            var songs=kind is SoundEvent.LowTime or SoundEvent.TimeReached or SoundEvent.FocusLost;
            var pool=tracks.Where(t=>RandomAudio.For(kind,t,null).Enabled).ToArray();
            check(pool.All(t=>RandomAudio.IsSong(t)==songs)&&pool.Length==(songs?25:6),"Random defaults exclude the other audio category: "+kind);
            check(pool.Select((track,index)=>RandomAudio.Select(kind,null,tracks,(index+.5)/pool.Length)==track).All(x=>x),"Every default eligible track has exactly the same chance: "+kind);
        }
        var weighted=tracks.Select(t=>new RandomTrackWeight(t,t is LibrarySound.TrainerBattle or LibrarySound.ChampionBattle,t==LibrarySound.ChampionBattle?3:1)).ToImmutableList();
        check(RandomAudio.Select(SoundEvent.LowTime,weighted,tracks,0)==LibrarySound.TrainerBattle&&RandomAudio.Select(SoundEvent.LowTime,weighted,tracks,.249999)==LibrarySound.TrainerBattle
            &&RandomAudio.Select(SoundEvent.LowTime,weighted,tracks,.25)==LibrarySound.ChampionBattle&&RandomAudio.Select(SoundEvent.LowTime,weighted,tracks,.999999)==LibrarySound.ChampionBattle,"Weights one and three produce exact 25/75 percent selection boundaries");
        check(RandomAudio.Select(SoundEvent.LowTime,weighted,[LibrarySound.ChampionBattle],0)==LibrarySound.ChampionBattle,"Unavailable files are excluded and remaining probabilities are normalized");
        check(RandomAudio.Select(SoundEvent.LowTime,weighted,[LibrarySound.TrainerBattle,LibrarySound.TrainerBattle,LibrarySound.ChampionBattle],.25)==LibrarySound.ChampionBattle,"Repeated available entries cannot increase a track's probability");
        var empty=tracks.Select(t=>new RandomTrackWeight(t,false)).ToImmutableList();
        check(RandomAudio.Select(SoundEvent.LowTime,empty,tracks,.5)==LibrarySound.None&&SoundLibrary.Resolve(SoundEvent.LowTime,new(){Track=LibrarySound.Random,RandomTracks=empty}) is null,"An entirely unchecked Random pool stays silent without playing an excluded fallback");
        var zero=tracks.Select(t=>new RandomTrackWeight(t,true,0)).ToImmutableList();
        check(RandomAudio.Select(SoundEvent.Success,zero,tracks,.5)==LibrarySound.None,"Zero chance weights are silent even if their checkboxes are checked");
        foreach(var bad in new ImmutableList<RandomTrackWeight>[]{[new(LibrarySound.Random)],[new(LibrarySound.None)],[new(LibrarySound.Default)],[new((LibrarySound)999)],
            [new(LibrarySound.LevelUp,true,-1)],[new(LibrarySound.LevelUp,true,1001)],[new(LibrarySound.LevelUp),new(LibrarySound.LevelUp)],[null!]}){
            var rejected=false;try{AudioSettings.Validate(new SoundSetting{RandomTracks=bad});}catch(ArgumentException){rejected=true;}
            check(rejected,"Invalid, duplicate or out-of-range Random entries are rejected before saving");
        }
        var store=new MemoryStore();var engine=new TimerEngine(store);engine.SetSound(SoundEvent.FocusLost,new(){Track=LibrarySound.Random,RandomTracks=weighted,Volume=37,FadeOutEnabled=true,FadeOutAfterSeconds=9});
        var reopened=new TimerEngine(store);var saved=AudioSettings.From(reopened.Snapshot).FocusLost;
        check(saved.Track==LibrarySound.Random&&saved.RandomTracks!.SequenceEqual(weighted)&&saved.Volume==37&&saved.FadeOutAfterSeconds==9,"Random choices, weights, volume and fade preferences survive profile reload");
        var low=PreviewSession.ReadLow(JsonSerializer.SerializeToElement(new{enabled=true,inherit=false,threshold=12,track=(int)LibrarySound.Random,randomTracks=weighted},PreviewSession.Json),new());
        foreach(var track in tracks.Where(t=>(int)t>=17)) {
            var selected=PreviewSession.ReadLow(JsonSerializer.SerializeToElement(new{enabled=true,inherit=false,threshold=12,track=(int)track},PreviewSession.Json),new());
            check(selected.Track==track,"Timer and Scheduler inputs accept the expanded library track: "+track);
            foreach(var kind in Enum.GetValues<SoundEvent>())engine.SetSound(kind,new(){Track=track});
            check(Enum.GetValues<SoundEvent>().All(kind=>AudioSettings.From(new TimerEngine(store).Snapshot).For(kind).Track==track),
                "All audio events retain the selected new recording after reopening: "+track);
        }
        var id=engine.SaveSchedule(null,DateTimeOffset.Now.AddHours(1),120,false,50,lowTime:low);
        check(engine.Snapshot.Schedules.Single(s=>s.Id==id).LowTime.RandomTracks!.SequenceEqual(weighted),"A scheduled session keeps its own Random probabilities");
        var inherited=new AudioSettings{LowTime=new(){Track=LibrarySound.Random,RandomTracks=empty}};
        check(ReferenceEquals(inherited.ForLowTime(new()).RandomTracks,empty)&&inherited.ForLowTime(low).RandomTracks!.SequenceEqual(weighted),"Use Audio settings inherits its pool; explicit Timer/Scheduler Random keeps separate choices");
        var one=tracks.Select(t=>new RandomTrackWeight(t,t==LibrarySound.RegiBattle)).ToImmutableList();
        foreach(var kind in Enum.GetValues<SoundEvent>())check(SoundLibrary.Resolve(kind,new(){Track=LibrarySound.Random,RandomTracks=one})==Path.Combine(AppContext.BaseDirectory,SoundLibrary.FileName(LibrarySound.RegiBattle)),"Any event can explicitly include a song otherwise unchecked by default: "+kind);
        var backend=new RecordingAudio();var directory=Path.Combine(Path.GetTempPath(),"ReflectionTimer-RandomAudio-"+Guid.NewGuid().ToString("N"));
        using(var services=new PreviewServices(engine,directory,audio:backend,speech:new SilentSpeech())) {
            var settings=JsonSerializer.SerializeToElement(services.Settings(),PreviewSession.Json);
            check(settings.GetProperty("tracks")[0].GetProperty("id").GetInt32()==(int)LibrarySound.Random,"The desktop settings bridge puts Random first");
            await services.Play(SoundEvent.LowTime,true,new(){Track=LibrarySound.Random,RandomTracks=one,Volume=42,FadeOutEnabled=true,FadeOutAfterSeconds=7},announcePreview:false);
            check(backend.Path==Path.Combine(AppContext.BaseDirectory,SoundLibrary.FileName(LibrarySound.RegiBattle))&&backend.Level is{SoundVolume:42,FadeOutAfterSeconds:7},"Random preview resolves the chosen MP3 and retains its volume and fading effects");
        }
        if(Directory.Exists(directory)) {
            foreach(var file in new[]{"diagnostics.dat","diagnostics.dat.bak"})File.Delete(Path.Combine(directory,file));
            Directory.Delete(directory);
        }
    }
    private sealed class RecordingAudio:IAlertAudioBackend
    {
        public string? Path;public AudioLevel? Level;
        public Task PlayAsync(string path,AudioLevel level,CancellationToken token){token.ThrowIfCancellationRequested();Path=path;Level=level;return Task.CompletedTask;}
    }
}
