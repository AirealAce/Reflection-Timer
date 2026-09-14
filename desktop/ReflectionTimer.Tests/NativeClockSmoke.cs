using System.Reflection;
using System.Text.Json;
using Microsoft.Web.WebView2.WinForms;
using ReflectionTimer.Accessible;
using ReflectionTimer.Core;
using ReflectionTimer.Desktop;

// Real WebView2 documents and bridge, with a muted in-memory timer and a
// disposable browser profile. Never drives the user's installed app or Sheets.
static class NativeClockSmoke
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
                var session=new PreviewSession(new MemoryStore{State=new AppState{LoggingEnabled=false,AutoSendIncompleteReflections=false,Timer=new(){Volume=0}}},()=>now,isolatedProfile:true);
                session.Engine.Start(20,false,0,lowTime:new(){Enabled=false});
                app=new(session,Path.Combine(Path.GetTempPath(),"ReflectionTimer-ClockSmoke-"+Guid.NewGuid().ToString("N")),startInTray:true,profileName:"clock-smoke",shortcutRegistration:new Registration());
                // Advance the test's clock deliberately, without wall-clock tick races.
                ((System.Windows.Forms.Timer)typeof(PreviewApplication).GetField("pulse",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(app)!).Stop();
                _=app.MainForm!.Handle;
                app.MainForm.BeginInvoke(async()=>{
                    try {
                        app.Open("main");app.Open("compact");
                        var windows=(List<PreviewWindow>)typeof(PreviewApplication).GetField("windows",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(app)!;
                        var main=windows.Single(w=>w.View=="main");var compact=windows.Single(w=>w.View=="compact");
                        foreach(var window in new[]{main,compact})await ((TaskCompletionSource)typeof(PreviewWindow).GetField("interfaceReady",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(window)!).Task.WaitAsync(TimeSpan.FromSeconds(20));
                        async Task Both(string expected,string message){await Until(async()=>await Text(main)==expected&&await Text(compact)==expected);Check(true,message);}
                        var id=session.Engine.CheckIn();app.Open("reflection",id);
                        await Until(()=>Task.FromResult(windows.Any(w=>w.ReflectionOpen&&w.Visible)));
                        now=now.AddSeconds(19);app.Broadcast(new{type="clock",clock=session.Clock()});
                        await Both("0:01","App and floating timer count down while a session prompt is open");
                        now=now.AddSeconds(1);app.Broadcast(new{type="clock",clock=session.Clock()});
                        await Both("0:00","Both viewers can show zero at the countdown deadline");
                        session.Tick();await Both("0:20","Natural completion restores the twenty-second input duration in both real WebViews");
                        for(var tick=0;tick<3;tick++){now=now.AddSeconds(1);app.Broadcast(new{type="clock",clock=session.Clock()});await Both("0:20","Finished clock stays at the entered duration on later native ticks: "+(tick+1));}
                        app.Broadcast(new{type="clock",clock=new{seconds=0,text="0 seconds",status="Finished"}});
                        await Both("0:20","Zero-valued finished feedback cannot erase the native viewers' input preview");
                        compact.Post(new{type="shrinkCompact"});await Until(()=>Task.FromResult(compact.IsTimeOnly));
                        await Both("0:20","Time-only mode retains the entered duration after completion");
                        compact.Close();app.Open("compact");await Both("0:20","Closing and reopening the floating viewer retains its finished duration preview");
                        await Script(main,"document.querySelector('#seconds').value='45';document.querySelector('#seconds').dispatchEvent(new Event('input',{bubbles:true}))");
                        await Both("0:45","Editing the finished duration updates App, Compact, and Time-only together");
                        Check(JsonSerializer.SerializeToElement(session.Clock(),PreviewSession.Json).GetProperty("text").GetString()=="45 seconds"&&session.Engine.Snapshot.Timer is {DurationSeconds:20,RemainingSeconds:0},"Read-time feedback follows the input while completed-session accounting stays intact");
                        await Script(main,"document.querySelector('#seconds').value='0';document.querySelector('#seconds').dispatchEvent(new Event('input',{bubbles:true}))");
                        await Both("0:00","All-zero input boxes intentionally keep a zero preview");
                        session.Execute("toggle",JsonSerializer.SerializeToElement(new{seconds=45,repeat=false,lowTime=false}));now=now.AddSeconds(5);session.Engine.Pause();
                        await Both("0:40","A paused timer keeps its remaining time, not the original forty-five seconds");
                        session.Engine.Resume();session.Execute("end",JsonSerializer.SerializeToElement(new{}));
                        await Both("0:45","Ending early also restores the entered duration");
                        session.Execute("toggle",JsonSerializer.SerializeToElement(new{seconds=12,repeat=true,lowTime=false}));now=now.AddSeconds(12);session.Tick();now=now.AddSeconds(2);app.Broadcast(new{type="clock",clock=session.Clock()});
                        await Both("0:10","Auto-start continues the next countdown instead of freezing on a duration preview");
                        session.Engine.SetPreferences(false,0);session.Engine.SaveSchedule(null,now.AddSeconds(10),25,false,0);now=now.AddSeconds(10);session.Tick();
                        await Both("0:25","A scheduled handoff shows the newly started session");
                        var promptCount=session.Engine.Snapshot.Prompts.Count;
                        var sessionId=session.Engine.Snapshot.Timer.SessionId;
                        async Task ControlEnter(string target,bool repeat=false)=>await Script(compact,
                            "document.querySelector("+JsonSerializer.Serialize(target)+").dispatchEvent(new KeyboardEvent('keydown',{key:'Enter',ctrlKey:true,bubbles:true,cancelable:true,repeat:"+(repeat?"true":"false")+"}))");
                        await Until(()=>Task.FromResult(compact.IsTimeOnly));now=now.AddSeconds(3);
                        await ControlEnter("#read-time");
                        await Until(()=>Task.FromResult(!session.Engine.Snapshot.Timer.IsRunning));
                        Check(session.Engine.Snapshot.Timer is {RemainingSeconds:22,PausedRemainingMilliseconds:not null}&&session.Engine.Snapshot.Timer.SessionId==sessionId,"Time-only Ctrl+Enter pauses the real engine without resetting elapsed time");
                        await Until(()=>Task.FromResult(!compact.IsTimeOnly));await ControlEnter("#app");
                        await Until(()=>Task.FromResult(session.Engine.Snapshot.Timer.IsRunning));
                        Check(session.Engine.Snapshot.Timer.SessionId==sessionId,"Compact Ctrl+Enter resumes the same real session instead of activating App");
                        await Until(()=>Task.FromResult(compact.IsTimeOnly));await ControlEnter("#read-time",repeat:true);
                        await Task.Delay(75);
                        Check(session.Engine.Snapshot.Timer.IsRunning&&session.Engine.Snapshot.Prompts.Count==promptCount,"Held Ctrl+Enter does not pause again or create a reflection through the native bridge");
                        session.Execute("reset",JsonSerializer.SerializeToElement(new{seconds=30}));await Both("0:30","Reset prepares an isolated idle timer for the compact shortcut check");
                        await ControlEnter("#repeat");await Until(()=>Task.FromResult(session.Engine.Snapshot.Timer.IsRunning));
                        Check(session.Engine.Snapshot.Timer is {DurationSeconds:30,AutoRestart:false}&&session.Engine.Snapshot.Prompts.Count==promptCount,"Compact Ctrl+Enter starts the real input duration without changing Auto-start or reflections");
                        async Task Space(string target,bool repeat=false)=>await Script(compact,
                            "document.querySelector("+JsonSerializer.Serialize(target)+").dispatchEvent(new KeyboardEvent('keydown',{key:' ',bubbles:true,cancelable:true,repeat:"+(repeat?"true":"false")+"}))");
                        await Until(()=>Task.FromResult(compact.IsTimeOnly));now=now.AddSeconds(4);
                        var spaceSession=session.Engine.Snapshot.Timer.SessionId;
                        await Space("#read-time");await Until(()=>Task.FromResult(!session.Engine.Snapshot.Timer.IsRunning));
                        Check(session.Engine.Snapshot.Timer is {RemainingSeconds:26,PausedRemainingMilliseconds:not null}&&session.Engine.Snapshot.Timer.SessionId==spaceSession,"Time-only Space pauses the real session without losing elapsed time");
                        await Space("#read-time",repeat:true);await Task.Delay(75);
                        Check(!session.Engine.Snapshot.Timer.IsRunning,"A repeated Space event does not resume the real timer");
                        await Until(()=>Task.FromResult(!compact.IsTimeOnly));await Space("#app");await Until(()=>Task.FromResult(session.Engine.Snapshot.Timer.IsRunning));
                        Check(session.Engine.Snapshot.Timer.SessionId==spaceSession&&session.Engine.Snapshot.Prompts.Count==promptCount,"Compact Space resumes the same session without opening or submitting a reflection");
                        session.Execute("reset",JsonSerializer.SerializeToElement(new{seconds=45}));await Both("0:45","Reset prepares the compact Space start check");
                        await Space("#repeat");await Until(()=>Task.FromResult(session.Engine.Snapshot.Timer.IsRunning));
                        Check(session.Engine.Snapshot.Timer is {DurationSeconds:45,AutoRestart:false}&&session.Engine.Snapshot.Prompts.Count==promptCount,"Compact Space starts the specified duration without toggling Auto-start");
                        var keys=(PreviewShortcuts)typeof(PreviewApplication).GetField("shortcuts",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(app)!;
                        foreach(var window in windows)window.Hide();
                        var draftBefore=JsonSerializer.Serialize(session.Engine.Snapshot.Prompts);
                        var globalSession=session.Engine.Snapshot.Timer.SessionId;now=now.AddMilliseconds(4123);
                        Check(keys.Dispatch(GlobalShortcut.HotKeyMessage,GlobalShortcut.TimerToggleId),"Native Ctrl+Space registration routes to the app while all viewers are hidden");
                        Check(session.Engine.Snapshot.Timer is {IsRunning:false,PausedRemainingMilliseconds:40877}&&session.Engine.Snapshot.Timer.SessionId==globalSession,"Global Ctrl+Space pauses the same session with precise remaining time");
                        keys.Dispatch(GlobalShortcut.HotKeyMessage,GlobalShortcut.TimerToggleId);
                        Check(session.Engine.Snapshot.Timer.IsRunning&&session.Engine.Snapshot.Timer.SessionId==globalSession,"Global Ctrl+Space resumes the same session without a visible Compact window");
                        keys.Dispatch(GlobalShortcut.HotKeyMessage,GlobalShortcut.TimerToggleId);
                        await Script(main,"document.querySelector('#minutes').value='2';document.querySelector('#seconds').value='3';document.querySelector('#seconds').dispatchEvent(new Event('input',{bubbles:true}))");
                        await Both("2:03","A hidden App duration edit reaches the shared native draft");
                        keys.Dispatch(GlobalShortcut.HotKeyMessage,GlobalShortcut.TimerToggleId);
                        Check(session.Engine.Snapshot.Timer is {IsRunning:true,DurationSeconds:123},"Global Ctrl+Space starts the shared edited duration through the production shortcut action");
                        Check(windows.All(w=>!w.Visible)&&JsonSerializer.Serialize(session.Engine.Snapshot.Prompts)==draftBefore,"Global toggling keeps viewers hidden and leaves pending reflection text unchanged");
                        var aliasSession=session.Engine.Snapshot.Timer.SessionId;
                        foreach(var running in new[]{false,true}){
                            Check(keys.Dispatch(GlobalShortcut.HotKeyMessage,GlobalShortcut.TimerToggleAltId)
                                &&session.Engine.Snapshot.Timer.IsRunning==running&&session.Engine.Snapshot.Timer.SessionId==aliasSession
                                &&windows.All(w=>!w.Visible),"Ctrl+Alt+Space toggles the same timer once while all windows stay hidden: running="+running);
                        }
                        compact.Show();
                        await Until(()=>Task.FromResult(compact.Visible&&compact.IsTimeOnly));
                        var timeOnlyBounds=compact.Bounds;
                        var resizedModes=new List<bool>();
                        var compactBrowser=(WebView2)typeof(PreviewWindow).GetField("browser",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(compact)!;
                        compactBrowser.CoreWebView2.WebMessageReceived+=(_,e)=>{
                            using var message=JsonDocument.Parse(e.WebMessageAsJson);
                            if(message.RootElement.GetProperty("action").GetString()=="compactSize")resizedModes.Add(message.RootElement.GetProperty("data").GetProperty("tiny").GetBoolean());
                        };
                        foreach(var shortcutId in new[]{GlobalShortcut.TimerToggleId,GlobalShortcut.TimerToggleAltId})foreach(var focused in new[]{true,false}){
                            var shortcut=shortcutId==GlobalShortcut.TimerToggleId?"Ctrl+Space":"Ctrl+Alt+Space";
                            if(focused)WindowActivation.Focus(compact);else app.Open("main");
                            await Script(compact,"document.querySelector('#read-time').focus()");
                            var toggleSession=session.Engine.Snapshot.Timer.SessionId;
                            foreach(var running in new[]{false,true,false,true}){
                                keys.Dispatch(GlobalShortcut.HotKeyMessage,shortcutId);
                                await Until(async()=>JsonDocument.Parse(await Script(compact,"document.querySelector('#toggle').title")).RootElement.GetString()==(running?"Pause timer":"Resume timer"));
                                await Task.Delay(75);
                                Check(session.Engine.Snapshot.Timer.IsRunning==running&&session.Engine.Snapshot.Timer.SessionId==toggleSession
                                    &&compact.Visible&&compact.IsTimeOnly&&compact.Bounds==timeOnlyBounds&&!resizedModes.Contains(false),
                                    $"Global {shortcut} preserves time-only mode, size and position without even briefly expanding: focused={focused}, running={running}");
                            }
                        }
                        Check(JsonSerializer.Serialize(session.Engine.Snapshot.Prompts)==draftBefore,"Time-only global pause/resume retains all reflection drafts");
                        foreach(var shortcutId in new[]{GlobalShortcut.TimerToggleId,GlobalShortcut.TimerToggleAltId}){
                            var shortcut=shortcutId==GlobalShortcut.TimerToggleId?"Ctrl+Space":"Ctrl+Alt+Space";
                            session.Execute("reset",JsonSerializer.SerializeToElement(new{seconds=123}));
                            await Until(()=>Task.FromResult(!compact.IsTimeOnly));
                            compact.Post(new{type="shrinkCompact"});await Until(()=>Task.FromResult(compact.IsTimeOnly));
                            keys.Dispatch(GlobalShortcut.HotKeyMessage,shortcutId);
                            await Until(async()=>JsonDocument.Parse(await Script(compact,"document.querySelector('#toggle').title")).RootElement.GetString()=="Pause timer");
                            Check(session.Engine.Snapshot.Timer is {IsRunning:true,DurationSeconds:123}&&compact.IsTimeOnly&&compact.Bounds==timeOnlyBounds,shortcut+" starts a ready timer without expanding time-only view");
                            session.Engine.Pause();await Until(()=>Task.FromResult(!compact.IsTimeOnly));
                            Check(true,"Keeping time-only for "+shortcut+" does not change later ordinary pause behavior");
                        }
                        Check(session.Engine.Snapshot.Connection.WebAppUrl==""&&session.Engine.Snapshot.Outbox.Count==0,"Clock checks stay disconnected and never submit a reflection");
                    } catch(Exception error){failure=error;}
                    finally {await app.CloseMainAsync();}
                });
                Application.Run(app);
            } catch(Exception error){failure=error;}
            finally {app?.Dispose();}
        });
        thread.SetApartmentState(ApartmentState.STA);thread.Start();
        if(!thread.Join(TimeSpan.FromSeconds(90)))throw new TimeoutException("Native clock smoke did not finish.");
        if(failure is not null)throw new Exception("Native clock smoke failed",failure);
        Console.WriteLine($"{passed} native clock checks passed.");
    }
    private static Task<string> Script(PreviewWindow window,string script)=>((WebView2)typeof(PreviewWindow).GetField("browser",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(window)!).CoreWebView2.ExecuteScriptAsync(script);
    private static async Task<string?> Text(PreviewWindow window)=>JsonDocument.Parse(await Script(window,"document.querySelector('#visual-clock').textContent")).RootElement.GetString();
    private static async Task Until(Func<Task<bool>> predicate){using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(20));while(!await predicate())await Task.Delay(25,timeout.Token);}
    private sealed class Registration : IHotKeyRegistration
    {
        public bool Register(nint window,int id,uint modifiers,uint key)=>true;
        public bool Unregister(nint window,int id)=>true;
    }
}
