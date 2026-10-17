using System.Reflection;
using Microsoft.Web.WebView2.WinForms;
using ReflectionTimer.Accessible;
using ReflectionTimer.Core;
using ReflectionTimer.Desktop;

// Isolated interface loading only: no production profile, network delivery,
// keyboard input, accessibility-tree traversal or screen-reader process.
static class NativeInterfaceLoadingSmoke
{
    internal static void Run()
    {
        Exception? failure=null;var passed=0;
        void Check(bool condition,string message){if(!condition)throw new Exception(message);passed++;Console.WriteLine("PASS "+message);}
        var thread=new Thread(()=>{
            PreviewApplication? app=null;
            var errors=new List<string>();
            try {
                var store=new MemoryStore{State=new(){Theme=AppColorTheme.Glamour,LoggingEnabled=false,ShowAppView=false,ShowFloatingTimer=false,
                    Timer=new(){Mode=SessionMode.Stopwatch,SessionId=Guid.NewGuid(),ElapsedMilliseconds=2300,Volume=0},
                    Prompts=[new(Guid.NewGuid(),DateTimeOffset.Now.ToUnixTimeMilliseconds(),60,0,false,"Synthetic saved draft")],
                    Outbox=[new(){Message="Synthetic retained entry",LocalOnly=true,Status=DeliveryStatus.Sent}]}};
                var expected=System.Text.Json.JsonSerializer.Serialize(store.State,DataJson.Options);
                var session=new PreviewSession(store,isolatedProfile:true);
                var retainedTimer=session.Engine.CurrentTimer;
                app=new(session,Path.Combine(Path.GetTempPath(),"ReflectionTimer-InterfaceLoading-"+Guid.NewGuid().ToString("N")),startInTray:true,
                    profileName:"interface-loading-smoke",shortcutRegistration:new Registration());
                ((System.Windows.Forms.Timer)Field(app,"pulse")).Stop();
                var main=(PreviewWindow)app.MainForm!;var browser=(WebView2)Field(main,"browser");
                browser.CoreWebView2InitializationCompleted+=(_,result)=>{
                    if(!result.IsSuccess)return;
                    browser.CoreWebView2.GetDevToolsProtocolEventReceiver("Runtime.exceptionThrown").DevToolsProtocolEventReceived+=(_,error)=>errors.Add(error.ParameterObjectAsJson);
                    _=browser.CoreWebView2.CallDevToolsProtocolMethodAsync("Runtime.enable","{}");
                };
                _=main.Handle;
                main.BeginInvoke(async()=>{
                    try {
                        await main.RestartBrowserAsync();
                        Check(Ready(main).IsCompletedSuccessfully,"Cold startup completes the native/document handshake");
                        Check(errors.Count==0,"Cold startup has no JavaScript exceptions");
                        Check(System.Text.Json.JsonSerializer.Serialize(store.State,DataJson.Options)==expected,"Cold startup preserves all saved preferences");
                        Invoke(main,"ResetReadiness");Invoke(main,"ShowStartupTimeout");
                        var pending=Ready(main);
                        Check(!pending.IsCompleted&&OptionalField(main,"recoveryPanel") is Panel&&OptionalField(main,"retryRecovery") is Action,"Startup deadline leaves the handshake pending and offers a retry");
                        await browser.CoreWebView2.ExecuteScriptAsync("window.chrome.webview.postMessage({requestId:'late-startup',action:'ready',data:{}})");
                        await Until(()=>Ready(main).IsCompletedSuccessfully&&OptionalField(main,"recoveryPanel") is null);
                        Check(ReferenceEquals(pending,Ready(main)),"A late settings load completes the original handshake instead of poisoning it");
                        Check(JsonBoolean(await browser.CoreWebView2.ExecuteScriptAsync("document.documentElement.dataset.theme==='3'&&document.querySelector('#theme').value==='3'")),"Late startup reveals the saved theme and Settings controls");
                        Check(System.Text.Json.JsonSerializer.Serialize(store.State,DataJson.Options)==expected,"Late startup cannot replace saved preferences with defaults");
                        Invoke(main,"ResetReadiness");Invoke(main,"ShowStartupTimeout");
                        ((Action)Field(main,"retryRecovery"))();
                        await Until(()=>Ready(main).IsCompletedSuccessfully&&OptionalField(main,"recoveryPanel") is null);
                        Check(ReferenceEquals(browser,Field(main,"browser")),"Retry reloads only this view without replacing the browser environment");
                        Check(System.Text.Json.JsonSerializer.Serialize(store.State,DataJson.Options)==expected,"Retry preserves the complete profile and timer state");
                        Check(session.Engine.CurrentTimer==retainedTimer,"Retry keeps the paused stopwatch's exact elapsed time and session identity");
                        Invoke(main,"ShowStartupTimeout");
                        Check(OptionalField(main,"recoveryPanel") is null,"An already-ready view cannot be covered by a stale timeout");
                        Invoke(main,"ResetReadiness");Invoke(main,"ShowFailure","Synthetic resource failure.");
                        Invoke(main,"ShowStartupTimeout");
                        Check(Ready(main).IsFaulted&&((TextBox)Field(main,"recoveryText")).Text=="Synthetic resource failure.","A specific initialization failure is not replaced by a settings-timeout message");
                        _=Ready(main).Exception;
                        Check(errors.Count==0&&!main.Visible,"Recovery remains exception-free without showing or focusing a test window");
                    } catch(Exception error){failure=new Exception(error.Message+" JavaScript diagnostics: "+string.Join("; ",errors),error);}
                    finally {main.ClosePermanently();Application.ExitThread();}
                });
                Application.Run(app);
            } catch(Exception error){failure=error;}
            finally {app?.Dispose();}
        });
        thread.SetApartmentState(ApartmentState.STA);thread.Start();
        if(!thread.Join(TimeSpan.FromSeconds(50)))throw new TimeoutException("Interface loading smoke timed out.");
        if(failure is not null)throw new Exception("Interface loading checks failed.",failure);
        Console.WriteLine($"{passed} interface loading checks passed.");
    }
    private static object Field(object target,string name)=>target.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(target)!;
    private static object? OptionalField(object target,string name)=>target.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(target);
    private static void Invoke(object target,string name,params object[] arguments)=>target.GetType().GetMethod(name,BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(target,arguments);
    private static bool JsonBoolean(string result)=>System.Text.Json.JsonSerializer.Deserialize<bool>(result);
    private static async Task Until(Func<bool> ready){using var limit=new CancellationTokenSource(TimeSpan.FromSeconds(15));while(!ready())await Task.Delay(25,limit.Token);}
    private static Task Ready(PreviewWindow window)=>((TaskCompletionSource)Field(window,"interfaceReady")).Task;
    private sealed class Registration:IHotKeyRegistration{public bool Register(nint window,int id,uint modifiers,uint key)=>true;public bool Unregister(nint window,int id)=>true;}
}
