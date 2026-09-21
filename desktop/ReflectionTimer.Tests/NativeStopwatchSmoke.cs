using System.Reflection;
using System.Text.Json;
using Microsoft.Web.WebView2.WinForms;
using ReflectionTimer.Accessible;
using ReflectionTimer.Core;
using ReflectionTimer.Desktop;

// Opt-in integration test: real native bridge, synthetic clock, muted audio,
// fake hotkey registration, disposable browser profile, no connected Sheet.
static class NativeStopwatchSmoke
{
    internal static void RunInteractive()
    {
        Exception? failure=null;
        var thread=new Thread(()=>{
            Application.EnableVisualStyles();
            using var launcher=new Form{Text="Synthetic stopwatch shortcut checks",Size=new(420,140),StartPosition=FormStartPosition.CenterScreen};
            launcher.Shown+=(_,_)=>launcher.BeginInvoke(()=>WindowActivation.Focus(launcher));
            var run=new Button{Text="Run stopwatch focus checks",Dock=DockStyle.Fill};
            run.Click+=(_,_)=>{try{Run(focusShortcuts:true);}catch(Exception error){failure=error;}finally{launcher.Close();}};
            launcher.Controls.Add(run);Application.Run(launcher);
        });
        thread.SetApartmentState(ApartmentState.STA);thread.Start();thread.Join();
        if(failure is not null)throw new Exception("Interactive stopwatch shortcut checks failed",failure);
    }
    internal static void Run(bool focusShortcuts=false)
    {
        Exception? failure=null;var passed=0;
        void Check(bool value,string message){if(!value)throw new Exception(message);passed++;Console.WriteLine("PASS "+message);}
        var thread=new Thread(()=>{
            PreviewApplication? app=null;
            try{
                Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);Application.EnableVisualStyles();
                var now=DateTimeOffset.Now;
                var store=new DelayedStore{State=new AppState{LoggingEnabled=false,AutoSendIncompleteReflections=false,Theme=AppColorTheme.Glamour,Timer=new(){Volume=0}}};
                var session=new PreviewSession(store,()=>now,isolatedProfile:true);
                app=new(session,Path.Combine(Path.GetTempPath(),"ReflectionTimer-StopwatchSmoke-"+Guid.NewGuid().ToString("N")),startInTray:true,profileName:"stopwatch-smoke",shortcutRegistration:new Registration());
                ((System.Windows.Forms.Timer)Field(app,"pulse")).Stop();_=app.MainForm!.Handle;
                app.MainForm.BeginInvoke(async()=>{
                    try{
                        app.Open("main");app.Open("compact");
                        var windows=(List<PreviewWindow>)Field(app,"windows");
                        var main=windows.Single(w=>w.View=="main");var compact=windows.Single(w=>w.View=="compact");
                        foreach(var window in new[]{main,compact})await ((TaskCompletionSource)Field(window,"interfaceReady")).Task.WaitAsync(TimeSpan.FromSeconds(20));
                        session.Engine.Start(120,false,0);now=now.AddSeconds(10);
                        await Script(compact,"document.querySelector('#session-mode').click()");
                        await Until(()=>Task.FromResult(session.Engine.Snapshot.Timer.Mode==SessionMode.Stopwatch));
                        Check(session.Engine.Snapshot.ParkedTimer is{IsRunning:false,RemainingSeconds:110},"Native S button pauses and preserves the countdown");
                        await Script(main,"document.querySelector('#toggle').click()");
                        await Until(()=>Task.FromResult(session.Engine.Snapshot.Timer.IsRunning));
                        now=now.AddSeconds(65);app.Broadcast(new{type="clock",clock=session.Clock()});
                        await Until(async()=>await Text(main,"#visual-clock")=="1:05"&&await Text(compact,"#visual-clock")=="1:05");
                        await Until(()=>Task.FromResult(compact.IsTimeOnly));Check(true,"Stopwatch counts up in both real WebViews and uses the small running layout");
                        var keys=(PreviewShortcuts)Field(app,"shortcuts");
                        main.Hide();compact.Hide();
                        var pairs=(ConsecutiveShortcutPresses)Field(app,"compactPresses");pairs.Press();
                        now=now.AddSeconds(3);store.BeforeNextSave=()=>now=now.AddSeconds(5);
                        Check(keys.Dispatch(GlobalShortcut.HotKeyMessage,GlobalShortcut.ModeToggleId,TimeSpan.FromSeconds(3)),"Native Ctrl+Alt+apostrophe carries its queued timestamp to the existing mode switch");
                        Check(session.Engine.Snapshot.Timer is {Mode:SessionMode.Timer,IsRunning:false,RemainingSeconds:110}
                            &&session.Engine.Snapshot.ParkedTimer is {IsRunning:false,ElapsedMilliseconds:65000}
                            &&!main.Visible&&!compact.Visible&&session.Engine.Snapshot.Prompts.Count==0,
                            "Global mode switch excludes queue/save delay, restores the paused countdown, and leaves hidden windows hidden");
                        Check(!pairs.Press(),"Mode shortcut clears a pending Compact focus double-press");
                        now=now.AddSeconds(20);keys.Dispatch(GlobalShortcut.HotKeyMessage,GlobalShortcut.ModeToggleId);
                        Check(session.Engine.Snapshot.Timer is {Mode:SessionMode.Stopwatch,IsRunning:false,ElapsedMilliseconds:65000}
                            &&!main.Visible&&!compact.Visible,"Second apostrophe restores the paused stopwatch without counting the break or showing windows");
                        compact.Show();keys.Dispatch(GlobalShortcut.HotKeyMessage,GlobalShortcut.TimerToggleId);
                        await Until(async()=>await Text(compact,"#visual-clock")=="1:05");
                        Check(compact.IsTimeOnly&&session.Engine.Snapshot.Timer.IsRunning,"Mode switching preserves Time-only layout and the existing start/resume shortcut");
                        now=now.AddSeconds(4);store.BeforeNextSave=()=>now=now.AddSeconds(7);
                        Check(keys.Dispatch(GlobalShortcut.HotKeyMessage,GlobalShortcut.ReflectionFocusId,TimeSpan.FromSeconds(4)),"Existing Ctrl+Alt+/ carries its queued timestamp to the stopwatch reflection");
                        Check(!session.Engine.Snapshot.Timer.IsRunning&&session.Engine.Snapshot.Timer.ElapsedMilliseconds==65000,
                            "The real hotkey handler freezes Stopwatch before waiting for the reflection WebView");
                        now=now.AddSeconds(10);
                        await Until(()=>Task.FromResult(windows.Any(w=>w.ReflectionOpen&&w.Visible)));
                        var prompt=windows.Single(w=>w.ReflectionOpen&&w.Visible);
                        Check(!session.Engine.Snapshot.Timer.IsRunning&&TimerEngine.ActualSeconds(session.Engine.Snapshot.Timer,session.Engine.Now)==65,"Slash excludes queue, storage and popup-loading delays from active time");
                        Check(await Text(prompt,"#reflection-heading")=="Stopwatch reflection"&&JsonDocument.Parse(await Script(prompt,"document.querySelector('#reason-group').hidden")).RootElement.GetBoolean(),"Native stopwatch prompt is labeled correctly without an early-ending reason");
                        await Script(prompt,"document.querySelector('#reflection-text').value='Synthetic stopwatch work';document.querySelector('#reflection-text').dispatchEvent(new Event('input',{bubbles:true}));document.querySelector('#later').click()");
                        await Until(()=>Task.FromResult(session.Engine.Snapshot.Timer.IsRunning&&!prompt.ReflectionOpen));
                        Check(session.Engine.Snapshot.Prompts.Single().Draft=="Synthetic stopwatch work","Save closes the prompt, retains its draft, and resumes the same stopwatch");
                        now=now.AddSeconds(5);keys.Dispatch(GlobalShortcut.HotKeyMessage,GlobalShortcut.ReflectionFocusId);
                        await Until(()=>Task.FromResult(windows.Any(w=>w.ReflectionOpen&&w.Visible)));
                        prompt=windows.Single(w=>w.ReflectionOpen&&w.Visible);
                        Check(JsonDocument.Parse(await Script(prompt,"document.querySelector('#reflection-text').value")).RootElement.GetString()!.Contains("Synthetic stopwatch work"),"Second slash restores the saved stopwatch response without creating another prompt");
                        now=now.AddMinutes(2);
                        await Script(prompt,"document.querySelector('#reflection-form button[type=submit]').click()");
                        await Until(()=>Task.FromResult(session.Engine.Snapshot.Outbox.Count==1));
                        Check(session.Engine.Snapshot.Timer.StopwatchCompleted&&session.Engine.Snapshot.Outbox.Single() is{Mode:SessionMode.Stopwatch,ActualDurationSeconds:70,DurationSeconds:0,LocalOnly:true},"Send finishes the native stopwatch with exactly seventy active seconds and no allotted duration");
                        await Until(async()=>await Text(main,"#visual-clock")=="0:00"&&await Text(compact,"#visual-clock")=="0:00");
                        Check(true,"Sending a stopwatch reflection resets App and Compact displays to 0:00");
                        await Script(compact,"if(document.body.dataset.tiny!=='true')document.querySelector('#shrink').click()");
                        await Until(()=>Task.FromResult(compact.IsTimeOnly));
                        now=now.AddMinutes(1);app.Broadcast(new{type="clock",clock=session.Clock()});
                        await Script(compact,"document.querySelector('#read-time').click()");
                        await Until(async()=>(await Text(compact,"#time-snapshot"))!.Contains("0 seconds elapsed. Finished."));
                        Check(await Text(main,"#visual-clock")=="0:00"&&await Text(compact,"#visual-clock")=="0:00"
                            &&session.Engine.Snapshot.Timer.ElapsedMilliseconds==70000,"Time-only display and accessible read-time remain at zero while recorded elapsed time is retained");
                        await Script(main,"document.querySelector('#session-mode').click()");
                        await Until(()=>Task.FromResult(session.Engine.Snapshot.Timer.Mode==SessionMode.Timer));
                        Check(TimerEngine.IsPaused(session.Engine.Snapshot.Timer)&&session.Engine.Snapshot.Timer.RemainingSeconds==110,"T restores the original paused countdown after stopwatch completion");
                        if(focusShortcuts){
                            session.SetDurationDraft(["0","2","10"]);
                            foreach(var viewer in new[]{main,compact}){
                                app.Open(viewer.View,timerPage:true);
                                await Until(()=>Task.FromResult(WindowActivation.IsForeground(viewer)));
                                await Until(async()=>await Script(viewer,"document.activeElement.id")=="\"minutes\"");
                                keys.Dispatch(GlobalShortcut.HotKeyMessage,GlobalShortcut.ModeToggleId);
                                var button=viewer.View=="main"?"playback-toggle":"toggle";
                                await Until(async()=>await Script(viewer,"document.activeElement.id")==JsonSerializer.Serialize(button));
                                Check(session.Engine.Snapshot.Timer.Mode==SessionMode.Stopwatch&&WindowActivation.IsForeground(viewer),viewer.View+": one apostrophe switches to Stopwatch and focuses its visible play button");
                                keys.Dispatch(GlobalShortcut.HotKeyMessage,GlobalShortcut.ModeToggleId);
                                await Until(async()=>await Script(viewer,"document.activeElement.id")=="\"minutes\"");
                                Check(session.Engine.Snapshot.Timer.Mode==SessionMode.Timer&&WindowActivation.IsForeground(viewer),viewer.View+": one apostrophe switches back and selects the usual Timer duration field");
                            }
                            keys.Dispatch(GlobalShortcut.HotKeyMessage,GlobalShortcut.ModeToggleId);
                            foreach(var viewer in new[]{compact,main}){
                                keys.Dispatch(GlobalShortcut.HotKeyMessage,GlobalShortcut.CompactFocusId);
                                var button=viewer.View=="main"?"playback-toggle":"toggle";
                                await Until(async()=>WindowActivation.IsForeground(viewer)&&await Script(viewer,"document.activeElement.id")==JsonSerializer.Serialize(button));
                                Check(session.Engine.Snapshot.Timer.Mode==SessionMode.Stopwatch,viewer.View+": period focuses the Stopwatch play button without changing its mode");
                            }
                        }
                        Check(session.Engine.Snapshot.Connection.WebAppUrl=="","Native stopwatch checks never connect to a real spreadsheet");
                    }catch(Exception error){failure=error;}
                    finally{await app.CloseMainAsync();}
                });
                Application.Run(app);
            }catch(Exception error){failure=error;}
            finally{app?.Dispose();}
        });
        thread.SetApartmentState(ApartmentState.STA);thread.Start();
        if(!thread.Join(TimeSpan.FromSeconds(90)))throw new TimeoutException("Native stopwatch test did not finish.");
        if(failure is not null)throw new Exception("Native stopwatch test failed",failure);
        Console.WriteLine($"{passed} native stopwatch checks passed.");
    }
    private static object Field(object value,string name)=>value.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(value)!;
    private static Task<string> Script(PreviewWindow window,string script)=>((WebView2)Field(window,"browser")).CoreWebView2.ExecuteScriptAsync(script);
    private static async Task<string?> Text(PreviewWindow window,string selector)=>JsonDocument.Parse(await Script(window,$"document.querySelector({JsonSerializer.Serialize(selector)}).textContent")).RootElement.GetString();
    private static async Task Until(Func<Task<bool>> predicate){using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(20));while(!await predicate())await Task.Delay(25,timeout.Token);}
    private sealed class DelayedStore:IStateStore
    {
        internal AppState State=new();
        internal Action? BeforeNextSave;
        public AppState Load()=>DataJson.Clone(State);
        public void Save(AppState state){var delay=BeforeNextSave;BeforeNextSave=null;delay?.Invoke();State=DataJson.Clone(state);}
    }
    private sealed class Registration:IHotKeyRegistration
    {
        public bool Register(nint window,int id,uint modifiers,uint key)=>true;
        public bool Unregister(nint window,int id)=>true;
    }
}
