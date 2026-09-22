using System.Reflection;
using System.Text.Json;
using Microsoft.Web.WebView2.WinForms;
using ReflectionTimer.Accessible;
using ReflectionTimer.Core;
using ReflectionTimer.Desktop;

// Real WebView/host shortcut routing with synthetic, muted, disconnected data.
static class NativeSettingsSaveSmoke
{
    internal static void Run()
    {
        Exception? failure=null;var passed=0;
        void Check(bool value,string message){if(!value)throw new Exception(message);passed++;Console.WriteLine("PASS "+message);}
        var thread=new Thread(()=>{
            PreviewApplication? app=null;
            try{
                Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);Application.EnableVisualStyles();
                var store=new MemoryStore{State=new(){LoggingEnabled=false,Timer=new(){Volume=0},ShowFloatingTimer=false}};
                var session=new PreviewSession(store,isolatedProfile:true);
                app=new(session,Path.Combine(Path.GetTempPath(),"ReflectionTimer-SettingsKeys-"+Guid.NewGuid().ToString("N")),startInTray:true,profileName:"settings-keys-smoke",shortcutRegistration:new Registration());
                ((System.Windows.Forms.Timer)typeof(PreviewApplication).GetField("pulse",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(app)!).Stop();
                _=app.MainForm!.Handle;
                app.MainForm.BeginInvoke(async()=>{
                    try{
                        app.Open("main");var main=(PreviewWindow)app.MainForm;
                        await Ready(main);
                        Check(!NativeKey(main,Keys.Control|Keys.Enter),"Native Ctrl+Enter remains available to Timer controls outside Settings");
                        await Script(main,"document.querySelector('#tab-settings').click()");await Until(()=>Task.FromResult(Scope(main)));
                        var value=310;
                        foreach(var key in new[]{Keys.Enter,Keys.S})foreach(var focus in new[]{"settings-time-reached-seconds","sheet-url","theme","preview-sound-0","page-title"}){
                            value++;
                            await Script(main,$"document.querySelector('#settings-time-reached-seconds').value='{value}';document.querySelector('#settings-time-reached-seconds').dispatchEvent(new Event('input',{{bubbles:true}}));document.querySelector('#{focus}').tabIndex=0;document.querySelector('#{focus}').focus();document.querySelector('#status').textContent=''");
                            Check(NativeKey(main,Keys.Control|key),"Native Settings shortcut intercepted from "+focus+" / "+key);
                            await Until(()=>Task.FromResult(AudioSettings.From(session.Engine.Snapshot).TimeReachedSeconds==value));
                            await Until(async()=>await Bool(main,"document.querySelector('#status').textContent==='Settings saved.'"));
                            Check(AudioSettings.From(store.State).TimeReachedSeconds==value&&await Bool(main,$"document.activeElement.id==='{focus}'"),"Native shortcut durably saves the current input and retains focus: "+focus+" / "+key);
                        }
                        await Script(main,"document.querySelector('#guided-setup').click()");await Until(()=>Task.FromResult(!Scope(main)));
                        Check(!NativeKey(main,Keys.Control|Keys.Enter)&&!NativeKey(main,Keys.Control|Keys.S),"Settings accelerators leave an open setup dialog's keys alone");
                        await Script(main,"document.querySelector('#guided-dialog').close()");await Until(()=>Task.FromResult(Scope(main)));
                        await Script(main,"document.querySelector('#settings-time-reached-seconds').value='333';document.querySelector('#settings-time-reached-seconds').dispatchEvent(new Event('input',{bubbles:true}));document.querySelector('#status').textContent=''");
                        NativeKey(main,Keys.Control|Keys.S,release:false);await Until(async()=>await Bool(main,"document.querySelector('#status').textContent==='Settings saved.'"));
                        await Script(main,"document.querySelector('#settings-time-reached-seconds').value='334';document.querySelector('#settings-time-reached-seconds').dispatchEvent(new Event('input',{bubbles:true}))");
                        NativeKey(main,Keys.Control|Keys.S,release:false);await Script(main,"Promise.resolve()");
                        Check(AudioSettings.From(session.Engine.Snapshot).TimeReachedSeconds==333,"Holding a native save key cannot repeatedly save later edits");
                        Release(main,Keys.S);NativeKey(main,Keys.Control|Keys.S);await Until(()=>Task.FromResult(AudioSettings.From(session.Engine.Snapshot).TimeReachedSeconds==334));
                        Check(AudioSettings.From(store.State).TimeReachedSeconds==334,"Releasing the native key permits a deliberate next save");
                        await Script(main,"document.querySelector('#tab-timer').click()");await Until(()=>Task.FromResult(!Scope(main)));
                        Check(!NativeKey(main,Keys.Control|Keys.S),"Leaving Settings removes its native save interception");
                        Check(store.State.Connection.WebAppUrl==""&&store.State.Outbox.Count==0&&store.State.Timer.DurationSeconds==900,"Settings shortcut tests do not send reflections or alter the timer");
                        await Script(main,"document.querySelector('#settings-volume').value='42';document.querySelector('#settings-volume').dispatchEvent(new Event('input',{bubbles:true}))");
                        await main.FlushDraftAsync();
                        Check(store.State.Timer.Volume==42,"Native quit handshake durably flushes a pending master-volume drag");
                        store.Fail=true;
                        await Script(main,"document.querySelector('#settings-volume').value='63';document.querySelector('#settings-volume').dispatchEvent(new Event('input',{bubbles:true}))");
                        try {await main.FlushDraftAsync();Check(false,"Expected failed quit handshake");}catch(IOException){}
                        Check(store.State.Timer.Volume==42,"Failed native settings flush leaves the last durable volume intact");
                        await Until(async()=>await Bool(main,"!document.body.inert"));
                        store.Fail=false;await main.FlushDraftAsync();
                        Check(store.State.Timer.Volume==63,"Native settings flush retries the retained dirty value after storage recovers");
                    }catch(Exception error){failure=error;}
                    finally{await app.CloseMainAsync();}
                });
                Application.Run(app);
            }catch(Exception error){failure=error;}
            finally{app?.Dispose();}
        });
        thread.SetApartmentState(ApartmentState.STA);thread.Start();
        if(!thread.Join(TimeSpan.FromSeconds(120)))throw new TimeoutException("Native Settings shortcut checks timed out.");
        if(failure is not null)throw new Exception("Native Settings shortcut checks failed",failure);
        Console.WriteLine($"{passed} native Settings shortcut checks passed.");
    }
    private static bool NativeKey(PreviewWindow window,Keys keys,bool release=true){var key=new KeyEventArgs(keys);typeof(Control).GetMethod("OnKeyDown",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(Browser(window),[key]);if(release)Release(window,key.KeyCode);return key.Handled;}
    private static void Release(PreviewWindow window,Keys key)=>typeof(Control).GetMethod("OnKeyUp",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(Browser(window),[new KeyEventArgs(key)]);
    private static bool Scope(PreviewWindow window)=>(bool)typeof(PreviewWindow).GetField("settingsShortcutsActive",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(window)!;
    private static WebView2 Browser(PreviewWindow window)=>(WebView2)typeof(PreviewWindow).GetField("browser",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(window)!;
    private static Task Ready(PreviewWindow window)=>((TaskCompletionSource)typeof(PreviewWindow).GetField("interfaceReady",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(window)!).Task.WaitAsync(TimeSpan.FromSeconds(20));
    private static Task<string> Script(PreviewWindow window,string script)=>Browser(window).CoreWebView2.ExecuteScriptAsync(script);
    private static async Task<bool> Bool(PreviewWindow window,string script)=>JsonDocument.Parse(await Script(window,script)).RootElement.ValueKind==JsonValueKind.True;
    private static async Task Until(Func<Task<bool>> predicate){using var limit=new CancellationTokenSource(TimeSpan.FromSeconds(20));while(!await predicate())await Task.Delay(25,limit.Token);}
    private sealed class Registration:IHotKeyRegistration{public bool Register(nint window,int id,uint modifiers,uint key)=>true;public bool Unregister(nint window,int id)=>true;}
}
