using System.Reflection;
using System.Text.Json;
using Microsoft.Web.WebView2.WinForms;
using ReflectionTimer.Accessible;
using ReflectionTimer.Core;
using ReflectionTimer.Desktop;

// Real pulse, settings bridge and reflection-window policy. Synthetic profile,
// muted audio, fake hotkey registration; no production data, Sheets or JAWS.
static class NativePopupPolicySmoke
{
    internal static void Run()
    {
        Exception? failure=null;var passed=0;
        void Check(bool ok,string message){if(!ok)throw new Exception(message);passed++;Console.WriteLine("PASS "+message);}
        var thread=new Thread(()=>{
            try {
                var now=DateTimeOffset.Now;
                var store=new MemoryStore{State=new(){ShowAppView=false,ShowFloatingTimer=false,AutoSendIncompleteReflections=false,LoggingEnabled=false,Timer=new(){Volume=0}}};
                var session=new PreviewSession(store,()=>now,isolatedProfile:true);
                using var app=new PreviewApplication(session,Path.Combine(Path.GetTempPath(),"ReflectionTimer-PopupNative-"+Guid.NewGuid().ToString("N")),
                    startInTray:true,profileName:"popup-policy",shortcutRegistration:new Registration());
                var main=(PreviewWindow)app.MainForm!;_=main.Handle;
                main.BeginInvoke(async()=>{
                    try {
                        app.Open("main");await Ready(main);
                        await Script(main,"document.querySelector('#tab-settings').click()");
                        await Until(async()=>await Bool(main,"!document.querySelector('#voice-announcements').disabled"));
                        Check(await Bool(main,"document.querySelector('#sessionEndPopups').checked"),"Settings initially displays automatic popups on");
                        await Script(main,"document.querySelector('#sessionEndPopups').click()");
                        await Until(()=>Task.FromResult(!store.State.SessionEndPopups));
                        Check(!session.Engine.SettingsSnapshot.SessionEndPopups,"Changing the checkbox saves the native preference immediately");
                        await Script(main,"document.querySelector('#save-settings').click()");
                        await Until(async()=>await Bool(main,"document.querySelector('#status').textContent==='Settings saved.'"));
                        Check(!store.State.SessionEndPopups,"Save settings retains popup opt-out");
                        app.HideAppView();session.Engine.Start(3,true,0);now=now.AddSeconds(3);
                        await Until(()=>Task.FromResult(session.Engine.Snapshot.Prompts.Count==1));
                        await Task.Delay(150);
                        Check(Windows(app).All(w=>w.View!="reflection"),"Real timer pulse does not create a reflection WebView when automatic popups are off");
                        Check(!main.Visible&&session.Engine.CurrentTimer.IsRunning,"Suppressed completion keeps App hidden and auto-start running");
                        var pending=session.Engine.Snapshot.Prompts.Single().Id;
                        app.Open("reflection",pending);
                        await Until(()=>Task.FromResult(Windows(app).Any(w=>w.View=="reflection"&&w.Visible&&w.PromptId==pending)));
                        Check(true,"Manually opening a suppressed reflection still displays its editor");
                        var reflection=Windows(app).Single(w=>w.View=="reflection");reflection.CloseAfterSave();
                        session.Engine.SetSessionEndPopups(true);now=now.AddSeconds(3);
                        await Until(()=>Task.FromResult(Windows(app).Any(w=>w.View=="reflection"&&w.Visible&&w.PromptId!=pending)));
                        Check(session.Engine.Snapshot.Prompts.Count==2,"Re-enabled automatic completion displays the next reflection and retains the older one");
                        session.Engine.SetSessionEndPopups(false);session.Engine.Pause();
                        session.Engine.SaveSchedule(null,now.AddSeconds(1),2,false,0);
                        now=now.AddSeconds(1);await Until(()=>Task.FromResult(session.Engine.CurrentTimer.IsRunning&&session.Engine.CurrentTimer.DurationSeconds==2));
                        Windows(app).Single(w=>w.View=="reflection").CloseAfterSave();
                        now=now.AddSeconds(2);await Until(()=>Task.FromResult(session.Engine.Snapshot.Prompts.Count==3));await Task.Delay(150);
                        Check(Windows(app).All(w=>w.View!="reflection"||!w.Visible),"Scheduled completion also obeys popup opt-out");
                    } catch(Exception error){failure=error;}
                    finally {await app.CloseMainAsync();}
                });
                Application.Run(app);
            } catch(Exception error){failure=error;}
        });
        thread.SetApartmentState(ApartmentState.STA);thread.Start();
        if(!thread.Join(TimeSpan.FromSeconds(55)))throw new TimeoutException("Native popup policy checks timed out.");
        if(failure is not null)throw new Exception("Native popup policy checks failed.",failure);
        Console.WriteLine($"{passed} native popup policy checks passed.");
    }
    private static List<PreviewWindow> Windows(PreviewApplication app)=>(List<PreviewWindow>)typeof(PreviewApplication).GetField("windows",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(app)!;
    private static Task Ready(PreviewWindow window)=>((TaskCompletionSource)typeof(PreviewWindow).GetField("interfaceReady",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(window)!).Task.WaitAsync(TimeSpan.FromSeconds(15));
    private static Task<string> Script(PreviewWindow window,string script)=>((WebView2)typeof(PreviewWindow).GetField("browser",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(window)!).CoreWebView2.ExecuteScriptAsync(script);
    private static async Task<bool> Bool(PreviewWindow window,string script)=>JsonDocument.Parse(await Script(window,script)).RootElement.GetBoolean();
    private static async Task Until(Func<Task<bool>> test){using var limit=new CancellationTokenSource(TimeSpan.FromSeconds(12));while(!await test())await Task.Delay(25,limit.Token);}
    private sealed class Registration:IHotKeyRegistration
    {
        public bool Register(nint handle,int id,uint modifiers,uint key)=>true;
        public bool Unregister(nint handle,int id)=>true;
    }
}
