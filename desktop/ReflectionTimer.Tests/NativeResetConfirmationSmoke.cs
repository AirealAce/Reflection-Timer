using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Web.WebView2.WinForms;
using ReflectionTimer.Accessible;
using ReflectionTimer.Core;
using ReflectionTimer.Desktop;

// Real WebView commands, draft flushing and reloads; synthetic data and decisions.
static class NativeResetConfirmationSmoke
{
    internal static void RunInteractive(bool focusOnly=false)
    {
        Exception? failure=null;
        var thread=new Thread(()=>{
            Application.EnableVisualStyles();
            using var launcher=new Form{Text="Synthetic reset clock focus checks",ClientSize=new(420,90),StartPosition=FormStartPosition.CenterScreen};
            launcher.Shown+=(_,_)=>WindowActivation.Focus(launcher);
            var run=new Button{Text="Run reset clock focus checks",Dock=DockStyle.Fill};
            run.Click+=(_,_)=>{try{Run(focusOnly);}catch(Exception error){failure=error;}finally{launcher.Close();}};
            launcher.Controls.Add(run);Application.Run(launcher);
        });
        thread.SetApartmentState(ApartmentState.STA);thread.Start();thread.Join();
        if(failure is not null)throw new Exception("Interactive reset clock focus checks failed",failure);
    }
    internal static void Run(bool focusOnly=false)
    {
        Exception? failure=null;var passed=0;
        void Check(bool value,string message){if(!value)throw new Exception(message);passed++;Console.WriteLine("PASS "+message);}
        var thread=new Thread(()=>{
            PreviewApplication? app=null;
            try {
                Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);Application.EnableVisualStyles();
                var now=DateTimeOffset.Now;
                var store=new MemoryStore{State=new(){LoggingEnabled=false,ShowFloatingTimer=false,AutoSendIncompleteReflections=false,Timer=new(){Volume=0}}};
                var session=new PreviewSession(store,()=>now,isolatedProfile:true);
                TaskCompletionSource<bool>? decision=null;ResetWarning? warning=null;var asked=0;var nativeDecision=false;
                app=new(session,Path.Combine(Path.GetTempPath(),"ReflectionTimer-ResetConfirm-"+Guid.NewGuid().ToString("N")),startInTray:true,profileName:"reset-confirm-smoke",shortcutRegistration:new Registration(),
                    resetConfirmation:(owner,value)=>{warning=value;asked++;if(nativeDecision)return owner.ConfirmResetAsync(value);decision=new(TaskCreationOptions.RunContinuationsAsynchronously);return decision.Task;});
                ((System.Windows.Forms.Timer)typeof(PreviewApplication).GetField("pulse",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(app)!).Stop();
                _=app.MainForm!.Handle;
                app.MainForm.BeginInvoke(async()=>{
                    try {
                        app.MainForm.Hide();
                        session.Engine.Start(900,false,0);var coldSession=session.Engine.Snapshot.Timer.SessionId;
                        GlobalReset(app);await Until(()=>Task.FromResult(asked==1));
                        Check(!app.MainForm.Visible&&session.Engine.Snapshot.Timer.SessionId==coldSession,"Global reset can ask from a cold tray launch without opening App view");
                        decision!.SetResult(false);await Idle(app);
                        Check(session.Engine.Snapshot.Timer.IsRunning&&!app.MainForm.Visible,"Cancelling a global reset retains the running session and hidden views");
                        GlobalReset(app);await Until(()=>Task.FromResult(asked==2));decision!.SetResult(true);await Idle(app);
                        Check(session.Engine.Snapshot.Timer.SessionId is null&&app.MainForm.Visible,"Confirmed global reset opens the timer view when its original owner was hidden");
                        app.Open("main");app.Open("compact");
                        var windows=(List<PreviewWindow>)typeof(PreviewApplication).GetField("windows",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(app)!;
                        var main=windows.Single(w=>w.View=="main");var compact=windows.Single(w=>w.View=="compact");
                        await Ready(main);await Ready(compact);
                        await Script(main,"window.resetFocusTrace=[];document.addEventListener('focusin',e=>resetFocusTrace.push({event:'focusin',id:e.target.id,focused:document.hasFocus()}));window.chrome.webview.addEventListener('message',e=>{if(e.data.type==='focusTime'||e.data.type==='focusTimer')resetFocusTrace.push({event:e.data.type,focused:document.hasFocus(),active:document.activeElement?.id,tab:document.body.dataset.tab,dialog:!!document.querySelector('dialog[open]')})});");
                        session.Engine.Start(900,false,0);WindowActivation.Focus(main);
                        Console.WriteLine("FOCUSSETUP "+JsonSerializer.Serialize(new{MainForeground=WindowActivation.IsForeground(main),MainFocus=main.ContainsFocus,BrowserFocus=Browser(main).ContainsFocus}));
                        var resetCount=0;session.Engine.ActivityRecorded+=activity=>{if(activity.Event=="timer.reset")resetCount++;};
                        nativeDecision=true;var nativeAsked=asked;var originalSession=session.Engine.CurrentTimer.SessionId;
                        using(var confirmPoll=new System.Windows.Forms.Timer{Interval=25}){
                            var observed=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                            confirmPoll.Tick+=(_,_)=>{
                                var dialog=Application.OpenForms.OfType<ResetConfirmationDialog>().FirstOrDefault(d=>d.Visible);
                                if(dialog is null)return;confirmPoll.Stop();
                                try{
                                    GlobalReset(app,TimeSpan.FromSeconds(1));
                                    Check(dialog.Visible&&dialog.DialogResult==DialogResult.None&&session.Engine.CurrentTimer.SessionId==originalSession,
                                        "A queued reset key from before the warning appeared cannot silently approve it");
                                    GlobalReset(app);GlobalReset(app);observed.TrySetResult();
                                }catch(Exception error){dialog.Close();observed.TrySetException(error);}
                            };
                            confirmPoll.Start();GlobalReset(app);GlobalReset(app);
                            await observed.Task.WaitAsync(TimeSpan.FromSeconds(20));await Idle(app);
                        }
                        Check(asked==nativeAsked+1&&resetCount==1&&session.Engine.CurrentTimer.SessionId is null&&!session.Engine.CurrentTimer.IsRunning,
                            "A second Ctrl+Alt+R confirms the displayed native warning and resets exactly once; queued duplicates do not stack warnings");
                        await ClockFocus(main,"visual-clock");
                        Check(Browser(main).ContainsFocus,"Accepted native confirmation returns keyboard focus to the App clock");
                        if(focusOnly)return;
                        nativeDecision=false;
                        foreach(var mode in new[]{SessionMode.Timer,SessionMode.Stopwatch}) {
                            session.Engine.SwitchMode(mode);
                            foreach(var route in new[]{"App button","App playback","Compact button","App Ctrl+R","Time-only Ctrl+R","Global Ctrl+Alt+R"}) {
                                if(mode==SessionMode.Timer)session.Engine.Start(900,false,0);else session.Engine.StartStopwatch();
                                now=now.AddSeconds(25);
                                var tiny=route is "Time-only Ctrl+R" or "Global Ctrl+Alt+R";
                                compact.Post(new{type=tiny?"shrinkCompact":"expandCompact"});
                                await Until(()=>Task.FromResult(compact.IsTimeOnly==tiny));
                                var target=route.StartsWith("App")?main:compact;
                                var reload=route.Contains("Ctrl+R");
                                var before=JsonSerializer.Serialize(session.Engine.Snapshot.Timer);var prior=asked;
                                await Script(target,"window.resetConfirmMarker=true");
                                async Task Invoke(){if(route=="Global Ctrl+Alt+R")GlobalReset(app);else if(reload)NativeReset(target);else await Script(target,"document.querySelector('"+(route=="App playback"?"#playback-reset":"#reset")+"').click()");}
                                await Invoke();await Until(()=>Task.FromResult(asked>prior));
                                Check(warning is {Running:true,HasDraft:false}&&warning.Mode==mode&&JsonSerializer.Serialize(session.Engine.Snapshot.Timer)==before,route+" asks before changing a running "+mode);
                                // Neither another window nor another shortcut may stack decisions.
                                await Script(target==main?compact:main,"document.querySelector('#reset').click()");
                                await Script(target,"Promise.resolve()");
                                Check(asked==prior+1,route+" keeps one reset confirmation across windows");
                                decision!.SetResult(false);await Idle(app);
                                if(reload)await Until(async()=>await Bool(target,"sessionStorage.getItem('timer-reset-reload-view')===null"));
                                Check(JsonSerializer.Serialize(session.Engine.Snapshot.Timer)==before&&await Bool(target,"window.resetConfirmMarker===true"),route+" Cancel preserves the "+mode+" and does not reload");
                                prior=asked;await Invoke();await Until(()=>Task.FromResult(asked>prior));decision!.SetResult(true);await Idle(app);
                                Check(!session.Engine.Snapshot.Timer.IsRunning&&session.Engine.Snapshot.Timer.SessionId is null&&
                                    (mode==SessionMode.Stopwatch?session.Engine.Snapshot.Timer.ElapsedMilliseconds==0:session.Engine.Snapshot.Timer.RemainingSeconds==900),route+" confirmation performs the normal "+mode+" reset");
                                Check(await Bool(target,"window.resetConfirmMarker"+(reload?"===undefined":"===true")),route+" reloads only when requested");
                                await ClockFocus(target,target==main?"visual-clock":"read-time");
                                Check(Browser(target).ContainsFocus,route+" accepted confirmation focuses the "+mode+" time display after any requested reload");
                                if(tiny)Check(compact.IsTimeOnly,"Confirmed reset retains Time-only view");
                            }
                        }
                        session.Engine.SwitchMode(SessionMode.Timer);session.Engine.Start(900,false,0);
                        var id=session.Engine.CheckIn();session.Engine.Pause();app.Open("reflection",id);
                        await Until(()=>Task.FromResult(windows.Any(w=>w.ReflectionOpen&&w.Visible)));
                        var reflection=windows.Single(w=>w.ReflectionOpen);await Ready(reflection);
                        // No input event/autosave: only the shared reset gate can see these edits.
                        await Script(reflection,"document.querySelector('#reflection-text').value='Immediate unsent text';document.querySelector('#early-reason').value='Immediate reason';window.resetConfirmMarker=true");
                        var count=asked;await Script(main,"window.resetConfirmMarker=true");NativeReset(main);await IdleAfterRequest(app,main);
                        Check(asked==count&&session.Engine.Snapshot.Timer.SessionId is null&&session.Engine.Snapshot.Prompts.Single(p=>p.Id==id).Draft=="Immediate unsent text",
                            "A paused timer with unblurred reflection text resets without warning and flushes the draft first");
                        await Until(async()=>await Bool(reflection,"!document.querySelector('#reflection-text').readOnly"));
                        Check(!session.Engine.CurrentTimer.IsRunning&&await Bool(reflection,"document.querySelector('#early-reason').value==='Immediate reason'"),"A paused no-warning reset keeps both reflection fields and stays stopped");
                        count=asked;NativeReset(reflection);await Until(()=>Task.FromResult(asked>count));decision!.SetResult(true);await Idle(app);
                        await Until(async()=>await Bool(reflection,"!document.querySelector('#reflection-text').readOnly"));
                        Check(await Bool(reflection,"window.resetConfirmMarker===undefined&&document.querySelector('#reflection-text').value==='Immediate unsent text'&&!document.querySelector('#reflection-text').readOnly")&&session.Engine.Snapshot.Outbox.Count==0,"Session-end Ctrl+R confirms, refreshes, and retains the unsent draft without sending");
                        await Script(reflection,"document.querySelector('#reflection-text').value='';document.querySelector('#early-reason').value='Reason only'");
                        count=asked;NativeReset(main);await Until(()=>Task.FromResult(asked>count));
                        Check(warning is {Running:false,HasDraft:true},"Reason-only unsent text asks even when the timer is ready");decision!.SetResult(false);await Idle(app);
                        await Until(async()=>await Bool(main,"sessionStorage.getItem('timer-reset-reload-view')===null"));
                        await Script(reflection,"document.querySelector('#reflection-text').value='';document.querySelector('#early-reason').value=''");
                        count=asked;await Script(main,"window.resetConfirmMarker=true");NativeReset(main);await IdleAfterRequest(app,main);
                        Check(asked==count,"Empty reflection boxes with an idle timer do not ask");
                        await Script(reflection,"document.querySelector('#reflection-text').value='Saved closed draft'");await reflection.FlushDraftAsync();reflection.CloseAfterSave();
                        count=asked;NativeReset(main);await Until(()=>Task.FromResult(asked>count));
                        Check(warning is {HasDraft:true},"A closed unsent draft also asks for confirmation");decision!.SetResult(false);await Idle(app);
                        await Until(async()=>await Bool(main,"sessionStorage.getItem('timer-reset-reload-view')===null"));
                        session.Engine.Start(900,false,0);count=asked;NativeReset(main);await Until(()=>Task.FromResult(asked>count));
                        session.Engine.Reset();session.Engine.Start(300,false,0);var replacement=session.Engine.Snapshot.Timer.SessionId;
                        decision!.SetResult(true);await Idle(app);
                        await Until(async()=>await Bool(main,"document.querySelector('#error').textContent.includes('active session changed')"));
                        Check(session.Engine.Snapshot.Timer.SessionId==replacement&&session.Engine.Snapshot.Timer.IsRunning,"A confirmation cannot reset a different session that started while it was open");
                        await Script(main,"document.querySelector('#tab-settings').click();document.querySelector('#confirmBeforeReset').click()");
                        await Until(()=>Task.FromResult(!store.State.ConfirmBeforeReset));
                        Check(!session.Engine.Snapshot.ConfirmBeforeReset,"Settings checkbox persists the reset confirmation opt-out immediately");
                        count=asked;await Script(main,"window.resetConfirmMarker=true");NativeReset(main);await IdleAfterRequest(app,main);
                        Check(asked==count&&!session.Engine.Snapshot.Timer.IsRunning&&session.Engine.Snapshot.Prompts.Single(p=>p.Id==id).Draft=="Saved closed draft","Disabled confirmation resets running sessions with drafts without asking or losing text");
                        Check(await Bool(main,"document.body.dataset.tab==='settings'&&!document.querySelector('#confirmBeforeReset').checked"),"Opt-out and current Settings tab survive Ctrl+R reload");
                        session.Engine.Start(300,false,0);session.SetDurationDraft(["0","2","3"]);main.Hide();compact.Hide();
                        count=asked;GlobalReset(app);await Until(()=>Task.FromResult(session.Engine.Snapshot.Timer.SessionId is null));await Idle(app);
                        Check(asked==count&&session.Engine.Snapshot.Timer.RemainingSeconds==123&&!main.Visible&&!compact.Visible,"Global reset uses shared duration inputs and keeps hidden windows hidden");
                        session.Engine.SetViewerAutoHide(true,1);session.Engine.SetConfirmBeforeReset(true);
                        compact.SetCompactMode(true,false);session.Engine.SetFloatingTimer(false);app.ApplyDisplayPreferences();
                        session.Engine.Start(300,false,0);session.SetDurationDraft(["0","2","3"]);var hiddenSession=session.Engine.CurrentTimer.SessionId;
                        count=asked;GlobalReset(app);await Until(()=>Task.FromResult(asked>count));decision!.SetResult(false);await Idle(app);
                        Check(!main.Visible&&!compact.Visible&&!session.Engine.SettingsSnapshot.ShowFloatingTimer
                            &&session.Engine.CurrentTimer.IsRunning&&session.Engine.CurrentTimer.SessionId==hiddenSession,
                            "Cancelling reset with auto-hide enabled preserves the hidden running session");
                        count=asked;GlobalReset(app);await Until(()=>Task.FromResult(asked>count));decision!.SetResult(true);await Idle(app);
                        Check(!main.Visible&&compact.Visible&&compact.IsTimeOnly&&session.Engine.SettingsSnapshot.ShowFloatingTimer
                            &&session.Engine.CurrentTimer.SessionId is null&&session.Engine.CurrentTimer.RemainingSeconds==123,
                            "Confirmed global reset with auto-hide enabled restores Time-only, not App view");
                        Check(session.Engine.Snapshot.Outbox.Count==0&&session.Engine.Snapshot.Connection.WebAppUrl=="","Reset confirmation tests never send reflections or connect to Sheets");
                    }catch(Exception error){failure=error;decision?.TrySetResult(false);}
                    finally{await app.CloseMainAsync();}
                });
                Application.Run(app);
            }catch(Exception error){failure=error;}finally{app?.Dispose();}
        });
        thread.SetApartmentState(ApartmentState.STA);thread.Start();
        if(!thread.Join(TimeSpan.FromSeconds(150)))throw new TimeoutException("Native reset confirmation checks timed out.");
        if(failure is not null)throw new Exception("Native reset confirmation checks failed",failure);
        Console.WriteLine($"{passed} native reset confirmation checks passed.");
    }
    private static void NativeReset(PreviewWindow window){var key=new KeyEventArgs(Keys.Control|Keys.R);typeof(Control).GetMethod("OnKeyDown",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(Browser(window),[key]);if(!key.Handled)throw new Exception("Ctrl+R was not intercepted");typeof(Control).GetMethod("OnKeyUp",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(Browser(window),[new KeyEventArgs(Keys.R)]);}
    private static void GlobalReset(PreviewApplication app,TimeSpan queueDelay=default){var keys=(PreviewShortcuts)typeof(PreviewApplication).GetField("shortcuts",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(app)!;if(!keys.Dispatch(GlobalShortcut.HotKeyMessage,GlobalShortcut.ResetTimerId,queueDelay))throw new Exception("Global Ctrl+Alt+R was not registered");}
    private static Task Idle(PreviewApplication app)=>Until(()=>Task.FromResult(!(bool)typeof(PreviewApplication).GetField("resetInProgress",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(app)!));
    private static async Task IdleAfterRequest(PreviewApplication app,PreviewWindow window){await Until(async()=>await Bool(window,"window.resetConfirmMarker===undefined&&sessionStorage.getItem('timer-reset-reload-view')===null"));await Idle(app);await Ready(window);}
    private static WebView2 Browser(PreviewWindow window)=>(WebView2)typeof(PreviewWindow).GetField("browser",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(window)!;
    private static Task Ready(PreviewWindow window)=>((TaskCompletionSource)typeof(PreviewWindow).GetField("interfaceReady",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(window)!).Task.WaitAsync(TimeSpan.FromSeconds(20));
    private static Task<string> Script(PreviewWindow window,string script)=>Browser(window).CoreWebView2.ExecuteScriptAsync(script);
    private static async Task<bool> Bool(PreviewWindow window,string script)=>JsonDocument.Parse(await Script(window,script)).RootElement.ValueKind==JsonValueKind.True;
    private static async Task ClockFocus(PreviewWindow window,string id)
    {
        try{
            using var limit=new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while(!await Bool(window,"document.activeElement?.id==='"+id+"'"))await Task.Delay(25,limit.Token);
        }catch(OperationCanceledException error){
            var dom=await Script(window,"JSON.stringify({focused:document.hasFocus(),active:document.activeElement?.id,tab:document.body.dataset.tab,inert:document.body.inert,dialog:!!document.querySelector('dialog[open]'),trace:window.resetFocusTrace??[]})");
            var detail=JsonSerializer.Serialize(new{window.View,window.Visible,window.Enabled,window.WindowState,WindowHandle=window.Handle.ToInt64(),Foreground=GetForegroundWindow().ToInt64(),
                NativeForeground=WindowActivation.IsForeground(window),WindowFocus=window.ContainsFocus,BrowserFocus=Browser(window).ContainsFocus,
                Pending=typeof(PreviewWindow).GetField("focusTimeOnReady",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(window),Dom=dom});
            Console.WriteLine("CLOCKFOCUSDETAIL "+detail);
            throw new InvalidOperationException("Clock focus did not arrive: "+detail,error);
        }
    }
    [DllImport("user32.dll")]private static extern nint GetForegroundWindow();
    private static async Task Until(Func<Task<bool>> predicate){using var limit=new CancellationTokenSource(TimeSpan.FromSeconds(20));while(!await predicate())await Task.Delay(25,limit.Token);}
    private sealed class Registration:IHotKeyRegistration{public bool Register(nint window,int id,uint modifiers,uint key)=>true;public bool Unregister(nint window,int id)=>true;}
}
