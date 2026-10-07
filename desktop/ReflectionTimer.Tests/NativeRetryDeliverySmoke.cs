using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using Microsoft.Web.WebView2.WinForms;
using ReflectionTimer.Accessible;
using ReflectionTimer.Core;
using ReflectionTimer.Desktop;

// Opt-in real WebView bridge check. The in-memory state, temporary browser
// profile, silent audio/speech, and fake hotkeys never use an installed profile.
// Sheets is disabled; CSV destinations are test-local and never written here.
static class NativeRetryDeliverySmoke
{
    internal static void Run()
    {
        Exception? failure=null;var passed=0;
        void Check(bool condition,string name){if(!condition)throw new Exception(name);passed++;Console.WriteLine("PASS "+name);}
        var thread=new Thread(()=>{
            PreviewApplication? app=null;
            try{
                Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);Application.EnableVisualStyles();
                var now=DateTimeOffset.Parse("2026-10-07T12:00:00-04:00");
                var directory=Path.Combine(Path.GetTempPath(),"ReflectionTimer-NativeRetry-"+Guid.NewGuid().ToString("N"));
                var csvDirectory=Path.Combine(directory,"synthetic-csv");
                var earlier=new OutboxItem{Message="Earlier failed reflection.",SubmittedAt=now.AddHours(-2),
                    SheetUrl="https://docs.google.com/spreadsheets/d/synthetic-native-test/edit",SheetsRequested=false,
                    Status=DeliveryStatus.Sent,CsvStatus=CsvDeliveryStatus.NeedsReview,CsvError="csv_locked",
                    CsvAttempts=1,CsvDirectory=csvDirectory,DurationSeconds=900,ActualDurationSeconds=500};
                var latest=earlier with{Id=Guid.NewGuid(),Message="Most recent unsent reflection.",SubmittedAt=now.AddHours(-1)};
                var delivered=earlier with{Id=Guid.NewGuid(),Message="Already delivered.",SubmittedAt=now,
                    CsvStatus=CsvDeliveryStatus.Saved,CsvError="",CsvFile=Path.Combine(csvDirectory,"already-saved.csv")};
                var store=new Store{State=new(){LoggingEnabled=false,ShowAppView=false,ShowFloatingTimer=false,
                    AutoSendIncompleteReflections=false,ExtensionDisabledConfirmed=false,Csv=new(){Directory=csvDirectory},
                    Timer=new(){Volume=50},Outbox=[latest,delivered,earlier],Audio=new(){
                        SessionEnd=new(){Track=LibrarySound.Default,Behavior=SoundBehavior.Polite},
                        Success=new(){Track=LibrarySound.Default,Behavior=SoundBehavior.Polite}}}};
                var session=new PreviewSession(store,()=>now,isolatedProfile:true);
                app=new(session,directory,startInTray:true,profileName:"native-retry",shortcutRegistration:new Registration(),
                    screenReaderNotification:(_,_,_)=>true,speech:new Speech());
                ((System.Windows.Forms.Timer)Field(app,"pulse")).Stop();
                var audio=new Audio();var sounds=(AlertSoundPlayer)Field(app.Services,"sounds");
                typeof(AlertSoundPlayer).GetField("backend",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(sounds,audio);
                _=app.MainForm!.Handle;
                app.MainForm.BeginInvoke(async()=>{
                    try{
                        await app.Services.Play(SoundEvent.Success);
                        Check(audio.Paths.Count==1,"Native retry uses the silent recording audio backend");
                        var endPath=SoundLibrary.Resolve(SoundEvent.SessionEnd,AudioSettings.From(session.Engine.Snapshot).SessionEnd);
                        session.Engine.Start(900,false,50,lowTime:new(){Enabled=false});
                        var activeTimer=session.Engine.CurrentTimer;
                        var originalPrompt=session.Engine.CheckIn();session.Engine.SaveDraft(originalPrompt,"Original current draft","");
                        app.Open("reflection",originalPrompt);
                        await Until(()=>Task.FromResult(Windows(app).Any(w=>w.View=="reflection"&&w.Visible&&w.ReflectionOpen)));
                        var window=Windows(app).Single(w=>w.View=="reflection");var browser=Browser(window);
                        var core=browser.CoreWebView2;var process=core.BrowserProcessId;
                        await Until(async()=>await Bool(window,"!document.querySelector('#reflection-failed').hidden"));
                        await Script(window,"document.querySelector('#reflection-text').value='Current draft before warning';document.querySelector('#reflection-text').dispatchEvent(new Event('input',{bubbles:true}));document.querySelector('#reflection-failed').click()");
                        await Until(()=>Task.FromResult(window.PromptId!=originalPrompt&&window.ReflectionOpen));
                        await Until(async()=>await Bool(window,"document.activeElement.id==='reflection-text'&&!document.querySelector('#reflection-text').readOnly"));
                        var proxy=window.PromptId!.Value;
                        Check(session.Engine.Snapshot.Prompts.Single(p=>p.Id==originalPrompt).Draft=="Current draft before warning",
                            "Native warning saves the in-flight current draft before changing reflections");
                        Check(session.Engine.Snapshot.Prompts.Single(p=>p.Id==proxy).RetryOutboxId==latest.Id,
                            "Native warning selects the most recent unsent entry rather than list order or a successful entry");
                        Check(ReferenceEquals(window,Windows(app).Single(w=>w.View=="reflection"))&&ReferenceEquals(browser,Browser(window))
                            &&ReferenceEquals(core,Browser(window).CoreWebView2)&&process==core.BrowserProcessId,
                            "Warning reuses the same native window, WebView control, CoreWebView2, and browser process");
                        Check(((IReflectionPromptWindow)window).ReflectionId==proxy&&await Text(window,"#reflection-text","value")==latest.Message
                            &&await Bool(window,"document.activeElement.id==='reflection-text'"),
                            "The native retry reflection ID and focused text box contain the saved unsent message");
                        Check(await Text(window,"#reflection-heading")=="Unsent reflection"&&await Text(window,"#reflection-context")=="Send retries only undelivered destinations.",
                            "Native retry editor identifies its delivery purpose without active-session ending guidance");
                        await Script(window,"document.dispatchEvent(new KeyboardEvent('keydown',{key:'Enter',ctrlKey:true,bubbles:true,cancelable:true}))");
                        await Until(()=>Task.FromResult(!window.ReflectionOpen&&!window.Visible));
                        var queued=session.Engine.Snapshot.Outbox.Single(o=>o.Id==latest.Id);
                        Check(session.Engine.Snapshot.Prompts.All(p=>p.Id!=proxy)&&session.Engine.Snapshot.Outbox.Count==3
                            &&queued is{CsvStatus:CsvDeliveryStatus.Pending,Status:DeliveryStatus.Sent,Message:"Most recent unsent reflection."},
                            "Sending the unchanged retry removes its proxy and updates the existing Outbox entry only");
                        Check(session.Engine.Snapshot.Outbox.Single(o=>o.Id==earlier.Id)==earlier
                            &&session.Engine.Snapshot.Outbox.Single(o=>o.Id==delivered.Id)==delivered,
                            "Sending one native retry preserves older failures and completed deliveries");
                        Check(session.Engine.CurrentTimer==activeTimer&&session.Engine.CurrentTimer.IsRunning,
                            "Sending an old unsent message leaves the newer timer running unchanged");
                        app.Open("reflection",originalPrompt);
                        await Until(()=>Task.FromResult(window.ReflectionOpen&&window.Visible&&window.PromptId==originalPrompt));
                        await Until(async()=>await Bool(window,"!document.querySelector('#reflection-text').readOnly"));
                        await Script(window,"document.querySelector('#reflection-failed').click()");
                        await Until(()=>Task.FromResult(window.PromptId!=originalPrompt&&window.ReflectionOpen));
                        await Until(async()=>await Bool(window,"!document.querySelector('#reflection-text').readOnly"));
                        var blankProxy=window.PromptId!.Value;
                        await Script(window,"document.querySelector('#reflection-text').value='';document.querySelector('#reflection-text').dispatchEvent(new Event('input',{bubbles:true}));document.querySelector('#early-reason').value='';document.querySelector('#early-reason').dispatchEvent(new Event('input',{bubbles:true}));document.querySelector('#later').click()");
                        await Until(()=>Task.FromResult(!window.ReflectionOpen&&!window.Visible));
                        Check(store.State.Prompts.Single(p=>p.Id==blankProxy).Draft=="",
                            "An emptied unsent reflection can be saved durably without changing its Outbox message");
                        var blankView=JsonSerializer.SerializeToElement(session.View(),PreviewSession.Json);
                        Check(blankView.GetProperty("prompts").EnumerateArray().Any(p=>p.GetProperty("id").GetGuid()==blankProxy),
                            "A saved empty retry proxy remains safe in full native view snapshots");
                        app.Open("reflection",blankProxy);
                        await Until(()=>Task.FromResult(window.ReflectionOpen&&window.Visible&&window.PromptId==blankProxy));
                        await Until(async()=>await Bool(window,"document.querySelector('#reflection-text').value===''&&document.activeElement.id==='reflection-text'"));
                        await Script(window,"document.dispatchEvent(new KeyboardEvent('keydown',{key:'Enter',ctrlKey:true,bubbles:true,cancelable:true}))");
                        await Until(()=>Task.FromResult(!window.ReflectionOpen&&!window.Visible));
                        Check(session.Engine.Snapshot.Prompts.All(p=>p.Id!=blankProxy)&&session.Engine.Snapshot.Outbox.Single(o=>o.Id==latest.Id)==queued,
                            "Skipping an empty retry proxy keeps the saved unsent Outbox message intact");
                        await Task.Delay(100);
                        Check(!audio.Paths.Contains(endPath!)&&session.Engine.CurrentTimer==activeTimer,
                            "Opening, sending, saving, reopening, and skipping old delivery drafts never replay session-end audio or alter the timer");
                        Check(!session.Engine.SettingsSnapshot.ExtensionDisabledConfirmed&&session.Engine.SettingsSnapshot.Connection.WebAppUrl==""
                            &&Directory.GetFiles(directory,"*.csv",SearchOption.AllDirectories).Length==0,
                            "Native retry checks remain disconnected with all state and browser data confined to their disposable profile");
                    }catch(Exception error){failure=error;}
                    finally{await app.CloseMainAsync();}
                });
                Application.Run(app);
            }catch(Exception error){failure=error;}
            finally{app?.Dispose();}
        });
        thread.SetApartmentState(ApartmentState.STA);thread.Start();
        if(!thread.Join(TimeSpan.FromSeconds(100)))throw new TimeoutException("Native retry delivery smoke did not finish.");
        if(failure is not null)throw new Exception("Native retry delivery smoke failed.",failure);
        Console.WriteLine($"{passed} native retry delivery checks passed.");
    }
    private static object Field(object value,string name)=>value.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(value)!;
    private static List<PreviewWindow> Windows(PreviewApplication app)=>(List<PreviewWindow>)Field(app,"windows");
    private static WebView2 Browser(PreviewWindow window)=>(WebView2)Field(window,"browser");
    private static Task<string> Script(PreviewWindow window,string script)=>Browser(window).CoreWebView2.ExecuteScriptAsync(script);
    private static async Task<bool> Bool(PreviewWindow window,string script)=>JsonDocument.Parse(await Script(window,script)).RootElement.GetBoolean();
    private static async Task<string?> Text(PreviewWindow window,string selector,string property="textContent")=>JsonDocument.Parse(await Script(window,$"document.querySelector({JsonSerializer.Serialize(selector)}).{property}")).RootElement.GetString();
    private static async Task Until(Func<Task<bool>> test){using var limit=new CancellationTokenSource(TimeSpan.FromSeconds(20));while(!await test())await Task.Delay(25,limit.Token);}
    private sealed class Store:IStateStore
    {
        internal AppState State=new();
        public AppState Load()=>DataJson.Clone(State);
        public void Save(AppState state)=>State=DataJson.Clone(state);
    }
    private sealed class Registration:IHotKeyRegistration
    {
        public bool Register(nint window,int id,uint modifiers,uint key)=>true;
        public bool Unregister(nint window,int id)=>true;
    }
    private sealed class Audio:IAlertAudioBackend
    {
        internal ConcurrentQueue<string> Paths {get;}=new();
        public Task PlayAsync(string path,AudioLevel level,CancellationToken cancellationToken){Paths.Enqueue(path);return Task.CompletedTask;}
    }
    private sealed class Speech:IVoiceOutput
    {
        public void Speak(string text,int volume){}
        public void SetVolume(int volume){}
        public void Stop(){}
        public bool TakeFailure()=>false;
        public void Dispose(){}
    }
}
