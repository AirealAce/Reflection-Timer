using ReflectionTimer.Accessible;
using ReflectionTimer.Core;
using ReflectionTimer.Desktop;

internal static class FocusTargetToggleTests
{
    internal static void Run(Action<bool,string> check)
    {
        var window=new FocusTarget(Guid.NewGuid(),FocusTargetKind.Window,"Browser","chrome",101,202,303){ProcessPath=@"C:\Apps\chrome.exe"};
        var app=FocusApps.FromWindow(window)!;
        var tab=window with{Kind=FocusTargetKind.BrowserTab,Name="Tab",TabRuntimeId="tab"};var group=tab with{Kind=FocusTargetKind.BrowserTabGroup,Name="Work",TabRuntimeId="group"};
        var site=tab with{Kind=FocusTargetKind.Site,Name="example.com",SiteHost="example.com"};
        var browserChoices=FocusTargetToggle.ShortcutChoices([site,group,tab,window,app]);
        check(browserChoices.Select(t=>t.Key).SequenceEqual(new[]{app.Key,window.Key,tab.Key,group.Key,site.Key}),
            "The shortcut orders App, Window, Tab, Tab Group and Site consistently");
        CaptureChoices(check,window,app,tab,group,site).GetAwaiter().GetResult();
        var reopenedApp=FocusApps.FromWindow(window with{WindowHandle=999,ProcessId=888,ProcessStartedAt=777,Name="New window"})!;
        var removedApp=FocusTargetToggle.Toggle(new(){Enabled=true,MultipleTargets=true,Targets=[app,window,tab]},reopenedApp);
        check(!removedApp.Added&&removedApp.Settings.SelectedTargets.SequenceEqual(new[]{window,tab})
            &&FocusTargetToggle.Label(app)=="App: "+app.Name,
            "The App shortcut unchecks its durable bookmark after relaunch and keeps individual Window and Tab targets separate");
        check(FocusTargetToggle.ShortcutChoices([reopenedApp]).Single().Id==FocusTargetToggle.ShortcutChoices([app]).Single().Id,
            "A relaunched App receives the same stable shortcut/picker ID");
        check(FocusTargetToggle.ShortcutChoices([window,tab,site]).Select(t=>t.Key).SequenceEqual(new[]{window.Key,tab.Key,site.Key})
            &&FocusTargetToggle.ShortcutChoices([window,tab]).SequenceEqual(new[]{window,tab}),
            "The browser shortcut offers a captured Site for an ungrouped tab without inventing an unreadable website");
        var savedSite=site with{Id=Guid.NewGuid(),App="msedge",WindowHandle=9,ProcessId=8,ProcessStartedAt=7,TabRuntimeId="other-tab",SiteHost="https://WWW.EXAMPLE.COM/page"};
        var removeSite=FocusTargetToggle.Toggle(new(){Enabled=true,MultipleTargets=true,Targets=[window,savedSite]},site);
        check(!removeSite.Added&&removeSite.Settings.SelectedTargets.SequenceEqual(new[]{window}),
            "A Site shortcut unchecks the saved website across browsers, sessions, page paths and www aliases");
        var addSite=FocusTargetToggle.Toggle(removeSite.Settings,site);
        check(addSite.Added&&addSite.Settings.SelectedTargets.SequenceEqual(new[]{window,site})
            &&addSite.Settings.MultipleTargets&&addSite.Settings.Enabled,
            "Checking a Site again preserves existing window targets and enabled Focus mode");
        check(FocusTargetToggle.Label(savedSite)=="Site: example.com"
            &&FocusTargetToggle.Toggle(new(){Target=savedSite},site with{SiteHost="example.com:8443"}).Added,
            "Site feedback uses the canonical host while separately chosen website ports stay independent");
        var firstShortcutSite=FocusTargetToggle.ShortcutChoices([site with{Id=Guid.Empty}]).Single();
        var otherSite=site with{Id=Guid.Empty,SiteHost="other.example",Name="other.example"};
        var secondShortcutSite=FocusTargetToggle.ShortcutChoices([otherSite]).Single();
        check(firstShortcutSite.Id!=Guid.Empty&&secondShortcutSite.Id!=Guid.Empty&&firstShortcutSite.Id!=secondShortcutSite.Id
            &&firstShortcutSite.Id==PreviewApplication.FocusChoices(FocusTargetKind.Site,[savedSite]).Single(t=>!t.UseFocused).Id,
            "Native shortcut Sites receive distinct stable picker IDs by canonical host instead of sharing Guid.Empty");
        var shortcutSites=FocusTargetToggle.Toggle(FocusTargetToggle.Toggle(new(),firstShortcutSite).Settings,secondShortcutSite).Settings;
        // Reopening the picker seeds saved targets before refreshing available
        // sites. The second saved site is now absent from the native inventory.
        var pickerLookup=shortcutSites.SelectedTargets.ToDictionary(t=>t.Id);
        foreach(var choice in PreviewApplication.FocusChoices(FocusTargetKind.Site,[firstShortcutSite]))pickerLookup[choice.Id]=choice;
        var pickerSelection=shortcutSites.SelectedTargets.Select(t=>t.Id).Distinct().Select(id=>pickerLookup[id]).ToArray();
        var siteStore=new MemoryStore();var siteEngine=new TimerEngine(siteStore);
        siteEngine.SetFocusMode(shortcutSites with{Target=pickerSelection.FirstOrDefault(),Targets=[..pickerSelection]});
        check(new TimerEngine(siteStore).Snapshot.FocusMode.SelectedTargets.Select(t=>t.Key).Order().SequenceEqual(new[]{firstShortcutSite.Key,secondShortcutSite.Key}.Order()),
            "Two shortcut-added Sites both survive a picker save when only one is currently available");
        var first=FocusTargetToggle.Toggle(new(){DelaySeconds=7,IdleEnabled=true,IdleSeconds=30},window);
        check(first.Added&&first.Settings.Target==window&&!first.Settings.Enabled&&first.Settings.IdleSeconds==30&&first.Settings.DelaySeconds==7,"Target hotkey adds a bookmark without changing Focus enablement, delay or idle options");
        var mixed=FocusTargetToggle.Toggle(first.Settings with{Enabled=true},tab);
        check(mixed.Settings.MultipleTargets&&mixed.Settings.SelectedTargets.Length==2&&mixed.Settings.Enabled,"Adding another target enables Multiple Targets and keeps the existing target");
        var removed=FocusTargetToggle.Toggle(mixed.Settings,tab with{Id=Guid.NewGuid(),Name="Renamed"});
        check(!removed.Added&&removed.Settings.SelectedTargets.SequenceEqual(new[]{window}),"Toggling the same runtime target removes it even after its title/list ID changes");
        check(!FocusTargetToggle.Toggle(new(){Enabled=true,Target=window},window).Settings.Enabled,"Removing the final target turns Focus off when no idle trigger remains");
        check(FocusTargetToggle.Toggle(first.Settings with{Enabled=true},window).Settings.Enabled,"Removing the last window keeps idle-only Focus enabled");
        var obsolete=window with{Id=Guid.NewGuid(),ProcessId=100,ProcessStartedAt=10,WindowHandle=50};
        var stale=obsolete with{Id=Guid.NewGuid(),WindowHandle=51};
        var duplicates=new FocusModeSettings{Enabled=true,MultipleTargets=true,Targets=[window,obsolete,stale,tab,FocusTarget.Focused(FocusTargetKind.Window)]};
        var uncheckedWindow=FocusTargetToggle.Toggle(duplicates,window,[window]);
        check(!uncheckedWindow.Added&&uncheckedWindow.Settings.SelectedTargets.SequenceEqual(new[]{tab,FocusTarget.Focused(FocusTargetKind.Window)}),"Unchecking a window removes its uniquely matching obsolete copies and keeps tab/dynamic checkboxes intact");
        check(!SavedFocusWindows.Reconnect(uncheckedWindow.Settings,[window]).SelectedTargets.Any(t=>t.Key==window.Key),"A later restoration poll cannot recheck a window just unchecked by the shortcut");
        var nextPress=FocusTargetToggle.Toggle(uncheckedWindow.Settings,window,[window]);
        check(nextPress.Added&&nextPress.Settings.SelectedTargets.Count(t=>t.Key==window.Key)==1,"The next press checks that same window once instead of creating duplicate bookmarks");
        var secondWindow=window with{Id=Guid.NewGuid(),WindowHandle=999};
        var independent=FocusTargetToggle.Toggle(duplicates with{Targets=[window,secondWindow]},window,[window,secondWindow]);
        check(!independent.Added&&independent.Settings.SelectedTargets.Single()==secondWindow,"Unchecking one of two live same-title app windows keeps the other window checked");
        var otherDocument=obsolete with{Name="Other document",WindowName="Other document"};
        check(FocusTargetToggle.Toggle(duplicates with{Targets=[window,otherDocument]},window,[window]).Settings.SelectedTargets.Single()==otherDocument,"A different unavailable document remains a separate saved checkbox when unchecking the current window");
        var store=new MemoryStore();var engine=new TimerEngine(store);engine.Start(900,false,50);var before=engine.Snapshot;
        engine.SetFocusMode(mixed.Settings);
        check(engine.Snapshot.Timer==before.Timer&&engine.Snapshot.Audio==before.Audio&&engine.Snapshot.Connection==before.Connection,"Adding/removing targets does not start, pause or reset the session or alter audio/Sheets");
        var saved=engine.Snapshot.FocusMode;store.Fail=true;
        try{engine.SetFocusMode(FocusTargetToggle.Toggle(saved,tab).Settings);throw new Exception("Failed target toggle accepted");}catch(IOException){}
        check(engine.Snapshot.FocusMode==saved&&new TimerEngine(store).Snapshot.FocusMode.SelectedTargets.SequenceEqual(saved.SelectedTargets),
            "A failed shortcut target save retains the live and durable checked targets");
        store.Fail=false;
        check(PreviewShortcuts.Chords.Any(c=>c.Id==GlobalShortcut.FocusTargetToggleId&&c.Key==0xDD&&c.Modifiers==GlobalShortcut.Modifiers),"Ctrl+Alt+] is a registered NoRepeat global chord with its own action");
        Exception? failure=null;var thread=new Thread(()=>{
            try{
                foreach(var theme in Enum.GetValues<AppColorTheme>()){
                    using var dialog=new FocusTargetToggleDialog([app,window,tab,group,site],theme,t=>t.Kind is FocusTargetKind.App or FocusTargetKind.Window or FocusTargetKind.Site,_=>{});
                    check(dialog.AccessibleRole==AccessibleRole.Dialog&&dialog.ShowInTaskbar&&dialog.ActiveControl==dialog.Choices[0]&&dialog.ChoiceList.AccessibleName=="Target type",theme+": browser popup exposes a named native dialog and starts on the first checkbox");
                    check(dialog.DialogResult==DialogResult.None&&dialog.Choices[0].AccessibleName!.StartsWith("1. App:")&&dialog.Choices[0].Checked&&!dialog.Choices[2].Checked,
                        theme+": constructing saved checkbox states does not submit a target toggle");
                    check(dialog.Choices[4].AccessibleName=="5. Site: example.com"&&dialog.Choices[4].Checked,
                        theme+": the native Site checkbox exposes its full host and number separately from its checked state");
                    CheckLayout(dialog,check,theme+": five targets");
                    using var largeFont=new Font(dialog.Font.FontFamily,20);
                    dialog.Font=largeFont;CheckLayout(dialog,check,theme+": larger text");
                    dialog.ChooseKey(Keys.Down);check(dialog.SelectedTarget==window,theme+": Down targets Window");
                    check(dialog.ActiveControl==dialog.Choices[1]&&dialog.Choices[1].AccessibilityObject.State.HasFlag(AccessibleStates.Checked),
                        theme+": keyboard navigation focuses the native checked Window checkbox without toggling it");
                    dialog.ChooseKey(Keys.Up);check(dialog.SelectedTarget==app,theme+": Up returns to App");
                    dialog.ChooseKey(Keys.Down);dialog.ChooseKey(Keys.Down);dialog.ChooseKey(Keys.Down);dialog.ChooseKey(Keys.Down);
                    check(dialog.SelectedTarget==site&&dialog.ActiveControl==dialog.Choices[4]&&dialog.Choices[4].AccessibilityObject.State.HasFlag(AccessibleStates.Checked),
                        theme+": Down reaches Site and exposes its native checked state");
                    check(dialog.ChooseKey(Keys.Enter)&&dialog.DialogResult==DialogResult.OK&&!dialog.Choices[4].Checked,
                        theme+": Enter unchecks the focused Site once and confirms that target");
                    check(dialog.ChooseKey(Keys.Enter)&&!dialog.Choices[4].Checked&&dialog.SelectedTarget==site,
                        theme+": a repeated Enter cannot toggle the confirmed choice twice");
                }
                foreach(var number in new[]{Keys.D1,Keys.D2,Keys.D3,Keys.D4,Keys.D5,Keys.NumPad1,Keys.NumPad2,Keys.NumPad3,Keys.NumPad4,Keys.NumPad5}){
                    using var dialog=new FocusTargetToggleDialog([app,window,tab,group,site],AppColorTheme.Dark,_=>false,_=>{});
                    var expected=number switch{Keys.D1 or Keys.NumPad1=>FocusTargetKind.App,Keys.D2 or Keys.NumPad2=>FocusTargetKind.Window,Keys.D3 or Keys.NumPad3=>FocusTargetKind.BrowserTab,Keys.D4 or Keys.NumPad4=>FocusTargetKind.BrowserTabGroup,_=>FocusTargetKind.Site};
                    check(dialog.ChooseKey(number)&&dialog.SelectedTarget!.Kind==expected&&dialog.Choices.Single(c=>c.Checked).Name=="focus-shortcut-choice-"+FocusTargetToggleDialog.Number(dialog.SelectedTarget!),"Top-row/numpad "+number+" checks its numbered browser target once");
                }
                using var ungrouped=new FocusTargetToggleDialog([app,window,tab,site],AppColorTheme.Dark,_=>false,_=>{});
                CheckLayout(ungrouped,check,"Ungrouped browser with Site");
                check(ungrouped.Choices.Count==4&&!ungrouped.ChooseKey(Keys.D4)&&ungrouped.ChooseKey(Keys.D5)&&ungrouped.SelectedTarget==site,
                    "Site remains choice 5 when no tab group is available, keeping target numbers stable");
                using var noGroup=new FocusTargetToggleDialog([app,window,tab],AppColorTheme.Dark,_=>false,_=>{});
                CheckLayout(noGroup,check,"Ungrouped browser");
                check(noGroup.Choices.Count==3&&!noGroup.ChooseKey(Keys.D4)&&!noGroup.ChooseKey(Keys.D5)&&noGroup.ChooseKey(Keys.D3)&&noGroup.SelectedTarget==tab,"An ungrouped browser tab with no readable website offers App, Window and Tab; unavailable choices cannot be selected");
                check(!noGroup.ChooseKey(Keys.Control|Keys.D1)&&noGroup.SelectedTarget==tab,"Modified number shortcuts do not accidentally confirm a target");
                using var space=new FocusTargetToggleDialog([window,tab],AppColorTheme.Dark,_=>false,_=>{});
                space.ChooseKey(Keys.Down);var spaceChanges=0;space.Choices[1].CheckedChanged+=(_,_)=>spaceChanges++;
                check(space.ChooseKey(Keys.Space)&&space.DialogResult==DialogResult.OK&&space.SelectedTarget==tab&&space.Choices[1].Checked&&spaceChanges==1,
                    "Space checks the focused native checkbox and confirms one target");
                space.ChooseKey(Keys.Space);check(spaceChanges==1&&space.Choices[1].Checked,"Repeated Space cannot toggle a confirmed checkbox twice");
                using var activate=new FocusTargetToggleDialog([window,tab],AppColorTheme.Dark,_=>false,_=>{});
                _=activate.Choices[1].Handle;activate.Choices[1].AccessibilityObject.DoDefaultAction();
                check(activate.DialogResult==DialogResult.OK&&activate.SelectedTarget==tab&&activate.Choices[1].Checked,
                    "Native checkbox default action checks and confirms its own target without requiring Enter");
                activate.Choices[1].AccessibilityObject.DoDefaultAction();
                check(activate.Choices[1].Checked,"A repeated native checkbox click cannot reverse the confirmed choice");
                using var togglePattern=new FocusTargetToggleDialog([window,tab],AppColorTheme.Dark,_=>true,_=>{});
                togglePattern.Choices[1].Checked=false;
                check(togglePattern.DialogResult==DialogResult.OK&&togglePattern.SelectedTarget==tab,
                    "A native UIA-style Checked change confirms its checkbox even without a Click event");
                using var cancel=new FocusTargetToggleDialog([window,tab],AppColorTheme.Dark,_=>true,_=>{});
                check(cancel.CancelButton!.DialogResult==DialogResult.Cancel&&cancel.DialogResult==DialogResult.None&&cancel.Choices.All(c=>c.Checked),
                    "Cancel has an explicit cancellation result and initialization preserves every original checkbox state");
                foreach(var key in new[]{Keys.D1,Keys.D2,Keys.NumPad1,Keys.NumPad2}){
                    using var ordinary=new FocusTargetToggleDialog([app,window],AppColorTheme.Dark,_=>false,_=>{});
                    CheckLayout(ordinary,check,"Ordinary app and window");
                    var expected=key is Keys.D1 or Keys.NumPad1?FocusTargetKind.App:FocusTargetKind.Window;
                    check(ordinary.Choices[0].AccessibleName!.StartsWith("1. App:")&&ordinary.Choices[1].AccessibleName!.StartsWith("2. Window:")
                        &&ordinary.ChooseKey(key)&&ordinary.SelectedTarget?.Kind==expected,
                        "Ordinary apps offer separate App and Window checkboxes with accessible stable number "+key);
                }
            }catch(Exception error){failure=error;}
        });thread.SetApartmentState(ApartmentState.STA);thread.Start();if(!thread.Join(TimeSpan.FromSeconds(15)))throw new TimeoutException("Target popup checks timed out.");if(failure is not null)throw failure;
        var speech=new Speech();var reader=new List<(string Message,bool Supplementary)>();engine.SetVoiceAnnouncements(true);
        using var voice=new SessionVoice(engine,speech,(m,extra)=>reader.Add((m,extra)));
        voice.Feedback("4. Tab Group: Work (checked)",nativeControlAnnounces:true);
        check(speech.Messages.Single()=="4. Tab Group: Work (checked)"&&reader.Count==0,"Native popup selection uses optional app speech without duplicating its native screen-reader announcement");
        voice.Feedback("5. Site: example.com (unchecked)",nativeControlAnnounces:true);
        check(speech.Messages.Last()=="5. Site: example.com (unchecked)"&&reader.Count==0,
            "Site selection uses the same optional app speech and native screen-reader announcement path as other target types");
        voice.Feedback("Tab checked as a Focus target.");
        check(reader.Single()==("Tab checked as a Focus target.",false)&&speech.Messages.Last()==reader.Single().Message,
            "Successful target feedback reaches the reader and optional vocalizer with action priority after the popup closes");
        engine.SetVoiceAnnouncements(false);speech.Messages.Clear();reader.Clear();voice.Feedback("Tab unchecked as a Focus target.");
        check(reader.Single()==("Tab unchecked as a Focus target.",false)&&speech.Messages.Count==0,"Target feedback remains an action notification when the optional vocalizer is off");
        engine.SetVoiceAnnouncements(true);engine.SetAppVolume(0);speech.Messages.Clear();reader.Clear();voice.Feedback("Tab checked as a Focus target.");
        check(reader.Single()==("Tab checked as a Focus target.",false)&&speech.Messages.Count==0,
            "Muted app audio still publishes one committed target confirmation with reader action priority");
    }
    private static async Task CaptureChoices(Action<bool,string> check,FocusTarget window,FocusTarget app,FocusTarget tab,FocusTarget group,FocusTarget site)
    {
        using var source=new TargetSource{App=app,Captured=[window,tab,group,site]};
        Task? pending=null;
        var ordinary=await FocusTargetToggle.CaptureChoicesAsync(source,window with{App="editor"},false,null,capture=>pending=capture);
        check(ordinary.Select(t=>t.Kind).SequenceEqual(new[]{FocusTargetKind.App,FocusTargetKind.Window})&&source.Reads==0,
            "An ordinary application routes to an App/Window chooser without any browser accessibility discovery");
        var browser=await FocusTargetToggle.CaptureChoicesAsync(source,window,true,null,capture=>pending=capture);
        check(browser.Select(t=>t.Kind).SequenceEqual(new[]{FocusTargetKind.App,FocusTargetKind.Window,FocusTargetKind.BrowserTab,FocusTargetKind.BrowserTabGroup,FocusTargetKind.Site}),
            "A browser merges its resolved App and Window with available Tab, Tab Group and Site choices");
        var noSites=await FocusTargetToggle.CaptureChoicesAsync(source,window,false,null,_=>{});
        check(noSites.All(t=>t.Kind!=FocusTargetKind.Site)&&noSites[0].Kind==FocusTargetKind.App,
            "A missing companion hides Site while retaining native App and Window choices");
        source.Throw=true;
        var failed=await FocusTargetToggle.CaptureChoicesAsync(source,window,true,null,_=>{});
        check(failed.Select(t=>t.Kind).SequenceEqual(new[]{FocusTargetKind.App,FocusTargetKind.Window}),
            "A synchronous browser provider failure still offers the native App and Window");
        source.Throw=false;source.Pending=new(TaskCreationOptions.RunContinuationsAsynchronously);var priorReads=source.Reads;
        var timedOut=await FocusTargetToggle.CaptureChoicesAsync(source,window,true,null,capture=>pending=capture,TimeSpan.FromMilliseconds(5));
        var repeated=await FocusTargetToggle.CaptureChoicesAsync(source,window,true,pending,_=>throw new Exception("Another browser read was queued"),TimeSpan.FromMilliseconds(5));
        check(timedOut.Select(t=>t.Kind).SequenceEqual(new[]{FocusTargetKind.App,FocusTargetKind.Window})
            &&repeated.Select(t=>t.Kind).SequenceEqual(new[]{FocusTargetKind.App,FocusTargetKind.Window})&&source.Reads==priorReads+1,
            "A hung browser read falls back to App/Window and later shortcut presses do not queue more blocked reads");
        source.Pending.SetResult([window,tab]);source.Pending=null;
        var hosted=app with{ApplicationId="Contoso.Editor_abc123def4567!App",ProcessPath=@"C:\Apps\HostedEditor.exe"};
        source.App=hosted;
        var hostWindow=window with{App="ApplicationFrameHost",ProcessPath=@"C:\Windows\ApplicationFrameHost.exe"};
        var hostedChoices=await FocusTargetToggle.CaptureChoicesAsync(source,hostWindow,false,null,_=>{});
        check(hostedChoices.First().Key==hosted.Key&&hostedChoices.First().ApplicationId==hosted.ApplicationId,
            "The shortcut uses the source's resolved hosted App identity rather than the generic foreground process");
        source.App=null;
        check((await FocusTargetToggle.CaptureChoicesAsync(source,hostWindow,false,null,_=>{})).Single().Kind==FocusTargetKind.Window,
            "An unidentifiable hosted app still offers its exact Window without inventing a broad shared App target");
    }
    private sealed class TargetSource:IFocusTargetSource
    {
        internal FocusTarget? App;
        internal IReadOnlyList<FocusTarget> Captured=[];
        internal TaskCompletionSource<IReadOnlyList<FocusTarget>>? Pending;
        internal bool Throw;
        internal int Reads;
        public FocusTarget? CaptureApp(FocusTarget window)=>App;
        public Task<IReadOnlyList<FocusTarget>> CaptureAsync(FocusTarget window,IReadOnlyList<FocusTargetKind> kinds)
        {Reads++;if(Throw)throw new InvalidOperationException("Synthetic browser unavailable");return Pending?.Task??Task.FromResult(Captured);}
        public Task<IReadOnlyList<FocusTarget>> ListAsync(FocusTargetKind kind)=>Task.FromResult(Captured);
        public Task<FocusPresence> CheckAsync(FocusTarget target)=>Task.FromResult(FocusPresence.Focused);
        public void Dispose(){}
    }
    private static void CheckLayout(FocusTargetToggleDialog dialog,Action<bool,string> check,string label)
    {
        dialog.PerformAutoScale();dialog.Size=dialog.GetPreferredSize(Size.Empty);dialog.PerformLayout();
        _=dialog.ChoiceList.Handle;dialog.PerformLayout();
        check(!dialog.ChoiceList.AutoScroll&&dialog.Choices.All(c=>c.Bottom<=dialog.ChoiceList.ClientSize.Height&&c.Height>=c.Font.Height*2+c.Padding.Vertical),
            label+": every native checkbox and both text lines fit without a scrollbar cutting off an option");
        var layout=dialog.Controls.OfType<TableLayoutPanel>().Single();
        var heading=layout.Controls.Find("focus-shortcut-heading",false).Single();
        var keys=layout.Controls.Find("focus-shortcut-keys",false).Single();
        var actions=layout.Controls.Find("focus-shortcut-actions",false).Single();
        check(heading.Left==dialog.ChoiceList.Left&&keys.Left==dialog.ChoiceList.Left&&heading.Bottom<dialog.ChoiceList.Top
            &&dialog.ChoiceList.Bottom<keys.Top&&keys.Bottom<actions.Top&&actions.Right==dialog.ChoiceList.Right
            &&actions.Bottom<=layout.ClientSize.Height-layout.Padding.Bottom,
            label+": heading, rows and hint share a left edge; actions are right aligned and contained below them");
        check(dialog.Choices.All(c=>c.AccessibilityObject.Role==AccessibleRole.CheckButton&&c.AccessibilityObject.Name==c.AccessibleName
            &&c.AccessibilityObject.State.HasFlag(AccessibleStates.Checked)==c.Checked),
            label+": every target exposes native checkbox role, full name and current checked state independently of visual ellipsis");
    }
    private sealed class Speech:IVoiceOutput
    {
        internal List<string> Messages=[];
        public void Speak(string text,int volume)=>Messages.Add(text);
        public void SetVolume(int volume){}public void Stop(){}public bool TakeFailure()=>false;public void Dispose(){}
    }
}
