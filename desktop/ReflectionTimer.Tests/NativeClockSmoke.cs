using System.Reflection;
using System.Text.Json;
using Microsoft.Web.WebView2.WinForms;
using ReflectionTimer.Accessible;
using ReflectionTimer.Core;

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
                app=new(session,Path.Combine(Path.GetTempPath(),"ReflectionTimer-ClockSmoke-"+Guid.NewGuid().ToString("N")),startInTray:true,profileName:"clock-smoke");
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
}
