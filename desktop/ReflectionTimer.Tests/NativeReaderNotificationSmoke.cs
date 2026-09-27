using System.Runtime.InteropServices;
using ReflectionTimer.Accessible;

// Exercises the notification provider API only, with a hidden synthetic form.
// Does not traverse the accessibility tree or launch/test JAWS or keyboard use.
static class NativeReaderNotificationSmoke
{
    internal static void Run()
    {
        Exception? failure=null;var passed=0;
        void Check(bool condition,string text){if(!condition)throw new Exception(text);passed++;Console.WriteLine("PASS "+text);}
        var thread=new Thread(()=>{
            try {
                using var owner=new Form{Text="Reflection Timer notification smoke",ShowInTaskbar=false};
                var foreground=GetForegroundWindow();
                Check(ScreenReaderAnnouncements.TryAnnounce(owner,"Timer paused with 2 minutes remaining.",false),"Windows accepts action/time notification from a hidden native provider");
                Check(ScreenReaderAnnouncements.TryAnnounce(owner,"Time-only view.",true),"Windows accepts supplementary view notification from the same provider");
                Check(!owner.Visible&&!owner.ShowInTaskbar&&GetForegroundWindow()==foreground,"Notifications neither show a window nor move foreground focus");
                Check(!ScreenReaderAnnouncements.TryAnnounce(owner,"",false),"Empty feedback does not generate a notification");
                owner.Dispose();
                Check(!ScreenReaderAnnouncements.TryAnnounce(owner,"Timer mode.",false),"Disposed providers fail safely for live-region fallback");
            } catch(Exception error){failure=error;}
        });
        thread.SetApartmentState(ApartmentState.STA);thread.Start();
        if(!thread.Join(TimeSpan.FromSeconds(20)))throw new TimeoutException("Native reader notification checks timed out.");
        if(failure is not null)throw new Exception("Native reader notification checks failed.",failure);
        Console.WriteLine($"{passed} native reader notification API checks passed; actual screen-reader speech not tested.");
    }
    [DllImport("user32.dll")]private static extern nint GetForegroundWindow();
}
