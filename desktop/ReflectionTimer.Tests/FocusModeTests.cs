using System.Text.Json;
using System.Collections.Immutable;
using System.Threading.Channels;
using ReflectionTimer.Accessible;
using ReflectionTimer.Core;
using ReflectionTimer.Desktop;

internal static class FocusModeTests
{
    internal static async Task Run(Action<bool,string> check)
    {
        FocusWindowListingTests.Run(check);
        SavedFocusWindowTests.Run(check);
        BackgroundFocusTests.Run(check);
        FocusVisualTests.Run(check);
        FocusTargetToggleTests.Run(check);
        var target=new FocusTarget(Guid.NewGuid(),FocusTargetKind.Window,"Synthetic focus target","synthetic",123,456,789);
        check(PreviewApplication.MatchesSavedFocusTarget(target with{Id=Guid.NewGuid(),Name="Renamed window"},target),"Reopening the picker retains the saved window despite a new list ID or title");
        var tab=target with{Kind=FocusTargetKind.BrowserTab,TabRuntimeId="tab-one"};
        check(PreviewApplication.MatchesSavedFocusTarget(tab with{Id=Guid.NewGuid(),Name="Renamed tab",TabPosition=3},tab),"Navigation and reordering retain the exact saved browser tab in the picker");
        check(!PreviewApplication.MatchesSavedFocusTarget(tab with{TabRuntimeId="tab-two"},tab),"Another tab with the same title cannot replace the saved target");
        check(!PreviewApplication.MatchesSavedFocusTarget(tab with{WindowHandle=124},tab)&&!PreviewApplication.MatchesSavedFocusTarget(target with{ProcessStartedAt=790},target),"Moved tabs and reused process identities are never preselected as the saved target");
        var group=tab with{Kind=FocusTargetKind.BrowserTabGroup,TabRuntimeId="group-one"};
        check(PreviewApplication.MatchesSavedFocusTarget(group with{Id=Guid.NewGuid(),Name="Renamed group",TabPosition=4},group)&&!PreviewApplication.MatchesSavedFocusTarget(group with{TabRuntimeId="group-two"},group),"Group identity survives renaming and reordering without confusing duplicate names");
        check(JsonSerializer.Deserialize<FocusTarget>(JsonSerializer.Serialize(group))==group,"A selected browser group survives settings serialization");
        Groups(check);
        TabListing(check,target);
        DisplayBridge(check,target);
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
            check(!gate.Evaluate(options with{Target=target with{Id=Guid.NewGuid(),WindowHandle=124}},timer,FocusPresence.Away,24000).Alert,mode+": selecting another target resets the grace period");
            check(!gate.Evaluate(options,timer with{SessionId=Guid.NewGuid()},FocusPresence.Away,30000).Alert,mode+": a new session cannot inherit an old away period");
            check(!gate.Evaluate(options with{Enabled=false},timer,FocusPresence.Away,50000).Alert,mode+": turning focus mode off stops monitoring");
            check(gate.Evaluate(options with{DelaySeconds=0},timer,FocusPresence.Away,50001).Alert,mode+": zero delay alerts on the first away sample");
        }
        await Monitor(check,target);
        await DynamicFocusTests.Run(check,target);
        await MultipleAndIdle(check,target);
        await Audio(check,target);
    }
    private static async Task MultipleAndIdle(Action<bool,string> check,FocusTarget target)
    {
        var defaults=JsonSerializer.Deserialize<FocusModeSettings>("{}")!;
        check(!defaults.MultipleTargets&&!defaults.IdleEnabled&&defaults.IdleSeconds==20,"Multiple Targets and Idle for default unchecked, with 20 seconds retained");
        var legacy=new FocusModeSettings{Target=target,Enabled=true};
        check(JsonSerializer.Deserialize<FocusModeSettings>(JsonSerializer.Serialize(legacy))!.SelectedTargets.Single()==target,"An older single-target profile migrates without losing its saved target");
        var tab=target with{Id=Guid.NewGuid(),Kind=FocusTargetKind.BrowserTab,TabRuntimeId="tab",Name="Tab",TabPosition=2};
        var group=tab with{Id=Guid.NewGuid(),Kind=FocusTargetKind.BrowserTabGroup,TabRuntimeId="group",Name="Group"};
        var options=new FocusModeSettings{Enabled=true,MultipleTargets=true,Targets=[target,tab,group],IdleEnabled=true,IdleSeconds=20,DelaySeconds=5};
        var store=new MemoryStore();var engine=new TimerEngine(store);var audio=engine.Snapshot.Audio;var connection=engine.Snapshot.Connection;
        engine.SetFocusMode(options);var restored=new TimerEngine(store).Snapshot.FocusMode;
        check(restored.SelectedTargets.SequenceEqual(options.Targets)&&restored.Target==target&&restored.IdleSeconds==20&&restored.IdleEnabled&&restored.MultipleTargets,"Mixed window, tab and group choices and idle options survive profile reload");
        var jsonRestored=JsonSerializer.Deserialize<FocusModeSettings>(JsonSerializer.Serialize(restored))!;
        check(jsonRestored.SelectedTargets.SequenceEqual(options.Targets)&&jsonRestored.IdleEnabled&&jsonRestored.IdleSeconds==20&&jsonRestored.MultipleTargets,"The profile JSON round-trip preserves immutable targets and both new options");
        check(engine.Snapshot.Audio==audio&&engine.Snapshot.Connection==connection,"Multiple-target saving leaves audio and Sheets settings intact");
        engine.SetFocusMode(restored with{Enabled=false,DelaySeconds=8});
        check(engine.Snapshot.FocusMode.SelectedTargets.Length==3&&engine.Snapshot.FocusMode.IdleEnabled,"Turning Focus off or editing its delay keeps every target and idle preference");
        engine.SetFocusMode(options with{Targets=[target,target with{Id=Guid.NewGuid()},tab]});
        check(engine.Snapshot.FocusMode.SelectedTargets.Length==2,"Duplicate runtime targets cannot be stored twice under different list IDs");
        var before=JsonSerializer.Serialize(engine.Snapshot.FocusMode);
        foreach(var invalid in new[]{options with{MultipleTargets=false},options with{IdleSeconds=0},options with{IdleSeconds=TimerEngine.MaxDuration+1}}){
            try{engine.SetFocusMode(invalid);throw new Exception("Invalid focus settings accepted");}catch(ArgumentException){}
        }
        check(JsonSerializer.Serialize(engine.Snapshot.FocusMode)==before,"Invalid multiple-target and idle edits are atomic and cannot replace saved choices");
        var many=Enumerable.Range(0,257).Select(i=>target with{Id=Guid.NewGuid(),WindowHandle=i+1000}).ToImmutableArray();
        try{engine.SetFocusMode(options with{Targets=many});throw new Exception("Too many targets accepted");}catch(ArgumentException){}
        check(JsonSerializer.Serialize(engine.Snapshot.FocusMode)==before,"Selections are bounded to 256 targets");
        engine.SetFocusMode(options with{Targets=[],Target=null});
        check(engine.Snapshot.FocusMode.Enabled&&engine.Snapshot.FocusMode.SelectedTargets.IsEmpty,"Idle-only focus mode can run without a window target");
        check(WindowsFocusTargets.InputAge(100,90)==10&&WindowsFocusTargets.InputAge(5,uint.MaxValue-4)==10,"Idle timestamp arithmetic handles the Windows tick counter wrapping");
        check(WindowsFocusTargets.InputAge(100,101) is null,"A future last-input timestamp fails quietly instead of reporting a huge idle duration");
        var source=new FakeSource{ByTarget=new(){[target.Key]=FocusPresence.Away,[tab.Key]=FocusPresence.Focused,[group.Key]=FocusPresence.Unavailable}};
        check(await ((IFocusTargetSource)source).CheckAnyAsync(options.Targets)==FocusPresence.Focused,"Any one selected window, tab or group satisfies multi-target focus");
        source.ByTarget[tab.Key]=FocusPresence.Away;
        check(await ((IFocusTargetSource)source).CheckAnyAsync(options.Targets)==FocusPresence.Away,"Closed targets do not block checking other available selected targets");
        source.ByTarget[tab.Key]=FocusPresence.Unknown;
        check(await ((IFocusTargetSource)source).CheckAnyAsync(options.Targets)==FocusPresence.Unknown,"An unreadable possible focused target suppresses a false away alert");
        source.ByTarget[target.Key]=FocusPresence.Focused;
        check(await ((IFocusTargetSource)source).CheckAnyAsync(options.Targets)==FocusPresence.Focused,"A known focused target wins over unreadable or closed choices");
        foreach(var mode in Enum.GetValues<SessionMode>()){
            var timer=new TimerState{Mode=mode,IsRunning=true,SessionId=Guid.NewGuid()};var gate=new FocusModeGate();
            check(!gate.Evaluate(options,timer,FocusPresence.Focused,0,19999).Alert&&gate.Evaluate(options,timer,FocusPresence.Focused,1,20000).Alert,mode+": inactivity starts audio exactly at the configured threshold even on an allowed target");
            check(!gate.Evaluate(options,timer,FocusPresence.Focused,2,0).Alert,mode+": fresh input stops an idle alert immediately");
            check(!gate.Evaluate(options with{IdleEnabled=false},timer,FocusPresence.Focused,3,999999).Alert,mode+": unchecked Idle for ignores inactivity");
            check(gate.Evaluate(options,timer,FocusPresence.Unknown,4,20000).Alert&&gate.Evaluate(options,timer,FocusPresence.Unavailable,5,20000).Alert,mode+": idle detection works independently of closed or unreadable targets");
            check(!gate.Evaluate(options,timer with{IsRunning=false},FocusPresence.Away,6,20000).Alert&&!gate.Evaluate(options with{Enabled=false},timer,FocusPresence.Away,7,20000).Alert,mode+": pausing or disabling Focus stops both triggers");
            check(!gate.Evaluate(options,timer,FocusPresence.Unknown,8,null).Alert,mode+": unreadable last-input time cannot trigger idle audio");
            gate.Evaluate(options,timer,FocusPresence.Away,1000,0);
            check(gate.Evaluate(options,timer,FocusPresence.Away,6000,20000).Alert&&gate.Evaluate(options,timer,FocusPresence.Away,6001,0).Alert,mode+": activity does not stop an already due away alert");
            check(!gate.Evaluate(options,timer,FocusPresence.Focused,6002,0).Alert,mode+": returning with fresh input stops the shared alert");
            gate.Evaluate(options,timer,FocusPresence.Away,7000,0);
            check(gate.Evaluate(options with{Targets=[group,tab,target]},timer,FocusPresence.Away,12000,0).Alert,mode+": reordering the same saved targets does not restart the away delay");
        }
        var clock=DateTimeOffset.Now;engine=new TimerEngine(new MemoryStore(),()=>clock);engine.SetFocusMode(options);engine.Start(900,false,50);
        source=new FakeSource{Pending=new(TaskCreationOptions.RunContinuationsAsynchronously),IdleMilliseconds=20000};var transitions=new List<bool>();
        using var monitor=new FocusModeMonitor(engine,source,transitions.Add);monitor.Poll();
        check(transitions.LastOrDefault(),"Idle audio starts while a browser's accessibility provider is still answering");
        source.IdleMilliseconds=0;monitor.Poll();check(!transitions.Last(),"Input stops idle audio without waiting for a hung browser provider");
        engine.SetFocusMode(options with{Targets=[tab]});monitor.Poll();engine.SetFocusMode(options with{Targets=[group]});clock=clock.AddSeconds(3);monitor.Poll();
        check(source.Reads==1,"Changing selected targets cannot queue more probes behind a stalled accessibility provider");
        source.IdleMilliseconds=20000;monitor.Poll();engine.Pause();check(!transitions.Last(),"Pausing synchronously cancels idle audio during an outstanding probe");
        engine.Resume();source.IdleMilliseconds=20000;monitor.Poll();engine.Reset();check(!transitions.Last(),"Reset cancels idle audio synchronously");
        var otherClock=DateTimeOffset.Now;var otherEngine=new TimerEngine(new MemoryStore(),()=>otherClock);
        otherEngine.SetFocusMode(options);otherEngine.Start(900,false,50);
        var otherSource=new FakeSource{Presence=FocusPresence.Away,IdleMilliseconds=0};var otherTransitions=new List<bool>();
        using var otherMonitor=new FocusModeMonitor(otherEngine,otherSource,otherTransitions.Add);
        otherMonitor.Poll();
        for(var seconds=1;seconds<=5;seconds++){
            otherClock=otherClock.AddSeconds(1);otherSource.Pending=new(TaskCreationOptions.RunContinuationsAsynchronously);otherMonitor.Poll();
            otherSource.Pending.TrySetResult(FocusPresence.Away);await Task.Yield();otherMonitor.Poll();
        }
        check(otherTransitions.LastOrDefault(),"Normal asynchronous focus reads do not restart the away delay when Idle for is enabled");
    }
    private static void TabListing(Action<bool,string> check,FocusTarget target)
    {
        var first=target with{Kind=FocusTargetKind.BrowserTab,Name="Same tab title",WindowName="Recent window",TabRuntimeId="first",TabPosition=1};
        var second=first with{Id=Guid.NewGuid(),TabRuntimeId="second",TabPosition=2};
        var other=first with{Id=Guid.NewGuid(),WindowHandle=124,TabRuntimeId="other",WindowName="Another window"};
        var listing=BrowserTabListing.Create([new(first,false),new(second,true),new(second with{Id=Guid.NewGuid()},true),new(other,true)]);
        check(listing.Targets.Count==3&&listing.Targets.Select(BrowserTabListing.Identity).Distinct().Count()==3,"Repeated native identities appear once while separate same-title tabs remain selectable");
        check(listing.Current==second&&listing.Targets[0]==second,"The selected tab from the most recent browser window is first, ahead of alphabetical window ordering");
        check(listing.Targets.Select(t=>t.TabPosition).SequenceEqual(new[]{2,1,1}),"Putting the current tab first preserves original tab positions and identities");
        var switched=BrowserTabListing.Create([new(first,true),new(second,false),new(other,true)]);
        check(switched.Current==first&&switched.Targets[0]==first,"Refreshing after a tab switch updates the current tab without reusing a cached title");
        var ambiguous=BrowserTabListing.Create([new(first,true),new(second,true)]);
        check(ambiguous.Current is null,"Multiple selected browser tabs without keyboard focus are not falsely labeled current");
        var focused=BrowserTabListing.Create([new(first,true),new(second,true,true)]);
        check(focused.Current==second&&focused.Targets[0]==second,"Keyboard focus identifies the current tab among a multi-selection when available");
        check(BrowserTabListing.Create([new(first,null),new(second,true)]).Current is null,"Unreadable selection in the most recent browser window does not produce a guessed current tab");
        check(BrowserTabListing.Create([new(first,null),new(other,true)]).Current is null,"An unreadable recent browser window does not label another window's tab current");
        check(BrowserTabListing.Create([]) is {Targets.Count:0,Current:null},"An empty browser list has no stale current tab");
    }
    private static void DisplayBridge(Action<bool,string> check,FocusTarget target)
    {
        var store=new MemoryStore();var session=new PreviewSession(store);
        var directory=Path.Combine(Path.GetTempPath(),"reflection-focus-display-"+Guid.NewGuid().ToString("N"));
        try {
            using var services=new PreviewServices(session.Engine,directory,audio:new HoldingAudio(),speech:new SilentVoice());
            foreach(var kind in Enum.GetValues<FocusTargetKind>()) {
                var selected=target with{Kind=kind,WindowName=kind==FocusTargetKind.Window?"":"Browser window",TabPosition=kind==FocusTargetKind.Window?0:2};
                session.Engine.SetFocusMode(false,5,selected);
                foreach(var (name,view) in new[]{("Timer state",session.View()),("Settings",services.Settings())}) {
                    var focus=JsonSerializer.SerializeToElement(view,PreviewSession.Json).GetProperty("focusMode");
                    check(focus.GetProperty("target").GetString()==selected.Name&&focus.GetProperty("targetApp").GetString()==selected.App
                        &&focus.GetProperty("targetKind").GetInt32()==(int)kind&&focus.GetProperty("targetWindowName").GetString()==selected.WindowName
                        &&focus.GetProperty("targetPosition").GetInt32()==selected.TabPosition,
                        name+" carries the saved "+kind+" app, window and position even when Focus is off");
                }
            }
        }finally {if(Directory.Exists(directory))Directory.Delete(directory,true);}
    }
    private static void Groups(Action<bool,string> check)
    {
        BrowserTabSlot Header(string id,string name,string parent="strip")=>new(id,parent,"group "+name+" - 2 tabs, • Example - Expanded",true,null);
        BrowserTabSlot Tab(string id,string name,bool? selected=false,string parent="strip")=>new(id,parent,name,false,selected);
        BrowserTabSlot[] slots=[Header("work","Work"),Tab("one","Example - Part of group Work",true),Tab("two","Example - Part of group Work - Memory usage - 100 MB"),Tab("outside","Other tab"),Header("other","Work"),Tab("three","Example - Part of group Work")];
        var groups=BrowserTabGroups.Read(slots);
        check(groups.Count==2&&groups[0].Members.Select(t=>t.Id).SequenceEqual(new[]{"one","two"})&&groups[1].Members.Single().Id=="three","Duplicate group and tab titles remain distinct; an ungrouped tab is excluded");
        check(BrowserTabGroups.Check("work",slots,true)==FocusPresence.Focused&&BrowserTabGroups.Check("other",slots,true)==FocusPresence.Away,"Only the group containing the selected tab counts as focused");
        check(BrowserTabGroups.Check("work",slots,false)==FocusPresence.Away,"Group membership does not count as focus when another window is in front");
        var changed=slots.Select(t=>t.Id=="one"?t with{Selected=false}:t.Id=="two"?t with{Selected=true}:t).ToArray();
        check(BrowserTabGroups.Check("work",changed,true)==FocusPresence.Focused,"Switching between member tabs stays focused");
        changed=slots.Select(t=>t.Id=="one"?t with{Selected=false}:t.Id=="outside"?t with{Selected=true}:t).ToArray();
        check(BrowserTabGroups.Check("work",changed,true)==FocusPresence.Away,"Switching to an ungrouped tab counts as away");
        BrowserTabSlot[] added=[slots[0],slots[1] with{Selected=false},slots[2],Tab("added","New - Part of group Work",true),..slots.Skip(3)];
        check(BrowserTabGroups.Check("work",added,true)==FocusPresence.Focused,"New tabs join the chosen group dynamically without refreshing its saved target");
        var removed=slots.Select(t=>t.Id=="one"?t with{Name="Example"}:t).ToArray();
        check(BrowserTabGroups.Check("work",removed,true)==FocusPresence.Away,"A selected tab removed from its group stops counting as a member");
        var renamed=slots.Select(t=>t.Id is "work" or "one" or "two"?t with{Name=t.Name.Replace("Work","New name")}:t).ToArray();
        check(BrowserTabGroups.Check("work",renamed,true)==FocusPresence.Focused,"Renaming a live group retains its runtime identity and updates membership");
        var selectedBoth=slots.Select(t=>t.Id=="outside"?t with{Selected=true}:t).ToArray();
        check(BrowserTabGroups.Check("work",selectedBoth,true)==FocusPresence.Unknown,"Selections spanning groups fail quietly because they do not identify the active tab");
        selectedBoth=slots.Select(t=>t.Id=="two"?t with{Selected=true}:t).ToArray();
        check(BrowserTabGroups.Check("work",selectedBoth,true)==FocusPresence.Focused,"Multiple selected tabs in the same group still identify the chosen group");
        check(BrowserTabGroups.Check("work",slots.Where(t=>t.Id!="work").ToArray(),true)==FocusPresence.Unavailable,"Closing the chosen group never silently substitutes another group with the same name");
        BrowserTabSlot[] collapsed=[slots[0] with{Name="group Work - 2 tabs, • Example - Collapsed"},Tab("outside","Other tab",true)];
        check(BrowserTabGroups.Read(collapsed).Count==1&&BrowserTabGroups.Check("work",collapsed,true)==FocusPresence.Away,"Collapsed groups remain selectable without exposing or activating their hidden tabs");
        BrowserTabSlot[] nested=[slots[0],Tab("unrelated","Example - Part of group Work",true,"different-strip")];
        check(BrowserTabGroups.Read(nested)[0].Members.Count==0,"A group cannot acquire tabs from a different accessible container");
        BrowserTabSlot[] unnamed=[new("unnamed","strip","unnamed group - 1 tab, • Example - Expanded",true,null),Tab("one","Example - Part of unnamed group",true)];
        check(BrowserTabGroups.Check("unnamed",unnamed,true)==FocusPresence.Focused,"Unnamed groups are recognized by identity and explicit membership labels");
        BrowserTabSlot[] hyphenated=[Header("hyphen","Work - notes"),Tab("one","Example - Part of group Work - notes",true)];
        check(BrowserTabGroups.Read(hyphenated).Single().Name=="Work - notes"&&BrowserTabGroups.Check("hyphen",hyphenated,true)==FocusPresence.Focused,"Group names containing separators retain their complete names");
        BrowserTabSlot[] older=[new("older","strip","Group Work - Example and 1 other tab - Expanded",true,null),slots[1]];
        check(BrowserTabGroups.Check("older",older,true)==FocusPresence.Focused,"The previous Chromium group-header label format is also recognized");
        check(BrowserTabGroups.Check("work",slots.Select(t=>t.Id=="one"?t with{Selected=null}:t).ToArray(),true)==FocusPresence.Unknown,"Unreadable tab selection does not produce a false group alert");
        check(BrowserTabGroups.Read([slots[0] with{Name="Unsupported localized label"},slots[1]]).Count==0,"Unrecognized browser group labels are not offered as usable group targets");
        check((int)FocusTargetKind.Window==0&&(int)FocusTargetKind.BrowserTab==1&&(int)FocusTargetKind.BrowserTabGroup==2,"Adding groups preserves existing saved window and tab enum values");
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
        internal Dictionary<string,FocusPresence>? ByTarget;
        public long? IdleMilliseconds { get; set; }
        public Task<IReadOnlyList<FocusTarget>> ListAsync(FocusTargetKind kind)=>Task.FromResult<IReadOnlyList<FocusTarget>>([]);
        public Task<FocusPresence> CheckAsync(FocusTarget target){Reads++;return Pending?.Task??Task.FromResult(ByTarget?.GetValueOrDefault(target.Key)??Presence);}
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
