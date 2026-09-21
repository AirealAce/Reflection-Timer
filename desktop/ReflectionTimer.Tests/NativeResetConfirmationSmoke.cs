using System.Reflection;
using System.Text.Json;
using Microsoft.Web.WebView2.WinForms;
using ReflectionTimer.Accessible;
using ReflectionTimer.Core;
using ReflectionTimer.Desktop;

// Real WebView commands, draft flushing and reloads; synthetic data and decisions.
static class NativeResetConfirmationSmoke
{
    internal static void Run()
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
                TaskCompletionSource<bool>? decision=null;ResetWarning? warning=null;var asked=0;
                app=new(session,Path.Combine(Path.GetTempPath(),"ReflectionTimer-ResetConfirm-"+Guid.NewGuid().ToString("N")),startInTray:true,profileName:"reset-confirm-smoke",shortcutRegistration:new Registration(),
                    resetConfirmation:(_,value)=>{warning=value;asked++;decision=new(TaskCreationOptions.RunContinuationsAsynchronously);return decision.Task;});
                ((System.Windows.Forms.Timer)typeof(PreviewApplication).GetField("pulse",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(app)!).Stop();
                _=app.MainForm!.Handle;
                app.MainForm.BeginInvoke(async()=>{
                    try {
                        app.Open("main");app.Open("compact");
                        var windows=(List<PreviewWindow>)typeof(PreviewApplication).GetField("windows",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(app)!;
                        var main=windows.Single(w=>w.View=="main");var compact=windows.Single(w=>w.View=="compact");
                        await Ready(main);await Ready(compact);
                        foreach(var mode in new[]{SessionMode.Timer,SessionMode.Stopwatch}) {
                            session.Engine.SwitchMode(mode);
                            foreach(var route in new[]{"App button","App playback","Compact button","App Ctrl+R","Time-only Ctrl+R"}) {
                                if(mode==SessionMode.Timer)session.Engine.Start(900,false,0);else session.Engine.StartStopwatch();
                                now=now.AddSeconds(25);
                                compact.Post(new{type=route=="Time-only Ctrl+R"?"shrinkCompact":"expandCompact"});
                                await Until(()=>Task.FromResult(compact.IsTimeOnly==(route=="Time-only Ctrl+R")));
                                var target=route.StartsWith("App")?main:compact;
                                var reload=route.Contains("Ctrl+R");
                                var before=JsonSerializer.Serialize(session.Engine.Snapshot.Timer);var prior=asked;
                                await Script(target,"window.resetConfirmMarker=true");
                                async Task Invoke(){if(reload)NativeReset(target);else await Script(target,"document.querySelector('"+(route=="App playback"?"#playback-reset":"#reset")+"').click()");}
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
                                if(route=="Time-only Ctrl+R")Check(compact.IsTimeOnly,"Confirmed reset retains Time-only view");
                            }
                        }
                        session.Engine.SwitchMode(SessionMode.Timer);session.Engine.Start(900,false,0);
                        var id=session.Engine.CheckIn();session.Engine.Pause();app.Open("reflection",id);
                        await Until(()=>Task.FromResult(windows.Any(w=>w.ReflectionOpen&&w.Visible)));
                        var reflection=windows.Single(w=>w.ReflectionOpen);await Ready(reflection);
                        // No input event/autosave: only the shared reset gate can see these edits.
                        await Script(reflection,"document.querySelector('#reflection-text').value='Immediate unsent text';document.querySelector('#early-reason').value='Immediate reason';window.resetConfirmMarker=true");
                        var count=asked;NativeReset(main);await Until(()=>Task.FromResult(asked>count));
                        Check(warning is {Running:false,HasDraft:true}&&session.Engine.Snapshot.Prompts.Single(p=>p.Id==id).Draft=="Immediate unsent text","Reset from another window flushes unblurred reflection text before deciding");
                        decision!.SetResult(false);await Idle(app);
                        await Until(async()=>await Bool(reflection,"!document.querySelector('#reflection-text').readOnly"));
                        Check(session.Engine.Snapshot.Timer.SessionId is not null&&await Bool(reflection,"document.querySelector('#early-reason').value==='Immediate reason'"),"Cancel restores both reflection fields without resetting a paused timer");
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
    private static Task Idle(PreviewApplication app)=>Until(()=>Task.FromResult(!(bool)typeof(PreviewApplication).GetField("resetInProgress",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(app)!));
    private static async Task IdleAfterRequest(PreviewApplication app,PreviewWindow window){await Until(async()=>await Bool(window,"window.resetConfirmMarker===undefined&&sessionStorage.getItem('timer-reset-reload-view')===null"));await Idle(app);await Ready(window);}
    private static WebView2 Browser(PreviewWindow window)=>(WebView2)typeof(PreviewWindow).GetField("browser",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(window)!;
    private static Task Ready(PreviewWindow window)=>((TaskCompletionSource)typeof(PreviewWindow).GetField("interfaceReady",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(window)!).Task.WaitAsync(TimeSpan.FromSeconds(20));
    private static Task<string> Script(PreviewWindow window,string script)=>Browser(window).CoreWebView2.ExecuteScriptAsync(script);
    private static async Task<bool> Bool(PreviewWindow window,string script)=>JsonDocument.Parse(await Script(window,script)).RootElement.ValueKind==JsonValueKind.True;
    private static async Task Until(Func<Task<bool>> predicate){using var limit=new CancellationTokenSource(TimeSpan.FromSeconds(20));while(!await predicate())await Task.Delay(25,limit.Token);}
    private sealed class Registration:IHotKeyRegistration{public bool Register(nint window,int id,uint modifiers,uint key)=>true;public bool Unregister(nint window,int id)=>true;}
}
