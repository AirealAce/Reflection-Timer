using ReflectionTimer.Core;

namespace ReflectionTimer.Accessible;

internal sealed record ResetWarning(SessionMode Mode, bool Running, bool HasDraft)
{
    internal static ResetWarning? For(AppState state)
    {
        if(!state.ConfirmBeforeReset)return null;
        var hasDraft=state.Prompts.Any(p=>!string.IsNullOrWhiteSpace(p.Draft)||!string.IsNullOrWhiteSpace(p.EarlyEndReason));
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
    private bool resetInProgress;
    private readonly Func<PreviewWindow,ResetWarning,Task<bool>> confirmReset;
    private void ResetFromGlobalShortcut()
    {
        compactPresses.Reset();
        if(closing||resetInProgress||MainForm is null||MainForm.IsDisposed)return;
        // Leave the registered-hotkey callback before any modal/browser work.
        MainForm.BeginInvoke(async()=>{
            if(closing||resetInProgress)return;
            var owner=windows.FirstOrDefault(w=>!w.IsDisposed&&ReflectionTimer.Desktop.WindowActivation.IsForeground(w))
                ??windows.FirstOrDefault(w=>!w.IsDisposed&&w.Visible&&w.View=="compact")??(PreviewWindow)MainForm;
            try {
                await WithResetConfirmationAsync(owner,()=>{
                    var result=KeepingTimeOnly(Session.ResetTimerFromShortcut);
                    Announce(result.Message);return Task.CompletedTask;
                },requireReady:false);
            } catch(Exception error) {
                Announce(error is ArgumentException or InvalidOperationException?error.Message:"The timer could not be reset. Your saved session and drafts are retained.");
            }
        });
    }
    internal async Task<bool> WithResetConfirmationAsync(PreviewWindow owner,Func<Task> reset,bool requireReady=true)
    {
        // One decision across all windows; repeated buttons/shortcuts cannot
        // queue another reset behind the first confirmation.
        if(resetInProgress)return false;
        resetInProgress=true;var completed=false;
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
                        var current=Session.Engine.Snapshot.Timer;
                        if(current.SessionId!=before.SessionId||current.Mode!=before.Mode)
                            throw new InvalidOperationException("The active session changed while confirmation was open. Review it before resetting again.");
                    }
                    if(closing||owner.IsDisposed)return;
                    await reset();completed=true;
                } finally {foreach(var editor in prepared)editor.ResumeEditing();}
            });
            return completed;
        } finally {resetInProgress=false;}
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
        // A native modal must open after WebView's message callback returns.
        // Windows exposes its text/buttons to screen readers even from Time-only.
        BeginInvoke(()=>{
            try {decision.TrySetResult(!IsDisposed&&MessageBox.Show(this,warning.Message,warning.Title,
                MessageBoxButtons.OKCancel,MessageBoxIcon.Question,MessageBoxDefaultButton.Button2)==DialogResult.OK);}
            catch(Exception error){decision.TrySetException(error);}
        });
        return decision.Task;
    }
    private async Task ResetFromButtonAsync(System.Text.Json.JsonElement data,string requestId,long requestedAt)
    {
        if(View=="reflection")throw new ArgumentException("Use Ctrl+R to reset from a reflection window.");
        var completed=await app.WithResetConfirmationAsync(this,()=>{
            var result=app.KeepingTimeOnly(()=>app.Session.Execute("reset",data,requestedAt));
            app.Announce(result.Message);return Task.CompletedTask;
        });
        Reply(requestId,cancelled:!completed);
    }
}
