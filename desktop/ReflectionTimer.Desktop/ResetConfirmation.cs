using ReflectionTimer.Core;

namespace ReflectionTimer.Accessible;

internal sealed record ResetWarning(SessionMode Mode, bool Running, bool HasDraft)
{
    internal static ResetWarning? For(AppState state)
    {
        if(!state.ConfirmBeforeReset||TimerEngine.IsPaused(state.Timer))return null;
        var hasDraft=state.Prompts.Any(p=>!string.IsNullOrWhiteSpace(p.Draft)||!string.IsNullOrWhiteSpace(p.EarlyEndReason)||p.Pauses.Any(pause=>!string.IsNullOrWhiteSpace(pause.Reason)));
        return state.Timer.IsRunning||hasDraft ? new(state.Timer.Mode,state.Timer.IsRunning,hasDraft) : null;
    }
    internal string Title=>Mode==SessionMode.Stopwatch?"Reset stopwatch?":"Reset timer?";
    internal string Message=>string.Join("\n\n",new[]{
        Running?(Mode==SessionMode.Stopwatch?"The stopwatch is running.":"The timer is running."):null,
        HasDraft?"An unsent reflection contains text. Your reflection text will stay saved.":null,
        Mode==SessionMode.Stopwatch?"Reset the stopwatch to 0:00?":"Reset the timer to the duration in its input boxes?"
    }.Where(text=>text is not null));
}

internal sealed partial class PreviewApplication
{
    private bool resetInProgress,resetShortcutQueued,resetTimeFocusPending;
    private ResetConfirmationDialog? resetConfirmationDialog;
    private long resetConfirmationShownAt=long.MaxValue;
    internal bool ResetTimeFocusPending=>resetTimeFocusPending;
    private readonly Func<PreviewWindow,ResetWarning,Task<bool>> confirmReset;
    private void ResetFromGlobalShortcut(long requestedAt)
    {
        compactPresses.Reset();
        if(closing||MainForm is null||MainForm.IsDisposed)return;
        if(resetInProgress){
            // A queued key from draft preparation is not a second decision
            // made after the warning appeared. Only accept a displayed warning.
            if(requestedAt>=resetConfirmationShownAt)resetConfirmationDialog?.ConfirmFromShortcut();
            return;
        }
        if(resetShortcutQueued)return;
        resetShortcutQueued=true;
        // Leave the registered-hotkey callback before any modal/browser work.
        MainForm.BeginInvoke(async()=>{
            resetShortcutQueued=false;
            if(closing||resetInProgress)return;
            var owner=windows.FirstOrDefault(w=>!w.IsDisposed&&ReflectionTimer.Desktop.WindowActivation.IsForeground(w))
                ??windows.FirstOrDefault(w=>!w.IsDisposed&&w.Visible&&w.View=="compact")??(PreviewWindow)MainForm;
            try {
                await WithResetConfirmationAsync(owner,()=>{
                    var result=KeepingTimeOnly(Session.ResetTimerFromShortcut);
                    Announce(result);return Task.CompletedTask;
                },requireReady:false);
            } catch(Exception error) {
                Services.AnnounceFeedback(error is ArgumentException or InvalidOperationException?error.Message:"The timer could not be reset. Your saved session and drafts are retained.");
            }
        });
    }
    internal bool ShowResetConfirmationDialog(PreviewWindow owner,ResetWarning warning)
    {
        using var dialog=new ResetConfirmationDialog(warning,Session.Engine.SettingsSnapshot.Theme);
        resetConfirmationDialog=dialog;resetConfirmationShownAt=long.MaxValue;
        dialog.Shown+=(_,_)=>resetConfirmationShownAt=Session.Engine.ElapsedNow;
        try{return dialog.ShowDialog(owner)==DialogResult.OK;}
        finally{if(ReferenceEquals(resetConfirmationDialog,dialog)){resetConfirmationDialog=null;resetConfirmationShownAt=long.MaxValue;}}
    }
    internal async Task<bool> WithResetConfirmationAsync(PreviewWindow owner,Func<Task> reset,bool requireReady=true)
    {
        // One decision across all windows; repeated buttons/shortcuts cannot
        // queue another reset behind the first confirmation.
        if(resetInProgress)return false;
        resetInProgress=true;var completed=false;var accepted=false;var focusOrigin=(nint)0;
        try {
            await WithPromptLock(async()=>{
                if(requireReady)owner.EnsureResetAvailable();
                var prepared=new List<IReflectionPromptWindow>();
                try {
                    foreach(var window in windows.Where(w=>w.ReflectionOpen&&!w.IsDisposed).ToArray()) {
                        var editor=(IReflectionPromptWindow)window;prepared.Add(editor);
                        await editor.PrepareHandoffAsync();
                    }
                    var before=Session.Engine.Snapshot.Timer;
                    if(ResetWarning.For(Session.Engine.Snapshot) is {} warning) {
                        if(!await confirmReset(owner,warning))return;
                        accepted=true;focusOrigin=ReflectionTimer.Desktop.WindowActivation.Foreground;
                        resetTimeFocusPending=true;
                        var current=Session.Engine.Snapshot.Timer;
                        if(current.SessionId!=before.SessionId||current.Mode!=before.Mode)
                            throw new InvalidOperationException("The active session changed while confirmation was open. Review it before resetting again.");
                    }
                    if(closing||owner.IsDisposed)return;
                    await reset();completed=true;
                } finally {foreach(var editor in prepared)editor.ResumeEditing();}
            });
            if(completed&&accepted)try{FocusResetTime(owner,focusOrigin);}
                catch{Services.AnnounceFeedback("The timer was reset, but its time display could not be focused. Open a timer view to continue.");}
            return completed;
        } finally {resetTimeFocusPending=false;resetInProgress=false;}
    }
    private void FocusResetTime(PreviewWindow origin,nint focusOrigin)
    {
        if(closing||origin.IsDisposed||ReflectionTimer.Desktop.WindowActivation.Foreground!=focusOrigin)return;
        var viewer=(origin.View is "main" or "compact")&&origin.Visible?origin:
            windows.FirstOrDefault(w=>!w.IsDisposed&&w.View=="compact"&&w.Visible)
            ??windows.FirstOrDefault(w=>!w.IsDisposed&&w.View=="main"&&w.Visible);
        if(viewer is null&&Session.Engine.SettingsSnapshot.ShowFloatingTimer){
            viewer=windows.FirstOrDefault(w=>!w.IsDisposed&&w.View=="compact")??Create("compact");
        }
        viewer??=(PreviewWindow)MainForm!;
        if(viewer.View=="main")Open("main",timerPage:true);
        else{
            if(!Session.Engine.SettingsSnapshot.ShowFloatingTimer)Session.Engine.SetFloatingTimer(true);
            viewer.Show();viewer.ApplyPosition();ReflectionTimer.Desktop.WindowActivation.Focus(viewer);
        }
        viewer.FocusTimeDisplay();
    }
}

internal sealed partial class PreviewWindow
{
    internal void EnsureResetAvailable()
    {
        if(!ready||recoveringInterface||handoffInProgress||requestingClose||(View=="reflection"&&!ReflectionOpen))
            throw new InvalidOperationException("Wait for this view to finish opening or saving before resetting the timer.");
    }
    internal Task<bool> ConfirmResetAsync(ResetWarning warning)
    {
        var decision=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        // Open outside WebView's message callback. This dialog has its own
        // taskbar/Alt+Tab entry and focus, including for a hidden/tool owner.
        BeginInvoke(()=>{
            try {
                if(IsDisposed){decision.TrySetResult(false);return;}
                decision.TrySetResult(app.ShowResetConfirmationDialog(this,warning));
            }
            catch(Exception error){decision.TrySetException(error);}
        });
        return decision.Task;
    }
    private async Task ResetFromButtonAsync(System.Text.Json.JsonElement data,string requestId,long requestedAt)
    {
        if(View=="reflection")throw new ArgumentException("Use Ctrl+R to reset from a reflection window.");
        var completed=await app.WithResetConfirmationAsync(this,()=>{
            var result=app.KeepingTimeOnly(()=>app.Session.Execute("reset",data,requestedAt));
            app.Announce(result);return Task.CompletedTask;
        });
        Reply(requestId,cancelled:!completed);
    }
}
