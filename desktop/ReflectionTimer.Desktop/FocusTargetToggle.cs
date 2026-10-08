using System.Collections.Immutable;
using ReflectionTimer.Core;

namespace ReflectionTimer.Accessible;

internal static class FocusTargetToggle
{
    internal static FocusTarget[] BrowserChoices(IReadOnlyList<FocusTarget> captured)
        => new[]{FocusTargetKind.Window,FocusTargetKind.BrowserTab,FocusTargetKind.BrowserTabGroup,FocusTargetKind.Site}
            .Select(kind=>captured.FirstOrDefault(t=>t.Kind==kind)).OfType<FocusTarget>()
            // Native sites have no runtime list ID. Use the same host-based
            // ID as the picker so multiple shortcut sites remain selectable.
            .Select(target=>target.Kind==FocusTargetKind.Site
                ?PreviewApplication.FocusChoices(FocusTargetKind.Site,[target]).Single(t=>!t.UseFocused):target).ToArray();
    internal static FocusTarget[] Matching(FocusModeSettings current,FocusTarget target,IReadOnlyList<FocusTarget>? open=null)
        => current.SelectedTargets.Where(saved=>SavedFocusWindows.MatchesForToggle(current,saved,target,open??[])).ToArray();
    internal static (FocusModeSettings Settings,bool Added) Toggle(FocusModeSettings current,FocusTarget target,IReadOnlyList<FocusTarget>? open=null)
    {
        var matching=Matching(current,target,open).Select(t=>t.Key).ToHashSet();
        var added=matching.Count==0;
        var targets=(added?current.SelectedTargets.Append(target):current.SelectedTargets.Where(t=>!matching.Contains(t.Key))).ToImmutableArray();
        return (current with{Target=targets.FirstOrDefault(),Targets=targets,MultipleTargets=current.MultipleTargets||targets.Length>1,
            Enabled=current.Enabled&&(targets.Length>0||current.IdleEnabled)},added);
    }
    internal static string Label(FocusTarget target)=>target.Kind switch{
        FocusTargetKind.BrowserTab=>"Tab: "+target.Name,
        FocusTargetKind.BrowserTabGroup=>"Tab Group: "+target.Name,
        FocusTargetKind.Site=>"Site: "+FocusSites.CanonicalHost(target.SiteHost),
        _=>"Window: "+target.App+" — "+target.Name
    };
}

internal sealed partial class PreviewApplication
{
    private bool targetShortcutBusy;
    private Task? targetShortcutBrowserRead;
    private FocusTargetToggleDialog? targetShortcutDialog;
    internal async Task<FocusTarget?> ChooseBrowserFocusTargetAsync(nint sourceWindow,IReadOnlyList<FocusTarget> choices,IReadOnlyList<FocusTarget> open)
    {
        if(closing)return null;
        var current=Session.Engine.SettingsSnapshot.FocusMode;
        using var dialog=new FocusTargetToggleDialog(choices,Session.Engine.SettingsSnapshot.Theme,
            target=>FocusTargetToggle.Matching(current,target,open).Length>0,
            message=>Services.AnnounceFeedback(message,nativeControlAnnounces:true));
        targetShortcutDialog=dialog;
        try {return await dialog.ShowForShortcutAsync(sourceWindow)==DialogResult.OK?dialog.SelectedTarget:null;}
        finally {if(ReferenceEquals(targetShortcutDialog,dialog))targetShortcutDialog=null;}
    }
    private void ToggleTargetFromGlobalShortcut()
    {
        compactPresses.Reset();
        if(closing||targetShortcutBusy||MainForm is null||MainForm.IsDisposed)return;
        // Capture the initiating window before creating a popup or announcing.
        var window=focusTargets.CaptureForeground();
        if(window is null){Services.AnnounceFeedback("The focused window could not be identified. No targets changed.");return;}
        // A timed-out accessibility read can still occupy the shared worker.
        // Keep one request pending instead of queuing another on every press.
        if(WindowsFocusTargets.IsBrowser(window)&&targetShortcutBrowserRead is {IsCompleted:false}){
            Services.AnnounceFeedback("The browser target is still loading. Choose a window in Focus settings, or reopen the app if it stays unavailable.");return;
        }
        targetShortcutBusy=true;
        MainForm.BeginInvoke(async()=>{
            try{
                if(await focusTargets.CheckAsync(window).WaitAsync(TimeSpan.FromSeconds(6))!=FocusPresence.Focused)
                    throw new ArgumentException("The initiating window is no longer focused. Press Ctrl+Alt+] again in the window you want.");
                var open=await focusTargets.ListAsync(FocusTargetKind.Window).WaitAsync(TimeSpan.FromSeconds(6));
                focusMonitor.RestoreWindows(open);
                FocusTarget? chosen=window;
                if(WindowsFocusTargets.IsBrowser(window)){
                    var capture=focusTargets.CaptureAsync(window,Enum.GetValues<FocusTargetKind>());targetShortcutBrowserRead=capture;
                    var captured=await capture.WaitAsync(TimeSpan.FromSeconds(6));
                    var choices=FocusTargetToggle.BrowserChoices(captured.Where(t=>t.Kind!=FocusTargetKind.Site||BrowserConnected).ToArray());
                    if(choices.Length==0)throw new ArgumentException("The browser target could not be identified. No targets changed.");
                    if(await focusTargets.CheckAsync(window).WaitAsync(TimeSpan.FromSeconds(6))!=FocusPresence.Focused)
                        throw new ArgumentException("The initiating browser is no longer focused. Press Ctrl+Alt+] again in the browser you want.");
                    if(closing)return;
                    chosen=await ChooseBrowserFocusTargetAsync(new nint(window.WindowHandle),choices,open);
                }
                if(closing)return;
                if(chosen is null){Services.AnnounceFeedback("Focus target selection cancelled.",supplementary:true);return;}
                var validation=focusTargets.CheckAsync(chosen);if(chosen.Kind!=FocusTargetKind.Window)targetShortcutBrowserRead=validation;
                if(await validation.WaitAsync(TimeSpan.FromSeconds(6))==FocusPresence.Unavailable)
                    throw new ArgumentException("That target closed or moved before it could be saved. No targets changed.");
                open=await focusTargets.ListAsync(FocusTargetKind.Window).WaitAsync(TimeSpan.FromSeconds(6));
                if(closing)return;
                focusMonitor.RestoreWindows(open);
                var before=Session.Engine.SettingsSnapshot.FocusMode;
                var changedKeys=FocusTargetToggle.Matching(before,chosen,open).Select(PreviewApplication.FocusTargetKey)
                    .Append(PreviewApplication.FocusTargetKey(chosen)).Concat(focusMonitor.PreviousWindowKeys(chosen).Select(HashFocusKey)).Distinct().ToArray();
                var result=FocusTargetToggle.Toggle(before,chosen,open);
                FocusSitePolicy.ValidateSelection(before,result.Settings,BrowserConnected);
                Session.Engine.SetFocusMode(result.Settings);
                if(result.Added)focusChoices[chosen.Id]=chosen;
                Broadcast(new{type="focusTargetToggled",target=PreviewSession.FocusTargetView(chosen),@checked=result.Added,
                    keys=changedKeys,result.Settings.MultipleTargets,result.Settings.Enabled});
                Services.AnnounceFeedback(FocusTargetToggle.Label(chosen)+(result.Added?" checked as a Focus target.":" unchecked as a Focus target."));
            }catch(Exception error){Services.AnnounceFeedback(error is ArgumentException?error.Message:"The Focus target could not be saved. Your existing targets and session are retained.");}
            finally{targetShortcutBusy=false;}
        });
    }
}
