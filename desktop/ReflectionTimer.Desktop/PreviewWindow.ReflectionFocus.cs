using ReflectionTimer.Desktop;

namespace ReflectionTimer.Accessible;

internal sealed partial class PreviewWindow
{
    private System.Windows.Forms.Timer? reflectionFocusPoll;
    private KeyboardReleasedFeedback? reflectionInputFocus;
    private Action? reflectionFocusTick;
    private int reflectionFocusRequestId;

    private bool CanFocusReflectionInput() => View=="reflection" && ReflectionOpen && ready
        && !IsDisposed && !allowClose && !requestingClose && !handoffInProgress && !recoveringInterface
        && browser.Visible && !browser.IsDisposed && WindowActivation.IsForeground(this);

    private void RequestReflectionInputFocus()
    {
        if(!CanFocusReflectionInput())return;
        // Form activation alone does not hand native focus to Chromium. Let
        // WebView2 restore its real focused child before targeting HTML text.
        browser.Focus();
        if(!browser.ContainsFocus)return;
        var requestId=++reflectionFocusRequestId;
        // Establish the field promptly, then arm a refresh which web-side user
        // input/focus changes can invalidate while the chord is still held.
        Post(new{type="prepareReflectionFocus",focusRequestId=requestId});
        if(reflectionFocusPoll is null){
            reflectionFocusPoll=new(){Interval=40};
            reflectionFocusPoll.Tick+=(_,_)=>{
                reflectionFocusPoll.Stop();var callback=reflectionFocusTick;reflectionFocusTick=null;callback?.Invoke();
            };
            Deactivate+=(_,_)=>CancelReflectionInputFocus();
            VisibleChanged+=(_,_)=>{if(!Visible)CancelReflectionInputFocus();};
        }
        reflectionInputFocus??=new(callback=>{reflectionFocusTick=callback;reflectionFocusPoll.Start();},
            ()=>KeyboardReleasedFeedback.ShortcutKeysPressed()||ReflectionSlashPressed(),()=>Environment.TickCount64);
        var requestedBrowser=browser;var requestedPrompt=PromptId;
        reflectionInputFocus.Publish(()=>{
            // An asynchronous renderer/key release must not reclaim focus from
            // another app, a native control, a later prompt, or a replaced core.
            if(!CanFocusReflectionInput()||!ReferenceEquals(browser,requestedBrowser)
                ||PromptId!=requestedPrompt||!browser.ContainsFocus)return;
            browser.Focus();
            Post(new{type="focusReflection",refreshFocus=true,focusRequestId=requestId});
        });
    }

    private void CancelReflectionInputFocus()
    {
        reflectionFocusPoll?.Stop();reflectionFocusTick=null;
        reflectionInputFocus?.Dispose();reflectionInputFocus=null;
    }
    private static bool ReflectionSlashPressed() => (GetReflectionFocusKeyState(0xBF)&0x8000)!=0;
    [System.Runtime.InteropServices.DllImport("user32.dll",EntryPoint="GetAsyncKeyState")]
    private static extern short GetReflectionFocusKeyState(int key);
}
