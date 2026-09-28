using System.Reflection;
using System.Text.Json;
using Microsoft.Web.WebView2.WinForms;
using ReflectionTimer.Accessible;
using ReflectionTimer.Core;
using ReflectionTimer.Desktop;

// Synthetic windows/profile, muted audio, fake registrations, no Sheets calls.
// Interactive launch grants normal foreground rights for focus-return checks.
static class NativeCompactCycleSmoke
{
    internal static void RunInteractive()
    {
        Exception? failure=null;
        var thread=new Thread(()=>{
            Application.EnableVisualStyles();
            using var launcher=new Form{Text="Synthetic compact focus checks",Size=new(420,140),StartPosition=FormStartPosition.CenterScreen};
            launcher.Shown+=(_,_)=>launcher.BeginInvoke(()=>WindowActivation.Focus(launcher));
            var button=new Button{Text="Run compact focus checks",Dock=DockStyle.Fill};
            button.Click+=(_,_)=>{try{Run(true);}catch(Exception error){failure=error;}finally{launcher.Close();}};
            launcher.Controls.Add(button);Application.Run(launcher);
        });
        thread.SetApartmentState(ApartmentState.STA);thread.Start();thread.Join();
        if(failure is not null)throw new Exception("Interactive compact checks failed",failure);
    }
    internal static void Run(bool focus=false)
    {
        Exception? failure=null;var passed=0;
        void Check(bool value,string message){if(!value)throw new Exception(message);passed++;Console.WriteLine("PASS "+message);}
        var thread=new Thread(()=>{
            PreviewApplication? app=null;
            try {
                var store=new MemoryStore{State=new(){LoggingEnabled=false,VoiceAnnouncements=false,ShowAppView=false,ShowFloatingTimer=false,Timer=new(){Volume=0}}};
                var session=new PreviewSession(store,isolatedProfile:true);
                using var other=new Form{Text="Synthetic previous window",Size=new(360,140),StartPosition=FormStartPosition.CenterScreen};
                app=new(session,Path.Combine(Path.GetTempPath(),"ReflectionTimer-CompactCycle-"+Guid.NewGuid().ToString("N")),startInTray:true,profileName:"compact-cycle",shortcutRegistration:new Registration());
                ((System.Windows.Forms.Timer)typeof(PreviewApplication).GetField("pulse",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(app)!).Stop();
                _=app.MainForm!.Handle;
                app.MainForm.BeginInvoke(async()=>{
                    try {
                        if(focus){other.Show();WindowActivation.Focus(other);await Until(()=>Task.FromResult(WindowActivation.IsForeground(other)));}
                        var clock=JsonSerializer.Serialize(session.Engine.CurrentTimer);
                        var shortcuts=(PreviewShortcuts)typeof(PreviewApplication).GetField("shortcuts",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(app)!;
                        void Cycle(bool reverse)=>shortcuts.Dispatch(GlobalShortcut.HotKeyMessage,reverse?GlobalShortcut.CompactReverseId:GlobalShortcut.CompactId);
                        Cycle(true);
                        var compact=Windows(app).Single(w=>w.View=="compact");await Ready(compact);await Layout(compact,true);
                        Check(compact.Visible&&session.Engine.SettingsSnapshot.FloatingTimeOnly==true&&!app.MainForm.Visible,"Reverse from a cold hidden viewer opens only Time-only and saves that layout");
                        if(focus)Check(WindowActivation.IsForeground(other),"Showing Time-only from hidden never takes the previous window's focus (actual: "+(WindowActivation.Foreground==compact.Handle?"compact":WindowActivation.Foreground==app.MainForm.Handle?"main":WindowActivation.Foreground==other.Handle?"previous":"outside test")+")");
                        Cycle(true);await Layout(compact,false);
                        Check(compact.Visible,"Reverse Time-only expands to Compact");
                        if(focus){await Until(()=>Task.FromResult(WindowActivation.IsForeground(compact)));Check(true,"Expanding Compact still focuses its editable controls");}
                        Cycle(false);await Layout(compact,true);
                        if(focus){await Until(()=>Task.FromResult(WindowActivation.IsForeground(other)));await Script(compact,"Promise.resolve()");Check(WindowActivation.IsForeground(other),"Forward shrink releases native focus and no later browser focus steals it back");}
                        Check(await Script(compact,"document.activeElement.id")!="\"read-time\"","Shrink does not focus the clock in the document");
                        Cycle(false);Check(!compact.Visible&&!session.Engine.SettingsSnapshot.ShowFloatingTimer,"Forward Time-only hides the viewer");
                        Cycle(false);await Layout(compact,false);Check(compact.Visible,"Forward hidden restores full Compact");
                        Cycle(true);Check(!compact.Visible,"Reverse Compact hides the viewer");
                        Cycle(true);await Layout(compact,true);Check(compact.Visible,"Reverse hidden restores Time-only, not full Compact");
                        Cycle(true);await Layout(compact,false);
                        Cycle(false);Cycle(false);Cycle(false);await Layout(compact,false);
                        Check(compact.Visible&&session.Engine.SettingsSnapshot.FloatingTimeOnly==false,"Three rapid forward requests complete a full cycle without stale resize rollback");
                        Cycle(true);Cycle(true);Cycle(true);await Layout(compact,false);
                        Check(compact.Visible&&session.Engine.SettingsSnapshot.FloatingTimeOnly==false,"Three rapid reverse requests complete a full cycle");
                        await Script(compact,"window.chrome.webview.postMessage({requestId:'stale-size',action:'compactSize',data:{width:96,height:40,tiny:true,revision:0}})");
                        await Script(compact,"Promise.resolve()");Check(!compact.IsTimeOnly,"Old browser layout revisions cannot undo the newest native cycle");
                        if(focus){WindowActivation.Focus(other);await Until(()=>Task.FromResult(WindowActivation.IsForeground(other)));}
                        Cycle(false);await Layout(compact,true);
                        if(focus)Check(WindowActivation.IsForeground(other),"Shrinking from another foreground window leaves its focus untouched");
                        Cycle(true);await Layout(compact,false);
                        await Script(compact,"document.querySelector('#shrink').click()");await Layout(compact,true);
                        Check(compact.Visible,"The on-screen minus button still enters Time-only");
                        if(focus){await Until(()=>Task.FromResult(WindowActivation.IsForeground(other)));Check(true,"The minus button also returns native focus");}
                        Check(JsonSerializer.Serialize(session.Engine.CurrentTimer)==clock&&session.Engine.Snapshot.Prompts.Count==0&&session.Engine.Snapshot.Outbox.Count==0,"View cycling never changes the timer or creates/submits a reflection");
                        Cycle(true);await Layout(compact,false);
                        session.Engine.Start(900,false,0);await Layout(compact,true);
                        if(focus){await Until(()=>Task.FromResult(WindowActivation.IsForeground(other)));Check(true,"Automatic shrink when a timer starts also releases focus");}
                        Check(session.Engine.CurrentTimer.IsRunning,"Releasing focus does not pause the running timer");
                        if(focus) {
                            session.Engine.Pause();
                            using var editor=new BrowserFocusTarget();
                            await editor.FocusAsync();
                            await Until(editor.HasEditorFocusAsync);
                            Check(true,"Separate browser input queue starts with native and DOM editor focus");
                            app.Open("compact");await Layout(compact,false);
                            await Until(()=>Task.FromResult(WindowActivation.IsForeground(compact)));
                            Cycle(false);await Layout(compact,true);
                            await Task.Delay(500);
                            Check(await editor.HasEditorFocusAsync(),"Shrinking restores browser editor and caret without reactivation: "+await editor.StateAsync());
                            Cycle(false);await Task.Delay(500);
                            Check(await editor.HasEditorFocusAsync(),"Hiding Time-only preserves browser editor focus: "+await editor.StateAsync());
                            Cycle(false);await Layout(compact,false);
                            Cycle(true);await Task.Delay(500);
                            Check(await editor.HasEditorFocusAsync(),"Hiding Compact directly restores browser editor focus: "+await editor.StateAsync());
                            app.Open("main");await Ready((PreviewWindow)app.MainForm);
                            await Until(()=>Task.FromResult(WindowActivation.IsForeground(app.MainForm)));
                            app.Open("compact");await Layout(compact,false);
                            await Until(()=>Task.FromResult(WindowActivation.IsForeground(compact)));
                            Cycle(false);await Layout(compact,true);await Task.Delay(500);
                            Check(await editor.HasEditorFocusAsync(),"App-to-Compact-to-Time-only returns to the browser, not another timer window: "+await editor.StateAsync());
                            app.Open("main");
                            Cycle(false);await Task.Delay(500);
                            Check(!compact.Visible&&await editor.HasEditorFocusAsync(),"Hiding the overlay while App holds focus returns to the external editor");
                            app.Open("main");app.Open("compact");await Layout(compact,false);
                            Cycle(true);await Task.Delay(500);
                            Check(!compact.Visible&&await editor.HasEditorFocusAsync(),"Reverse hide also skips the main timer window");
                            app.Open("main");app.Open("compact");await Layout(compact,false);
                            await Script(compact,"document.querySelector('#shrink').click()");await Layout(compact,true);await Task.Delay(500);
                            Check(await editor.HasEditorFocusAsync(),"The minus button returns to the external editor after App-to-Compact navigation");
                            app.Open("main");app.Open("compact");await Layout(compact,false);
                            await Script(compact,"document.querySelector('#close').click()");
                            await Until(()=>Task.FromResult(!compact.Visible));await Task.Delay(500);
                            Check(await editor.HasEditorFocusAsync(),"The close button releases focus before hiding its native window");
                            app.Open("compact");await Layout(compact,false);await editor.FocusAsync();
                            Cycle(false);await Layout(compact,true);await Task.Delay(500);
                            Check(await editor.HasEditorFocusAsync(),"Background view changes never redirect an already-focused browser editor");
                        }
                    }catch(Exception error){failure=error;}
                    finally{await app.CloseMainAsync();}
                });
                Application.Run(app);
            }catch(Exception error){failure=error;}finally{app?.Dispose();}
        });
        thread.SetApartmentState(ApartmentState.STA);thread.Start();
        if(!thread.Join(TimeSpan.FromSeconds(90)))throw new TimeoutException("Compact checks timed out.");
        if(failure is not null)throw new Exception("Compact cycle checks failed",failure);
        Console.WriteLine($"{passed} native compact cycle/focus checks passed.");
    }
    private static async Task Layout(PreviewWindow window,bool tiny)
    {
        try{await Until(async()=>window.IsTimeOnly==tiny&&await Script(window,"document.body.dataset.tiny")==JsonSerializer.Serialize(tiny?"true":"false"));}
        catch(Exception error){throw new Exception($"Expected tiny={tiny}; native={window.IsTimeOnly}; browser="+await Script(window,"JSON.stringify({tiny:document.body.dataset.tiny,error:document.querySelector('#error').textContent,clock:document.querySelector('#visual-clock').textContent})"),error);}
    }
    private static async Task Until(Func<Task<bool>> ready){using var limit=new CancellationTokenSource(TimeSpan.FromSeconds(12));while(!await ready())await Task.Delay(25,limit.Token);}
    private static List<PreviewWindow> Windows(PreviewApplication app)=>(List<PreviewWindow>)typeof(PreviewApplication).GetField("windows",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(app)!;
    private static Task Ready(PreviewWindow window)=>((TaskCompletionSource)typeof(PreviewWindow).GetField("interfaceReady",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(window)!).Task;
    private static Task<string> Script(PreviewWindow window,string script)=>((WebView2)typeof(PreviewWindow).GetField("browser",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(window)!).CoreWebView2.ExecuteScriptAsync(script);
    private sealed class Registration:IHotKeyRegistration
    {
        public bool Register(nint window,int id,uint modifiers,uint key)=>true;
        public bool Unregister(nint window,int id)=>true;
    }
}
