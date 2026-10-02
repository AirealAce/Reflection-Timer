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
                var readerAvailable=true;var readerMessages=new List<(string Text,bool Accepted)>();
                app=new(session,Path.Combine(Path.GetTempPath(),"ReflectionTimer-SettingsKeys-"+Guid.NewGuid().ToString("N")),startInTray:true,profileName:"settings-keys-smoke",shortcutRegistration:new Registration(),screenReaderNotification:(provider,text,extra)=>{
                    var accepted=readerAvailable&&ScreenReaderAnnouncements.TryAnnounce(provider,text,extra);
                    readerMessages.Add((text,accepted));return accepted;
                });
                ((System.Windows.Forms.Timer)typeof(PreviewApplication).GetField("pulse",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(app)!).Stop();
                _=app.MainForm!.Handle;
                app.MainForm.BeginInvoke(async()=>{
                    try{
                        app.Open("main");var main=(PreviewWindow)app.MainForm;
                        Check(FocusKey(app),"The global Focus shortcut is registered before the WebView is ready");
                        await Ready(main);
                        try{await Until(async()=>await Bool(main,"document.querySelector('#focus-target-dialog').open&&document.activeElement.id==='focus-target-kind'"));}
                        catch(Exception error){throw new Exception("First-use chooser state: "+await Script(main,"JSON.stringify({open:document.querySelector('#focus-target-dialog').open,active:document.activeElement.id,dialogs:[...document.querySelectorAll('dialog[open]')].map(d=>d.id),error:document.querySelector('#error').textContent})"),error);}
                        Check(!store.State.FocusMode.Enabled,"First-use Focus shortcut opens the accessible chooser without enabling an unconfigured alert");
                        await Script(main,"document.querySelector('#focus-target-cancel').click()");await Until(()=>Task.FromResult(!Scope(main)));
                        Check(!store.State.FocusMode.Enabled,"Canceling the shortcut's first-use chooser keeps Focus off");
                        using(var source=new WindowsFocusTargets()){
                            var listed=await source.ListAsync(FocusTargetKind.Window);
                            var own=listed.SingleOrDefault(t=>t.WindowHandle==main.Handle.ToInt64());
                            Check(own is not null&&own.ProcessId==Environment.ProcessId,"The native focus chooser lists its own visible App view");
                            Check(await source.CheckAsync(own!) is FocusPresence.Focused or FocusPresence.Away,"The App view is a usable native focus target");
                            var choices=JsonSerializer.SerializeToElement(await app.ListFocusTargetsAsync(FocusTargetKind.Window),PreviewSession.Json);
                            var choice=choices.EnumerateArray().Single(t=>t.GetProperty("name").GetString()==main.Text);
                            app.SelectFocusTarget(choice.GetProperty("id").GetGuid(),enable:false);
                            Check(store.State.FocusMode.Target?.WindowHandle==main.Handle.ToInt64(),"The chooser can save the App view through its host cache");
                            var options=store.State.FocusMode with{DelaySeconds=7,MultipleTargets=true,IdleEnabled=true,IdleSeconds=27};
                            session.Engine.SetFocusMode(options);
                            foreach(var mode in new[]{SessionMode.Timer,SessionMode.Stopwatch}){
                                app.HideAppView();var timer=session.Engine.CurrentTimer;
                                Check(timer.Mode==mode&&FocusKey(app)&&store.State.FocusMode.Enabled&&!main.Visible&&session.Engine.CurrentTimer==timer,
                                    mode+": global Focus enables from a hidden App without changing the session or showing a viewer");
                                await Until(async()=>await Bool(main,"document.querySelector('#focus-enabled').getAttribute('aria-pressed')==='true'&&document.querySelector('#focus-settings-enabled').getAttribute('aria-pressed')==='true'"));
                                Check(FocusKey(app)&&!store.State.FocusMode.Enabled&&!main.Visible&&JsonSerializer.Serialize(new TimerEngine(store).Snapshot.FocusMode)==JsonSerializer.Serialize(options)&&session.Engine.CurrentTimer==timer,
                                    mode+": global Focus disables durably and retains targets, delay, Multiple Targets and idle options");
                                session.ToggleModeFromShortcut();
                            }
                            session.Engine.SetFocusMode(options with{Target=null,Targets=[],Enabled=false});
                            Check(FocusKey(app)&&store.State.FocusMode.Enabled&&!main.Visible,"The global Focus shortcut supports a saved idle-only configuration without opening the chooser");
                            FocusKey(app);session.Engine.SetFocusMode(options);
                            app.Open("main");await Until(async()=>await Bool(main,"document.querySelector('#focus-enabled').getAttribute('aria-pressed')==='false'"));
                            await Script(main,"window.focusLiveMessages=[];chrome.webview.addEventListener('message',event=>{if(event.data.type==='announcement'&&event.data.message.startsWith('Focus mode '))focusLiveMessages.push(event.data.message)})");
                            readerMessages.Clear();Check(FocusKey(app),"Configured Focus shortcut dispatches in visible App view");
                            await Until(async()=>await Bool(main,"document.querySelector('#session-status').textContent==='Focus mode on.'"));
                            Check(readerMessages.SequenceEqual(new[]{("Focus mode on.",true)})&&await Bool(main,"focusLiveMessages.length===0"),"Visible App gets readable Focus status without duplicating successful native feedback in its live region");
                            readerAvailable=false;Check(FocusKey(app),"Focus shortcut still dispatches when the native reader provider is unavailable");
                            await Until(async()=>await Bool(main,"document.querySelector('#status').textContent==='Focus mode off.'"));
                            Check(readerMessages.Last()==("Focus mode off.",false)&&await Bool(main,"JSON.stringify(focusLiveMessages)===JSON.stringify(['Focus mode off.'])"),"Unavailable native notification falls back to one visible live-region announcement");
                            readerAvailable=true;
                            await Script(main,"document.querySelector('#choose-focus-target').click()");await Until(async()=>await Bool(main,"document.querySelector('#focus-target-dialog').open&&document.querySelector('#focus-target-list').getAttribute('aria-busy')==='false'"));
                            await Script(main,"document.querySelector('#focus-delay').value='9';document.querySelector('#focus-delay').dispatchEvent(new Event('input',{bubbles:true}));document.querySelector('#focus-delay').focus()");
                            Check(FocusKey(app)&&store.State.FocusMode.Enabled&&store.State.FocusMode.DelaySeconds==7,"Global Focus toggles while its delay field has an unblurred edit");
                            await Until(async()=>await Bool(main,"document.querySelector('#focus-enabled').getAttribute('aria-pressed')==='true'&&document.querySelector('#focus-delay').value==='9'"));
                            await main.FlushDraftAsync();
                            Check(store.State.FocusMode.Enabled&&store.State.FocusMode.DelaySeconds==9,"Saving the pending delay retains the global toggle and the typed value");
                            FocusKey(app);session.Engine.SetFocusMode(options);
                            await Script(main,"document.querySelector('#focus-target-cancel').click()");await Until(()=>Task.FromResult(!Scope(main)));
                            app.Open("compact");var compact=Application.OpenForms.OfType<PreviewWindow>().Single(w=>w.View=="compact");
                            await Ready(compact);
                            listed=await source.ListAsync(FocusTargetKind.Window);
                            Check(compact.Visible&&listed.All(t=>t.WindowHandle!=compact.Handle.ToInt64())&&listed.Any(t=>t.WindowHandle==main.Handle.ToInt64()),"The floating viewer stays excluded while the App view remains selectable");
                            compact.CloseAfterSave();
                        }
                        Check(!NativeKey(main,Keys.Control|Keys.Enter),"Native Ctrl+Enter remains available to Timer controls outside Settings");
                        await Script(main,"document.querySelector('#tab-settings').click()");await Until(()=>Task.FromResult(Scope(main)));
                        Check(!store.State.ShowAllExplanations&&await Bool(main,"!document.querySelector('#showAllExplanations').checked&&document.querySelector('#viewer-hide-help').hidden"),"Native Settings starts with explanatory help collapsed");
                        await Script(main,"document.querySelector('#showAllExplanations').click()");await Until(()=>Task.FromResult(store.State.ShowAllExplanations));
                        Check(new TimerEngine(store).Snapshot.ShowAllExplanations&&await Bool(main,"!document.querySelector('#viewer-hide-help').hidden&&document.querySelector('#help-toggle-display').getAttribute('aria-expanded')==='true'"),"Native help option autosaves and opens section guidance");
                        await Script(main,"document.querySelector('#showAllExplanations').checked=false;document.querySelector('#showAllExplanations').dispatchEvent(new Event('input',{bubbles:true}));document.querySelector('#showAllExplanations').focus()");
                        Check(NativeKey(main,Keys.Control|Keys.S),"Native Settings save intercepts Ctrl+S from the guidance checkbox");await Until(()=>Task.FromResult(!store.State.ShowAllExplanations));
                        await Until(async()=>await Bool(main,"document.querySelector('#viewer-hide-help').hidden"));
                        Check(!new TimerEngine(store).Snapshot.ShowAllExplanations,"Explicit native Save settings persists an unblurred help choice");
                        var value=310;
                        foreach(var key in new[]{Keys.Enter,Keys.S})foreach(var focus in new[]{"settings-time-reached-seconds","sheet-url","theme","preview-sound-0","page-title"}){
                            value++;
                            await Script(main,$"document.querySelector('#settings-time-reached-seconds').value='{value}';document.querySelector('#settings-time-reached-seconds').dispatchEvent(new Event('input',{{bubbles:true}}));document.querySelector('#{focus}').tabIndex=0;document.querySelector('#{focus}').focus();document.querySelector('#status').textContent=''");
                            Check(NativeKey(main,Keys.Control|key),"Native Settings shortcut intercepted from "+focus+" / "+key);
                            await Until(()=>Task.FromResult(AudioSettings.From(session.Engine.Snapshot).TimeReachedSeconds==value));
                            await Until(async()=>await Bool(main,"document.querySelector('#status').textContent==='Settings saved.'"));
                            Check(AudioSettings.From(store.State).TimeReachedSeconds==value&&await Bool(main,$"document.activeElement.id==='{focus}'"),"Native shortcut durably saves the current input and retains focus: "+focus+" / "+key);
                        }
                        foreach(var key in new[]{Keys.Enter,Keys.S}){
                            value++;
                            await Script(main,$"document.querySelector('#settings-time-reached-seconds').value='{value}';document.querySelector('#settings-time-reached-seconds').dispatchEvent(new Event('input',{{bubbles:true}}));document.querySelector('#settings-search-input').value='color theme';document.querySelector('#settings-search-input').dispatchEvent(new Event('input',{{bubbles:true}}));document.querySelector('#settings-search-input').focus()");
                            Check(await Bool(main,"document.querySelector('#settings-time-reached-seconds').getClientRects().length===0")&&NativeKey(main,Keys.Control|key),"Native Settings save works from Search while an edited setting is filtered out: "+key);
                            await Until(()=>Task.FromResult(AudioSettings.From(store.State).TimeReachedSeconds==value));
                            Check(await Bool(main,"document.activeElement.id==='settings-search-input'&&document.querySelector('#settings-search-input').value==='color theme'"),"Native save retains the search query and keyboard focus: "+key);
                            await Script(main,"document.querySelector('#settings-search-clear').click()");
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
                        var focusFade=20;
                        foreach(var tab in new[]{"timer","settings"})foreach(var key in new[]{Keys.Enter,Keys.S}){
                            focusFade++;
                            await Script(main,$"document.querySelector('#tab-{tab}').click();document.querySelector('#choose-focus-target{(tab=="settings"?"-settings":"")}').click()");
                            await Until(()=>Task.FromResult(Scope(main)));
                            await Until(async()=>await Bool(main,"document.querySelector('#focus-target-list').getAttribute('aria-busy')==='false'&&!!document.querySelector('#focus-target-list tbody tr')"));
                            await Script(main,"document.querySelector('#focus-multiple-targets').checked=true;document.querySelector('#focus-multiple-targets').dispatchEvent(new Event('change',{bubbles:true}));document.querySelector('#focus-idle-enabled').checked=true;document.querySelector('#focus-idle-enabled').dispatchEvent(new Event('change',{bubbles:true}));document.querySelector('#focus-idle-seconds').value='27'");
                            // A native listing refresh must retain a Window selected in
                            // another category; its ID still has to pass the host cache.
                            await Script(main,"document.querySelector('#focus-target-kind').value='1';document.querySelector('#focus-target-kind').dispatchEvent(new Event('change',{bubbles:true}))");
                            await Until(async()=>await Bool(main,"document.querySelector('#focus-target-list').getAttribute('aria-busy')==='false'"));
                            await Script(main,$"document.querySelector('#focus-picker-audio').open=true;document.querySelector('#sound-fade-5-picker').checked=true;document.querySelector('#sound-fade-5-picker').dispatchEvent(new Event('input',{{bubbles:true}}));document.querySelector('#sound-fade-seconds-5-picker').value='{focusFade}';document.querySelector('#sound-fade-seconds-5-picker').dispatchEvent(new Event('input',{{bubbles:true}}));document.querySelector('#sound-fade-seconds-5-picker').focus();document.querySelector('#status').textContent=''");
                            Check(NativeKey(main,Keys.Control|key),"Native chooser save is intercepted from its audio number input on "+tab+" / "+key);
                            await Until(async()=>await Bool(main,"!document.querySelector('#focus-target-dialog').open"));
                            Check(store.State.FocusMode is{MultipleTargets:true,IdleEnabled:true,IdleSeconds:27}
                                &&store.State.FocusMode.SelectedTargets.Length==1&&store.State.FocusMode.SelectedTargets[0].Kind==FocusTargetKind.Window,
                                "Native chooser saves idle options and retains a window selection after listing tabs: "+tab+" / "+key);
                            Check(AudioSettings.From(store.State).FocusLost is{FadeOutEnabled:true}&&AudioSettings.From(store.State).FocusLost.FadeOutAfterSeconds==focusFade,"Native chooser save durably flushes unblurred Focus audio timing: "+tab+" / "+key);
                            Check(await Bool(main,$"document.activeElement.id==='choose-focus-target{(tab=="settings"?"-settings":"")}'"),"Native chooser restores its opener after "+tab+" / "+key);
                            if(tab=="timer")await Until(()=>Task.FromResult(!Scope(main)));
                        }
                        using(var source=new WindowsFocusTargets())Check(source.IdleMilliseconds>=0,"Windows last-input API provides an idle duration without reading input content");
                        await Script(main,"document.querySelector('#tab-timer').click()");await Until(()=>Task.FromResult(!Scope(main)));
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
    private static bool FocusKey(PreviewApplication app)=>((PreviewShortcuts)typeof(PreviewApplication).GetField("shortcuts",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(app)!).Dispatch(GlobalShortcut.HotKeyMessage,GlobalShortcut.FocusToggleId);
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
