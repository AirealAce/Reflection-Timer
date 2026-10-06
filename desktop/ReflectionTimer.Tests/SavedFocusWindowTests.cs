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
