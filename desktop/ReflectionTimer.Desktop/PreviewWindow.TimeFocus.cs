using ReflectionTimer.Desktop;

namespace ReflectionTimer.Accessible;

internal sealed partial class PreviewWindow
{
    private bool focusTimeOnReady,timeFocusEvents;
    internal void FocusTimeDisplay()
    {
        if(IsDisposed||View is not ("main" or "compact"))return;
        if(!timeFocusEvents){
            timeFocusEvents=true;
            Deactivate+=(_,_)=>focusTimeOnReady=false;
            VisibleChanged+=(_,_)=>{if(!Visible)focusTimeOnReady=false;};
        }
        focusOnReady=false;selectTimerOnReady=false;
        focusTimeOnReady=true;CompleteTimeDisplayFocus();
    }
    private void CompleteTimeDisplayFocus()
    {
        if(!focusTimeOnReady||!ready||!interfaceReady.Task.IsCompletedSuccessfully)return;
        focusTimeOnReady=false;
        if(IsDisposed||!Visible||recoveringInterface||!WindowActivation.IsForeground(this)||!WindowActivation.CanReceiveFocus(this))return;
        browser.Focus();
        if(browser.ContainsFocus)Post(new{type="focusTime"});
    }
}
