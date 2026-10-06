using ReflectionTimer.Accessible;
using ReflectionTimer.Core;
using System.Text.Json;

internal static class BackgroundFocusTests
{
    internal static void Run(Action<bool,string> check)
    {
        var choices=Enum.GetValues<FocusTargetKind>().SelectMany(k=>PreviewApplication.FocusChoices(k,[])).ToArray();
        check(choices.Length==7&&choices.All(t=>t.UseFocused)&&choices.Select(t=>t.Id).Distinct().Count()==7,"All seven capture choices have separate stable identities and retain the original focused choices");
        check(FocusTarget.Focused(FocusTargetKind.BrowserTab).Key=="focused:1"&&FocusTarget.Focused(FocusTargetKind.Window).Key=="focused:0","Legacy singular capture keys stay unchanged across the upgrade");
        var backgrounds=choices.Where(t=>t.CaptureScope!=FocusCaptureScope.Focused).ToArray();
        check(backgrounds.Select(t=>t.Name).SequenceEqual(new[]{"Use open windows (including background)","Use focused tabs (including background)","Use focused tab groups (including background)","Use open tab groups (including background)"}),"Background options use the requested labels and order");
        var settings=new FocusModeSettings{MultipleTargets=true,Targets=[..choices]};
        var restored=JsonSerializer.Deserialize<FocusModeSettings>(JsonSerializer.Serialize(settings))!;
        check(restored.SelectedTargets.Length==7&&restored.SelectedTargets.Select(t=>t.CaptureScope).SequenceEqual(choices.Select(t=>t.CaptureScope)),"Mixed original/background choices survive encrypted-profile JSON round trips");
        check(!PreviewApplication.MatchesSavedFocusTarget(choices.First(t=>t.Kind==FocusTargetKind.Window),backgrounds[0]),"Different capture scopes in one category cannot preselect each other");
        var window=new FocusTarget(Guid.NewGuid(),FocusTargetKind.Window,"Browser","chrome",101,202,303);
        var first=window with{Kind=FocusTargetKind.BrowserTab,TabRuntimeId="one",Name="Selected",TabPosition=1};
        var second=first with{TabRuntimeId="two",Name="Inactive",TabPosition=2};
        BrowserTabSlot[] strip=[new("work","strip","group Work - 1 tab - Expanded",true,null),new("one","strip","Selected - Part of group Work",false,true),new("other","strip","group Other - 1 tab - Expanded",true,null),new("two","strip","Inactive - Part of group Other",false,false)];
        var captured=BackgroundFocusTargets.Capture(window,backgrounds,[new(first,true),new(second,false)],strip);
        check(captured.Count==4&&captured.Any(t=>t.Kind==FocusTargetKind.BrowserTab&&t.TabRuntimeId=="one")&&!captured.Any(t=>t.Kind==FocusTargetKind.BrowserTab&&t.TabRuntimeId=="two"),"Background focused tabs capture the selected tab, excluding inactive tabs in its window");
        check(captured.Count(t=>t.Kind==FocusTargetKind.BrowserTabGroup&&t.CaptureScope==FocusCaptureScope.FocusedIncludingBackground)==1
            &&captured.Count(t=>t.Kind==FocusTargetKind.BrowserTabGroup&&t.CaptureScope==FocusCaptureScope.OpenIncludingBackground)==2,"Focused groups capture the selected tab's group while open groups include both groups");
        var ungrouped=BackgroundFocusTargets.Capture(window,backgrounds,[new(first,true)], [new("one","strip","Selected",false,true)]);
        check(ungrouped.Count==1&&!ungrouped[0].CaptureUnknown,"An ungrouped active tab gives no fake group and remains a usable tab target");
        check(BackgroundFocusTargets.Capture(window,backgrounds,[],strip).Any(t=>t.Kind==FocusTargetKind.BrowserTab&&t.CaptureUnknown),"An unreadable selected tab is Unknown instead of producing a false away alert");
        foreach(var mode in Enum.GetValues<SessionMode>()){
            var store=new MemoryStore();var engine=new TimerEngine(store);engine.SwitchMode(mode);
            var option=backgrounds.Single(t=>t.Kind==FocusTargetKind.BrowserTab);
            engine.SetFocusMode(new(){Enabled=true,DelaySeconds=0,Targets=[option]});
            var other=first with{WindowHandle=102,TabRuntimeId="other-window"};
            var source=new Source{Open=[window,window with{WindowHandle=102}],Result=[first with{CaptureScope=option.CaptureScope},other with{CaptureScope=option.CaptureScope}],FocusedKey=other.Key};
            var transitions=new List<bool>();using var monitor=new FocusModeMonitor(engine,source,transitions.Add);
            if(mode==SessionMode.Stopwatch)engine.StartStopwatch();else engine.Start(900,false,50);
            source.Open=[];monitor.Poll();
            check(source.Snapshots==1&&source.Captures==1&&source.Snapshot.Count==2&&source.Checked.Count==2&&!transitions.Contains(true),mode+": start pins both background windows and any captured selected tab can satisfy Focus");
            source.FocusedKey=null;monitor.Poll();check(transitions.Last(),mode+": leaving every captured selected tab starts the normal away alert");
            source.Open=[window with{WindowHandle=999}];engine.SetAppVolume(40);monitor.Poll();
            check(source.Snapshots==1&&source.Captures==1,mode+": clock, volume and later open windows do not change the pinned capture");
            engine.Pause();check(!transitions.Last(),mode+": pausing stops background-target audio immediately");
            source.Result=[other with{WindowHandle=999,CaptureScope=option.CaptureScope}];engine.Resume();monitor.Poll();
            check(source.Snapshots==2&&source.Captures==2&&source.Snapshot.Single().WindowHandle==999,mode+": explicit resume takes a new background snapshot");
            engine.SwitchMode(mode==SessionMode.Timer?SessionMode.Stopwatch:SessionMode.Timer);engine.SwitchMode(mode);monitor.Poll();
            check(source.Snapshots==2&&store.State.FocusMode.SelectedTargets.Single().UseFocused,mode+": mode switches preserve the capture and never replace the saved dynamic option");
            engine.Resume();source.Result=[first with{CaptureScope=option.CaptureScope,CaptureUnknown=true}];engine.Pause();engine.Resume();monitor.Poll();
            check(!transitions.Last(),mode+": an unreadable background browser suppresses a false away alert");
        }
    }
    private sealed class Source:IFocusTargetSource
    {
        internal IReadOnlyList<FocusTarget> Open=[],Result=[],Snapshot=[];
        internal int Snapshots,Captures;
        internal string? FocusedKey;
        internal List<FocusTarget> Checked=[];
        public FocusTarget? CaptureForeground()=>null;
        public IReadOnlyList<FocusTarget> CaptureOpenWindows(){Snapshots++;return Open.ToArray();}
        public Task<IReadOnlyList<FocusTarget>> CaptureSelectionsAsync(FocusTarget? foreground,IReadOnlyList<FocusTarget> windows,IReadOnlyList<FocusTarget> choices){Captures++;Snapshot=windows;return Task.FromResult(Result);}
        public Task<IReadOnlyList<FocusTarget>> ListAsync(FocusTargetKind kind)=>Task.FromResult<IReadOnlyList<FocusTarget>>([]);
        public Task<FocusPresence> CheckAsync(FocusTarget target){Checked.Add(target);return Task.FromResult(target.CaptureUnknown?FocusPresence.Unknown:target.Key==FocusedKey?FocusPresence.Focused:FocusPresence.Away);}
        public void Dispose(){}
    }
}
