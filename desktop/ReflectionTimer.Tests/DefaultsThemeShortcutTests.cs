using System.Text.Json;
using ReflectionTimer.Accessible;
using ReflectionTimer.Core;
using ReflectionTimer.Desktop;

static class DefaultsThemeShortcutTests
{
    internal static void Run(Action<bool,string> check)
    {
        var store=new MemoryStore{State=AppState.CreateDefault()};
        var session=new PreviewSession(store);
        var state=session.Engine.Snapshot;
        check(state.Timer.DurationSeconds==900&&state.Timer.RemainingSeconds==900&&!state.Timer.IsRunning&&!state.Timer.AutoRestart&&state.Timer.AutoRestartUntil is null,"New profile starts ready at 15 minutes with repeat and cutoff off");
        check(state.ShowFloatingTimer&&state.FloatingPlacement==FloatingTimerPlacement.BottomLeft&&state.PopupPosition==ReflectionPopupPosition.BottomRight,"New profile enables Compact at bottom left and reflections at bottom right");
        check(state.Timer.LowTime.Enabled&&state.Timer.LowTime.ThresholdSeconds is null&&AudioSettings.From(state).LowTimeThresholdSeconds==15,"New profile enables the inherited 15-second low-time warning");
        check(state.Timer.Volume==50&&state.Theme==AppColorTheme.Dark&&state.LoggingEnabled&&!state.StartAtLogin,"New profile keeps the original volume, Dark theme, diagnostics, and startup defaults");
        check(state.ScheduleOverlap==ScheduleOverlapPolicy.EndWithReflection&&state.Schedules.Count==0&&state.Outbox.Count==0&&state.Prompts.Count==0,"New profile has the original overlap policy and no demonstration entries");
        foreach(var (kind,file) in new[]{(SoundEvent.SessionEnd,"popup.mp3"),(SoundEvent.Success,"pokemon-level-up.mp3"),(SoundEvent.Failure,"kirby-out-of-health.mp3"),(SoundEvent.LowTime,"pokemon-battle-trainer.mp3")}){
            var sound=AudioSettings.From(state).For(kind);
            var path=SoundLibrary.Resolve(kind,sound)!;
            var behavior=kind==SoundEvent.SessionEnd?SoundBehavior.Assertive:kind==SoundEvent.LowTime?SoundBehavior.Polite:SoundBehavior.Disruptive;
            check(Path.GetFileName(path)==file&&File.Exists(path)&&sound.Behavior==behavior&&sound.Volume==100&&!sound.FadeOutEnabled&&sound.FadeOutAfterSeconds==10,"Bundled MP3 and requested new-user playback defaults for "+kind);
        }
        var newProfile=new EncryptedStore(Path.Combine(Path.GetTempPath(),"ReflectionTimer-Uncreated-"+Guid.NewGuid().ToString("N"))).Load();
        check(AudioSettings.From(newProfile).SessionEnd.Behavior==SoundBehavior.Assertive&&AudioSettings.From(newProfile).LowTime.Behavior==SoundBehavior.Polite,"A new encrypted profile receives the requested audio behaviors");
        var legacy=JsonSerializer.Deserialize<AppState>("{}",DataJson.Options)!;
        check(Enum.GetValues<SoundEvent>().All(kind=>AudioSettings.From(legacy).For(kind).Behavior==SoundBehavior.Disruptive),"Existing profiles without audio settings retain their legacy playback behaviors");
        var chosen=legacy with{Audio=new(){SessionEnd=new(){Behavior=SoundBehavior.Polite},LowTime=new(){Behavior=SoundBehavior.Assertive}}};
        var roundTrip=DataJson.Clone(chosen);
        check(AudioSettings.From(roundTrip).SessionEnd.Behavior==SoundBehavior.Polite&&AudioSettings.From(roundTrip).LowTime.Behavior==SoundBehavior.Assertive,"Existing explicit audio choices are never replaced by new-user defaults");
        foreach(var track in SoundLibrary.Tracks){
            var path=Path.Combine(AppContext.BaseDirectory,SoundLibrary.FileName(track));
            check(Mp3AudioBackend.ValidateCustomFile(path)==path,"Packaged audio decodes: "+SoundLibrary.FileName(track));
        }
        check(SoundLibrary.Tracks.Count()==8,"All eight MP3 library tracks ship with the preview");
        foreach(var theme in Enum.GetValues<AppColorTheme>()){
            session.Engine.SetTheme(theme);
            check(new PreviewSession(store).Engine.Snapshot.Theme==theme,"Theme preference survives restart: "+theme);
            var palette=PreviewTheme.Palette(theme);
            check(palette.Text!=palette.Background&&palette.SelectionText!=palette.Selection&&palette.Accent!=palette.Raised,"Native frame and menu palette has distinct text/selection colors: "+theme);
        }
        var contrast=PreviewTheme.Palette(AppColorTheme.Glamour,true);
        check(contrast.Background==SystemColors.Control&&contrast.Selection==SystemColors.Highlight,"Windows contrast overrides decorative native colors");
        session.Engine.SetFloatingTimer(false);session.Engine.SetAppVolume(31);
        session.Engine.SetSound(SoundEvent.Success,new(){Track=LibrarySound.PokemonHealed,Volume=42});
        var retained=new PreviewSession(store).Engine.Snapshot;
        check(!retained.ShowFloatingTimer&&retained.Timer.Volume==31&&AudioSettings.From(retained).Success.Track==LibrarySound.PokemonHealed&&AudioSettings.From(retained).Success.Volume==42,"Reopening preserves explicit saved preferences instead of resetting defaults");

        var background=new ReflectionTarget();var foreground=new ReflectionTarget{IsForegroundReflection=true};var checkIns=0;
        ReflectionShortcut.Invoke([background,foreground],()=>checkIns++);
        check(foreground.Saves==1&&background.Saves==0&&checkIns==0,"Comma saves only the foreground reflection without creating another check-in");
        foreground.IsForegroundReflection=false;
        ReflectionShortcut.Invoke([background,foreground],()=>checkIns++);
        check(foreground.Saves==1&&background.Saves==0&&foreground.FocusCalls==1&&checkIns==0,"Comma focuses an existing background reflection instead of saving it or opening App");
        ReflectionShortcut.Invoke([],()=>checkIns++);
        check(checkIns==1,"Comma checks pending reflections or check-ins when no reflection window is open");
        foreground.IsForegroundReflection=true;foreground.SaveFailure=true;
        try{ReflectionShortcut.Invoke([foreground],()=>checkIns++);throw new Exception("Save failure ignored");}catch(IOException){}
        check(checkIns==1,"A failed foreground save never falls back to creating a check-in");
        var shortcutSession=new PreviewSession(new MemoryStore());
        check(shortcutSession.ReflectionForShortcut() is null&&shortcutSession.Engine.Snapshot.Prompts.Count==0,"Idle comma with no pending draft has no target and does not start a timer");
        shortcutSession.Engine.Start(900,false,0);
        var timerBefore=shortcutSession.Engine.Snapshot.Timer;
        var checkIn=shortcutSession.ReflectionForShortcut();
        check(checkIn.HasValue&&shortcutSession.Engine.Snapshot.Timer==timerBefore&&shortcutSession.Engine.Snapshot.Prompts.Single().IsCheckIn,"Comma without pending drafts opens a running-session check-in without altering the countdown");
        shortcutSession.Engine.SaveDraft(checkIn!.Value,"Retained local draft");
        shortcutSession.Engine.EndEarly();
        var pendingBefore=JsonSerializer.Serialize(shortcutSession.Engine.Snapshot);
        check(shortcutSession.ReflectionForShortcut()==shortcutSession.Engine.Snapshot.Prompts.Last().Id&&JsonSerializer.Serialize(shortcutSession.Engine.Snapshot)==pendingBefore,"Idle comma reopens the latest session-end draft without creating a prompt or changing saved text");

        Exception? failure=null;
        var thread=new Thread(()=>{
            try {
                var backend=new Registration();var calls=new int[7];
                backend.Blocked.Add(GlobalShortcut.CompactId);
                backend.Blocked.Add(GlobalShortcut.TimerToggleId);
                backend.Blocked.Add(GlobalShortcut.TimerToggleAltId);
                using var keys=new PreviewShortcuts(Enumerable.Range(0,7).Select(i=>(Action)(()=>calls[i]++)).ToArray(),backend:backend);
                check(backend.Requests.Take(5).Select(r=>r.Key).SequenceEqual(new uint[]{0x54,0xC0,0xBC,0xBE,0xBF})&&backend.Requests.Take(5).All(r=>r.Modifiers==(0x0002|0x0001|0x4000)),"All five global chords use the swapped comma/slash mappings with Ctrl+Alt and no key-repeat");
                check(backend.Requests.Count==7&&backend.Requests[5]==(0x20u,0x4002u),"Ctrl+Space keeps its exact registration and suppresses held-key repeats");
                check(backend.Requests[6]==(0x20u,0x4003u),"The alias registers Ctrl+Alt+Space with held-key repeat suppressed");
                var states=JsonSerializer.SerializeToElement(keys.Status,PreviewSession.Json);
                check(states.EnumerateArray().Select(s=>s.GetProperty("available").GetBoolean()).SequenceEqual(new[]{true,true,false,true,true,false,false}),"Compact and both timer toggle conflicts report their own unavailable status");
                foreach(var chord in PreviewShortcuts.Chords)keys.Dispatch(GlobalShortcut.HotKeyMessage,chord.Id);
                check(calls.SequenceEqual(new[]{1,1,0,1,1,0,0}),"Registered shortcut messages invoke exactly their matching action");
                var attempts=backend.Requests.Count;keys.RetryUnavailable();
                check(backend.Requests.Count==attempts+3&&!keys.Dispatch(GlobalShortcut.HotKeyMessage,GlobalShortcut.TimerToggleId)&&!keys.Dispatch(GlobalShortcut.HotKeyMessage,GlobalShortcut.TimerToggleAltId),"Only unavailable chords retry and blocked toggle shortcuts cannot change the timer");
                backend.Blocked.Remove(GlobalShortcut.TimerToggleId);
                check(keys.RetryUnavailable()&&keys.Dispatch(GlobalShortcut.HotKeyMessage,GlobalShortcut.TimerToggleId)&&calls[5]==1&&calls[1]==1,"Ctrl+Space recovers independently and invokes only its timer toggle action");
                backend.Blocked.Remove(GlobalShortcut.TimerToggleAltId);
                check(keys.RetryUnavailable()&&keys.Dispatch(GlobalShortcut.HotKeyMessage,GlobalShortcut.TimerToggleAltId)&&calls[6]==1&&calls[5]==1,"Ctrl+Alt+Space recovers independently and dispatches once without invoking Ctrl+Space again");
                backend.Blocked.Clear();check(keys.RetryUnavailable(),"Releasing a competing shortcut recovers without restarting");
                keys.Dispatch(GlobalShortcut.HotKeyMessage,GlobalShortcut.CompactId);
                check(calls[2]==1&&!keys.Dispatch(0,GlobalShortcut.HotKeyId)&&!keys.Dispatch(GlobalShortcut.HotKeyMessage,-1),"Recovered shortcut works and unrelated messages are ignored");
                keys.Dispose();check(backend.Removed.Count==7&&!keys.Dispatch(GlobalShortcut.HotKeyMessage,GlobalShortcut.TimerToggleId)&&!keys.Dispatch(GlobalShortcut.HotKeyMessage,GlobalShortcut.TimerToggleAltId),"Exit releases all seven owned shortcuts and stops both toggle shortcuts");
                var clock=new Clock();var pairs=new ConsecutiveShortcutPresses(clock);
                var first=pairs.Press();clock.Ticks+=TimeSpan.FromMilliseconds(799).Ticks;
                check(!first&&pairs.Press()&&!pairs.Press(),"Period double press selects App once and consumes the pair");
                clock.Ticks+=TimeSpan.FromMilliseconds(801).Ticks;check(!pairs.Press(),"A slow period press starts a new Compact action");
                pairs.Reset();check(!pairs.Press(),"Another shortcut clears the pending period pair");
            }catch(Exception error){failure=error;}
        });
        thread.SetApartmentState(ApartmentState.STA);thread.Start();thread.Join();
        if(failure is not null)throw failure;
    }
    private sealed class Registration : IHotKeyRegistration
    {
        public HashSet<int> Blocked=[];public List<(uint Key,uint Modifiers)> Requests=[];public List<int> Removed=[];
        public bool Register(nint window,int id,uint modifiers,uint key){Requests.Add((key,modifiers));return !Blocked.Contains(id);}
        public bool Unregister(nint window,int id){Removed.Add(id);return true;}
    }
    private sealed class ReflectionTarget : IReflectionShortcutTarget
    {
        public bool IsForegroundReflection { get; set; }
        public int Saves,FocusCalls;public bool SaveFailure;
        public void FocusOrSaveDraft(){if(SaveFailure)throw new IOException("Cannot save draft");Saves++;}
        public void FocusReflection()=>FocusCalls++;
    }
    private sealed class Clock : TimeProvider
    {
        public long Ticks;
        public override long GetTimestamp()=>Ticks;
        public override long TimestampFrequency=>TimeSpan.TicksPerSecond;
    }
}
