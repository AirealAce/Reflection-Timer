using System.Reflection;
using System.Text.Json;
using Microsoft.Web.WebView2.WinForms;
using ReflectionTimer.Accessible;
using ReflectionTimer.Core;
using ReflectionTimer.Desktop;

// Real native timeout and settings bridge. Isolated profile, muted audio,
// fake global registrations; no user settings, Sheets or accessibility-tree tests.
static class NativeViewerAutoHideSmoke
{
    internal static void Run()
    {
        Exception? failure=null;var passed=0;
        void Check(bool value,string label){if(!value)throw new Exception(label);passed++;Console.WriteLine("PASS "+label);}
        var thread=new Thread(()=>{
            try {
                var store=new MemoryStore{State=new(){ShowAppView=false,ShowFloatingTimer=false,LoggingEnabled=false,Timer=new(){Volume=0}}};
                var now=DateTimeOffset.Now;
                var session=new PreviewSession(store,()=>now,isolatedProfile:true);
                using var app=new PreviewApplication(session,Path.Combine(Path.GetTempPath(),"ReflectionTimer-AutoHide-"+Guid.NewGuid().ToString("N")),
                    startInTray:true,profileName:"auto-hide",shortcutRegistration:new Registration());
                var main=(PreviewWindow)app.MainForm!;_=main.Handle;
                main.BeginInvoke(async()=>{
                    try {
                        app.Open("main");await Ready(main);
                        await Script(main,"document.querySelector('#tab-settings').click()");
                        await Until(async()=>await Bool(main,"!document.querySelector('#voice-announcements').disabled"));
                        Check(await Bool(main,"!document.querySelector('#viewerAutoHide').checked&&document.querySelector('#viewerAutoHideSeconds').value==='3'"),"Settings loads disabled with 3 seconds");
                        app.Open("compact");var compact=Windows(app).Single(w=>w.View=="compact");await Ready(compact);
                        await Task.Delay(3200);Check(compact.Visible,"Default-disabled viewer remains visible past 3 seconds");
                        await Script(main,"document.querySelector('#viewerAutoHideSeconds').value='1';document.querySelector('#viewerAutoHideSeconds').dispatchEvent(new Event('change',{bubbles:true}))");
                        await Until(()=>Task.FromResult(store.State.ViewerAutoHideSeconds==1));
                        await Script(main,"document.querySelector('#viewerAutoHide').click()");
                        await Task.Delay(1250);
                        Check(store.State.ViewerAutoHide&&store.State.ViewerAutoHideSeconds==1&&compact.Visible,"Settings autosave without hiding an idle viewer");
                        foreach(var mode in Enum.GetValues<SessionMode>()) {
                            session.Engine.SwitchMode(mode);session.Engine.Reset();
                            app.Open("compact");compact.SetCompactMode(false,false);
                            if(mode==SessionMode.Timer)session.Engine.Start(60,false,0);else session.Engine.StartStopwatch();
                            var id=session.Engine.CurrentTimer.SessionId;
                            await Task.Delay(650);compact.FocusControls();compact.SetCompactMode(true,false);
                            await Task.Delay(500);
                            Check(!compact.Visible,"Focus and floating layout changes do not restart the start deadline: "+mode);
                            Check(session.Engine.CurrentTimer.IsRunning&&session.Engine.CurrentTimer.SessionId==id&&main.Visible&&session.Engine.Snapshot.Prompts.Count==0,
                                "Auto-hide leaves the selected session running and App visible: "+mode);
                            app.Open("compact");await Task.Delay(1250);
                            Check(compact.Visible,"Reopening a running viewer does not start another hide countdown: "+mode);
                            session.Engine.Pause();session.Engine.Resume();await Task.Delay(400);session.Engine.Pause();await Task.Delay(850);
                            Check(compact.Visible,"Pausing cancels the pending resume deadline: "+mode);
                            compact.SetCompactMode(true,false);session.Engine.Resume();
                            await Until(()=>Task.FromResult(!compact.Visible));
                            Check(session.Engine.CurrentTimer.IsRunning&&session.Engine.CurrentTimer.SessionId==id,"Resuming in Time-only starts a fresh hide delay: "+mode);
                            session.Engine.Pause();
                        }
                        app.Open("compact");session.Engine.Resume();await Task.Delay(200);session.Engine.SetViewerAutoHide(false,1);await Task.Delay(1250);
                        Check(compact.Visible,"Disabling immediately cancels pending auto-hide");
                        session.Engine.Pause();session.Engine.SetViewerAutoHide(true,1);
                        session.Engine.Resume();await Task.Delay(650);session.Engine.SetViewerAutoHide(true,2);await Task.Delay(900);
                        Check(compact.Visible,"Editing the delay uses the original resume timestamp");
                        await Until(()=>Task.FromResult(!compact.Visible));session.Engine.Pause();
                        app.Open("compact");session.Engine.Resume();
                        var review=session.Engine.ReviewStopwatch();await Task.Delay(1250);
                        Check(compact.Visible&&!session.Engine.CurrentTimer.IsRunning,"Opening a stopwatch reflection cancels pending hiding");
                        session.Engine.SaveReflectionForLater(review,"Synthetic saved review");await Until(()=>Task.FromResult(!compact.Visible));
                        Check(session.Engine.CurrentTimer.IsRunning,"Saving a stopwatch review resumes and begins the hide delay");
                        session.Engine.Pause();session.Engine.SkipPrompt(review);
                        app.Open("compact");compact.Enabled=false;session.Engine.Resume();await Task.Delay(1250);
                        Check(compact.Visible,"A modal-disabled owner is not auto-hidden");
                        compact.Enabled=true;await Until(()=>Task.FromResult(!compact.Visible));session.Engine.Pause();
                        app.Open("compact");session.Engine.Resume();store.Fail=true;await Task.Delay(1300);
                        Check(compact.Visible&&session.Engine.SettingsSnapshot.ShowFloatingTimer,"A failed visibility save keeps the viewer visible");
                        store.Fail=false;session.Engine.Pause();
                        session.Engine.SwitchMode(SessionMode.Timer);session.Engine.Reset();session.Engine.Start(1,true,0);
                        var beforeRestart=session.Engine.CurrentTimer.SessionId;
                        await Task.Delay(650);now=now.AddSeconds(1);session.Engine.Advance();await Task.Delay(500);
                        Check(compact.Visible&&session.Engine.CurrentTimer.SessionId!=beforeRestart,"Automatic restart replaces the previous session's hide deadline");
                        await Until(()=>Task.FromResult(!compact.Visible));session.Engine.Reset();
                        foreach(var pending in session.Engine.Snapshot.Prompts)session.Engine.SkipPrompt(pending.Id);
                        app.Open("compact");session.Engine.SaveSchedule(null,now.AddSeconds(1),60,false,0);now=now.AddSeconds(1);session.Engine.Advance();
                        await Until(()=>Task.FromResult(!compact.Visible));
                        Check(session.Engine.CurrentTimer.IsRunning,"A scheduled start begins the hide delay");
                        app.Open("compact");compact.ClosePermanently();app.Open("compact");compact=Windows(app).Single(w=>w.View=="compact");await Ready(compact);await Task.Delay(1250);
                        Check(compact.Visible,"Creating a viewer for an already-running session does not count as starting it");
                        session.Engine.Pause();
                        var prompt=session.Engine.TestPrompt();app.Open("reflection",prompt);
                        await Until(()=>Task.FromResult(Windows(app).Any(w=>w.View=="reflection"&&w.Visible)));
                        session.Engine.Resume();await Until(()=>Task.FromResult(!compact.Visible));
                        Check(Windows(app).Any(w=>w.View=="reflection"&&w.Visible),"Reflection prompts stay open when the floating viewer auto-hides");
                        await Script(main,"document.querySelector('#save-settings').click()");
                        await Until(async()=>await Bool(main,"document.querySelector('#status').textContent==='Settings saved.'"));
                        Check(!compact.Visible&&!session.Engine.SettingsSnapshot.ShowFloatingTimer,"Save settings does not resurrect an automatically hidden viewer");
                    } catch(Exception error){failure=error;}
                    finally {store.Fail=false;await app.CloseMainAsync();}
                });
                Application.Run(app);
            } catch(Exception error){failure=error;}
        });
        thread.SetApartmentState(ApartmentState.STA);thread.Start();
        if(!thread.Join(TimeSpan.FromSeconds(80)))throw new TimeoutException("Native auto-hide checks timed out");
        if(failure is not null)throw new Exception("Native auto-hide checks failed",failure);
        Console.WriteLine($"{passed} native auto-hide checks passed.");
    }
    private static List<PreviewWindow> Windows(PreviewApplication app)=>(List<PreviewWindow>)typeof(PreviewApplication).GetField("windows",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(app)!;
    private static Task Ready(PreviewWindow window)=>((TaskCompletionSource)typeof(PreviewWindow).GetField("interfaceReady",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(window)!).Task.WaitAsync(TimeSpan.FromSeconds(20));
    private static Task<string> Script(PreviewWindow window,string code)=>((WebView2)typeof(PreviewWindow).GetField("browser",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(window)!).CoreWebView2.ExecuteScriptAsync(code);
    private static async Task<bool> Bool(PreviewWindow window,string code){using var doc=JsonDocument.Parse(await Script(window,code));return doc.RootElement.GetBoolean();}
    private static async Task Until(Func<Task<bool>> condition){using var limit=new CancellationTokenSource(TimeSpan.FromSeconds(12));while(!await condition())await Task.Delay(25,limit.Token);}
    private sealed class Registration:IHotKeyRegistration
    {
        public bool Register(nint window,int id,uint modifiers,uint key)=>true;
        public bool Unregister(nint window,int id)=>true;
    }
}
