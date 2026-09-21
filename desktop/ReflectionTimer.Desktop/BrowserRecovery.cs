using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using ReflectionTimer.Desktop;

namespace ReflectionTimer.Accessible;

internal sealed partial class PreviewApplication
{
    private Task<CoreWebView2Environment>? browserEnvironment;
    private TaskCompletionSource browserExited=new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool browserRecovery;
    private readonly Queue<DateTime> browserRestarts=[];
    internal bool BrowserRecoveryInProgress=>browserRecovery;
    internal Task WithPromptLock(Func<Task> action)=>promptCoordinator.ExclusivelyAsync(action);
    internal Task<CoreWebView2Environment> BrowserEnvironmentAsync()=>browserEnvironment??=CreateBrowserEnvironmentAsync();
    private async Task<CoreWebView2Environment> CreateBrowserEnvironmentAsync()
    {
        var exited=browserExited;
        var environment=await CoreWebView2Environment.CreateAsync(null,Path.Combine(ProfileDirectory,"WebView2"));
        environment.BrowserProcessExited+=(_,e)=>{
            exited.TrySetResult();
            if(ReferenceEquals(exited,browserExited)&&e.BrowserProcessExitKind==CoreWebView2BrowserProcessExitKind.Failed)RecoverBrowser();
        };
        return environment;
    }
    internal void RecoverBrowser(bool automatic=true)
    {
        if(closing||MainForm is null||MainForm.IsDisposed)return;
        var affected=windows.Where(w=>!w.IsDisposed).ToArray();
        foreach(var window in affected)window.MarkInterfaceUnavailable();
        if(browserRecovery)return;
        while(browserRestarts.TryPeek(out var at)&&DateTime.UtcNow-at>TimeSpan.FromMinutes(1))browserRestarts.Dequeue();
        if(automatic&&browserRestarts.Count>=3){foreach(var window in affected)window.ShowBrowserRecoveryFailure();return;}
        browserRestarts.Enqueue(DateTime.UtcNow);browserRecovery=true;
        var exited=browserExited;
        // Leave WebView2's event callback before disposing controls. Wait for the
        // environment's exit event: ProcessFailed alone doesn't release resources.
        MainForm.BeginInvoke(async()=>{
            try {
                // A manual retry after an initialization failure can still have
                // live controllers. Release those first so the environment exits.
                if(!automatic&&!exited.Task.IsCompleted)await WithPromptLock(()=>{
                    foreach(var window in windows.Where(w=>!w.IsDisposed).ToArray())window.ResetBrowser();
                    return Task.CompletedTask;
                });
                await exited.Task.WaitAsync(TimeSpan.FromSeconds(15));
                await WithPromptLock(async()=>{
                    if(closing)return;
                    var current=windows.Where(w=>!w.IsDisposed).ToArray();
                    Services.Log.Record("webview.recoveryStarted");
                    foreach(var window in current)window.ResetBrowser();
                    browserExited=new(TaskCreationOptions.RunContinuationsAsynchronously);browserEnvironment=null;
                    await Task.WhenAll(current.Select(w=>w.RestartBrowserAsync()));
                    Services.Log.Record("webview.recovered");
                });
            } catch {
                Services.Log.Record("webview.recoveryFailed");
                foreach(var window in windows.Where(w=>!w.IsDisposed).ToArray())window.ShowBrowserRecoveryFailure();
            } finally {browserRecovery=false;}
        });
    }
}

internal sealed partial class PreviewWindow
{
    private TaskCompletionSource interfaceReady=new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool recoveringInterface,rendererRecovery;
    private readonly Queue<DateTime> rendererRestarts=[];
    private Panel? recoveryPanel;
    private TextBox? recoveryText;
    private Button? retryInterface;
    private Action? retryRecovery;
    private bool settingsShortcutsActive;
    private WebView2 CreateBrowser()
    {
        var control=new WebView2 {Dock=DockStyle.Fill,AccessibleName=View=="main"?"Reflection Timer App view":View=="compact"?"Reflection Timer Compact and Time-only view":"Reflection Timer Session end prompt"};
        var resetPressed=false;var savePressed=false;
        // Preserve the native accelerator workaround on replacement controls too.
        control.KeyDown+=(_,e)=>{
            if(View=="main"&&ready&&settingsShortcutsActive&&e.Modifiers==Keys.Control&&e.KeyCode is Keys.Enter or Keys.S){
                // Native controls/browser accelerators can consume these keys
                // before the page sees them. Use the same save as the DOM route.
                e.Handled=true;
                if(savePressed)return;
                savePressed=true;
                BeginInvoke(()=>{if(!IsDisposed&&ReferenceEquals(browser,control))Post(new{type="settingsSaveShortcut"});});
                return;
            }
            if(e.KeyCode==Keys.R&&e.Modifiers==Keys.Control){
                // WebView handles Ctrl+R before DOM listeners. Defer browser
                // work until its synchronous accelerator callback has returned.
                e.Handled=true;
                if(resetPressed)return;
                resetPressed=true;
                if(ready)BeginInvoke(()=>{if(!IsDisposed&&ReferenceEquals(browser,control))Post(new{type="resetAndReloadShortcut"});});
                return;
            }
            if(View!="main"||!ready||e.KeyCode!=Keys.Tab||(e.Modifiers!=Keys.Control&&e.Modifiers!=(Keys.Control|Keys.Shift)))return;
            var backward=e.Shift;e.Handled=true;e.SuppressKeyPress=true;
            BeginInvoke(()=>Post(new{type="cycleAppTab",backward}));
        };
        control.KeyUp+=(_,e)=>{if(e.KeyCode is Keys.R or Keys.ControlKey)resetPressed=false;if(e.KeyCode is Keys.Enter or Keys.S or Keys.ControlKey)savePressed=false;};
        control.LostFocus+=(_,_)=>{resetPressed=false;savePressed=false;};
        return control;
    }
    private bool resetAndReloadInProgress;
    private async Task ResetAndReloadAsync(string requestId)
    {
        if(resetAndReloadInProgress)throw new InvalidOperationException("The timer is already being reset and refreshed.");
        resetAndReloadInProgress=true;
        var reloading=false;
        try {
            await app.WithPromptLock(async()=>{
                if(!ready||recoveringInterface||handoffInProgress||requestingClose||(View=="reflection"&&!ReflectionOpen))
                    throw new InvalidOperationException("Wait for this view to finish opening or saving before resetting the timer.");
                // Finish the current draft before navigation destroys its DOM.
                // The prompt lock also keeps Prev/Next and handoffs out of this gap.
                await FlushDraftAsync(freeze:true);
                var result=app.KeepingTimeOnly(app.Session.ResetTimerFromShortcut);
                Reply(requestId);
                recoveringInterface=true;ResetReadiness();reloading=true;
                browser.CoreWebView2.Reload();
                await interfaceReady.Task.WaitAsync(TimeSpan.FromSeconds(20));
                recoveringInterface=false;
                Post(new{type="announcement",message=result.Message+" Page refreshed."});
            });
        } catch {
            if(reloading)ShowFailure("The timer was reset, but this page could not be refreshed. Close and reopen this view. Saved drafts are retained.");
            else if(View=="reflection")Post(new{type="resumeReflection"});
            throw;
        } finally {resetAndReloadInProgress=false;}
    }
    private bool IsCurrent(object? core)=>!IsDisposed&&!allowClose&&!browser.IsDisposed&&ReferenceEquals(browser.CoreWebView2,core);
    internal void MarkInterfaceUnavailable()
    {
        if(IsDisposed||allowClose)return;
        ready=false;recoveringInterface=true;
        var error=new IOException("The interface is recovering. Saved drafts are retained.");
        flush?.TrySetException(error);reflectionReady.TrySetException(error);interfaceReady.TrySetException(error);
        if(View=="reflection")reflectionLoadError=error.Message;
        ShowRecoveryMessage("Restoring the interface. Your timer continues; saved drafts and settings are retained.",false);
    }
    internal void ResetBrowser()
    {
        var old=browser;
        browser=CreateBrowser(); // Ignore late events from the old core, including during Dispose.
        Controls.Remove(old);old.Dispose();
        browser.DefaultBackgroundColor=BackColor;browser.Visible=false;Controls.Add(browser);
        initialization=null;ResetReadiness();
    }
    private void ResetReadiness()
    {
        ready=false;settingsShortcutsActive=false;reflectionLoadError=null;
        reflectionReady=new(TaskCreationOptions.RunContinuationsAsynchronously);
        interfaceReady=new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    internal async Task RestartBrowserAsync()
    {
        if(IsDisposed||allowClose)return;
        _=Handle;_=browser.Handle;
        await (initialization??=InitializeAsync());
        await interfaceReady.Task.WaitAsync(TimeSpan.FromSeconds(20));
        FinishInterfaceRecovery();
    }
    private void FinishInterfaceRecovery()
    {
        if(IsDisposed||allowClose)return;
        reflectionLoadError=null;recoveringInterface=false;
        if(recoveryPanel is not null){Controls.Remove(recoveryPanel);recoveryPanel.Dispose();recoveryPanel=null;recoveryText=null;retryInterface=null;}
        browser.Visible=true;browser.BringToFront();
        if(Visible&&WindowActivation.IsForeground(this)){browser.Focus();Post(new{type="announcement",message="Interface restored. Saved drafts and settings have been reloaded."});}
    }
    internal void ShowBrowserRecoveryFailure()
    {
        if(IsDisposed||allowClose)return;
        ready=false;
        if(View=="reflection")reflectionLoadError="The interface needs to be retried before this reflection can be sent or replaced.";
        retryRecovery=()=>app.RecoverBrowser(automatic:false);
        ShowRecoveryMessage("The interface could not be restored. Your timer continues and saved drafts are retained. Choose Retry interface to try again.",true);
    }
    private void ShowRecoveryMessage(string text,bool retry)
    {
        if(IsDisposed||allowClose)return;
        var focus=ContainsFocus&&WindowActivation.IsForeground(this);
        browser.Visible=false;
        if(recoveryPanel is null){
            recoveryPanel=new(){Dock=DockStyle.Fill,BackColor=BackColor};
            recoveryText=new(){Multiline=true,ReadOnly=true,Dock=DockStyle.Fill,AccessibleName="Interface status",Font=new("Segoe UI",12)};
            retryInterface=new(){Text="Retry interface",AccessibleName="Retry interface",Dock=DockStyle.Bottom,Height=44};
            retryInterface.Click+=(_,_)=>retryRecovery?.Invoke();
            recoveryPanel.Controls.Add(recoveryText);recoveryPanel.Controls.Add(retryInterface);Controls.Add(recoveryPanel);
        }
        recoveryText!.Text=text;retryInterface!.Visible=retry;recoveryPanel.BringToFront();
        if(focus){if(retry)retryInterface.Focus();else recoveryText.Focus();}
    }
    private void RecoverRenderer(bool automatic=true)
    {
        if(IsDisposed||allowClose||app.BrowserRecoveryInProgress)return;
        MarkInterfaceUnavailable();
        if(rendererRecovery)return;
        while(rendererRestarts.TryPeek(out var at)&&DateTime.UtcNow-at>TimeSpan.FromMinutes(1))rendererRestarts.Dequeue();
        void Failed(){
            retryRecovery=()=>RecoverRenderer(automatic:false);
            ShowFailure("The interface could not be restored. Saved drafts are retained. Choose Retry interface to try again.");
            retryInterface!.Visible=true;
        }
        if(automatic&&rendererRestarts.Count>=3){Failed();return;}
        rendererRestarts.Enqueue(DateTime.UtcNow);rendererRecovery=true;
        var old=browser;
        BeginInvoke(async()=>{
            try {
                await app.WithPromptLock(async()=>{
                    if(IsDisposed||allowClose||app.BrowserRecoveryInProgress||!ReferenceEquals(old,browser))return;
                    app.Services.Log.Record("webview.recoveryStarted");ResetReadiness();
                    browser.CoreWebView2.Reload();
                    await interfaceReady.Task.WaitAsync(TimeSpan.FromSeconds(20));
                    FinishInterfaceRecovery();app.Services.Log.Record("webview.recovered");
                });
            } catch {
                if(!IsDisposed&&!allowClose&&!app.BrowserRecoveryInProgress){app.Services.Log.Record("webview.recoveryFailed");Failed();}
            } finally {rendererRecovery=false;}
        });
    }
}
