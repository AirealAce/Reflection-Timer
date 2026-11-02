using System.Runtime.InteropServices;
using ReflectionTimer.Accessible;

// Opt-in only: asks an already-running NVDA to speak one fixed phrase. It does
// not start a reader, touch a personal profile, synthesize keys or show a window.
internal static class NativeNvdaAnnouncementSmoke
{
    internal const string Phrase="Reflection Timer NVDA announcement test. Keyboard focus has not moved.";
    internal static void Run()
    {
        Exception? failure=null;
        var thread=new Thread(()=>{
            try{
                using var context=new ApplicationContext();
                using var output=new NvdaScreenReaderOutput();
                using var poll=new System.Windows.Forms.Timer{Interval=40};
                using var deadline=new System.Windows.Forms.Timer{Interval=12000};
                Action? next=null;
                using var feedback=new KeyboardReleasedFeedback(callback=>{next=callback;poll.Start();},KeyboardReleasedFeedback.ShortcutKeysPressed,()=>Environment.TickCount64);
                poll.Tick+=(_,_)=>{poll.Stop();var callback=next;next=null;callback?.Invoke();};
                deadline.Tick+=(_,_)=>{deadline.Stop();failure=new TimeoutException("NVDA announcement test did not finish after shortcut release.");context.ExitThread();};
                var foreground=GetForegroundWindow();var keyboard=KeyboardFocus(foreground);
                feedback.Publish(async()=>{
                    try{
                        if(!await output.TryAnnounceAsync(Phrase,false).WaitAsync(TimeSpan.FromSeconds(5)))
                            throw new InvalidOperationException("An already-running NVDA did not accept the test phrase. No reader was started or configured.");
                        // NVDA owns playback once the request is accepted. Keep
                        // this test alive briefly so a listener can verify it.
                        await Task.Delay(4000);
                        if(GetForegroundWindow()!=foreground||KeyboardFocus(foreground)!=keyboard)
                            throw new InvalidOperationException("Foreground or keyboard focus changed during the listening interval; this run cannot establish focus preservation.");
                        Console.WriteLine("PASS NVDA accepted the fixed test phrase after key release; foreground and keyboard focus remained unchanged.");
                        Console.WriteLine("Actual speech audibility requires listener confirmation.");
                    }catch(Exception error){failure=error;}
                    finally{deadline.Stop();context.ExitThread();}
                });
                deadline.Start();Application.Run(context);
            }catch(Exception error){failure=error;}
        });
        thread.SetApartmentState(ApartmentState.STA);thread.Start();
        if(!thread.Join(TimeSpan.FromSeconds(15)))throw new TimeoutException("NVDA announcement test thread did not finish.");
        if(failure is not null)throw new Exception("Opt-in NVDA announcement test failed.",failure);
    }
    private static nint KeyboardFocus(nint window){var info=new GuiThreadInfo{Size=Marshal.SizeOf<GuiThreadInfo>()};return GetGUIThreadInfo(GetWindowThreadProcessId(window,out _),ref info)?info.Focus:0;}
    [StructLayout(LayoutKind.Sequential)]private struct GuiThreadInfo{internal int Size,Flags;internal nint Active,Focus,Capture,MenuOwner,MoveSize,Caret;internal int Left,Top,Right,Bottom;}
    [DllImport("user32.dll")]private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")]private static extern uint GetWindowThreadProcessId(nint window,out uint process);
    [DllImport("user32.dll")][return:MarshalAs(UnmanagedType.Bool)]private static extern bool GetGUIThreadInfo(uint thread,ref GuiThreadInfo info);
}
