using System.Reflection;
using System.Text.Json;
using Microsoft.Web.WebView2.WinForms;
using ReflectionTimer.Accessible;
using ReflectionTimer.Core;
using ReflectionTimer.Desktop;

// Real WebViews and timer bridge, isolated from installed settings and Sheets.
static class NativeResetReloadSmoke
{
    internal static void Run()
    {
        Exception? failure=null;var passed=0;
        void Check(bool value,string message){if(!value)throw new Exception(message);passed++;Console.WriteLine("PASS "+message);}
        var thread=new Thread(()=>{
            PreviewApplication? app=null;
            try{
                Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);Application.EnableVisualStyles();
                var now=DateTimeOffset.Now;
                var store=new MemoryStore{State=new AppState{LoggingEnabled=false,AutoSendIncompleteReflections=false,Timer=new(){Volume=0}}};
                var session=new PreviewSession(store,()=>now,isolatedProfile:true);
                app=new(session,Path.Combine(Path.GetTempPath(),"ReflectionTimer-ResetSmoke-"+Guid.NewGuid().ToString("N")),startInTray:true,profileName:"reset-smoke",shortcutRegistration:new Registration());
                ((System.Windows.Forms.Timer)typeof(PreviewApplication).GetField("pulse",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(app)!).Stop();
                _=app.MainForm!.Handle;
                app.MainForm.BeginInvoke(async()=>{
                    try{
                        app.Open("main");app.Open("compact");
                        var windows=(List<PreviewWindow>)typeof(PreviewApplication).GetField("windows",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(app)!;
                        var main=windows.Single(w=>w.View=="main");var compact=windows.Single(w=>w.View=="compact");
                        await Ready(main);await Ready(compact);
                        var mainLoads=0;var compactLoads=0;
                        Browser(main).CoreWebView2.NavigationStarting+=(_,_)=>mainLoads++;
                        Browser(compact).CoreWebView2.NavigationStarting+=(_,_)=>compactLoads++;
                        foreach(var mode in new[]{"App","Settings","Compact","Time-only"}){
                            session.Execute("toggle",JsonSerializer.SerializeToElement(new{seconds=900,repeat=false,lowTime=false}));now=now.AddSeconds(27);
                            if(mode!="Settings")session.Engine.Pause();
                            await Until(async()=>await Bool(main,"document.querySelector('#toggle').textContent==='"+(mode=="Settings"?"Pause":"Resume")+"'"));
                            // The running case retains its 900-second inputs; paused cases edit to 123.
                            if(mode!="Settings"){
                                await Script(main,"document.querySelector('#minutes').value='2';document.querySelector('#seconds').value='3';document.querySelector('#seconds').dispatchEvent(new Event('input',{bubbles:true}))");
                                await Until(async()=>await Bool(compact,"document.querySelector('#seconds').value==='3'"));
                            }
                            if(mode=="Time-only"){compact.Post(new{type="shrinkCompact"});await Until(()=>Task.FromResult(compact.IsTimeOnly));}
                            if(mode=="Compact"){compact.Post(new{type="expandCompact"});await Until(()=>Task.FromResult(!compact.IsTimeOnly));}
                            var target=mode is "App" or "Settings"?main:compact;
                            if(mode=="Settings")await Script(main,"document.querySelector('#tab-settings').click();document.querySelector('#theme').focus()");
                            else await Script(target,"document.querySelector('"+(mode=="Time-only"?"#read-time":"#minutes")+"').focus()");
                            var oldMain=mainLoads;var oldCompact=compactLoads;var core=Browser(target).CoreWebView2;
                            await Script(target,"window.resetSmokeMarker=true");
                            if(mode is "App" or "Time-only")NativeKey(target);else await DomKey(target);
                            await Reloaded(target);
                            var duration=mode=="Settings"?900:123;
                            Check(session.Engine.Snapshot.Timer is {IsRunning:false,PausedRemainingMilliseconds:null,EndTime:null,SessionId:null}&&session.Engine.Snapshot.Timer.RemainingSeconds==duration,
                                mode+" Ctrl+R resets the real timer to its input duration");
                            Check(mainLoads-oldMain==(target==main?1:0)&&compactLoads-oldCompact==(target==compact?1:0)&&ReferenceEquals(core,Browser(target).CoreWebView2),
                                mode+" refreshes exactly the active document, retaining the browser and other views");
                            if(mode=="Time-only")Check(compact.IsTimeOnly,"Ctrl+R refresh retains Time-only mode");
                            if(mode=="Compact")Check(!compact.IsTimeOnly,"Ctrl+R refresh retains Compact mode");
                            if(mode=="Settings")Check(await Bool(main,"document.body.dataset.tab==='settings'&&document.activeElement.id==='theme'"),"Ctrl+R refresh retains Settings tab and focused control");
                        }
                        session.Execute("toggle",JsonSerializer.SerializeToElement(new{seconds=900,repeat=false,lowTime=false}));
                        var id=session.Engine.CheckIn();app.Open("reflection",id);
                        await Until(()=>Task.FromResult(windows.Any(w=>w.ReflectionOpen&&w.Visible)));
                        var reflection=windows.Single(w=>w.ReflectionOpen);await Ready(reflection);
                        var reflectionLoads=0;Browser(reflection).CoreWebView2.NavigationStarting+=(_,_)=>reflectionLoads++;
                        session.Engine.Pause();session.SetDurationDraft(["0","3","42"]);
                        await Script(reflection,"document.querySelector('#reflection-text').value='Just typed before reload';document.querySelector('#early-reason').value='Retain both fields';document.querySelector('#reflection-text').dispatchEvent(new Event('input',{bubbles:true}));document.querySelector('#later').focus();window.resetSmokeMarker=true");
                        NativeKey(reflection);await Reloaded(reflection);
                        Check(session.Engine.Snapshot.Timer is {RemainingSeconds:222,IsRunning:false,PausedRemainingMilliseconds:null}&&reflectionLoads==1,"Session-end native Ctrl+R resets the shared timer and refreshes its page from a button");
                        Check(reflection.PromptId==id&&reflection.ReflectionOpen&&reflection.Visible&&session.Engine.Snapshot.Outbox.Count==0&&await Bool(reflection,"document.querySelector('#reflection-text').value==='Just typed before reload'&&document.querySelector('#early-reason').value==='Retain both fields'&&!document.querySelector('#reflection-text').readOnly"),"Reload saves both immediate reflection edits and restores the same unsent editable prompt");
                        await Script(reflection,"window.resetSmokeMarker=true;document.querySelector('#reflection-text').value='Keep failed-save draft';document.querySelector('#reflection-text').dispatchEvent(new Event('input',{bubbles:true}))");
                        store.Fail=true;await DomKey(reflection);
                        await Until(async()=>await Bool(reflection,"document.querySelector('#error').textContent.length>0&&!document.querySelector('#reflection-text').readOnly"));
                        Check(reflectionLoads==1&&await Bool(reflection,"window.resetSmokeMarker===true&&document.querySelector('#reflection-text').value==='Keep failed-save draft'"),"Failed draft save prevents reset/reload and retains the editable text");
                        store.Fail=false;await DomKey(reflection);await Reloaded(reflection);
                        Check(reflectionLoads==2&&session.Engine.Snapshot.Prompts.Single(p=>p.Id==id).Draft=="Keep failed-save draft","Retry after a save failure refreshes successfully without losing the draft");
                        session.SetDurationDraft(["0","0","0"]);await Script(main,"window.resetSmokeMarker=true");
                        var before=mainLoads;NativeKey(main);
                        await Until(async()=>await Bool(main,"document.querySelector('#error').textContent.includes('one second')"));
                        Check(mainLoads==before&&session.Engine.Snapshot.Timer.RemainingSeconds==222,"Invalid zero duration prevents native reset and reload");
                        session.SetDurationDraft(null);
                        Check(session.Engine.Snapshot.Connection.WebAppUrl==""&&session.Engine.Snapshot.Outbox.Count==0,"Reset and reload checks never connect or send reflections");
                    }catch(Exception error){failure=error;}
                    finally{store.Fail=false;await app.CloseMainAsync();}
                });
                Application.Run(app);
            }catch(Exception error){failure=error;}
            finally{app?.Dispose();}
        });
        thread.SetApartmentState(ApartmentState.STA);thread.Start();
        if(!thread.Join(TimeSpan.FromSeconds(120)))throw new TimeoutException("Native reset/reload smoke timed out.");
        if(failure is not null)throw new Exception("Native reset/reload smoke failed",failure);
        Console.WriteLine($"{passed} native reset/reload checks passed.");
    }
    private static void NativeKey(PreviewWindow window){
        var control=Browser(window);var key=new KeyEventArgs(Keys.Control|Keys.R);
        typeof(Control).GetMethod("OnKeyDown",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(control,[key]);
        if(!key.Handled)throw new Exception("Native Ctrl+R accelerator was not intercepted");
        typeof(Control).GetMethod("OnKeyUp",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(control,[new KeyEventArgs(Keys.R)]);
    }
    private static Task<string> DomKey(PreviewWindow window)=>Script(window,"document.dispatchEvent(new KeyboardEvent('keydown',{key:'r',ctrlKey:true,bubbles:true,cancelable:true}))");
    private static WebView2 Browser(PreviewWindow window)=>(WebView2)typeof(PreviewWindow).GetField("browser",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(window)!;
    private static Task Ready(PreviewWindow window)=>((TaskCompletionSource)typeof(PreviewWindow).GetField("interfaceReady",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(window)!).Task.WaitAsync(TimeSpan.FromSeconds(20));
    private static Task<string> Script(PreviewWindow window,string script)=>Browser(window).CoreWebView2.ExecuteScriptAsync(script);
    private static async Task<bool> Bool(PreviewWindow window,string script)=>JsonDocument.Parse(await Script(window,script)).RootElement.ValueKind==JsonValueKind.True;
    private static Task Reloaded(PreviewWindow window)=>Until(async()=>!((bool)typeof(PreviewWindow).GetField("resetAndReloadInProgress",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(window)!)&&await Bool(window,"window.resetSmokeMarker===undefined&&document.readyState==='complete'"));
    private static async Task Until(Func<Task<bool>> predicate){using var limit=new CancellationTokenSource(TimeSpan.FromSeconds(20));while(!await predicate())await Task.Delay(25,limit.Token);}
    private sealed class Registration:IHotKeyRegistration{
        public bool Register(nint window,int id,uint modifiers,uint key)=>true;
        public bool Unregister(nint window,int id)=>true;
    }
}
