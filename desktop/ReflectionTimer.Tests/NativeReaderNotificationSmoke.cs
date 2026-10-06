using System.Runtime.InteropServices;
using System.Reflection;
using System.Text.Json;
using ReflectionTimer.Accessible;
using ReflectionTimer.Core;
using ReflectionTimer.Desktop;

// Exercises the notification API and global shortcut routing with hidden forms.
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
                var store=new MemoryStore{State=new(){LoggingEnabled=false,ShowAppView=false,ShowFloatingTimer=false,Timer=new(){Volume=0},FocusMode=new(){IdleEnabled=true}}};
                var session=new PreviewSession(store,isolatedProfile:true);
                var messages=new List<(string Text,bool Supplementary,bool Accepted)>();
                var speech=new Speech();
                using var app=new PreviewApplication(session,Path.Combine(Path.GetTempPath(),"ReflectionTimer-FocusReader-"+Guid.NewGuid().ToString("N")),startInTray:true,profileName:"focus-reader-smoke",shortcutRegistration:new Registration(),screenReaderNotification:(provider,text,extra)=>{
                    var accepted=ScreenReaderAnnouncements.TryAnnounce(provider,text,extra);
                    messages.Add((text,extra,accepted));return accepted;
                },speech:speech);
                var keys=(PreviewShortcuts)typeof(PreviewApplication).GetField("shortcuts",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(app)!;
                foreach(var mode in new[]{SessionMode.Timer,SessionMode.Stopwatch}){
                    session.Engine.SwitchMode(mode);messages.Clear();
                    var timer=session.Engine.CurrentTimer;var focus=store.State.FocusMode;
                    Check(keys.Dispatch(GlobalShortcut.HotKeyMessage,GlobalShortcut.FocusToggleId)&&messages.SequenceEqual(new[]{("Focus mode on.",false,true)}),mode+": global Focus on reaches the native reader provider exactly once with app voice off and sound muted");
                    Check(keys.Dispatch(GlobalShortcut.HotKeyMessage,GlobalShortcut.FocusToggleId)&&messages.SequenceEqual(new[]{("Focus mode on.",false,true),("Focus mode off.",false,true)}),mode+": global Focus off reaches the same native provider exactly once");
                    Check(!app.MainForm!.Visible&&GetForegroundWindow()==foreground&&session.Engine.CurrentTimer==timer&&JsonSerializer.Serialize(store.State.FocusMode)==JsonSerializer.Serialize(focus),mode+": reader feedback preserves foreground focus, hidden views, session and saved Focus options");
                }
                Check(!store.State.VoiceAnnouncements&&store.State.Timer.Volume==0&&store.State.Connection.WebAppUrl==""&&store.State.Outbox.Count==0,"Focus notification tests neither enable optional app speech nor touch production delivery");
                session.Engine.SetVoiceAnnouncements(true);session.Engine.SetAppVolume(55);
                foreach(var mode in new[]{SessionMode.Timer,SessionMode.Stopwatch}){
                    session.Engine.SwitchMode(mode);messages.Clear();speech.Messages.Clear();
                    keys.Dispatch(GlobalShortcut.HotKeyMessage,GlobalShortcut.FocusToggleId);
                    keys.Dispatch(GlobalShortcut.HotKeyMessage,GlobalShortcut.FocusToggleId);
                    Check(messages.Select(m=>m.Text).SequenceEqual(new[]{"Focus mode on.","Focus mode off."})&&messages.All(m=>m.Accepted)
                        &&speech.Messages.SequenceEqual(messages.Select(m=>m.Text)),mode+": global Focus toggle reaches optional voice and the native reader provider exactly once per change");
                }
                messages.Clear();speech.Messages.Clear();
                keys.Dispatch(GlobalShortcut.HotKeyMessage,GlobalShortcut.ReflectionFocusId);
                Check(messages.Single().Text=="No pending reflection. Start a timer before making a check-in."&&speech.Messages.SequenceEqual(messages.Select(m=>m.Text)),"A reflection shortcut with no session gives accessible and optional spoken guidance without opening App");
                Check(!app.MainForm!.Visible&&GetForegroundWindow()==foreground&&store.State.Outbox.Count==0,"Optional shortcut feedback preserves hidden views, external focus and isolated data");
                app.MainForm!.Dispose();
            } catch(Exception error){failure=error;}
        });
        thread.SetApartmentState(ApartmentState.STA);thread.Start();
        if(!thread.Join(TimeSpan.FromSeconds(20)))throw new TimeoutException("Native reader notification checks timed out.");
        if(failure is not null)throw new Exception("Native reader notification checks failed.",failure);
        Console.WriteLine($"{passed} native reader notification API checks passed; actual screen-reader speech not tested.");
    }
    [DllImport("user32.dll")]private static extern nint GetForegroundWindow();
    private sealed class Registration:IHotKeyRegistration{public bool Register(nint window,int id,uint modifiers,uint key)=>true;public bool Unregister(nint window,int id)=>true;}
    private sealed class Speech:IVoiceOutput
    {
        internal readonly List<string> Messages=[];
        public void Speak(string text,int volume)=>Messages.Add(text);
        public void SetVolume(int volume){}
        public void Stop(){}
        public bool TakeFailure()=>false;
        public void Dispose(){}
    }
}
