using ReflectionTimer.Accessible;
using ReflectionTimer.Core;
using System.Collections.Immutable;
using System.Text.Json;

internal static class SavedFocusWindowTests
{
    internal static void Run(Action<bool,string> check)
    {
        var old=new FocusTarget(Guid.NewGuid(),FocusTargetKind.Window,"Work","editor",123,456,789){ProcessPath=@"C:\Apps\editor.exe",WindowClass="EditorMain",WindowName="Work"};
        var reopened=old with{Id=Guid.NewGuid(),WindowHandle=124,ProcessId=457,ProcessStartedAt=790};
        var restored=SavedFocusWindows.Resolve(old,[reopened]);
        check(restored.Key==reopened.Key&&restored.Id==old.Id,"A restarted window reconnects to a unique app/title while preserving its bookmark");
        check(SavedFocusWindows.Resolve(old,[reopened,reopened with{WindowHandle=125}])==old,"Two real windows with the same name remain ambiguous and distinct");
        check(SavedFocusWindows.Resolve(old,[reopened,reopened with{WindowHandle=125}],new(){reopened.Key})==old,"Reserving another selected window cannot turn two same-title windows into a false unique match");
        check(SavedFocusWindows.Resolve(old,[reopened,reopened with{WindowHandle=125,Name="Other",WindowName="Other"}]).Key==reopened.Key,"An exact unique title identifies its window among multiple app windows");
        check(SavedFocusWindows.Resolve(old,[reopened with{Name="Renamed",WindowName="Renamed"}]).WindowHandle==124,"The sole eligible app window reconnects after its title changes");
        check(SavedFocusWindows.Resolve(old,[reopened with{ProcessPath=@"D:\Other\editor.exe"}])==old&&SavedFocusWindows.Resolve(old,[reopened with{WindowClass="EditorDialog"}])==old,"A different executable or native window type cannot replace a saved window");
        check(SavedFocusWindows.Resolve(old with{App="explorer"},[reopened with{App="explorer",Name="Other folder",WindowName="Other folder"}]).WindowHandle==123,"A shared host or another Explorer folder is never chosen by app name alone");
        check(SavedFocusWindows.Resolve(old,[old with{Name="Changed",WindowName="Changed"},reopened])==old,"A live native identity wins over another identically named window");
        var legacy=JsonSerializer.Deserialize<FocusTarget>("{\"Id\":\"00000000-0000-0000-0000-000000000001\",\"Kind\":0,\"Name\":\"Work\",\"App\":\"editor\",\"WindowHandle\":123,\"ProcessId\":456,\"ProcessStartedAt\":789}")!;
        check(SavedFocusWindows.Resolve(legacy,[reopened]).ProcessPath==reopened.ProcessPath,"Legacy bookmarks acquire durable hints on successful restoration");
        var app=old with{App="ReflectionTimer",Name="Reflection Timer — App view · 4.2.41",WindowName="Reflection Timer — App view · 4.2.41",WindowClass="WindowsForms10.Window.8.app.0.old"};
        var updated=reopened with{App=app.App,Name="Reflection Timer — App view · 4.2.45",WindowName="Reflection Timer — App view · 4.2.45",WindowClass="WindowsForms10.Window.8.app.0.new"};
        check(SavedFocusWindows.Resolve(app,[updated,updated with{WindowHandle=999,Name="Reset timer?",WindowName="Reset timer?"}]).Key==updated.Key,"App view restores across version and WinForms class hash changes without choosing its modal");
        var options=new FocusModeSettings{Enabled=true,MultipleTargets=true,Targets=[old,old with{Id=Guid.NewGuid(),WindowHandle=125}],DelaySeconds=7,IdleEnabled=true,IdleSeconds=30};
        check(SavedFocusWindows.Reconnect(options,[reopened]).SelectedTargets.Length==1,"Two obsolete bookmarks for one uniquely reopened window combine into one target");
        check(SavedFocusWindows.Reconnect(options with{Targets=[old,reopened]},[reopened]).SelectedTargets.Length==2,"A closed bookmark cannot steal a different still-live selected window");
        var another=old with{Id=Guid.NewGuid(),WindowHandle=125,Name="Other document",WindowName="Other document"};
        var distinct=SavedFocusWindows.Reconnect(options with{Targets=[old,another]},[reopened]);
        check(distinct.SelectedTargets[0].Key==reopened.Key&&distinct.SelectedTargets[1]==another,"A named match is restored without merging a different saved document through app-only fallback");
        var tab=old with{Kind=FocusTargetKind.BrowserTab,TabRuntimeId="tab"};var dynamic=FocusTarget.Focused(FocusTargetKind.Window);
        check(SavedFocusWindows.Reconnect(options with{Targets=[old,tab,dynamic]},[reopened]).SelectedTargets.Skip(1).SequenceEqual(new[]{tab,dynamic}),"Restoration never substitutes browser tabs or session-captured dynamic targets");
        var store=new MemoryStore();var now=DateTimeOffset.Now;var engine=new TimerEngine(store,()=>now);engine.SetFocusMode(options with{Targets=[old]});
        var before=engine.Snapshot;var source=new Source{Windows=[reopened]};var restorer=new SavedFocusWindows(engine,source);
        restorer.Poll();var after=engine.Snapshot;
        check(after.FocusMode.Target!.Key==reopened.Key&&new TimerEngine(store,()=>now).Snapshot.FocusMode.Target!.Key==reopened.Key,"Restoration runs and persists while the timer is stopped, including on startup");
        check(after.Timer==before.Timer&&after.Audio==before.Audio&&after.Connection==before.Connection&&after.Theme==before.Theme
            &&after.FocusMode.DelaySeconds==7&&after.FocusMode.IdleSeconds==30&&after.FocusMode.Enabled,"Reconnecting changes only bookmarks and preserves session, audio, Sheets, theme and Focus preferences");
        check(restorer.PreviousKeys(reopened).Contains(old.Key)&&restorer.Current(old).Key==reopened.Key,"An already-open picker can replace its old checked row and save the current native window");
        now=now.AddSeconds(3);var newest=reopened with{WindowHandle=126,ProcessId=458,ProcessStartedAt=791};source.Windows=[newest];restorer.Poll();
        check(restorer.Current(old).Key==newest.Key&&restorer.PreviousKeys(newest).Contains(reopened.Key),"Repeated restarts keep earlier picker aliases pointing to the newest window");
        engine=new(new MemoryStore(),()=>now);engine.SetFocusMode(true,0,old);source=new(){Pending=new(TaskCreationOptions.RunContinuationsAsynchronously)};restorer=new(engine,source);
        restorer.Poll();engine.SetFocusMode(true,0,another);source.Pending.SetResult([reopened]);restorer.Poll();
        check(engine.Snapshot.FocusMode.Target==another,"A slow restoration result cannot overwrite a newly selected target");
        store=new();engine=new(store);engine.SetFocusMode(true,0,old);restorer=new(engine,new Source{Windows=[reopened]});store.Fail=true;restorer.Poll();
        check(engine.Snapshot.FocusMode.Target==old&&restorer.Current(old)==old,"A failed disk save retains the original targets and creates no successful restoration alias");
        var bridge=JsonSerializer.Serialize(PreviewSession.FocusView(after.FocusMode),PreviewSession.Json);
        check(!bridge.Contains(reopened.ProcessPath)&&!bridge.Contains("windowClass"),"Durable process paths and native class hints stay out of WebView data");
        PackagedWindows(check);
    }
    private static void PackagedWindows(Action<bool,string> check)
    {
        const string oldPath=@"C:\Program Files\WindowsApps\Contoso.FocusNotes_1.2.3.4_x64__abc123def4567\app\FocusNotes.exe";
        const string currentPath=@"C:\Program Files\WindowsApps\Contoso.FocusNotes_2.0.0.0_x64__abc123def4567\app\FocusNotes.exe";
        var old=new FocusTarget(Guid.NewGuid(),FocusTargetKind.Window,"Project notes","FocusNotes",301,401,501){
            ProcessPath=oldPath,WindowClass="Chrome_WidgetWin_1",WindowName="Project notes"};
        var updated=old with{Id=Guid.NewGuid(),WindowHandle=302,ProcessId=402,ProcessStartedAt=502,ProcessPath=currentPath};
        var restored=SavedFocusWindows.Resolve(old,[updated]);
        check(restored.Key==updated.Key&&restored.Id==old.Id&&restored.ProcessPath==currentPath,
            "A saved WindowsApps window reconnects after only its package version and process identity change");
        check(SavedFocusWindows.Resolve(old,[updated with{App="focusnotes",ProcessPath=currentPath.ToLowerInvariant()}]).Key==updated.Key,
            "WindowsApps application and executable identity remain case insensitive");
        check(SavedFocusWindows.Resolve(old,[updated with{Name="Renamed notes",WindowName="Renamed notes"}]).Key==updated.Key,
            "The sole matching packaged app reconnects after both a version update and title change");
        var second=updated with{Id=Guid.NewGuid(),WindowHandle=303,Name="Other notes",WindowName="Other notes"};
        check(SavedFocusWindows.Resolve(old,[updated,second]).Key==updated.Key,
            "A unique saved title selects the correct window among multiple updated packaged app windows");
        check(SavedFocusWindows.Resolve(old,[updated,second with{Name=updated.Name,WindowName=updated.WindowName}])==old,
            "Two updated package windows with the saved title remain ambiguous");
        check(SavedFocusWindows.Resolve(old,[updated with{Name="First",WindowName="First"},second])==old,
            "A package version match alone cannot choose between multiple differently titled windows");
        check(SavedFocusWindows.Resolve(old,[updated,second with{Name=updated.Name,WindowName=updated.WindowName}],new(){updated.Key})==old,
            "Reserving one updated package window does not hide same-title ambiguity");

        var rejectedPaths=new (string Name,string Path)[]{
            ("publisher",@"C:\Program Files\WindowsApps\Contoso.FocusNotes_2.0.0.0_x64__xyz987uvw6543\app\FocusNotes.exe"),
            ("package name",@"C:\Program Files\WindowsApps\Contoso.OtherNotes_2.0.0.0_x64__abc123def4567\app\FocusNotes.exe"),
            ("architecture",@"C:\Program Files\WindowsApps\Contoso.FocusNotes_2.0.0.0_arm64__abc123def4567\app\FocusNotes.exe"),
            ("resource ID",@"C:\Program Files\WindowsApps\Contoso.FocusNotes_2.0.0.0_x64_neutral_abc123def4567\app\FocusNotes.exe"),
            ("executable name",@"C:\Program Files\WindowsApps\Contoso.FocusNotes_2.0.0.0_x64__abc123def4567\app\OtherNotes.exe"),
            ("relative executable folder",@"C:\Program Files\WindowsApps\Contoso.FocusNotes_2.0.0.0_x64__abc123def4567\bin\FocusNotes.exe"),
            ("installation drive",@"D:\Program Files\WindowsApps\Contoso.FocusNotes_2.0.0.0_x64__abc123def4567\app\FocusNotes.exe"),
            ("installation root",@"C:\PackageCache\WindowsApps\Contoso.FocusNotes_2.0.0.0_x64__abc123def4567\app\FocusNotes.exe"),
            ("short version",@"C:\Program Files\WindowsApps\Contoso.FocusNotes_2.0.0_x64__abc123def4567\app\FocusNotes.exe"),
            ("extra version part",@"C:\Program Files\WindowsApps\Contoso.FocusNotes_2.0.0.0.1_x64__abc123def4567\app\FocusNotes.exe"),
            ("version-like text",@"C:\Program Files\WindowsApps\Contoso.FocusNotes_2.0.beta.0_x64__abc123def4567\app\FocusNotes.exe"),
            ("missing version",@"C:\Program Files\WindowsApps\Contoso.FocusNotes__x64__abc123def4567\app\FocusNotes.exe"),
            ("nested package folder",@"C:\Program Files\WindowsApps\Backup\Contoso.FocusNotes_2.0.0.0_x64__abc123def4567\app\FocusNotes.exe"),
            ("WindowsApps-like folder",@"C:\Program Files\WindowsApps-Backup\Contoso.FocusNotes_2.0.0.0_x64__abc123def4567\app\FocusNotes.exe")
        };
        foreach(var test in rejectedPaths)
            check(SavedFocusWindows.Resolve(old,[updated with{ProcessPath=test.Path}])==old,
                "A changed WindowsApps "+test.Name+" cannot replace a saved target");
        check(SavedFocusWindows.Resolve(old,[updated with{App="OtherNotes"}])==old
            &&SavedFocusWindows.Resolve(old,[updated with{WindowClass="OtherWindowClass"}])==old,
            "Package version matching still requires the saved app name and native window type");
        var unpackaged=old with{ProcessPath=oldPath.Replace(@"\WindowsApps\",@"\Applications\")};
        check(SavedFocusWindows.Resolve(unpackaged,[updated with{ProcessPath=currentPath.Replace(@"\WindowsApps\",@"\Applications")}])==unpackaged,
            "Version-looking directories outside WindowsApps retain exact executable matching");
        var lookalike=old with{ProcessPath=oldPath.Replace(@"\WindowsApps\",@"\WindowsApps-Backup\")};
        check(SavedFocusWindows.Resolve(lookalike,[updated with{ProcessPath=currentPath.Replace(@"\WindowsApps\",@"\WindowsApps-Backup")}])==lookalike,
            "A WindowsApps-like parent name does not enable version-insensitive application matching");
        var versionInExecutable=old with{ProcessPath=@"C:\Apps\FocusNotes-1.2.3.4.exe"};
        check(SavedFocusWindows.Resolve(versionInExecutable,[updated with{ProcessPath=@"C:\Apps\FocusNotes-2.0.0.0.exe"}])==versionInExecutable,
            "Version-like executable names outside packaged installations are never substituted");
        check(SavedFocusWindows.Resolve(old,[updated with{ProcessPath=""}])==old,
            "A current window with no executable hint cannot replace a saved packaged executable");

        var settings=new FocusModeSettings{Enabled=true,MultipleTargets=true,Targets=[old],DelaySeconds=9,IdleEnabled=true,IdleSeconds=35};
        var store=new MemoryStore();var now=new DateTimeOffset(2026,10,8,12,0,0,TimeSpan.Zero);
        var engine=new TimerEngine(store,()=>now);engine.SetFocusMode(settings);
        var before=engine.Snapshot;var source=new Source{Windows=[updated]};var restorer=new SavedFocusWindows(engine,source);
        restorer.Poll();
        var persisted=new TimerEngine(store,()=>now).Snapshot;
        check(engine.Snapshot.FocusMode.Target!.Key==updated.Key&&persisted.FocusMode.Target!.Key==updated.Key
            &&persisted.FocusMode.Target!.Id==old.Id&&persisted.FocusMode.Target!.ProcessPath==currentPath,
            "Startup restoration saves the updated WindowsApps path and native identity under the original bookmark");
        check(engine.Snapshot.Timer==before.Timer&&engine.Snapshot.Audio==before.Audio&&engine.Snapshot.Connection==before.Connection
            &&engine.Snapshot.FocusMode.DelaySeconds==9&&engine.Snapshot.FocusMode.IdleSeconds==35,
            "Restoring a package update preserves timer, audio, connection and Focus delay preferences");
        check(restorer.PreviousKeys(updated).Contains(old.Key)&&restorer.Current(old).Key==updated.Key
            &&restorer.Current(old).ProcessPath==currentPath,
            "An open chooser resolves its checked pre-update row to the current packaged window");
        check(SavedFocusWindows.MatchesForToggle(settings,old,updated,[updated]),
            "The target shortcut recognizes a checked pre-update bookmark as the current packaged window");
        var uncheckedTarget=FocusTargetToggle.Toggle(settings,updated,[updated]);
        check(!uncheckedTarget.Added&&uncheckedTarget.Settings.SelectedTargets.Length==0
            &&!SavedFocusWindows.Reconnect(uncheckedTarget.Settings,[updated]).SelectedTargets.Any(),
            "Unchecking the updated packaged window removes its old bookmark without a later reconnect rechecking it");
        var ambiguousToggle=FocusTargetToggle.Toggle(settings,updated,[updated,second with{Name=updated.Name,WindowName=updated.WindowName}]);
        check(ambiguousToggle.Added&&ambiguousToggle.Settings.SelectedTargets.Length==2,
            "The shortcut cannot silently uncheck an ambiguous older packaged window");

        const string nextPath=@"C:\Program Files\WindowsApps\Contoso.FocusNotes_3.1.4.0_x64__abc123def4567\app\FocusNotes.exe";
        var next=updated with{Id=Guid.NewGuid(),WindowHandle=304,ProcessId=404,ProcessStartedAt=504,ProcessPath=nextPath};
        restorer.Apply([next]);
        check(restorer.Current(old).Key==next.Key&&restorer.Current(updated).Key==next.Key
            &&restorer.PreviousKeys(next).Contains(old.Key)&&restorer.PreviousKeys(next).Contains(updated.Key)
            &&new TimerEngine(store,()=>now).Snapshot.FocusMode.Target!.ProcessPath==nextPath,
            "Successive package updates persist the newest window and keep earlier chooser aliases current");
        var otherSaved=old with{Id=Guid.NewGuid(),WindowHandle=305,Name="Other notes",WindowName="Other notes"};
        var distinct=SavedFocusWindows.Reconnect(settings with{Targets=[old,otherSaved]},[updated,second]);
        check(distinct.SelectedTargets.Select(t=>t.Key).SequenceEqual(new[]{updated.Key,second.Key}),
            "Separate saved documents reconnect to their respective windows after a package update");
        var failedStore=new MemoryStore();var failedEngine=new TimerEngine(failedStore,()=>now);failedEngine.SetFocusMode(settings);
        var failedRestorer=new SavedFocusWindows(failedEngine,new Source{Windows=[updated]});failedStore.Fail=true;failedRestorer.Poll();
        check(failedEngine.Snapshot.FocusMode.Target==old&&failedRestorer.Current(old)==old,
            "A failed package-update bookmark save preserves the old selection and creates no chooser alias");
        var web=JsonSerializer.Serialize(PreviewSession.FocusView(persisted.FocusMode),PreviewSession.Json);
        check(!web.Contains("WindowsApps")&&!web.Contains("processPath")&&!web.Contains("windowClass"),
            "Package installation identity stays private when the updated target is sent to the chooser");
    }
    private sealed class Source:IFocusTargetSource
    {
        internal IReadOnlyList<FocusTarget> Windows=[];
        internal TaskCompletionSource<IReadOnlyList<FocusTarget>>? Pending;
        public Task<IReadOnlyList<FocusTarget>> ListAsync(FocusTargetKind kind)=>Pending?.Task??Task.FromResult(Windows);
        public Task<FocusPresence> CheckAsync(FocusTarget target)=>Task.FromResult(FocusPresence.Focused);
        public void Dispose(){}
    }
}
