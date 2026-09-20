using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using System.Runtime.InteropServices;

namespace ReflectionTimer.Accessible;

internal sealed partial class PreviewWindow : Form, IReflectionPromptWindow, IReflectionShortcutTarget
{
    internal const string Origin = "https://reflection-timer.invalid";
    internal string View { get; }
    internal Guid? PromptId { get; private set; }
    internal bool IsTimeOnly { get; private set; }
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal bool ReflectionOpen { get; set; }
    private readonly PreviewApplication app;
    private WebView2 browser = null!;
    private bool ready, allowClose, requestingClose;
    private Task? initialization;
    private string? reflectionLoadError;
    private TaskCompletionSource reflectionReady=new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool handoffInProgress;
    bool IReflectionShortcutTarget.IsForegroundReflection => View=="reflection" && ReflectionTimer.Desktop.WindowActivation.IsForeground(this);
    void IReflectionShortcutTarget.FocusOrSaveDraft(){if(!handoffInProgress&&!requestingClose)Post(new{type="reflectionShortcut"});}
    void IReflectionShortcutTarget.FocusReflection()=>app.Open("reflection",PromptId);
    Guid IReflectionPromptWindow.ReflectionId => PromptId!.Value;
    async Task IReflectionPromptWindow.PrepareHandoffAsync(){handoffInProgress=true;await FlushDraftAsync(freeze:true);}
    void IReflectionPromptWindow.ResumeEditing(){handoffInProgress=false;Post(new{type="resumeReflection"});}
    void IReflectionPromptWindow.CloseAfterSave()=>CloseAfterSave();
    private bool focusOnReady, selectTimerOnReady;
    private (ReflectionTimer.Core.AppColorTheme Theme,bool Contrast)? appliedTheme;
    private TaskCompletionSource? flush;
    internal PreviewWindow(PreviewApplication app, string view, Guid? prompt)
    {
        this.app = app; View = view; PromptId = prompt; ReflectionOpen=view=="reflection";
        browser=CreateBrowser();
        Icon=Icon.ExtractAssociatedIcon(Environment.ProcessPath!)??SystemIcons.Information;
        browser.AccessibleName=view=="main"?"Reflection Timer App view":view=="compact"?"Reflection Timer Compact and Time-only view":"Reflection Timer Session end prompt";
        Text = view == "main" ? "Reflection Timer — App view · 4.2.3" : view == "compact" ? "Reflection Timer — Compact view · 4.2.3" : "Reflection Timer — Session end · 4.2.3";
        StartPosition = FormStartPosition.Manual; AutoScaleMode = AutoScaleMode.Dpi;
        var state=app.Session.Engine.Snapshot;
        Size = view == "main" ? new(940, 810) : view == "compact" ? new(228, 200) : new(560, state.Prompts.Any(p=>p.Id==prompt&&ReflectionTimer.Core.TimerEngine.ShowEarlyEndReason(p,state.Timer,app.Session.Engine.Now))?525:440);
        MinimumSize = view == "main" ? new(420, 400) : view == "compact" ? new(80,32) : new(420,360);
        if(view=="compact") { FormBorderStyle=FormBorderStyle.None; ShowInTaskbar=false; MaximizeBox=false; MinimizeBox=false; }
        if(view=="reflection") { ShowInTaskbar=false; MinimizeBox=false; }
        var area = Screen.PrimaryScreen!.WorkingArea;
        Location = view == "main" ? ViewPlacement.Calculate(area,Size,1)
            : new(view == "compact" ? area.Left + 16 : Math.Max(area.Left, area.Right - Width - 16), Math.Max(area.Top, area.Bottom - Height - 16));
        ApplyTopMost();
        // Do not expose the HTML's dark/default controls during a cold start.
        // The native background already uses the saved theme; the browser is
        // revealed only after that document has applied its saved preferences.
        browser.Visible=false;
        Controls.Add(browser);
        HandleCreated+=(_,_)=>ApplyWindowTheme();
        Shown += async (_, _) => {
            await (initialization??=InitializeAsync());
            if(View=="reflection"||IsDisposed)return;
            try {await interfaceReady.Task.WaitAsync(TimeSpan.FromSeconds(20));}
            catch {if(!IsDisposed&&!recoveringInterface)ShowFailure("Saved settings could not be loaded into the interface. Close and reopen the app. Your saved data has not been reset.");}
        };
        ResizeEnd+=(_,_)=>{if(View=="compact")try{app.Session.Engine.SetFloatingTimerPosition(Left,Top);}catch{app.Announce("Could not save the compact position.");}};
        FormClosing += async (_, e) => {
            if (allowClose) return;
            e.Cancel = true;
            if (View == "main") { Hide(); return; }
            if(handoffInProgress)return;
            if (requestingClose) return;
            requestingClose = true;
            try { await FlushDraftAsync(); if(View=="compact") app.Session.Engine.SetFloatingTimer(false); CloseAfterSave(); }
            catch { Post(new { type = View=="reflection"?"reflectionCloseFailed":"announcement", message = "Draft could not be saved. This window is staying open. Try again." }); }
            finally { requestingClose = false; }
        };
    }
    protected override bool ShowWithoutActivation => View is "compact" or "reflection";
    protected override CreateParams CreateParams {get{var value=base.CreateParams;if(View is "compact" or "reflection")value.ExStyle=(value.ExStyle|0x80)&~0x40000;return value;}}
    internal void ApplyTopMost()
    {
        var state=app.Session.Engine.Snapshot;
        var top=View=="reflection"?state.PromptAlwaysOnTop:View=="compact"&&(IsTimeOnly?state.TimeOnlyAlwaysOnTop:state.CompactAlwaysOnTop);
        if(TopMost!=top)TopMost=top;
    }
    internal void FocusControls(bool timerPage=false){if(!ready){focusOnReady=true;selectTimerOnReady=timerPage;return;}Post(new{type=View=="compact"?"expandCompact":View=="reflection"?"focusReflection":"focusTimer",selectTimer=timerPage});}
    internal async Task PrepareReflectionAsync()
    {
        if(View!="reflection"||IsDisposed)return;
        if(reflectionLoadError is {} previousError)throw new InvalidOperationException(previousError);
        // Load the themed editor and its saved text before exposing the native
        // window. Reopening no longer flashes an empty/default-colored WebView.
        _=Handle;_=browser.Handle;
        await (initialization??=InitializeAsync());
        try {await reflectionReady.Task.WaitAsync(TimeSpan.FromSeconds(15));}
        catch(TimeoutException error){throw new InvalidOperationException("The reflection editor did not finish loading. Your current draft is retained; try opening the pending reflection again.",error);}
        if(reflectionLoadError is {} loadError)throw new InvalidOperationException(loadError);
    }
    internal async Task SwitchReflectionAsync(Guid id)
    {
        if(View!="reflection"||!ready||reflectionLoadError is not null)throw new InvalidOperationException("This reflection editor is unavailable. Close and reopen it to recover the saved draft.");
        var previous=PromptId;
        async Task Load(Guid target) {
            PromptId=target;reflectionReady=new(TaskCreationOptions.RunContinuationsAsynchronously);
            Post(new{type="showReflection",promptId=target,state=app.Session.View()});
            await reflectionReady.Task.WaitAsync(TimeSpan.FromSeconds(15));
            if(reflectionLoadError is {} error)throw new InvalidOperationException(error);
        }
        try {await Load(id);}
        catch {
            // Both fields were flushed before navigation. Restore the old saved
            // editor if loading fails; never submit or erase either draft.
            if(previous is {} old){
                PromptId=old;
                if(ready&&!IsDisposed)try {await Load(old);}catch {ShowFailure("The reflection could not be restored. Close and reopen it. Saved drafts are retained.");}
            }
            throw;
        }
    }
    internal void ApplyWindowTheme()
    {
        if(!IsHandleCreated)return;var theme=app.Session.Engine.Snapshot.Theme;var contrast=SystemInformation.HighContrast;
        if(appliedTheme==(theme,contrast))return;appliedTheme=(theme,contrast);
        var colors=PreviewTheme.Palette(theme,contrast);
        BackColor=colors.Background;browser.DefaultBackgroundColor=BackColor;
        int dark=!contrast&&(int)theme is 0 or 2?1:0,caption=contrast?-1:ColorTranslator.ToWin32(colors.Raised),text=contrast?-1:ColorTranslator.ToWin32(colors.Text),border=contrast?-1:ColorTranslator.ToWin32(colors.Border);
        DwmSetWindowAttribute(Handle,20,ref dark,4);DwmSetWindowAttribute(Handle,35,ref caption,4);DwmSetWindowAttribute(Handle,36,ref text,4);DwmSetWindowAttribute(Handle,34,ref border,4);
    }
    internal void ApplyPosition()
    {
        if(View=="main") return;
        var state=app.Session.Engine.Snapshot; var area=View=="reflection"?Screen.FromPoint(Cursor.Position).WorkingArea:Screen.FromControl(app.MainForm!).WorkingArea;
        if(View=="compact"&&state.FloatingPlacement==ReflectionTimer.Core.FloatingTimerPlacement.Custom&&state.FloatingTimerLeft is {} left&&state.FloatingTimerTop is {} top){area=Screen.FromPoint(new(left,top)).WorkingArea;Location=new(Math.Clamp(left,area.Left,Math.Max(area.Left,area.Right-Width)),Math.Clamp(top,area.Top,Math.Max(area.Top,area.Bottom-Height)));return;}
        var position=View=="compact" ? (int)state.FloatingPlacement : state.PopupPosition switch {
            ReflectionTimer.Core.ReflectionPopupPosition.TopLeft=>2, ReflectionTimer.Core.ReflectionPopupPosition.TopRight=>3,
            ReflectionTimer.Core.ReflectionPopupPosition.BottomLeft=>4, ReflectionTimer.Core.ReflectionPopupPosition.BottomRight=>5, _=>1 };
        Location=ViewPlacement.Calculate(area,Size,position);
    }
    private async Task InitializeAsync()
    {
        var initializingBrowser=browser;
        try {
            var environment = await app.BrowserEnvironmentAsync();
            if (IsDisposed||!ReferenceEquals(initializingBrowser,browser)) return;
            await initializingBrowser.EnsureCoreWebView2Async(environment);
            if (IsDisposed||!ReferenceEquals(initializingBrowser,browser)) return;
            var core = browser.CoreWebView2;
            core.Settings.AreHostObjectsAllowed = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsWebMessageEnabled = true;
            core.Settings.IsZoomControlEnabled = true;
            core.Settings.AreBrowserAcceleratorKeysEnabled = true;
            core.SetVirtualHostNameToFolderMapping("reflection-timer.invalid", Path.Combine(AppContext.BaseDirectory, "Web"), CoreWebView2HostResourceAccessKind.DenyCors);
            core.NavigationStarting += (_, e) => { if (!Allowed(e.Uri)) e.Cancel = true; };
            core.NewWindowRequested += (_, e) => e.Handled = true;
            core.DownloadStarting += (_, e) => e.Cancel = true;
            core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
            browser.ZoomFactorChanged+=(_,_)=>{if(View=="compact")Post(new{type="measureCompact"});};
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += (_, e) => {
                if (!Allowed(e.Request.Uri)) e.Response = environment.CreateWebResourceResponse(null, 403, "Forbidden", "Content-Type: text/plain");
            };
            core.WebMessageReceived += Receive;
            core.NavigationCompleted += (_, e) => { if (IsCurrent(core)&&!e.IsSuccess) ShowFailure("The local interface could not be loaded. Close and reopen the app."); };
            core.ProcessFailed += (_, e) => {if(IsCurrent(core))ProcessFailure(e.ProcessFailedKind,e.Reason,e.ExitCode);};
            core.Navigate(Origin + (View=="compact" ? "/compact.html" : "/index.html?view=" + View));
        }
        catch (WebView2RuntimeNotFoundException) { if(ReferenceEquals(initializingBrowser,browser))ShowFailure("Microsoft Edge WebView2 Runtime is required. Install the Evergreen Runtime from https://developer.microsoft.com/microsoft-edge/webview2/ and reopen the app."); }
        catch { if(ReferenceEquals(initializingBrowser,browser))ShowFailure("The local web interface could not start. Close and reopen the app. Your saved data is retained."); }
    }
    internal static bool Allowed(string address) => Uri.TryCreate(address, UriKind.Absolute, out var uri) && uri.Scheme == "https" && uri.Host == "reflection-timer.invalid"
        && uri.IsDefaultPort && uri.UserInfo.Length == 0 && uri.AbsolutePath is "/index.html" or "/app.js" or "/app.css" or "/ui.js" or "/settings.js" or "/audio.js" or "/setup.js" or "/low-time.js" or "/time-reached.js" or "/compact.html" or "/compact.js" or "/compact.css" or "/layout.js" or "/themes.css" or "/themes.js";
    internal void ProcessFailure(CoreWebView2ProcessFailedKind kind,CoreWebView2ProcessFailedReason reason,int exitCode)
    {
        if(IsDisposed||allowClose)return;
        app.Services.Log.Record("webview.processFailed",value:(int)kind);
        app.Services.Log.Record("webview.failureReason",value:(int)reason);
        app.Services.Log.Record("webview.exitCode",value:exitCode);
        // GPU/utility processes recover automatically. A busy renderer can also
        // recover; do not hide its still-live editor or discard unsaved text.
        if(kind is not (CoreWebView2ProcessFailedKind.BrowserProcessExited or CoreWebView2ProcessFailedKind.RenderProcessExited))return;
        if(kind==CoreWebView2ProcessFailedKind.BrowserProcessExited)app.RecoverBrowser();
        else RecoverRenderer();
    }
    private void ShowFailure(string text)
    {
        if (IsDisposed) return;
        if(View=="reflection")reflectionLoadError=text;
        ShowRecoveryMessage(text,false);
        interfaceReady.TrySetException(new IOException(text));
        reflectionReady.TrySetResult();
    }
    private async void Receive(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        var requestedAt=app.Session.Engine.Now;
        if (!IsCurrent(sender)||!Allowed(e.Source)) return;
        string? requestId = null;
        try {
            if (e.WebMessageAsJson.Length > 40000) throw new ArgumentException("Message too large.");
            using var document = JsonDocument.Parse(e.WebMessageAsJson);
            var root = document.RootElement;
            requestId = root.GetProperty("requestId").GetString();
            if (requestId is null || requestId.Length > 64) throw new ArgumentException("Invalid request.");
            var action = root.GetProperty("action").GetString() ?? "";
            if(handoffInProgress && action is "queue" or "reflectionSendStarted" or "saveForLater" or "saveOrSendReflection" or "skip" or "close" or "navigateReflection")throw new InvalidOperationException("This reflection is being saved before another prompt opens.");
            var data = root.GetProperty("data");
            if (action == "ready") {
                ready = true; Post(new { type = "init", view = View, promptId = PromptId, state = app.Session.View(), appViewVisible = app.AppViewVisible, timeOnly = recoveringInterface ? (bool?)IsTimeOnly : null });
                if(View=="main" && app.RecoveryNotice is { } notice) { Post(new { type="announcement", message=notice }); app.RecoveryNotice=null; }
                if(focusOnReady){focusOnReady=false;FocusControls(selectTimerOnReady);}
                if(View=="main"&&!app.StartInTray&&!recoveringInterface)ReflectionTimer.Desktop.WindowActivation.Focus(this);
                Reply(requestId); return;
            }
            if(action=="interfaceReady") {
                if(!recoveringInterface&&View!="reflection")browser.Visible=true;
                interfaceReady.TrySetResult();Reply(requestId);return;
            }
            if (action == "flushed") { flush?.TrySetResult(); Reply(requestId); return; }
            if (action == "resetAndReload") { await ResetAndReloadAsync(requestId); return; }
            if (action == "reflectionReady") {
                if(View!="reflection")throw new ArgumentException("Only a reflection can finish loading its editor.");
                if(!data.TryGetProperty("id",out var loaded)||!loaded.TryGetGuid(out var loadedId)||loadedId!=PromptId)throw new ArgumentException("This reflection load is no longer current.");
                browser.Visible=true;Reply(requestId);reflectionReady.TrySetResult();interfaceReady.TrySetResult();return;
            }
            if(action=="reflectionLoadFailed") {
                if(View!="reflection"||!data.TryGetProperty("id",out var failed)||!failed.TryGetGuid(out var failedId)||failedId!=PromptId)throw new ArgumentException("This reflection load is no longer current.");
                Reply(requestId);reflectionReady.TrySetException(new InvalidOperationException("The requested reflection could not be loaded."));return;
            }
            if (action == "flushFailed") { flush?.TrySetException(new IOException("Draft save failed.")); Reply(requestId); return; }
            if(View=="reflection"&&!ReflectionOpen&&(action is "draft" or "reflectionSendStarted" or "saveForLater" or "saveOrSendReflection" or "queue" or "skip" or "navigateReflection" or "close"))throw new InvalidOperationException("This reflection is closed. Reopen it before editing.");
            if (action == "compact") { app.Open("compact"); Reply(requestId); return; }
            if(action=="toggleCompact") { app.ToggleCompactVisibility(data.TryGetProperty("expandOnShow",out var expandOnShow)&&expandOnShow.ValueKind==JsonValueKind.True);Reply(requestId);return; }
            if(action=="quit") { if(View!="main")throw new ArgumentException("Quit from the App view.");Reply(requestId);await app.CloseMainAsync();return; }
            if(action=="durationDraft") {
                if(View=="reflection")throw new ArgumentException("Edit duration in the App or Compact view.");
                var parts=data.GetProperty("parts").EnumerateArray().Select(x=>x.GetString()??"").ToArray();
                app.Session.SetDurationDraft(parts);Reply(requestId);return;
            }
            if(action=="dragCompact") {
                if(View!="compact")throw new ArgumentException("Only the compact window can be dragged here.");
                Reply(requestId);ReleaseCapture();SendMessage(Handle,0x00A1,2,0);return;
            }
            if (action == "compactSize") {
                if(View!="compact") throw new ArgumentException("Only the compact window can request this size.");
                var width=ReadInt(data,"width",80,700);var height=ReadInt(data,"height",32,1000);
                IsTimeOnly=ReadFlag(data,"tiny");
                ApplyTopMost();
                Text="Reflection Timer — "+(IsTimeOnly?"Time-only":"Compact")+" view · 4.2.3";
                ClientSize=new((int)Math.Ceiling(width*DeviceDpi/96d*browser.ZoomFactor),(int)Math.Ceiling(height*DeviceDpi/96d*browser.ZoomFactor));
                ApplyPosition();Reply(requestId);return;
            }
            if (action == "main") { app.Open("main"); Reply(requestId); return; }
            if(action=="navigateReflection") {
                if(View!="reflection"||PromptId is not {} from)throw new ArgumentException("Navigate from a reflection window.");
                var direction=ReadInt(data,"direction",-1,1);
                await app.NavigateReflectionAsync(from,direction);if(IsCurrent(sender))Reply(requestId);return;
            }
            if (action == "close") { Reply(requestId); Close(); return; }
            if (action == "readTime") {
                var clock = app.Session.Clock(); Post(new { type = "timeRead", clock }); Reply(requestId); return;
            }
            if (await HandleSettings(action, data, requestId)) return;
            // A reflection window can edit only its own draft. Main/compact cannot submit drafts.
            if (action is "draft" or "reflectionSendStarted" or "saveForLater" or "saveOrSendReflection" or "queue" or "skip") {
                if (PromptId is null || data.GetProperty("id").GetGuid() != PromptId) throw new ArgumentException("This window cannot edit that reflection.");
            } else if (View == "reflection") throw new ArgumentException("That action is unavailable in a reflection window.");
            if(action=="reflectionSendStarted") { app.Services.ReflectionSendStarted(PromptId!.Value);Reply(requestId);return; }
            var result = View=="compact"&&action=="toggle"&&ReadFlag(data,"keepTimeOnly")
                ? app.KeepingTimeOnly(()=>app.Session.Execute(action,data,requestedAt))
                : app.Session.Execute(action, data,requestedAt);
            Reply(requestId);
            if (result.Close) {
                CloseAfterSave(); app.Announce(result.Message);
                if(result.SessionCompleted&&PromptId is {} submittedId)await app.CompleteSubmittedSessionAsync(submittedId);
            }
            else if (result.OpenReflection is { } prompt) app.Open("reflection", prompt,sessionCompleted:result.SessionCompleted);
            else app.Announce(result.Message);
        }
        catch (Exception error) {
            var message = error is ArgumentException or InvalidOperationException ? error.Message : "The change could not be saved. Review its current values and try again.";
            if (requestId is not null&&IsCurrent(sender)) Post(new { type = "reply", requestId, error = message });
        }
    }
    private void Reply(string requestId) => Post(new { type = "reply", requestId });
    internal void Post(object message)
    {
        if (!ready||IsDisposed||browser.IsDisposed)return;
        try {browser.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(message, PreviewSession.Json));}
        catch(Exception error) when(error is InvalidOperationException or COMException) {RecoverRenderer();}
    }
    internal async Task FlushDraftAsync(bool freeze=false)
    {
        if(freeze&&reflectionLoadError is not null)throw new InvalidOperationException("This editor is unavailable, so its reflection was not sent or replaced. Close and reopen it to recover the saved draft.");
        if(flush is {} pending)await pending.Task.WaitAsync(TimeSpan.FromSeconds(10));
        if (View != "reflection" || !ReflectionOpen || !ready || IsDisposed) return;
        var request = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        flush = request;
        Post(new { type = "flush",freeze });
        try { await request.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
        finally { if(ReferenceEquals(flush,request))flush = null; }
    }
    // Keep each viewer's browser and accessibility objects alive between uses.
    // A hidden reflection is no longer an editor: it cannot be flushed/submitted.
    internal void CloseAfterSave() { ReflectionOpen=false;handoffInProgress=false;Hide(); }
    internal void ClosePermanently() { allowClose = true; Close(); }
    protected override void Dispose(bool disposing) { if (disposing) browser.Dispose(); base.Dispose(disposing); }
    [DllImport("user32.dll")]private static extern bool ReleaseCapture();
    [DllImport("user32.dll",EntryPoint="SendMessageW")]private static extern nint SendMessage(nint window,int message,nint wParam,nint lParam);
    [DllImport("dwmapi.dll")]private static extern int DwmSetWindowAttribute(nint window,int attribute,ref int value,int size);
}
