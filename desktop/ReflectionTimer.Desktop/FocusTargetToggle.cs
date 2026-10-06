using System.Collections.Immutable;
using ReflectionTimer.Core;

namespace ReflectionTimer.Accessible;

internal static class FocusTargetToggle
{
    internal static (FocusModeSettings Settings,bool Added) Toggle(FocusModeSettings current,FocusTarget target)
    {
        var added=!current.SelectedTargets.Any(t=>t.Key==target.Key);
        var targets=(added?current.SelectedTargets.Append(target):current.SelectedTargets.Where(t=>t.Key!=target.Key)).ToImmutableArray();
        return (current with{Target=targets.FirstOrDefault(),Targets=targets,MultipleTargets=current.MultipleTargets||targets.Length>1,
            Enabled=current.Enabled&&(targets.Length>0||current.IdleEnabled)},added);
    }
    internal static string Label(FocusTarget target)=>target.Kind switch{
        FocusTargetKind.BrowserTab=>"Tab: "+target.Name,
        FocusTargetKind.BrowserTabGroup=>"Tab Group: "+target.Name,
        _=>"Window: "+target.App+" — "+target.Name
    };
}

internal sealed partial class PreviewApplication
{
    private bool targetShortcutBusy;
    private Task? targetShortcutBrowserRead;
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
                FocusTarget? chosen=window;
                if(WindowsFocusTargets.IsBrowser(window)){
                    var capture=focusTargets.CaptureAsync(window,Enum.GetValues<FocusTargetKind>());targetShortcutBrowserRead=capture;
                    var captured=await capture.WaitAsync(TimeSpan.FromSeconds(6));
                    var choices=new[]{FocusTargetKind.BrowserTab,FocusTargetKind.BrowserTabGroup,FocusTargetKind.Window}
                        .Select(kind=>captured.FirstOrDefault(t=>t.Kind==kind)).OfType<FocusTarget>().ToArray();
                    if(choices.Length==0)throw new ArgumentException("The browser target could not be identified. No targets changed.");
                    if(await focusTargets.CheckAsync(window).WaitAsync(TimeSpan.FromSeconds(6))!=FocusPresence.Focused)
                        throw new ArgumentException("The initiating browser is no longer focused. Press Ctrl+Alt+] again in the browser you want.");
                    if(closing)return;
                    using var dialog=new FocusTargetToggleDialog(choices,Session.Engine.SettingsSnapshot.Theme,
                        message=>Services.AnnounceFeedback(message,nativeControlAnnounces:true));
                    chosen=dialog.ShowDialog(MainForm)==DialogResult.OK?dialog.SelectedTarget:null;
                }
                if(chosen is null){Services.AnnounceFeedback("Focus target selection cancelled.",supplementary:true);return;}
                if(closing)return;
                var validation=focusTargets.CheckAsync(chosen);if(chosen.Kind!=FocusTargetKind.Window)targetShortcutBrowserRead=validation;
                if(await validation.WaitAsync(TimeSpan.FromSeconds(6))==FocusPresence.Unavailable)
                    throw new ArgumentException("That target closed or moved before it could be saved. No targets changed.");
                var open=await focusTargets.ListAsync(FocusTargetKind.Window).WaitAsync(TimeSpan.FromSeconds(6));
                focusMonitor.RestoreWindows(open);
                var result=FocusTargetToggle.Toggle(Session.Engine.SettingsSnapshot.FocusMode,chosen);
                Session.Engine.SetFocusMode(result.Settings);
                Services.AnnounceFeedback(FocusTargetToggle.Label(chosen)+(result.Added?" added as a Focus target.":" removed from Focus targets."),supplementary:true);
            }catch(Exception error){Services.AnnounceFeedback(error is ArgumentException?error.Message:"The Focus target could not be saved. Your existing targets and session are retained.");}
            finally{targetShortcutBusy=false;}
        });
    }
}
