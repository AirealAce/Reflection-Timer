using ReflectionTimer.Accessible;
using ReflectionTimer.Core;
using ReflectionTimer.Desktop;

internal static class FocusTargetToggleTests
{
    internal static void Run(Action<bool,string> check)
    {
        var window=new FocusTarget(Guid.NewGuid(),FocusTargetKind.Window,"Browser","chrome",101,202,303);
        var tab=window with{Kind=FocusTargetKind.BrowserTab,Name="Tab",TabRuntimeId="tab"};var group=tab with{Kind=FocusTargetKind.BrowserTabGroup,Name="Work",TabRuntimeId="group"};
        var site=tab with{Kind=FocusTargetKind.Site,Name="example.com",SiteHost="example.com"};
        var browserChoices=FocusTargetToggle.BrowserChoices([site,group,tab,window]);
        check(browserChoices.Take(3).SequenceEqual(new[]{window,tab,group})&&browserChoices[3].Key==site.Key,
            "The browser shortcut retains a captured Site after Window, Tab and Tab Group");
        check(FocusTargetToggle.BrowserChoices([window,tab,site]).Select(t=>t.Key).SequenceEqual(new[]{window.Key,tab.Key,site.Key})
            &&FocusTargetToggle.BrowserChoices([window,tab]).SequenceEqual(new[]{window,tab}),
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
        var firstShortcutSite=FocusTargetToggle.BrowserChoices([site with{Id=Guid.Empty}]).Single();
        var otherSite=site with{Id=Guid.Empty,SiteHost="other.example",Name="other.example"};
        var secondShortcutSite=FocusTargetToggle.BrowserChoices([otherSite]).Single();
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
        check(PreviewShortcuts.Chords.Any(c=>c.Id==GlobalShortcut.FocusTargetToggleId&&c.Key==0xDD&&c.Modifiers==GlobalShortcut.Modifiers),"Ctrl+Alt+] is a registered NoRepeat global chord with its own action");
        Exception? failure=null;var thread=new Thread(()=>{
            try{
                foreach(var theme in Enum.GetValues<AppColorTheme>()){
                    using var dialog=new FocusTargetToggleDialog([window,tab,group,site],theme,t=>t.Kind is FocusTargetKind.Window or FocusTargetKind.Site,_=>{});
                    check(dialog.AccessibleRole==AccessibleRole.Dialog&&dialog.ShowInTaskbar&&dialog.ActiveControl==dialog.Choices&&dialog.Choices.AccessibleName=="Target type",theme+": browser popup exposes a named native dialog and focuses its accessible choice list");
                    check(dialog.Choices.Items[0]!.ToString()!.StartsWith("1. Window:")&&dialog.Choices.Items[0]!.ToString()!.EndsWith("(checked)")&&dialog.Choices.Items[1]!.ToString()!.EndsWith("(unchecked)"),theme+": browser choices put Window first and expose their checkbox states in native accessible names");
                    check(dialog.Choices.Items[3]!.ToString()=="4. Site: example.com (checked)",theme+": the native accessible Site choice exposes its host, number and checked state");
                    CheckLayout(dialog,check,theme+": four targets");
                    using var largeFont=new Font(dialog.Font.FontFamily,20);
                    dialog.Font=largeFont;CheckLayout(dialog,check,theme+": larger text");
                    dialog.ChooseKey(Keys.Down);check(dialog.SelectedTarget==tab,theme+": Down targets Tab");
                    check(dialog.Choices.AccessibilityObject.GetChild(1)?.State.HasFlag(AccessibleStates.Selected)==true,
                        theme+": keyboard selection remains exposed by the native list accessibility provider after custom drawing");
                    dialog.ChooseKey(Keys.Up);check(dialog.SelectedTarget==window,theme+": Up returns to Window");
                    dialog.ChooseKey(Keys.Down);dialog.ChooseKey(Keys.Down);dialog.ChooseKey(Keys.Down);
                    check(dialog.SelectedTarget==site&&dialog.Choices.AccessibilityObject.GetChild(3)?.State.HasFlag(AccessibleStates.Selected)==true,
                        theme+": Down reaches Site and exposes its selected state through the native list provider");
                    check(dialog.ChooseKey(Keys.Enter)&&dialog.DialogResult==DialogResult.OK,theme+": Enter confirms the selected type");
                }
                foreach(var number in new[]{Keys.D1,Keys.D2,Keys.D3,Keys.D4,Keys.NumPad1,Keys.NumPad2,Keys.NumPad3,Keys.NumPad4}){
                    using var dialog=new FocusTargetToggleDialog([window,tab,group,site],AppColorTheme.Dark,_=>false,_=>{});
                    var expected=number switch{Keys.D1 or Keys.NumPad1=>FocusTargetKind.Window,Keys.D2 or Keys.NumPad2=>FocusTargetKind.BrowserTab,Keys.D3 or Keys.NumPad3=>FocusTargetKind.BrowserTabGroup,_=>FocusTargetKind.Site};
                    check(dialog.ChooseKey(number)&&dialog.SelectedTarget!.Kind==expected,"Top-row/numpad "+number+" selects its numbered browser target");
                }
                using var ungrouped=new FocusTargetToggleDialog([window,tab,site],AppColorTheme.Dark,_=>false,_=>{});
                CheckLayout(ungrouped,check,"Ungrouped browser with Site");
                check(ungrouped.Choices.Items.Count==3&&!ungrouped.ChooseKey(Keys.D3)&&ungrouped.ChooseKey(Keys.D4)&&ungrouped.SelectedTarget==site,
                    "Site remains choice 4 when no tab group is available, preserving the existing numbered target shortcuts");
                using var noGroup=new FocusTargetToggleDialog([window,tab],AppColorTheme.Dark,_=>false,_=>{});
                CheckLayout(noGroup,check,"Ungrouped browser");
                check(noGroup.Choices.Items.Count==2&&!noGroup.ChooseKey(Keys.D3)&&!noGroup.ChooseKey(Keys.D4)&&noGroup.ChooseKey(Keys.D2)&&noGroup.SelectedTarget==tab,"An ungrouped browser tab with no readable website offers Window and Tab; unavailable choices cannot be selected");
                check(!noGroup.ChooseKey(Keys.Control|Keys.D1)&&noGroup.SelectedTarget==tab,"Modified number shortcuts do not accidentally confirm a target");
            }catch(Exception error){failure=error;}
        });thread.SetApartmentState(ApartmentState.STA);thread.Start();if(!thread.Join(TimeSpan.FromSeconds(15)))throw new TimeoutException("Target popup checks timed out.");if(failure is not null)throw failure;
        var speech=new Speech();var reader=new List<string>();engine.SetVoiceAnnouncements(true);
        using var voice=new SessionVoice(engine,speech,(m,_)=>reader.Add(m));
        voice.Feedback("3. Tab Group: Work (checked)",nativeControlAnnounces:true);
        check(speech.Messages.Single()=="3. Tab Group: Work (checked)"&&reader.Count==0,"Native popup selection uses optional app speech without duplicating its native screen-reader announcement");
        voice.Feedback("4. Site: example.com (unchecked)",nativeControlAnnounces:true);
        check(speech.Messages.Last()=="4. Site: example.com (unchecked)"&&reader.Count==0,
            "Site selection uses the same optional app speech and native screen-reader announcement path as other target types");
        voice.Feedback("Tab checked as a Focus target.",supplementary:true);
        check(reader.Single()=="Tab checked as a Focus target."&&speech.Messages.Last()==reader.Single(),"Successful target feedback reaches both the reader provider and optional vocalizer");
        engine.SetVoiceAnnouncements(false);speech.Messages.Clear();reader.Clear();voice.Feedback("Tab unchecked as a Focus target.",supplementary:true);
        check(reader.Single()=="Tab unchecked as a Focus target."&&speech.Messages.Count==0,"Target feedback remains accessible when the optional vocalizer is off");
    }
    private static void CheckLayout(FocusTargetToggleDialog dialog,Action<bool,string> check,string label)
    {
        dialog.PerformAutoScale();dialog.Size=dialog.GetPreferredSize(Size.Empty);dialog.PerformLayout();
        _=dialog.Choices.Handle;dialog.PerformLayout();
        check(!dialog.Choices.HorizontalScrollbar&&Enumerable.Range(0,dialog.Choices.Items.Count)
            .All(i=>dialog.Choices.GetItemRectangle(i).Bottom<=dialog.Choices.ClientSize.Height),
            label+": every target row is fully visible without a horizontal scrollbar cutting off an option");
        var layout=dialog.Controls.OfType<TableLayoutPanel>().Single();
        var heading=layout.Controls.Find("focus-shortcut-heading",false).Single();
        var keys=layout.Controls.Find("focus-shortcut-keys",false).Single();
        var actions=layout.Controls.Find("focus-shortcut-actions",false).Single();
        check(heading.Left==dialog.Choices.Left&&keys.Left==dialog.Choices.Left&&heading.Bottom<dialog.Choices.Top
            &&dialog.Choices.Bottom<keys.Top&&keys.Bottom<actions.Top&&actions.Right==dialog.Choices.Right
            &&actions.Bottom<=layout.ClientSize.Height-layout.Padding.Bottom,
            label+": heading, rows and hint share a left edge; actions are right aligned and contained below them");
        check(Enumerable.Range(0,dialog.Choices.Items.Count).All(i=>dialog.Choices.AccessibilityObject.GetChild(i)?.Name==dialog.Choices.Items[i]!.ToString()),
            label+": native list accessibility retains complete item names and checked states independently of visual ellipsis");
    }
    private sealed class Speech:IVoiceOutput
    {
        internal List<string> Messages=[];
        public void Speak(string text,int volume)=>Messages.Add(text);
        public void SetVolume(int volume){}public void Stop(){}public bool TakeFailure()=>false;public void Dispose(){}
    }
}
