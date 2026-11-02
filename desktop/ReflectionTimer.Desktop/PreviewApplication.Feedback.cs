namespace ReflectionTimer.Accessible;

internal sealed partial class PreviewApplication
{
    private void OnFeedbackThread(Action action)
    {
        var owner=MainForm;
        if(owner is null||owner.IsDisposed||owner.Disposing)return;
        if(owner.InvokeRequired) {
            try { owner.BeginInvoke(()=>OnFeedbackThread(action)); }
            catch(InvalidOperationException) { }
            return;
        }
        action();
    }

    private void DispatchFeedback(Action action,bool supplementary)
        => OnFeedbackThread(()=>dispatchFeedback(()=>OnFeedbackThread(action),supplementary));

    private void DispatchGeneralFeedback(Action action)
        => OnFeedbackThread(()=>dispatchNotice(()=>OnFeedbackThread(action)));

    private void PostFeedback(string message,bool announce,PreviewWindow? source=null)
        => (source is {IsDisposed:false}?source:active is {Visible:true,IsDisposed:false}?active:MainForm as PreviewWindow)
            ?.Post(new {type="announcement",message,announce});

    private async Task DeliverReaderFeedbackAsync(string message,bool supplementary,bool notice=false,PreviewWindow? source=null)
    {
        var owner=MainForm;
        if(owner is null||owner.IsDisposed||owner.Disposing)return;
        bool handled;
        try { handled=await notifyScreenReader(owner,message,supplementary,notice); }
        catch(OperationCanceledException) { return; }
        catch { handled=false; }
        if(!handled)OnFeedbackThread(()=>PostFeedback(message,true,source));
    }
}
