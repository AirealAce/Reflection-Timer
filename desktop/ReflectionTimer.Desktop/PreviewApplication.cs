using ReflectionTimer.Core;
using ReflectionTimer.Desktop;

namespace ReflectionTimer.Accessible;

internal sealed partial class PreviewApplication : ApplicationContext
{
    internal PreviewSession Session { get; }
    internal string ProfileDirectory { get; }
    internal string? ProfileName { get; }
    internal bool StartInTray { get; }
    internal bool AppViewMayShow { get; private set; }
    internal object ShortcutState => shortcuts.Status;
    internal bool AppViewVisible => MainForm is { IsDisposed: false, Visible: true, WindowState: not FormWindowState.Minimized };
    internal PreviewServices Services { get; }
    internal string? RecoveryNotice { get; set; }
    private readonly List<PreviewWindow> windows = [];
    private readonly System.Windows.Forms.Timer pulse = new() { Interval = 1000 };
    private PreviewWindow? active;
    private TimerState viewerSession;
    private bool closing, tickFailed, keepTimeOnly, viewerResetCommitted;
    private bool? publishedAppViewVisible;
    private long lastSync;
    private readonly NotifyIcon tray;
    private (AppColorTheme Theme, bool Contrast)? menuTheme;
    private (AppColorTheme Theme, bool Contrast, bool Compact, bool Tiny, bool Prompt)? displayPreference;
    private readonly PreviewShortcuts shortcuts;
    private readonly ConsecutiveShortcutPresses compactPresses=new();
    private readonly ReflectionPromptCoordinator promptCoordinator;
    internal PreviewApplication(PreviewSession session, string directory, string? recoveryNotice = null, bool startInTray = false, string? profileName = null, IHotKeyRegistration? shortcutRegistration = null, Func<PreviewWindow,ResetWarning,Task<bool>>? resetConfirmation = null)
    {
        Session = session; ProfileDirectory = directory; ProfileName=profileName; StartInTray=startInTray; RecoveryNotice=recoveryNotice ?? session.Engine.ClockRecoveryNotice;
        viewerSession=session.Engine.CurrentTimer;
        AppViewMayShow = !startInTray && session.Engine.SettingsSnapshot.ShowAppView != false;
        confirmReset=resetConfirmation??((owner,warning)=>owner.ConfirmResetAsync(warning));
        Services = new(session.Engine, directory); Services.Announcement += Announce;
        InitializeFocusMode();
        Services.SessionAnnouncement += AnnounceSession;
        Services.DeliveryIssueChanged += () => Broadcast(new { type = "deliveryIssue", issue = Services.DeliveryIssue });
        Services.Log.Record("app.started");
        Services.Log.Record("theme.loaded",value:(int)session.Engine.Snapshot.Theme);
        promptCoordinator=new(session,()=>windows.Where(w=>w.ReflectionOpen&&!w.IsDisposed).Cast<IReflectionPromptWindow>().ToArray(),ShowReflection,()=>{_ = Services.Sync();});
        MainForm = Create("main");
        var menu=new ContextMenuStrip();
        menu.Items.Add("App view",null,(_,_)=>Open("main"));menu.Items.Add("Compact view",null,(_,_)=>Open("compact"));
        menu.Items.Add("Show / hide floating timer",null,(_,_)=>ToggleCompactVisibility());
        menu.Items.Add("Pending reflections",null,(_,_)=>{var p=Session.Engine.Snapshot.Prompts.LastOrDefault();if(p is not null)Open("reflection",p.Id);else Announce("No pending reflections.");});
        menu.Items.Add("Quit desktop app",null,async(_,_)=>await CloseMainAsync());
        tray=new(){Text="Reflection Timer",Icon=Icon.ExtractAssociatedIcon(Environment.ProcessPath!)??SystemIcons.Information,Visible=true,ContextMenuStrip=menu};
        tray.DoubleClick+=(_,_)=>Open("main");
        // Activity is emitted only after persistence succeeds. A reset can
        // leave the same ready/zero state, so a timer-state comparison alone
        // cannot distinguish it from an unrelated settings update.
        session.Engine.ActivityRecorded += activity => {if(activity.Event=="timer.reset")viewerResetCommitted=true;};
        session.Engine.Changed += () => {var restored=RestoreIdleViewer();ApplyTheme();foreach(var window in windows.ToArray())window.ConfigureAutoHide();Broadcast(new { type = "state", state = session.View(incremental:true), keepTimeOnly=keepTimeOnly||restored });};
        session.Announcement += Announce;
        session.DurationDraftChanged+=parts=>Broadcast(new{type="durationDraft",parts});
        pulse.Tick += (_, _) => {
            try {
                Services.PollVoiceStatus();
                foreach(var prompt in Session.Tick()) _ = OpenReflectionAsync(prompt.Id,false,true,automaticCompletion:true);
                ApplyTheme();Broadcast(new { type = "clock", clock = Session.Clock() }); tickFailed = false;
                if (Session.Engine.ElapsedNow - lastSync >= 15000) { lastSync = Session.Engine.ElapsedNow; _ = Services.Sync(); if(shortcuts?.RetryUnavailable()==true)Broadcast(new{type="shortcuts",shortcuts=ShortcutState}); } }
            catch { if (!tickFailed) Announce("Could not save a timer update. Your last saved state is retained."); tickFailed = true; }
        };
        shortcuts = new PreviewShortcuts([
            Shortcut(0, _=>{compactPresses.Reset();if(WindowActivation.IsForeground(MainForm))HideAppView();else Open("main");}),
            Shortcut(1, at=>{compactPresses.Reset();var result=Session.Execute("startOrEnd",System.Text.Json.JsonSerializer.SerializeToElement(new{}),at);if(result.OpenReflection is {} id)Open("reflection",id,sessionCompleted:result.SessionCompleted);}),
            Shortcut(2, _=>CycleCompact(false)),
            Shortcut(3, _=>{if(compactPresses.Press())Open("main",timerPage:true);else Open("compact");}),
            Shortcut(4, ReflectionHotkey),
            Shortcut(5, ToggleTimerFromGlobalShortcut),
            Shortcut(6, ToggleTimerFromGlobalShortcut),
            Shortcut(7, ToggleModeFromGlobalShortcut),
            Shortcut(8, _=>ResetFromGlobalShortcut()),
            Shortcut(9, _=>CycleCompact(true))
        ], (id,available)=>Services.Log.Record(available?"shortcut.registered":"shortcut.unavailable",value:id), shortcutRegistration);
        ApplyTheme();pulse.Start(); if(AppViewMayShow)MainForm.Show(); ApplyDisplayPreferences();
    }
    private bool RestoreIdleViewer()
    {
        var state=Session.Engine.SettingsSnapshot;
        var previous=viewerSession;
        var resetCommitted=viewerResetCommitted;
        // Consume the action and update before saving visibility, since that
        // save raises Changed too. Failed/cancelled resets never set the flag.
        viewerResetCommitted=false;
        viewerSession=state.Timer;
        var paused=previous.IsRunning&&state.Timer.Mode==previous.Mode
            &&state.Timer.SessionId==previous.SessionId&&TimerEngine.IsPaused(state.Timer);
        if(closing||!state.ViewerAutoHide||state.ShowFloatingTimer||state.Timer.IsRunning
            ||!(resetCommitted||paused))return false;
        var previousLayout=keepTimeOnly;
        keepTimeOnly=true;
        try {
            Session.Engine.SetFloatingTimer(true);
            // Passive Show preserves the saved floating layout and keyboard
            // focus. This also creates a viewer after a hidden/tray-only launch.
            ApplyDisplayPreferences();
            return true;
        } catch {Announce("The session is not running, but its viewer could not be shown. Try the Compact view shortcut or tray menu.");return false;}
        finally {keepTimeOnly=previousLayout;}
    }
    private void ApplyTheme()
    {
        var settings=Session.Engine.SettingsSnapshot;
        var display=(settings.Theme,SystemInformation.HighContrast,settings.CompactAlwaysOnTop,settings.TimeOnlyAlwaysOnTop,settings.PromptAlwaysOnTop);
        if(displayPreference==display)return;
        displayPreference=display;
        foreach(var window in windows.ToArray()){window.ApplyWindowTheme(settings);window.ApplyTopMost(settings);}
        var preference=(settings.Theme,SystemInformation.HighContrast);
        if(menuTheme==preference)return;
        menuTheme=preference;PreviewTheme.ApplyMenu(tray.ContextMenuStrip!,PreviewTheme.Palette(preference.Item1,preference.Item2));
    }
    private Action<TimeSpan> Shortcut(int id, Action<long> action) => queueDelay =>
    {
        var requestedAt=Session.Engine.ElapsedNow-(long)Math.Max(0,queueDelay.TotalMilliseconds);
        if(closing)return;
        try { action(requestedAt); }
        catch(Exception e) { if(id!=4)Open("main"); Announce(e is ArgumentException?e.Message:"That action is unavailable. Your timer is retained."); }
        finally { Services.Log.Record(new Activity(Session.Engine.CalendarTimestamp(requestedAt),"shortcut.used",null,id)); }
    };
    private void OpenPendingOrCheckIn(long requestedAt)
    {
        if(Session.ReflectionForShortcut(requestedAt) is {} id)Open("reflection",id);
        else Announce("No pending reflection. Start a timer before making a check-in.");
    }
    private void ReflectionHotkey(long requestedAt)
    {
        compactPresses.Reset();
        var state=Session.Engine.Snapshot;
        if(state.Timer.Mode==SessionMode.Stopwatch&&(state.Timer.IsRunning||TimerEngine.IsPaused(state.Timer))) {
            var attached=state.Prompts.LastOrDefault(p=>p.Mode==SessionMode.Stopwatch&&p.CheckInSessionId==state.Timer.SessionId);
            var visible=windows.FirstOrDefault(w=>w.ReflectionOpen&&!w.IsDisposed&&w.PromptId==attached?.Id);
            if(state.Timer.IsRunning||visible is null){OpenPendingOrCheckIn(requestedAt);return;}
            ReflectionShortcut.Invoke([visible],()=>OpenPendingOrCheckIn(requestedAt));
        } else ReflectionShortcut.Invoke(windows.Where(w=>w.ReflectionOpen&&!w.IsDisposed),()=>OpenPendingOrCheckIn(requestedAt));
    }
    private void ToggleTimerFromGlobalShortcut(long requestedAt)
    {
        compactPresses.Reset();
        var result=KeepingTimeOnly(()=>Session.ToggleTimerFromShortcut(requestedAt));Announce(result);
        if(result.OpenReflection is {} id)Open("reflection",id,sessionCompleted:result.SessionCompleted);
    }
    internal CommandResult KeepingTimeOnly(Func<CommandResult> action)
    {
        // Carry the initiating control's view preference with its state updates.
        var previous=keepTimeOnly;
        keepTimeOnly=true;
        try {return action();}
        finally {keepTimeOnly=previous;}
    }
    private void ToggleModeFromGlobalShortcut(long requestedAt)
    {
        compactPresses.Reset();
        // Keep focus in the viewer where the user switched modes. Capture it
        // before the state update can resize Compact or hide a duration field.
        var viewer=windows.FirstOrDefault(w=>WindowActivation.IsForeground(w)
            && (w.View=="main" || w.View=="compact"&&!w.IsTimeOnly));
        var result=KeepingTimeOnly(()=>Session.ToggleModeFromShortcut(requestedAt));Announce(result);
        if(viewer is not null)Open(viewer.View,timerPage:true);
        if(result.OpenReflection is {} id)Open("reflection",id,sessionCompleted:result.SessionCompleted);
    }
    internal void SetStartup(bool enabled)
    {
        var previous=Session.Engine.Snapshot.StartAtLogin;
        if(previous==enabled)return;
        var profile=ProfileName;
        PreviewStartup.Set(profile,enabled);
        try { var state=Session.Engine.Snapshot;Session.Engine.SaveSettings(state.Connection,state.LoggingEnabled,enabled,state.ExtensionDisabledConfirmed); }
        catch { PreviewStartup.Set(profile,previous); throw; }
    }
    internal void ToggleCompactVisibility(bool expandOnShow=false)
    {
        if(expandOnShow&&!Session.Engine.Snapshot.ShowFloatingTimer)Open("compact");
        else {Session.Engine.SetFloatingTimer(!Session.Engine.Snapshot.ShowFloatingTimer);ApplyDisplayPreferences();}
    }
    internal void CycleCompact(bool reverse)
    {
        compactPresses.Reset();
        var compact=windows.FirstOrDefault(w=>w.View=="compact");
        if(compact is not null&&!WindowActivation.CanReceiveFocus(compact))return;
        var next=FloatingViewCycle.Next(compact?.Visible==true,compact?.IsTimeOnly==true,reverse);
        if(next==FloatingView.Compact){Open("compact");return;}
        if(next==FloatingView.Hidden) {
            // Supersede queued measurements so rapid cycles use native intent.
            if(compact is not null)compact.SetCompactMode(compact.IsTimeOnly,false);
            Session.Engine.SetFloatingTimer(false);ReleaseTimerViewFocus();ApplyDisplayPreferences();return;
        }
        compact??=Create("compact");
        compact.SetCompactMode(true,false);
        if(!Session.Engine.SettingsSnapshot.ShowFloatingTimer)Session.Engine.SetFloatingTimer(true);
        ReleaseTimerViewFocus();
        ApplyDisplayPreferences(); // ShowWithoutActivation: do not focus tiny.
    }
    private PreviewWindow Create(string view, Guid? prompt = null)
    {
        var window = new PreviewWindow(this, view, prompt);
        windows.Add(window); window.Activated += (_, _) => {active = window;Services.Log.Record("app.activated");};
        window.Deactivate+=(_,_)=>Services.Log.Record("app.deactivated");
        if(view=="main") {
            window.VisibleChanged+=(_,_)=>PublishAppViewVisibility();
            window.Resize+=(_,_)=>PublishAppViewVisibility();
        }
        // A preload can fail before a native handle exists; disposing that hidden
        // form does not necessarily raise FormClosed. Never retain dead editors.
        window.Disposed += (_, _) => { windows.Remove(window); if (active == window) active = null; };
        return window;
    }
    internal void Open(string view, Guid? prompt = null, bool timerPage=false, bool sessionCompleted=false)
    {
        RememberReturnFocus(WindowActivation.Foreground);
        if(view=="reflection"){if(prompt is {} id)_ = OpenReflectionAsync(id,true,sessionCompleted);return;}
        string? viewSaveError=null;
        if(view=="main") {
            try { Session.Engine.SetAppViewVisibility(true); }
            catch {
                // A full/read-only disk must not strand the user in the tray.
                viewSaveError="App view opened, but its visibility could not be saved for the next launch.";
                RecoveryNotice??=viewSaveError;
            }
            AppViewMayShow=true;
        }
        var window = windows.FirstOrDefault(w => w.View == view && w.PromptId == prompt);
        if(window is null){window=Create(view,prompt);window.ApplyPosition();}
        if(view=="compact"&&!Session.Engine.SettingsSnapshot.ShowFloatingTimer) {
            window.SetCompactMode(false,false);
            Session.Engine.SetFloatingTimer(true);
        }
        if (window.WindowState == FormWindowState.Minimized) window.WindowState = FormWindowState.Normal;
        WindowActivation.Focus(window);
        if(WindowActivation.CanReceiveFocus(window))window.FocusControls(timerPage);
        if(viewSaveError is not null)Announce(viewSaveError);
    }
    internal void HideAppView()
    {
        // Save when the user chooses a view, not during shutdown, disposal, or
        // startup. A failed write must leave the current window available.
        if(!AppViewMayShow && !AppViewVisible)return;
        Session.Engine.SetAppViewVisibility(false);
        AppViewMayShow=false;
        MainForm?.Hide();
    }
    private async Task OpenReflectionAsync(Guid id,bool activate,bool sessionCompleted=false,bool automaticCompletion=false)
    {
        try {await promptCoordinator.OpenAsync(id,activate,()=>closing,sessionCompleted,automaticCompletion);}
        catch {Announce("The reflection could not be opened. Existing drafts are retained; any current editor stays open. Try Pending reflections again.");}
    }
    internal Task NavigateReflectionAsync(Guid from,int direction)=>promptCoordinator.NavigateAsync(from,direction,()=>closing);
    internal async Task CompleteSubmittedSessionAsync(Guid id)
    {
        try {await promptCoordinator.CompleteSubmittedAsync(id,()=>closing);}
        catch {Announce("Your session ended and its reflection is saved in Outbox. Older drafts could not be auto-sent and remain saved locally.");}
    }
    private async Task ShowReflection(Guid id,bool activate)
    {
        var window=windows.FirstOrDefault(w=>w.View=="reflection"&&w.PromptId==id);
        // Keep the visible browser and its accessibility objects alive while
        // browsing. Prev/Next must not tear down one WebView2 and create another.
        window??=windows.FirstOrDefault(w=>w.View=="reflection");
        if(window is null){window=Create("reflection",id);window.ApplyPosition();}
        try {
            await window.PrepareReflectionAsync();
            if(window.PromptId!=id||!window.ReflectionOpen)await window.SwitchReflectionAsync(id);
            window.ReflectionOpen=true;
        }
        catch {
            // Failed hidden targets must not replace the working editor or stay
            // in the shortcut/navigation window list. A retry gets a fresh view.
            if(!window.IsDisposed&&!window.Visible)window.ClosePermanently();
            throw;
        }
        if(closing||window.IsDisposed||!Session.Engine.Snapshot.Prompts.Any(p=>p.Id==id)){if(!window.IsDisposed)window.CloseAfterSave();return;}
        foreach(var previous in windows.Where(w=>w.View=="reflection"&&w!=window).ToArray())previous.CloseAfterSave();
        if(activate){WindowActivation.Focus(window);if(WindowActivation.CanReceiveFocus(window))window.FocusControls();}
        else if(!window.Visible)window.Show();
    }
    internal void ApplyDisplayPreferences()
    {
        var compact=windows.FirstOrDefault(w=>w.View=="compact");
        if(Session.Engine.Snapshot.ShowFloatingTimer) { compact ??= Create("compact"); compact.Show(); }
        else compact?.Hide();
        compact?.ApplyPosition();
    }
    internal void Broadcast(object message) {
        var json=System.Text.Json.JsonSerializer.Serialize(message,PreviewSession.Json);
        foreach (var window in windows.ToArray()) window.PostJson(json);
    }
    private void PublishAppViewVisibility()
    {
        var visible=AppViewVisible;
        if(publishedAppViewVisible==visible)return;
        publishedAppViewVisible=visible;
        Broadcast(new{type="appViewVisibility",visible});
    }
    internal void Announce(string message)
    {
        if (message.Length == 0) return;
        (active is { Visible: true } ? active : MainForm as PreviewWindow)?.Post(new { type = "announcement", message });
    }
    internal void Announce(CommandResult result)
    {
        // The committed transition already supplied the more informative
        // action+time message. Do not repeat its older terse command response.
        if(!result.HasSessionFeedback)Announce(result.Message);
    }
    private void AnnounceSession(string message,bool supplementary)
    {
        var delivered=ScreenReaderAnnouncements.TryAnnounce(MainForm,message,supplementary);
        Broadcast(new{type="sessionStatus",message}); // Readable, but not a second live event.
        if(!delivered)Announce(message);
    }
    internal async Task CloseMainAsync()
    {
        if (closing) return;
        closing = true;
        try {
            await promptCoordinator.ExclusivelyAsync(async()=>{
                foreach (var window in windows.ToArray()) await window.FlushDraftAsync();
                Session.Engine.Checkpoint();
                Services.Log.Record("app.exiting");pulse.Stop();
                foreach (var window in windows.Where(w => w != MainForm).ToArray()) window.ClosePermanently();
                ((PreviewWindow)MainForm!).ClosePermanently();
            });
        }
        catch { Announce("Could not save timer or reflection changes. The app is staying open. Try again."); }
        finally { closing = false; }
    }
    protected override void Dispose(bool disposing) { if (disposing) { focusPulse.Dispose();focusMonitor.Dispose();shortcuts.Dispose();tray.Visible=false;tray.ContextMenuStrip?.Dispose();tray.Dispose();pulse.Dispose(); Services.Dispose(); } base.Dispose(disposing); }
}
