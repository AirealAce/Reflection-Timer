using ReflectionTimer.Desktop;

namespace ReflectionTimer.Accessible;

internal sealed partial class PreviewWindow
{
    private int compactRevision;
    protected override void WndProc(ref Message message)
    {
        // WM_ACTIVATE supplies the previous window for mouse/key activation.
        // No global focus/key hook or window-title logging is needed.
        if(message.Msg==0x0006&&(message.WParam.ToInt64()&0xffff)!=0)app.RememberReturnFocus(message.LParam);
        base.WndProc(ref message);
    }
    internal void SetCompactMode(bool timeOnly,bool focus)
    {
        if(View!="compact")return;
        app.Session.Engine.SetFloatingTimeOnly(timeOnly);
        IsTimeOnly=timeOnly;compactRevision++;
        ApplyTopMost();UpdateCompactTitle();
        focusOnReady=!timeOnly&&focus&&!ready;
        Post(new{type="compactLayout",timeOnly,focus=!timeOnly&&focus,revision=compactRevision});
        if(timeOnly)app.ReleaseFocus(this);
        RestartAutoHide();
    }
    private void UpdateCompactTitle()=>Text=$"Reflection Timer — {(IsTimeOnly?"Time-only":"Compact")} view · {typeof(PreviewWindow).Assembly.GetName().Version?.ToString(3)}";
}
