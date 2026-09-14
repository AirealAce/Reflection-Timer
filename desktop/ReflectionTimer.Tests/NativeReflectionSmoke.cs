using System.Reflection;
using System.Text.Json;
using Microsoft.Web.WebView2.WinForms;
using Microsoft.Web.WebView2.Core;
using ReflectionTimer.Accessible;
using ReflectionTimer.Core;

// Opt-in native WebView smoke test. Only this test's in-memory session and its
// disposable WebView profile are used; no production windows or input injection.
static class NativeReflectionSmoke
{
    internal static void Run(bool sendModeOnly=false)
    {
        Exception? failure=null;var passed=0;
        void Check(bool condition,string name){if(!condition)throw new Exception(name);passed++;Console.WriteLine("PASS "+name);}
        var thread=new Thread(()=>{
            PreviewApplication? app=null;
            try {
                Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);Application.EnableVisualStyles();
                var now=DateTimeOffset.Now;
                var state=new AppState{ShowFloatingTimer=false,LoggingEnabled=false,Theme=AppColorTheme.Glamour,Timer=new(){Volume=0}};
                var session=new PreviewSession(new MemoryStore{State=state},()=>now,isolatedProfile:true);
                session.Engine.Start(60,false,0,lowTime:new(){Enabled=false});
                var id=session.Engine.CheckIn();session.Engine.SaveDraft(id,"Saved native-window draft","Native reason draft");
                var directory=Path.Combine(Path.GetTempPath(),"ReflectionTimer-NativeSmoke-"+Guid.NewGuid().ToString("N"));
                app=new PreviewApplication(session,directory,startInTray:true,profileName:"native-smoke");
                _=app.MainForm!.Handle;
                app.MainForm.BeginInvoke(async()=>{
                    try {
                        app.Open("reflection",id);
                        var window=Windows(app).Single(w=>w.View=="reflection");
                        Check(!window.Visible,"New reflection stays hidden while its editor is initializing");
                        await Until(()=>window.Visible);
                        var first=await Read(window);
                        Check(first.GetProperty("draft").GetString()=="Saved native-window draft"&&first.GetProperty("theme").GetString()=="3","First native display has the saved response and Glamour theme already loaded");
                        Check(first.GetProperty("reasonVisible").GetBoolean()&&first.GetProperty("reason").GetString()=="Native reason draft","Pre-completion native prompt shows its saved reason");
                        if(sendModeOnly){await ExerciseSendModes(app,window,seconds=>now=now.AddSeconds(seconds),Check);return;}
                        await Script(window,"document.querySelector('#later').focus()");
                        ((IReflectionShortcutTarget)window).FocusOrSaveDraft();
                        await UntilAsync(async()=>(await Read(window)).GetProperty("focused").GetString()=="reflection-text");
                        Check(!window.IsDisposed,"Comma from a non-text control focuses the first box without closing");
                        await Script(window,"document.querySelector('#reflection-text').value='Latest native draft';document.querySelector('#reflection-text').dispatchEvent(new Event('input',{bubbles:true}))");
                        ((IReflectionShortcutTarget)window).FocusOrSaveDraft();
                        await Until(()=>!window.ReflectionOpen&&!window.Visible);
                        Check(session.Engine.Snapshot.Prompts.Single().Draft=="Latest native draft"&&session.Engine.Snapshot.Outbox.Count==0,"Native Save shortcut persists the latest text and closes without submission");
                        app.Open("reflection",id);var reopened=Windows(app).Single(w=>w.View=="reflection");await Until(()=>reopened.Visible);
                        Check(ReferenceEquals(window,reopened)&&(await Read(reopened)).GetProperty("draft").GetString()=="Latest native draft\n","Reused native prompt restores the saved draft with its default newline before display");
                        await Script(reopened,"document.querySelector('#reflection-text').value='Typed just before zero';document.querySelector('#reflection-text').dispatchEvent(new Event('input',{bubbles:true}));document.querySelector('#early-reason').focus()");
                        now=now.AddSeconds(60);session.Tick();app.Open("reflection",id);
                        await UntilAsync(async()=>!(await Read(reopened)).GetProperty("reasonVisible").GetBoolean());
                        var completed=await Read(reopened);
                        Check(ReferenceEquals(reopened,Windows(app).Single(w=>w.View=="reflection"))&&completed.GetProperty("draft").GetString()=="Typed just before zero","Natural completion updates the existing native editor without blanking in-flight text");
                        Check(completed.GetProperty("focused").GetString()=="reflection-text"&&!completed.GetProperty("reasonVisible").GetBoolean(),"Natural completion hides the reason and leaves focus in the response box");
                        ((IReflectionShortcutTarget)reopened).FocusOrSaveDraft();await Until(()=>!reopened.ReflectionOpen&&!reopened.Visible);
                        Check(session.Engine.Snapshot.Prompts.Single() is {IsCheckIn:false,EndedEarly:false,Draft:"Typed just before zero"}&&session.Engine.Snapshot.Outbox.Count==0,"Completed native draft saves under the original ID without a duplicate entry");
                        session.Engine.SetAutoSendIncompleteReflections(false);
                        session.Engine.Start(60,false,0,lowTime:new(){Enabled=false});now=now.AddSeconds(5);
                        var result=session.Execute("startOrEnd",JsonSerializer.SerializeToElement(new{}));
                        var earlyId=result.OpenReflection!.Value;
                        Check(result.SessionCompleted&&session.Engine.Snapshot.Prompts.Single(p=>p.Id==earlyId).EndedEarly,"Backtick ending before zero identifies a genuine early-ended reflection");
                        app.Open("reflection",earlyId,sessionCompleted:result.SessionCompleted);
                        await Until(()=>Windows(app).Any(w=>w.PromptId==earlyId&&w.Visible));
                        var earlyWindow=Windows(app).Single(w=>w.PromptId==earlyId);
                        Check((await Read(earlyWindow)).GetProperty("reasonVisible").GetBoolean(),"Native early-ended popup shows the smaller reason-for-ending-early box");
                        var retainedBrowser=Browser(earlyWindow);var retainedProcess=retainedBrowser.CoreWebView2.BrowserProcessId;
                        foreach(var kind in new[]{CoreWebView2ProcessFailedKind.GpuProcessExited,CoreWebView2ProcessFailedKind.UtilityProcessExited,CoreWebView2ProcessFailedKind.RenderProcessUnresponsive}){
                            earlyWindow.ProcessFailure(kind,CoreWebView2ProcessFailedReason.Unexpected,0);
                            Check(retainedBrowser.Visible&&(await Read(earlyWindow)).ValueKind==JsonValueKind.Object,"Recoverable WebView2 event keeps the editor available: "+kind);
                        }
                        await Script(earlyWindow,"document.querySelector('#reflection-text').value='Early saved response';document.querySelector('#reflection-text').dispatchEvent(new Event('input',{bubbles:true}));document.querySelector('#early-reason').value='Needed a break';document.querySelector('#early-reason').dispatchEvent(new Event('input',{bubbles:true}));document.querySelector('#reflection-prev').click()");
                        await UntilAsync(async()=>earlyWindow.PromptId==id&&(await Read(earlyWindow)).GetProperty("draft").GetString()=="Typed just before zero\n");
                        var previous=Windows(app).Single(w=>w.PromptId==id);
                        Check(Windows(app).Count(w=>w.View=="reflection"&&w.Visible)==1&&(await Read(previous)).GetProperty("draft").GetString()=="Typed just before zero\n","Prev displays only one native reflection and restores the older saved response");
                        Check(session.Engine.Snapshot.Prompts.Single(p=>p.Id==earlyId) is {Draft:"Early saved response",EarlyEndReason:"Needed a break"}&&session.Engine.Snapshot.Outbox.Count==0,"Native navigation flushes both newly typed fields without submitting them");
                        Check(!(await Read(previous)).GetProperty("reasonVisible").GetBoolean(),"Prev to a naturally completed session hides only that session's reason box");
                        Check(ReferenceEquals(earlyWindow,previous)&&ReferenceEquals(retainedBrowser,Browser(previous))&&retainedProcess==retainedBrowser.CoreWebView2.BrowserProcessId,"Prev keeps the same native window, browser control, and browser process alive");
                        await UntilAsync(async()=>!JsonDocument.Parse(await Script(previous,"document.querySelector('#reflection-text').readOnly")).RootElement.GetBoolean());
                        await Script(previous,"document.querySelector('#reflection-next').click()");
                        await UntilAsync(async()=>previous.PromptId==earlyId&&(await Read(previous)).GetProperty("draft").GetString()=="Early saved response");
                        var next=Windows(app).Single(w=>w.PromptId==earlyId);var restored=await Read(next);
                        Check(Windows(app).Count(w=>w.View=="reflection"&&w.Visible)==1&&restored.GetProperty("reasonVisible").GetBoolean()&&restored.GetProperty("reason").GetString()=="Needed a break"&&restored.GetProperty("draft").GetString()=="Early saved response","Next restores the early-ended response and reason already populated in a single visible window");
                        for(var cycle=0;cycle<12;cycle++){
                            await app.NavigateReflectionAsync(earlyId,-1);await app.NavigateReflectionAsync(id,1);
                            var current=await Read(next);
                            Check(ReferenceEquals(retainedBrowser,Browser(next))&&retainedProcess==retainedBrowser.CoreWebView2.BrowserProcessId&&next.PromptId==earlyId&&current.GetProperty("draft").GetString()=="Early saved response"&&current.GetProperty("reason").GetString()=="Needed a break"&&session.Engine.Snapshot.Outbox.Count==0,"Repeated native Prev/Next keeps both drafts and the browser alive: cycle "+(cycle+1));
                        }
                        await ((IReflectionPromptWindow)next).PrepareHandoffAsync();
                        try{await next.SwitchReflectionAsync(Guid.NewGuid());throw new Exception("Missing reflection was loaded");}
                        catch(InvalidOperationException error)when(error.Message.Contains("could not be loaded")){}
                        ((IReflectionPromptWindow)next).ResumeEditing();
                        Check(next.PromptId==earlyId&&(await Read(next)).GetProperty("draft").GetString()=="Early saved response","Failed in-place load restores the previous reflection and its saved draft");
                        ((IReflectionShortcutTarget)next).FocusOrSaveDraft();await Until(()=>!next.ReflectionOpen&&!next.Visible);
                        app.Open("reflection",session.ReflectionForShortcut());
                        var reload=Windows(app).Single(w=>w.PromptId==earlyId);
                        Check(!reload.Visible,"Comma reopening an early-ended draft does not expose an empty editor");
                        await Until(()=>reload.Visible);var reloaded=await Read(reload);
                        Check(reloaded.GetProperty("draft").GetString()=="Early saved response\n"&&reloaded.GetProperty("reasonVisible").GetBoolean()&&reloaded.GetProperty("reason").GetString()=="Needed a break","Reopened early-ended popup has both saved text fields at its first native display");
                        var broken=(PreviewWindow)typeof(PreviewApplication).GetMethod("Create",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(app,["reflection",id])!;
                        typeof(PreviewWindow).GetMethod("ShowFailure",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(broken,["Synthetic target editor load failure."]);
                        try {await app.NavigateReflectionAsync(earlyId,-1);throw new Exception("Failed native target was treated as ready");}
                        catch(InvalidOperationException error) when(error.Message=="Synthetic target editor load failure."){}
                        await UntilAsync(async()=>!JsonDocument.Parse(await Script(reload,"document.querySelector('#reflection-text').readOnly")).RootElement.GetBoolean());
                        Check(broken.IsDisposed&&!reload.IsDisposed&&reload.Visible&&Windows(app).Count(w=>w.View=="reflection")==1,"A real native target-load failure removes the hidden failed view and retains the current visible editor");
                        Check((await Read(reload)).GetProperty("draft").GetString()=="Early saved response\n"&&session.Engine.Snapshot.Outbox.Count==0,"Failed native navigation leaves the original saved text editable and unsent");
                        await Script(reload,"document.querySelector('#reflection-prev').click()");
                        await UntilAsync(async()=>reload.PromptId==id&&(await Read(reload)).GetProperty("draft").GetString()=="Typed just before zero\n");
                        var retried=Windows(app).Single(w=>w.PromptId==id);
                        Check(ReferenceEquals(reload,retried)&&(await Read(retried)).GetProperty("draft").GetString()=="Typed just before zero\n"&&Windows(app).Count(w=>w.View=="reflection")==1,"Retry after a hidden target failure reuses the populated working editor successfully");
                        await ExerciseRecovery(app,retried,Check);
                        var queuedBeforeFailure=session.Engine.Snapshot.Outbox.Count;
                        typeof(PreviewWindow).GetMethod("ShowFailure",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(retried,["Synthetic running editor failure."]);
                        try {await ((IReflectionPromptWindow)retried).PrepareHandoffAsync();throw new Exception("Failed current editor was auto-sent");}
                        catch(InvalidOperationException error) when(error.Message.Contains("not sent or replaced")){}
                        ((IReflectionPromptWindow)retried).ResumeEditing();
                        Check(session.Engine.Snapshot.Outbox.Count==queuedBeforeFailure&&session.Engine.Snapshot.Prompts.Any(p=>p.Id==id),"An unavailable current editor cannot silently auto-send its older saved snapshot");
                        Check(session.Engine.Snapshot.Connection.WebAppUrl==""&&session.Engine.Snapshot.Timer.Volume==0,"Native smoke test stays muted and disconnected throughout");
                    } catch(Exception error){failure=error;}
                    finally {await app.CloseMainAsync();}
                });
                Application.Run(app);
            } catch(Exception error){failure=error;}
            finally {app?.Dispose();}
        });
        thread.SetApartmentState(ApartmentState.STA);thread.Start();
        if(!thread.Join(TimeSpan.FromSeconds(150)))throw new TimeoutException("Native smoke did not finish; inspect its isolated process.");
        if(failure is not null)throw new Exception("Native reflection smoke failed",failure);
        Console.WriteLine($"{passed} native smoke checks passed.");
    }
    private static List<PreviewWindow> Windows(PreviewApplication app)=>(List<PreviewWindow>)typeof(PreviewApplication).GetField("windows",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(app)!;
    private static async Task ExerciseSendModes(PreviewApplication app,PreviewWindow window,Action<int> advance,Action<bool,string> check)
    {
        var session=app.Session;var first=window.PromptId!.Value;var timer=session.Engine.Snapshot.Timer;
        advance(13);
        await Script(window,"document.querySelector('#later').focus();document.dispatchEvent(new KeyboardEvent('keydown',{key:'Enter',altKey:true,bubbles:true,cancelable:true}))");
        await Until(()=>!window.ReflectionOpen&&!window.Visible);
        check(session.Engine.Snapshot.Timer==timer&&session.Engine.Snapshot.Outbox.Single(o=>o.Id==first) is {IsCheckIn:true,EndedEarly:false,ActualDurationSeconds:13,Message:"Saved native-window draft"},"Native Alt+Enter bridge submits a check-in and hides the editor without changing the session");
        var second=session.Engine.CheckIn();session.Engine.SaveDraft(second,"Current response","Reason for stopping");
        session.Engine.SetPreferences(true,0);advance(7);
        app.Open("reflection",second);await UntilAsync(async()=>window.PromptId==second&&window.Visible&&(await Read(window)).GetProperty("draft").GetString()=="Current response");
        var browser=Browser(window);
        await Script(window,"document.querySelector('#early-reason').focus();document.dispatchEvent(new KeyboardEvent('keydown',{key:'s',ctrlKey:true,bubbles:true,cancelable:true}))");
        await Until(()=>!window.ReflectionOpen&&!window.Visible);
        check(session.Engine.Snapshot.Outbox.All(o=>o.Id!=second)&&session.Engine.Snapshot.Prompts.Single(p=>p.Id==second) is {IsCheckIn:true,Draft:"Current response",EarlyEndReason:"Reason for stopping"},"Native Ctrl+S saves both active-session fields without submitting them");
        check(session.Engine.Snapshot.Timer.IsRunning&&session.Engine.Snapshot.Timer.AutoRestart&&session.Engine.Snapshot.Timer.SessionId==timer.SessionId,"Native Ctrl+S leaves the same session and auto-start running");
        await Task.Delay(1100);
        check(!window.Visible&&!window.ReflectionOpen&&session.Engine.Snapshot.Prompts.Count==1&&ReferenceEquals(browser,Browser(window)),"A timer tick after Save cannot reopen the active draft");
        session.Engine.Pause();var paused=session.Engine.Snapshot.Timer;
        app.Open("reflection",second);await Until(()=>window.Visible&&window.ReflectionOpen);
        await Script(window,"document.dispatchEvent(new KeyboardEvent('keydown',{key:'s',ctrlKey:true,bubbles:true,cancelable:true}))");
        await Until(()=>!window.ReflectionOpen&&!window.Visible);
        check(session.Engine.Snapshot.Timer==paused&&session.Engine.Snapshot.Outbox.All(o=>o.Id!=second),"Native Ctrl+S saves a paused session without sending or resuming");
        session.Engine.Resume();advance(40);await Until(()=>window.Visible&&window.ReflectionOpen);
        check(window.PromptId==second&&(await Read(window)).GetProperty("draft").GetString()!.StartsWith("Current response"),"Natural completion reopens the same saved response");
        var nextSession=session.Engine.Snapshot.Timer.SessionId;
        await Script(window,"document.dispatchEvent(new KeyboardEvent('keydown',{key:'Enter',ctrlKey:true,bubbles:true,cancelable:true}))");
        await Until(()=>!window.ReflectionOpen&&!window.Visible);
        check(session.Engine.Snapshot.Outbox.Single(o=>o.Id==second) is {IsCheckIn:false,EndedEarly:false,ActualDurationSeconds:60,EarlyEndReason:""}
            &&session.Engine.Snapshot.Timer.SessionId==nextSession,"Native Ctrl+Enter sends the ended session without changing the next timer");
        app.Open("reflection",second,sessionCompleted:true);
        await Task.Delay(1100);
        check(!window.Visible&&!window.ReflectionOpen&&session.Engine.Snapshot.Prompts.Count==0,
            "Neither a stale open request nor the original deadline reopens the submitted reflection");
        advance(60);await Until(()=>window.Visible&&window.ReflectionOpen);
        check(window.PromptId!=second&&session.Engine.Snapshot.Prompts.Single().SessionId==nextSession&&ReferenceEquals(browser,Browser(window)),
            "The next session's own natural completion still opens its distinct reflection in the retained native editor");
        var skipped=0;session.Engine.ActivityRecorded+=activity=>{if(activity.Event=="prompt.skipped")skipped++;};
        foreach(var kind in new[]{"completed","running","paused"}){
            if(kind!="completed"){
                var emptyId=session.Engine.CheckIn();
                if(kind=="paused")session.Engine.Pause();
                app.Open("reflection",emptyId);
                await Until(()=>window.PromptId==emptyId&&window.Visible&&window.ReflectionOpen);
            }
            var empty=await Read(window);var emptyPrompt=window.PromptId!.Value;
            check(empty.GetProperty("draft").GetString()==""&&empty.GetProperty("reason").GetString()=="","Native empty shortcut starts with both fields blank: "+kind);
            var beforeTimer=session.Engine.Snapshot.Timer;var outboxCount=session.Engine.Snapshot.Outbox.Count;var beforeSkips=skipped;
            await Script(window,"document.querySelector('#later').focus();document.dispatchEvent(new KeyboardEvent('keydown',{key:'Enter',ctrlKey:true,bubbles:true,cancelable:true}))");
            await Until(()=>!window.ReflectionOpen&&!window.Visible);
            check(session.Engine.Snapshot.Prompts.All(p=>p.Id!=emptyPrompt)&&session.Engine.Snapshot.Outbox.Count==outboxCount
                &&session.Engine.Snapshot.Timer==beforeTimer&&skipped==beforeSkips+1,"Native empty Ctrl+Enter skips once from a button without sending or changing the timer: "+kind);
        }
        foreach(var useAlt in new[]{false,true})foreach(var pause in new[]{false,true})foreach(var elapsed in new[]{7,55}){
            session.Engine.Start(60,false,0,lowTime:new(){Enabled=false});advance(elapsed);
            if(pause)session.Engine.Pause();
            var ending=session.Engine.CheckIn();session.Engine.SaveDraft(ending,"Shortcut response","Shortcut reason");
            var endingSession=session.Engine.Snapshot.Timer.SessionId;
            app.Open("reflection",ending);await UntilAsync(async()=>window.PromptId==ending&&window.Visible&&(await Read(window)).GetProperty("draft").GetString()=="Shortcut response");
            var chord=useAlt?"key:'s',altKey:true":"key:'Enter',ctrlKey:true";
            await Script(window,"document.querySelector('#reflection-next').focus();document.dispatchEvent(new KeyboardEvent('keydown',{"+chord+",bubbles:true,cancelable:true}))");
            await Until(()=>!window.ReflectionOpen&&!window.Visible);
            var entry=session.Engine.Snapshot.Outbox.Single(o=>o.Id==ending);var early=elapsed==7;
            check(!entry.IsCheckIn&&entry.EndedEarly==early&&entry.ActualDurationSeconds==elapsed&&entry.Message=="Shortcut response"&&entry.EarlyEndReason==(early?"Shortcut reason":"")
                &&!session.Engine.Snapshot.Timer.IsRunning&&session.Engine.Snapshot.Timer.SessionId==endingSession,
                "Native send shortcut applies the capped grace period with actual elapsed time: Alt+S="+useAlt+", paused="+pause+", elapsed="+elapsed);
        }
        check(session.Engine.Snapshot.Connection.WebAppUrl==""&&session.Engine.Snapshot.Outbox.All(o=>o.LocalOnly)&&session.Engine.Snapshot.Timer.Volume==0,"Native shortcut smoke remains muted, isolated, and disconnected from Sheets");
    }
    private static async Task ExerciseRecovery(PreviewApplication app,PreviewWindow reflection,Action<bool,string> check)
    {
        var session=app.Session;var id=reflection.PromptId!.Value;
        await Script(reflection,"document.querySelector('#reflection-text').value='Saved through browser recovery';document.querySelector('#reflection-text').dispatchEvent(new Event('input',{bubbles:true}))");
        await reflection.FlushDraftAsync();
        app.Open("main");var main=Windows(app).Single(w=>w.View=="main");
        await UntilAsync(async()=>Browser(main).CoreWebView2 is not null&&JsonDocument.Parse(await Script(main,"document.querySelector('#sound-behavior-0')?.options.length>0")).RootElement.ValueKind==JsonValueKind.True);
        for(var separator=0;separator<4;separator++){
            await Script(main,$"document.querySelector('#reflectionSeparator').value='{separator}';document.querySelector('#reflectionSeparator').dispatchEvent(new Event('change',{{bubbles:true}}))");
            await Until(()=>session.Engine.Snapshot.ReflectionSeparator==(ReflectionSeparator)separator);
            check((await Read(reflection)).GetProperty("draft").GetString()=="Saved through browser recovery","Native separator setting persists without changing an open response: option "+separator);
        }
        app.Open("compact");var compact=Windows(app).Single(w=>w.View=="compact");
        await ((TaskCompletionSource)typeof(PreviewWindow).GetField("interfaceReady",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(compact)!).Task.WaitAsync(TimeSpan.FromSeconds(20));
        var compactBrowser=Browser(compact);var reflectionBrowser=Browser(reflection);var process=compactBrowser.CoreWebView2.BrowserProcessId;
        for(var i=0;i<12;i++){
            await Script(main,$"document.querySelector('#sound-behavior-0').value='{i%3}';document.querySelector('#sound-behavior-0').dispatchEvent(new Event('change',{{bubbles:true}}))");
            app.Open("main",timerPage:true);
            compact.Post(new{type="shrinkCompact"});await Until(()=>compact.IsTimeOnly);
            app.ToggleCompactVisibility();await Until(()=>!compact.Visible);
            app.Open("compact");await Until(()=>compact.Visible&&!compact.IsTimeOnly);
            check(ReferenceEquals(compactBrowser,Browser(compact))&&ReferenceEquals(reflectionBrowser,Browser(reflection))&&process==compactBrowser.CoreWebView2.BrowserProcessId&&(await Read(reflection)).GetProperty("draft").GetString()=="Saved through browser recovery","Audio edit, Timer tab, time-only hide and compact reopen retain both live browsers: cycle "+(i+1));
        }
        compact.Close();await Until(()=>!compact.Visible);
        check(!compact.IsDisposed&&!session.Engine.Snapshot.ShowFloatingTimer,"Floating close button hides the viewer and persists its visibility without disposing WebView2");
        app.Open("compact");compact.Post(new{type="shrinkCompact"});await Until(()=>compact.IsTimeOnly);
        session.SetDurationDraft(["0","3","42"]);
        // Crash only the renderer belonging to this test's disposable, disconnected profile.
        var rendererFailed=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        reflectionBrowser.CoreWebView2.ProcessFailed+=(_,e)=>{if(e.ProcessFailedKind==CoreWebView2ProcessFailedKind.RenderProcessExited)rendererFailed.TrySetResult();};
        _=reflectionBrowser.CoreWebView2.CallDevToolsProtocolMethodAsync("Page.crash","{}").ContinueWith(t=>{_ = t.Exception;},TaskContinuationOptions.OnlyOnFaulted);
        await rendererFailed.Task.WaitAsync(TimeSpan.FromSeconds(15));
        await UntilAsync(async()=>{
            try{return ReferenceEquals(reflectionBrowser,Browser(reflection))&&reflectionBrowser.Visible&&(await Read(reflection)).GetProperty("draft").GetString()=="Saved through browser recovery"&&!GetFlag(reflection,"recoveringInterface");}catch{return false;}
        });
        // The failure callback is asynchronous: require a reload of the actual document.
        await UntilAsync(async()=>JsonDocument.Parse(await Script(reflection,"document.readyState")).RootElement.GetString()=="complete");
        check(reflectionBrowser.CoreWebView2.BrowserProcessId==process&&session.Engine.Snapshot.Outbox.Count==0,"Renderer recovery preserves the browser process and never submits the reflection");
        // The browser process was obtained from this test's own WebView, never a user window.
        System.Diagnostics.Process.GetProcessById((int)process).Kill();
        await UntilAsync(async()=>{
            try{return !app.BrowserRecoveryInProgress&&Browser(reflection).CoreWebView2.BrowserProcessId!=process&&Browser(reflection).Visible&&(await Read(reflection)).GetProperty("draft").GetString()=="Saved through browser recovery";}catch{return false;}
        });
        var restarted=Browser(reflection).CoreWebView2.BrowserProcessId;
        check(Browser(main).CoreWebView2.BrowserProcessId==restarted&&Browser(compact).CoreWebView2.BrowserProcessId==restarted,"Shared browser crash recreates every viewer together in one replacement browser process");
        check(reflection.Visible&&reflection.PromptId==id&&session.Engine.Snapshot.Outbox.Count==0&&(await Read(reflection)).GetProperty("draft").GetString()=="Saved through browser recovery","Browser crash restores the open reflection's saved draft without closing or submitting it");
        check(compact.Visible&&compact.IsTimeOnly&&JsonDocument.Parse(await Script(compact,"document.querySelector('#visual-clock').textContent")).RootElement.GetString()=="3:42","Browser recovery preserves time-only mode and the shared duration draft");
        await Script(reflection,"document.querySelector('#reflection-text').value='Editable after recovery';document.querySelector('#reflection-text').dispatchEvent(new Event('input',{bubbles:true}))");
        await reflection.FlushDraftAsync();
        check(session.Engine.Snapshot.Prompts.Single(p=>p.Id==id).Draft=="Editable after recovery","Recovered reflection saves new edits through the replacement bridge");
        compact.Close();await Until(()=>!compact.Visible);
        session.Engine.Start(600,false,0,lowTime:new(){Enabled=false});var deadline=session.Engine.Snapshot.Timer.EndTime;
        System.Diagnostics.Process.GetProcessById((int)restarted).Kill();
        await UntilAsync(async()=>{try{return !app.BrowserRecoveryInProgress&&Browser(reflection).CoreWebView2.BrowserProcessId!=restarted&&Browser(reflection).Visible&&(await Read(reflection)).GetProperty("draft").GetString()=="Editable after recovery";}catch{return false;}});
        check(!compact.Visible&&!session.Engine.Snapshot.ShowFloatingTimer,"A second browser recovery leaves a closed compact viewer hidden");
        check(session.Engine.Snapshot.Timer.IsRunning&&session.Engine.Snapshot.Timer.EndTime==deadline,"An active timer keeps its original deadline through browser recovery");
        var third=Browser(reflection).CoreWebView2.BrowserProcessId;
        System.Diagnostics.Process.GetProcessById((int)third).Kill();
        await UntilAsync(async()=>{try{return !app.BrowserRecoveryInProgress&&Browser(reflection).CoreWebView2.BrowserProcessId!=third&&Browser(reflection).Visible&&(await Read(reflection)).GetProperty("draft").GetString()=="Editable after recovery";}catch{return false;}});
        var fourth=Browser(reflection).CoreWebView2.BrowserProcessId;
        System.Diagnostics.Process.GetProcessById((int)fourth).Kill();
        await Until(()=>!app.BrowserRecoveryInProgress&&typeof(PreviewWindow).GetField("retryInterface",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(reflection) is Button {Visible:true});
        check(!Browser(reflection).Visible&&session.Engine.Snapshot.Prompts.Single(p=>p.Id==id).Draft=="Editable after recovery","Repeated crashes stop automatic restarts and expose an accessible retry button without losing the draft");
        ((Button)typeof(PreviewWindow).GetField("retryInterface",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(reflection)!).PerformClick();
        await UntilAsync(async()=>{try{return !app.BrowserRecoveryInProgress&&Browser(reflection).CoreWebView2.BrowserProcessId!=fourth&&Browser(reflection).Visible&&(await Read(reflection)).GetProperty("draft").GetString()=="Editable after recovery";}catch{return false;}});
        check(session.Engine.Snapshot.Timer.EndTime==deadline&&session.Engine.Snapshot.Outbox.Count==0,"Manual retry restores the shared interface and leaves timer and Outbox intact");
        var cachedBrowser=Browser(reflection);var firstTest=session.Engine.TestPrompt();app.Open("reflection",firstTest);
        await UntilAsync(async()=>reflection.PromptId==firstTest&&(await Read(reflection)).GetProperty("draft").GetString()=="");
        await Script(reflection,"document.querySelector('#reflection-text').value='Explicit native test submission';document.querySelector('#reflection-text').dispatchEvent(new Event('input',{bubbles:true}));document.querySelector('#reflection-form').requestSubmit()");
        await Until(()=>!reflection.ReflectionOpen&&!reflection.Visible);
        await reflection.FlushDraftAsync();
        check(session.Engine.Snapshot.Outbox.Count==1&&!session.Engine.Snapshot.Prompts.Any(p=>p.Id==firstTest)&&ReferenceEquals(cachedBrowser,Browser(reflection)),"Save and send closes the logical editor while retaining its browser; a hidden submitted draft is never flushed");
        var secondTest=session.Engine.TestPrompt();app.Open("reflection",secondTest);
        await UntilAsync(async()=>reflection.PromptId==secondTest&&reflection.Visible&&(await Read(reflection)).GetProperty("draft").GetString()=="");
        await Script(reflection,"document.querySelector('#reflection-text').value='New draft in reused editor';document.querySelector('#reflection-text').dispatchEvent(new Event('input',{bubbles:true}));document.querySelector('#later').click()");
        await Until(()=>!reflection.ReflectionOpen&&!reflection.Visible);
        check(session.Engine.Snapshot.Prompts.Single(p=>p.Id==secondTest).Draft=="New draft in reused editor"&&session.Engine.Snapshot.Outbox.Count==1,"Reopening after submission clears the old submission flag and saves only the new prompt");
        session.Engine.SetAutoSendIncompleteReflections(true);
        var thirdTest=session.Engine.TestPrompt();app.Open("reflection",thirdTest,sessionCompleted:true);
        await UntilAsync(async()=>reflection.PromptId==thirdTest&&reflection.Visible&&(await Read(reflection)).GetProperty("draft").GetString()=="");
        check(session.Engine.Snapshot.Outbox.Count==2&&!session.Engine.Snapshot.Prompts.Any(p=>p.Id==secondTest)&&session.Engine.Snapshot.Prompts.Any(p=>p.Id==id)&&ReferenceEquals(cachedBrowser,Browser(reflection)),"A new completion auto-sends the prior saved reflection of the same mode and reuses its browser without affecting other drafts");
        await Script(reflection,"document.querySelector('#skip-reflection').click()");await Until(()=>!reflection.ReflectionOpen&&!reflection.Visible);
        session.Engine.SetAutoSendIncompleteReflections(false);app.Open("reflection",id);
        await UntilAsync(async()=>reflection.PromptId==id&&reflection.Visible&&(await Read(reflection)).GetProperty("draft").GetString()=="Editable after recovery");
        // Restore this test's expected text for the subsequent failed-handoff check.
        session.Engine.SaveDraft(id,"Typed just before zero","");
    }
    private static bool GetFlag(PreviewWindow window,string name)=>(bool)typeof(PreviewWindow).GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(window)!;
    private static WebView2 Browser(PreviewWindow window)=>(WebView2)typeof(PreviewWindow).GetField("browser",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(window)!;
    private static Task<string> Script(PreviewWindow window,string script)=>((WebView2)typeof(PreviewWindow).GetField("browser",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(window)!).CoreWebView2.ExecuteScriptAsync(script);
    private static async Task<JsonElement> Read(PreviewWindow window)=>JsonDocument.Parse(await Script(window,"JSON.parse(JSON.stringify({draft:document.querySelector('#reflection-text').value,reason:document.querySelector('#early-reason').value,reasonVisible:!document.querySelector('#reason-group').hidden,theme:document.documentElement.dataset.theme,focused:document.activeElement.id}))")).RootElement.Clone();
    private static async Task Until(Func<bool> predicate){using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(20));while(!predicate())await Task.Delay(25,timeout.Token);}
    private static async Task UntilAsync(Func<Task<bool>> predicate){using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(20));while(!await predicate())await Task.Delay(25,timeout.Token);}
}
