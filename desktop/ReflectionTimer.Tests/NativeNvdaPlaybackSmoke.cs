using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using ReflectionTimer.Accessible;
using ReflectionTimer.Core;
using ReflectionTimer.Desktop;

// Opt-in listening check for the real default output pipeline. Everything that
// could persist/export/play app audio is synthetic or disabled; only the reader
// output is real. No visible app window, physical key or personal profile.
internal static class NativeNvdaPlaybackSmoke
{
    internal static void Run()
    {
        Exception? failure=null;
        var thread=new Thread(()=>{
            PreviewApplication? app=null;
            try{
                var foreground=GetForegroundWindow();var keyboard=KeyboardFocus(foreground);
                var now=DateTimeOffset.Now;
                var store=new MemoryStore{State=new(){LoggingEnabled=false,ShowAppView=false,ShowFloatingTimer=false,
                    ViewerAutoHide=false,VoiceAnnouncements=false,ConfirmBeforeReset=false,SessionEndPopups=false,
                    AutoSendIncompleteReflections=false,Csv=new(){Enabled=false},
                    Timer=new(){Volume=0,DurationSeconds=90,RemainingSeconds=90}}};
                // False opts into the production reader router only. This
                // session still loads/saves exclusively through MemoryStore.
                var session=new PreviewSession(store,()=>now,isolatedProfile:false);
                var speech=new SilentSpeech();
                app=new(session,Path.Combine(Path.GetTempPath(),"ReflectionTimer-NvdaPlayback-"+Guid.NewGuid().ToString("N")),
                    startInTray:true,profileName:"nvda-playback-smoke",shortcutRegistration:new Registration(),speech:speech);
                ((System.Windows.Forms.Timer)Field(app,"pulse")).Stop();
                ((System.Windows.Forms.Timer)Field(app,"focusPulse")).Stop();
                _=app.MainForm!.Handle;
                var keys=(PreviewShortcuts)Field(app,"shortcuts");
                var emitted=new List<(string Text,bool Supplementary)>();
                app.Services.SessionAnnouncement+=(text,extra)=>emitted.Add((text,extra));
                var initialViewers=ViewerState(app);
                app.MainForm.BeginInvoke(async()=>{
                    try{
                        // Consecutive committed actions share the real release
                        // queue: the pending start must be replaced by pause.
                        if(!keys.Dispatch(GlobalShortcut.HotKeyMessage,GlobalShortcut.TimerToggleId)
                            ||!keys.Dispatch(GlobalShortcut.HotKeyMessage,GlobalShortcut.TimerToggleId)
                            ||!TimerEngine.IsPaused(session.Engine.CurrentTimer))
                            throw new Exception("Synthetic production shortcut did not commit a paused timer.");
                        await Task.Delay(3000);
                        if(!emitted.SequenceEqual(new[]{("Timer paused with 1 minute 30 seconds remaining.",false)}))
                            throw new Exception("Production release queue did not emit exactly one detailed pause message.");
                        if(!keys.Dispatch(GlobalShortcut.HotKeyMessage,GlobalShortcut.TimerToggleId)||!session.Engine.CurrentTimer.IsRunning)
                            throw new Exception("Synthetic production shortcut did not resume the timer.");
                        await Task.Delay(5000);
                        var expected=new[]{("Timer paused with 1 minute 30 seconds remaining.",false),("Timer resumed with 1 minute 30 seconds remaining.",false)};
                        if(!emitted.SequenceEqual(expected))throw new Exception("Production feedback did not emit exactly the pause and resume messages.");
                        if(GetForegroundWindow()!=foreground||KeyboardFocus(foreground)!=keyboard||ViewerState(app)!=initialViewers)
                            throw new Exception("Foreground focus or hidden viewer state changed during the listening check.");
                        if(store.State.Outbox.Count!=0||store.State.Prompts.Count!=0||store.State.Csv.Enabled
                            ||store.State.Connection.WebAppUrl.Length!=0||store.State.LoggingEnabled||store.State.VoiceAnnouncements
                            ||store.State.Timer.Volume!=0||speech.Calls!=0)
                            throw new Exception("Listening check unexpectedly enabled app speech, logging, delivery or reflection output.");
                        Console.WriteLine("PASS Real production SessionVoice, modifier-release dispatcher and default reader router emitted exactly:");
                        foreach(var message in emitted)Console.WriteLine(message.Text);
                        Console.WriteLine("PASS Foreground/native keyboard focus and hidden viewer state remained unchanged; state stayed in memory and app audio stayed muted.");
                        Console.WriteLine("This verifies production message generation/routing, not audible reader playback; the listener must confirm what was heard.");
                    }catch(Exception error){failure=error;}
                    finally{app.MainForm?.Dispose();app.Dispose();Application.ExitThread();}
                });
                Application.Run(app);
            }catch(Exception error){failure=error;}
            finally{app?.Dispose();}
        });
        thread.SetApartmentState(ApartmentState.STA);thread.Start();
        if(!thread.Join(TimeSpan.FromSeconds(15)))throw new TimeoutException("Opt-in production NVDA playback check timed out.");
        if(failure is not null)throw new Exception("Opt-in production NVDA playback check failed.",failure);
    }
    private static object Field(object value,string name)=>value.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(value)!;
    private static string ViewerState(PreviewApplication app)=>JsonSerializer.Serialize(new{app.AppViewMayShow,
        app.Session.Engine.SettingsSnapshot.ShowAppView,app.Session.Engine.SettingsSnapshot.ShowFloatingTimer,
        Views=((List<PreviewWindow>)Field(app,"windows")).Select(view=>new{view.View,view.Visible,view.Bounds,view.WindowState,view.IsTimeOnly})});
    private static nint KeyboardFocus(nint window){var info=new GuiThreadInfo{Size=Marshal.SizeOf<GuiThreadInfo>()};return GetGUIThreadInfo(GetWindowThreadProcessId(window,out _),ref info)?info.Focus:0;}
    private sealed class Registration:IHotKeyRegistration{public bool Register(nint window,int id,uint modifiers,uint key)=>true;public bool Unregister(nint window,int id)=>true;}
    private sealed class SilentSpeech:IVoiceOutput
    {internal int Calls;public void Speak(string text,int volume)=>Calls++;public void SpeakSupplement(string text,int volume)=>Calls++;public void SetVolume(int volume){}public void Stop(){}public bool TakeFailure()=>false;public void Dispose(){}}
    [StructLayout(LayoutKind.Sequential)]private struct GuiThreadInfo{internal int Size,Flags;internal nint Active,Focus,Capture,MenuOwner,MoveSize,Caret;internal int Left,Top,Right,Bottom;}
    [DllImport("user32.dll")]private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")]private static extern uint GetWindowThreadProcessId(nint window,out uint process);
    [DllImport("user32.dll")][return:MarshalAs(UnmanagedType.Bool)]private static extern bool GetGUIThreadInfo(uint thread,ref GuiThreadInfo info);
}
