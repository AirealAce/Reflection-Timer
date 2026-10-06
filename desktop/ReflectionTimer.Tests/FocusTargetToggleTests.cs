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
        var store=new MemoryStore();var engine=new TimerEngine(store);engine.Start(900,false,50);var before=engine.Snapshot;
        engine.SetFocusMode(mixed.Settings);
        check(engine.Snapshot.Timer==before.Timer&&engine.Snapshot.Audio==before.Audio&&engine.Snapshot.Connection==before.Connection,"Adding/removing targets does not start, pause or reset the session or alter audio/Sheets");
        check(PreviewShortcuts.Chords.Any(c=>c.Id==GlobalShortcut.FocusTargetToggleId&&c.Key==0xDD&&c.Modifiers==GlobalShortcut.Modifiers),"Ctrl+Alt+] is a registered NoRepeat global chord with its own action");
        Exception? failure=null;var thread=new Thread(()=>{
            try{
                foreach(var theme in Enum.GetValues<AppColorTheme>()){
                    using var dialog=new FocusTargetToggleDialog([tab,group,window],theme,_=>{});
                    check(dialog.AccessibleRole==AccessibleRole.Dialog&&dialog.ShowInTaskbar&&dialog.ActiveControl==dialog.Choices&&dialog.Choices.AccessibleName=="Target type",theme+": browser popup exposes a named native dialog and focuses its accessible choice list");
                    dialog.ChooseKey(Keys.Down);check(dialog.SelectedTarget==group,theme+": Down targets the group");dialog.ChooseKey(Keys.Up);check(dialog.SelectedTarget==tab,theme+": Up returns to Tab");
                    check(dialog.ChooseKey(Keys.Enter)&&dialog.DialogResult==DialogResult.OK,theme+": Enter confirms the selected type");
                }
                foreach(var number in new[]{Keys.D1,Keys.D2,Keys.D3,Keys.NumPad1,Keys.NumPad2,Keys.NumPad3}){
                    using var dialog=new FocusTargetToggleDialog([tab,group,window],AppColorTheme.Dark,_=>{});
                    check(dialog.ChooseKey(number)&&dialog.SelectedTarget!.Kind==(number is Keys.D1 or Keys.NumPad1?FocusTargetKind.BrowserTab:number is Keys.D2 or Keys.NumPad2?FocusTargetKind.BrowserTabGroup:FocusTargetKind.Window),"Top-row/numpad "+number+" selects its numbered browser target");
                }
                using var noGroup=new FocusTargetToggleDialog([tab,window],AppColorTheme.Dark,_=>{});
                check(!noGroup.ChooseKey(Keys.D2)&&noGroup.ChooseKey(Keys.D3)&&noGroup.SelectedTarget==window,"Window remains number 3 when no group is available; 2 never chooses the wrong type");
                check(!noGroup.ChooseKey(Keys.Control|Keys.D1)&&noGroup.SelectedTarget==window,"Modified number shortcuts do not accidentally confirm a target");
            }catch(Exception error){failure=error;}
        });thread.SetApartmentState(ApartmentState.STA);thread.Start();if(!thread.Join(TimeSpan.FromSeconds(15)))throw new TimeoutException("Target popup checks timed out.");if(failure is not null)throw failure;
        var speech=new Speech();var reader=new List<string>();engine.SetVoiceAnnouncements(true);
        using var voice=new SessionVoice(engine,speech,(m,_)=>reader.Add(m));
        voice.Feedback("2. Tab Group: Work",nativeControlAnnounces:true);
        check(speech.Messages.Single()=="2. Tab Group: Work"&&reader.Count==0,"Native popup selection uses optional app speech without duplicating its native screen-reader announcement");
        voice.Feedback("Tab added as a Focus target.",supplementary:true);
        check(reader.Single()=="Tab added as a Focus target."&&speech.Messages.Last()==reader.Single(),"Successful target feedback reaches both the reader provider and optional vocalizer");
        engine.SetVoiceAnnouncements(false);speech.Messages.Clear();reader.Clear();voice.Feedback("Tab removed from Focus targets.",supplementary:true);
        check(reader.Single()=="Tab removed from Focus targets."&&speech.Messages.Count==0,"Target feedback remains accessible when the optional vocalizer is off");
    }
    private sealed class Speech:IVoiceOutput
    {
        internal List<string> Messages=[];
        public void Speak(string text,int volume)=>Messages.Add(text);
        public void SetVolume(int volume){}public void Stop(){}public bool TakeFailure()=>false;public void Dispose(){}
    }
}
