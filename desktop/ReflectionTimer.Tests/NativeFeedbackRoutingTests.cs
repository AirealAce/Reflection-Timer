using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using ReflectionTimer.Accessible;
using ReflectionTimer.Core;
using ReflectionTimer.Desktop;

// Hidden native hosts, fake hotkey registration, fake reader/voice and an
// explicit release queue. No screen reader, speakers, real profile or website.
// Browser live-region behavior is checked separately by feedback-announcements.cjs.
internal static class NativeFeedbackRoutingTests
{
    internal static void Run(Action<bool,string> check)
    {
        Exception? failure=null;
        var thread=new Thread(()=>{
            PreviewApplication? app=null;
            try{
                var uiThread=Environment.CurrentManagedThreadId;
                var now=DateTimeOffset.Parse("2026-10-29T09:00:00-04:00");
                var store=new MemoryStore{State=new(){LoggingEnabled=false,ShowAppView=false,ShowFloatingTimer=false,
                    ViewerAutoHide=false,VoiceAnnouncements=false,ConfirmBeforeReset=false,SessionEndPopups=false,
                    AutoSendIncompleteReflections=false,Csv=new(){Enabled=false},Timer=new(){Volume=0,DurationSeconds=90,RemainingSeconds=90},
                    FocusMode=new(){IdleEnabled=true}}};
                var session=new PreviewSession(store,()=>now,isolatedProfile:true);
                var pending=new Queue<(Action Action,bool Supplementary)>();
                var dispatchThreads=new List<int>();var readers=new List<(Control? Owner,string Text,bool Supplementary,int Thread)>();
                var accepts=true;var speech=new Speech();
                app=new(session,Path.Combine(Path.GetTempPath(),"ReflectionTimer-FeedbackSweep-"+Guid.NewGuid().ToString("N")),
                    startInTray:true,profileName:"feedback-sweep",shortcutRegistration:new Registration(),speech:speech,
                    screenReaderNotification:(owner,text,extra)=>{readers.Add((owner,text,extra,Environment.CurrentManagedThreadId));return accepts;},
                    feedbackDispatcher:(action,extra)=>{dispatchThreads.Add(Environment.CurrentManagedThreadId);pending.Enqueue((action,extra));});
                ((System.Windows.Forms.Timer)Field(app,"pulse")).Stop();
                ((System.Windows.Forms.Timer)Field(app,"focusPulse")).Stop();
                _=app.MainForm!.Handle;
                var compact=(PreviewWindow)typeof(PreviewApplication).GetMethod("Create",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(app,["compact",null])!;
                _=compact.Handle;
                var keys=(PreviewShortcuts)Field(app,"shortcuts");
                string Windows()=>JsonSerializer.Serialize(((List<PreviewWindow>)Field(app,"windows")).Select(w=>new{w.View,w.Visible,w.Bounds,w.IsTimeOnly,w.WindowState}));
                async Task Drain(){var count=pending.Count;for(var i=0;i<count;i++)pending.Dequeue().Action();await Task.Yield();}
                async Task Clear(){await Drain();readers.Clear();speech.Messages.Clear();dispatchThreads.Clear();}
                app.MainForm.BeginInvoke(async()=>{
                    try{
                        var foreground=GetForegroundWindow();var keyboard=KeyboardFocus(foreground);
                        foreach(var layout in new[]{"App preference","Compact preference","Time-only preference","All viewers hidden"}){
                            session.Engine.SetAppViewVisibility(layout=="App preference");
                            compact.SetCompactMode(layout=="Time-only preference",false);
                            session.Engine.SetFloatingTimer(layout is "Compact preference" or "Time-only preference");
                            foreach(var mode in Enum.GetValues<SessionMode>()){
                                session.Engine.SwitchMode(mode);session.Engine.Reset(90);await Clear();
                                var windowState=Windows();
                                var labels=mode==SessionMode.Stopwatch?new[]{"Stopwatch started","Stopwatch paused","Stopwatch resumed"}:new[]{"Timer started","Timer paused","Timer resumed"};
                                foreach(var expected in labels){
                                    var before=readers.Count;
                                    check(keys.Dispatch(GlobalShortcut.HotKeyMessage,GlobalShortcut.TimerToggleId),layout+"/"+mode+": playback uses the registered shortcut route");
                                    check(readers.Count==before&&pending.Count==1,layout+"/"+mode+": committed playback waits for the feedback release queue");
                                    await Drain();
                                    check(readers.Count==before+1&&readers.Last().Text.StartsWith(expected,StringComparison.Ordinal)&&!readers.Last().Supplementary,
                                        layout+"/"+mode+": "+expected+" reaches the reader exactly once");
                                }
                                check(speech.Messages.Count==0,layout+"/"+mode+": app voice off and muted audio do not suppress reader transitions");
                                await Clear();var beforeStatus=JsonSerializer.Serialize(session.Engine.Snapshot);
                                for(var i=0;i<2;i++){
                                    keys.Dispatch(GlobalShortcut.HotKeyMessage,GlobalShortcut.TimerStatusId);await Drain();
                                }
                                check(readers.Count==2&&readers[0].Text==readers[1].Text&&JsonSerializer.Serialize(session.Engine.Snapshot)==beforeStatus,
                                    layout+"/"+mode+": repeated identical status requests both announce without changing the session");
                                session.Engine.Pause();await Clear();
                                keys.Dispatch(GlobalShortcut.HotKeyMessage,GlobalShortcut.ResetTimerId);
                                await Until(()=>pending.Count>0);await Drain();
                                check(readers.Count==1&&readers[0].Text.StartsWith(mode==SessionMode.Stopwatch?"Stopwatch reset":"Timer reset",StringComparison.Ordinal),
                                    layout+"/"+mode+": global reset confirmation reaches the same reader output once");
                                await Clear();keys.Dispatch(GlobalShortcut.HotKeyMessage,GlobalShortcut.ModeToggleId);await Drain();
                                check(readers.Count==1&&readers[0].Text==(mode==SessionMode.Timer?"Stopwatch mode.":"Timer mode."),
                                    layout+"/"+mode+": mode changes use shared reader feedback");
                                await Clear();keys.Dispatch(GlobalShortcut.HotKeyMessage,GlobalShortcut.FocusToggleId);await Drain();
                                check(readers.Count==1&&readers[0].Text=="Focus mode on.",layout+"/"+mode+": Focus on reaches shared reader feedback");
                                await Clear();keys.Dispatch(GlobalShortcut.HotKeyMessage,GlobalShortcut.FocusToggleId);await Drain();
                                check(readers.Count==1&&readers[0].Text=="Focus mode off.",layout+"/"+mode+": Focus off reaches shared reader feedback");
                                check(Windows()==windowState&&GetForegroundWindow()==foreground&&KeyboardFocus(foreground)==keyboard
                                    &&((List<PreviewWindow>)Field(app,"windows")).All(w=>!w.Visible),
                                    layout+"/"+mode+": feedback preserves hidden documents, saved viewer layout and external native focus");
                            }
                        }
                        await Clear();
                        session.Engine.SetVoiceAnnouncements(true);session.Engine.SetAppVolume(55);await Clear();
                        app.Announce("Settings saved.");
                        check(readers.Count==0&&pending.Count==1,"General confirmations use the shared delayed reader queue");
                        await Drain();
                        check(readers.Count==1&&readers[0].Text=="Settings saved."&&speech.Messages.Count==0,
                            "General confirmations reach the reader without enabling optional app speech for every status");
                        await Clear();
                        await Task.Run(()=>app.Announce("Synthetic background delivery completed."));
                        await Until(()=>pending.Count>0);await Drain();
                        check(readers.Count==1&&readers[0].Text=="Synthetic background delivery completed."
                            &&readers[0].Thread==uiThread&&dispatchThreads.All(id=>id==uiThread),
                            "Worker completions marshal both scheduling and native-reader delivery onto the UI thread");
                        await Clear();app.Announce("Could not save settings. Your previous values are retained.");await Drain();
                        check(readers.Count==1&&readers[0].Text.StartsWith("Could not save settings.",StringComparison.Ordinal),
                            "General failure guidance reaches the same reader path as success confirmations");
                        await Clear();
                        accepts=false;app.Announce("A reader-declined general message.");await Drain();
                        check(readers.Count==1&&readers[0].Text=="A reader-declined general message.",
                            "A declined general reader attempt falls back without recursively notifying the native reader twice");
                        await Clear();session.Engine.SwitchMode(SessionMode.Timer);session.Engine.Reset(90);await Clear();
                        session.Engine.Start(90,false,55);await Drain();
                        check(readers.Count==1&&readers[0].Text.StartsWith("Timer started",StringComparison.Ordinal),
                            "A declined session notification falls back without a second native reader attempt");
                        accepts=true;session.Engine.Pause();session.Engine.SetAppVolume(0);session.Engine.SetVoiceAnnouncements(false);await Clear();
                        var completed=(Task<bool>)typeof(PreviewWindow).GetMethod("HandleSettings",BindingFlags.Instance|BindingFlags.NonPublic)!
                            .Invoke(app.MainForm,["settingsSaveComplete",JsonSerializer.SerializeToElement(new{}),"settings-complete-probe"])!;
                        check(await completed&&pending.Count==1,"Explicit Settings completion queues one native confirmation after the batch is saved");
                        await Drain();
                        check(readers.Count==1&&readers[0].Text=="Settings saved."&&speech.Messages.Count==0,
                            "Settings button and keyboard completion have one canonical reader confirmation with app voice off");
                        await Clear();
                        await app.Services.Sync(true);await Drain();
                        check(readers.Count==1&&readers[0].Text=="No entries are waiting for an enabled logging destination.",
                            "The actual empty-Outbox delivery guidance reaches the reader without contacting a destination");
                        await Clear();
                        var prompt=session.Engine.TestPrompt();
                        app.Announce(session.Execute("skip",JsonSerializer.SerializeToElement(new{id=prompt})));await Drain();
                        check(readers.Count==1&&readers[0].Text=="Reflection skipped.",
                            "The actual reflection Skip command reaches general reader feedback after it is committed");
                        await Clear();
                        session.Engine.Start(2,false,0,lowTime:new(){Enabled=true,ThresholdSeconds=1});await Clear();
                        now=now.AddSeconds(1);session.Tick();await Drain();
                        check(readers.Count==1&&readers[0].Text=="Low time. 1 second remaining.",
                            "Low-time boundary guidance uses native reader output while audio remains muted");
                        await Clear();now=now.AddSeconds(1);session.Tick();await Drain();
                        check(readers.Count==1&&readers[0].Text=="Session finished. A reflection is available under Pending reflections.",
                            "Natural timer completion and available-reflection guidance enter the general reader path once");
                        session.Engine.SwitchMode(SessionMode.Stopwatch);session.Engine.SetTimeReached(true,1);session.Engine.StartStopwatch();await Clear();
                        now=now.AddSeconds(1);session.Tick();await Drain();
                        check(readers.Count==1&&readers[0].Text=="Time reached. 1 second elapsed.",
                            "The stopwatch time-reached threshold uses the same general reader route");
                        check(!store.State.Csv.Enabled&&store.State.Connection.WebAppUrl.Length==0&&store.State.Outbox.Count==0
                            &&!store.State.LoggingEnabled&&GetForegroundWindow()==foreground&&KeyboardFocus(foreground)==keyboard,
                            "Cross-feature checks remain disconnected, muted and isolated without moving the user's foreground focus");
                        await Clear();app.Announce("Pending during shutdown.");session.Engine.Reset();
                        check(pending.Count>0,"Shutdown coverage includes queued general and committed-session feedback");
                        app.MainForm.Dispose();app.Dispose();await Drain();
                        check(readers.Count==0&&speech.Messages.Count==0,"Disposal makes queued reader and optional-voice actions harmless");
                    }catch(Exception error){failure=error;}
                    finally{if(app.MainForm is {IsDisposed:false})app.MainForm.Dispose();app.Dispose();Application.ExitThread();}
                });
                Application.Run(app);
            }catch(Exception error){failure=error;}
            finally{app?.Dispose();}
        });
        thread.SetApartmentState(ApartmentState.STA);thread.Start();
        if(!thread.Join(TimeSpan.FromSeconds(25)))throw new TimeoutException("Cross-feature native feedback routing checks timed out.");
        if(failure is not null)throw new Exception("Cross-feature native feedback routing checks failed.",failure);
    }
    private static object Field(object value,string name)=>value.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(value)!;
    private static async Task Until(Func<bool> condition){using var limit=new CancellationTokenSource(TimeSpan.FromSeconds(5));while(!condition())await Task.Delay(5,limit.Token);}
    private static nint KeyboardFocus(nint window){var info=new GuiThreadInfo{Size=Marshal.SizeOf<GuiThreadInfo>()};return GetGUIThreadInfo(GetWindowThreadProcessId(window,out _),ref info)?info.Focus:0;}
    private sealed class Registration:IHotKeyRegistration{public bool Register(nint window,int id,uint modifiers,uint key)=>true;public bool Unregister(nint window,int id)=>true;}
    private sealed class Speech:IVoiceOutput
    {internal readonly List<string> Messages=[];public void Speak(string text,int volume)=>Messages.Add(text);public void SpeakSupplement(string text,int volume)=>Messages.Add(text);public void SetVolume(int volume){}public void Stop(){}public bool TakeFailure()=>false;public void Dispose(){}}
    [StructLayout(LayoutKind.Sequential)]private struct GuiThreadInfo{internal int Size,Flags;internal nint Active,Focus,Capture,MenuOwner,MoveSize,Caret;internal int Left,Top,Right,Bottom;}
    [DllImport("user32.dll")]private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")]private static extern uint GetWindowThreadProcessId(nint window,out uint process);
    [DllImport("user32.dll")][return:MarshalAs(UnmanagedType.Bool)]private static extern bool GetGUIThreadInfo(uint thread,ref GuiThreadInfo info);
}
