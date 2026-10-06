using ReflectionTimer.Accessible;
using ReflectionTimer.Core;
using ReflectionTimer.Desktop;

internal static class FocusTargetToggleTests
{
    internal static void Run(Action<bool,string> check)
    {
        var window=new FocusTarget(Guid.NewGuid(),FocusTargetKind.Window,"Browser","chrome",101,202,303);
        var tab=window with{Kind=FocusTargetKind.BrowserTab,Name="Tab",TabRuntimeId="tab"};var group=tab with{Kind=FocusTargetKind.BrowserTabGroup,Name="Work",TabRuntimeId="group"};
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
                    using var dialog=new FocusTargetToggleDialog([window,tab,group],theme,t=>t.Kind==FocusTargetKind.Window,_=>{});
                    check(dialog.AccessibleRole==AccessibleRole.Dialog&&dialog.ShowInTaskbar&&dialog.ActiveControl==dialog.Choices&&dialog.Choices.AccessibleName=="Target type",theme+": browser popup exposes a named native dialog and focuses its accessible choice list");
                    check(dialog.Choices.Items[0]!.ToString()!.StartsWith("1. Window:")&&dialog.Choices.Items[0]!.ToString()!.EndsWith("(checked)")&&dialog.Choices.Items[1]!.ToString()!.EndsWith("(unchecked)"),theme+": browser choices put Window first and expose their checkbox states in native accessible names");
                    dialog.ChooseKey(Keys.Down);check(dialog.SelectedTarget==tab,theme+": Down targets Tab");dialog.ChooseKey(Keys.Up);check(dialog.SelectedTarget==window,theme+": Up returns to Window");
                    check(dialog.ChooseKey(Keys.Enter)&&dialog.DialogResult==DialogResult.OK,theme+": Enter confirms the selected type");
                }
                foreach(var number in new[]{Keys.D1,Keys.D2,Keys.D3,Keys.NumPad1,Keys.NumPad2,Keys.NumPad3}){
                    using var dialog=new FocusTargetToggleDialog([window,tab,group],AppColorTheme.Dark,_=>false,_=>{});
                    check(dialog.ChooseKey(number)&&dialog.SelectedTarget!.Kind==(number is Keys.D1 or Keys.NumPad1?FocusTargetKind.Window:number is Keys.D2 or Keys.NumPad2?FocusTargetKind.BrowserTab:FocusTargetKind.BrowserTabGroup),"Top-row/numpad "+number+" selects its numbered browser target");
                }
                using var noGroup=new FocusTargetToggleDialog([window,tab],AppColorTheme.Dark,_=>false,_=>{});
                check(noGroup.Choices.Items.Count==2&&!noGroup.ChooseKey(Keys.D3)&&noGroup.ChooseKey(Keys.D2)&&noGroup.SelectedTarget==tab,"An ungrouped browser tab offers only Window and Tab; 3 cannot select a nonexistent group");
                check(!noGroup.ChooseKey(Keys.Control|Keys.D1)&&noGroup.SelectedTarget==tab,"Modified number shortcuts do not accidentally confirm a target");
            }catch(Exception error){failure=error;}
        });thread.SetApartmentState(ApartmentState.STA);thread.Start();if(!thread.Join(TimeSpan.FromSeconds(15)))throw new TimeoutException("Target popup checks timed out.");if(failure is not null)throw failure;
        var speech=new Speech();var reader=new List<string>();engine.SetVoiceAnnouncements(true);
        using var voice=new SessionVoice(engine,speech,(m,_)=>reader.Add(m));
        voice.Feedback("3. Tab Group: Work (checked)",nativeControlAnnounces:true);
        check(speech.Messages.Single()=="3. Tab Group: Work (checked)"&&reader.Count==0,"Native popup selection uses optional app speech without duplicating its native screen-reader announcement");
        voice.Feedback("Tab checked as a Focus target.",supplementary:true);
        check(reader.Single()=="Tab checked as a Focus target."&&speech.Messages.Last()==reader.Single(),"Successful target feedback reaches both the reader provider and optional vocalizer");
        engine.SetVoiceAnnouncements(false);speech.Messages.Clear();reader.Clear();voice.Feedback("Tab unchecked as a Focus target.",supplementary:true);
        check(reader.Single()=="Tab unchecked as a Focus target."&&speech.Messages.Count==0,"Target feedback remains accessible when the optional vocalizer is off");
    }
    private sealed class Speech:IVoiceOutput
    {
        internal List<string> Messages=[];
        public void Speak(string text,int volume)=>Messages.Add(text);
        public void SetVolume(int volume){}public void Stop(){}public bool TakeFailure()=>false;public void Dispose(){}
    }
}
