using System.Reflection;
using System.Text.Json;
using Microsoft.Web.WebView2.WinForms;
using ReflectionTimer.Accessible;
using ReflectionTimer.Core;
using ReflectionTimer.Desktop;

// Cold-start real WebViews from encrypted synthetic preferences. Never touches
// production data, startup registration, keyboard input, audio, or Sheets.
static class NativeStartupSmoke
{
    internal static void Run()
    {
        var passed=0;
        foreach(var theme in Enum.GetValues<AppColorTheme>())foreach(var tray in new[]{false,true}) {
            Exception? failure=null;
            void Check(bool condition,string message){if(!condition)throw new Exception(message);passed++;Console.WriteLine($"PASS {theme}, tray={tray}: {message}");}
            var thread=new Thread(()=>{
                PreviewApplication? app=null;
                try {
                    Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);Application.EnableVisualStyles();
                    var directory=Path.Combine(Path.GetTempPath(),"ReflectionTimer-StartupSmoke-"+Guid.NewGuid().ToString("N"));
                    var store=new EncryptedStore(directory);
                    var saved=new AppState{Theme=theme,LoggingEnabled=false,StartAtLogin=true,AutoSendIncompleteReflections=false,
                        CompactAlwaysOnTop=false,TimeOnlyAlwaysOnTop=false,PromptAlwaysOnTop=false,ReflectionSeparator=ReflectionSeparator.Bullet,
                        PopupPosition=ReflectionPopupPosition.TopLeft,FloatingPlacement=FloatingTimerPlacement.TopRight,
                        Timer=new(){DurationSeconds=163,RemainingSeconds=163,Volume=0},
                        Audio=new(){LowTimeThresholdSeconds=23,LowTime=new(){Behavior=SoundBehavior.Polite,FadeOutAfterMessageSent=true,MessageSentFadeSeconds=7}}};
                    store.Save(saved);var expected=JsonSerializer.Serialize(saved,DataJson.Options);
                    var session=new PreviewSession(store,isolatedProfile:true);
                    app=new(session,directory,startInTray:tray,profileName:"startup-smoke");
                    ((System.Windows.Forms.Timer)typeof(PreviewApplication).GetField("pulse",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(app)!).Stop();
                    var main=(PreviewWindow)app.MainForm!;var compact=Windows(app).Single(w=>w.View=="compact");
                    // Before processing any browser messages, neither view may
                    // expose HTML defaults instead of the saved preference.
                    Check(!Browser(main).Visible&&!Browser(compact).Visible,"uninitialized controls stay hidden, including automatic floating startup");
                    _=main.Handle;
                    main.BeginInvoke(async()=>{
                        try {
                            if(tray){app.Open("main");Check(!Browser(main).Visible,"tray-launched App stays hidden internally until preferences load");}
                            foreach(var window in new[]{main,compact}){
                                await Ready(window).WaitAsync(TimeSpan.FromSeconds(20));
                                var result=JsonDocument.Parse(await Browser(window).CoreWebView2.ExecuteScriptAsync("JSON.stringify({theme:document.documentElement.dataset.theme,minutes:document.querySelector('#minutes').value,seconds:document.querySelector('#seconds').value})")).RootElement.GetString()!;
                                var value=JsonDocument.Parse(result).RootElement;
                                Check(Browser(window).Visible&&value.GetProperty("theme").GetString()==((int)theme).ToString()&&value.GetProperty("minutes").GetString()=="2"&&value.GetProperty("seconds").GetString()=="43",
                                    window.View+" first ready display uses saved theme and duration");
                            }
                            var controls=JsonDocument.Parse(await Browser(main).CoreWebView2.ExecuteScriptAsync("JSON.stringify({theme:document.querySelector('#theme').value,threshold:document.querySelector('#default-threshold').value,startup:document.querySelector('#start-at-login').checked,autoSend:document.querySelector('#autoSendIncompleteReflections').checked})")).RootElement.GetString()!;
                            var fields=JsonDocument.Parse(controls).RootElement;
                            Check(fields.GetProperty("theme").GetString()==((int)theme).ToString()&&fields.GetProperty("threshold").GetString()=="23"&&fields.GetProperty("startup").GetBoolean()&&!fields.GetProperty("autoSend").GetBoolean(),"Settings controls are restored before the App becomes ready");
                            Check(JsonSerializer.Serialize(new EncryptedStore(directory).Load(),DataJson.Options)==expected,"cold startup does not rewrite saved preferences with UI defaults");
                            main.Hide();app.Open("main");
                            Check(JsonSerializer.Serialize(session.Engine.Snapshot,DataJson.Options)==expected,"hiding and reopening retains the complete saved state");
                            if(theme==AppColorTheme.Dark&&!tray){
                                await ThresholdControls(app,main,Check);
                                expected=JsonSerializer.Serialize(session.Engine.Snapshot,DataJson.Options);
                            }
                        } catch(Exception error){failure=error;}
                        finally {await app.CloseMainAsync();}
                    });
                    Application.Run(app);
                    Check(JsonSerializer.Serialize(new EncryptedStore(directory).Load(),DataJson.Options)==expected,"normal shutdown preserves the complete encrypted state for the next launch");
                } catch(Exception error){failure=error;}
                finally {app?.Dispose();}
            });
            thread.SetApartmentState(ApartmentState.STA);thread.Start();
            if(!thread.Join(TimeSpan.FromSeconds(60)))throw new TimeoutException("Native startup smoke timed out.");
            if(failure is not null)throw new Exception("Native startup smoke failed",failure);
        }
        Console.WriteLine($"{passed} native startup checks passed.");
    }
    private static List<PreviewWindow> Windows(PreviewApplication app)=>(List<PreviewWindow>)typeof(PreviewApplication).GetField("windows",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(app)!;
    private static async Task ThresholdControls(PreviewApplication app,PreviewWindow main,Action<bool,string> check)
    {
        var engine=app.Session.Engine;
        engine.Start(180,false,0);
        engine.SaveSchedule(null,DateTimeOffset.Now.AddDays(1),120,false,0);
        var schedule=engine.Snapshot.Schedules.Single();var deadline=engine.Snapshot.Timer.EndTime;
        Task<string> Script(string script)=>Browser(main).CoreWebView2.ExecuteScriptAsync(script);
        async Task Until(Func<Task<bool>> ready){using var limit=new CancellationTokenSource(TimeSpan.FromSeconds(15));while(!await ready())await Task.Delay(25,limit.Token);}
        await Script("document.querySelector('#tab-settings').click();document.querySelector('#settings-low-time').checked=false;document.querySelector('#settings-low-time').dispatchEvent(new Event('change',{bubbles:true}))");
        await Until(()=>Task.FromResult(!engine.Snapshot.Timer.LowTime.Enabled));
        check(engine.Snapshot.Timer.EndTime==deadline&&engine.Snapshot.Audio!.LowTimeThresholdSeconds==23&&engine.Snapshot.Schedules.Single()==schedule,
            "Settings Use threshold turns off the current warning without changing its deadline or saved schedule");
        await Script("document.querySelector('#settings-low-time').checked=true;document.querySelector('#settings-low-time').dispatchEvent(new Event('change',{bubbles:true}));document.querySelector('#default-threshold').value='37';document.querySelector('#default-threshold').dispatchEvent(new Event('input',{bubbles:true}));document.dispatchEvent(new KeyboardEvent('keydown',{key:'Enter',ctrlKey:true,bubbles:true,cancelable:true}))");
        await Until(async()=>JsonDocument.Parse(await Script("document.querySelector('#status').textContent")).RootElement.GetString()=="Settings saved.");
        check(engine.Snapshot.Timer.LowTime is {Enabled:true,ThresholdSeconds:37}&&engine.Snapshot.Timer.EndTime==deadline,
            "Native Ctrl+Enter saves the last typed Settings threshold as the Timer preference");
        check(engine.Snapshot.Audio!.LowTimeThresholdSeconds==23&&engine.Snapshot.Schedules.Single()==schedule,
            "Save settings does not change the legacy default used by an existing inherited schedule");
        check(JsonDocument.Parse(await Script("document.querySelector('#threshold').value==='37'&&!document.querySelector('#threshold').readOnly&&document.querySelector('#low-time').checked")).RootElement.GetBoolean(),
            "Native Settings and Timer controls show the same editable enabled threshold");
    }
    private static WebView2 Browser(PreviewWindow window)=>(WebView2)typeof(PreviewWindow).GetField("browser",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(window)!;
    private static Task Ready(PreviewWindow window)=>((TaskCompletionSource)typeof(PreviewWindow).GetField("interfaceReady",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(window)!).Task;
}
