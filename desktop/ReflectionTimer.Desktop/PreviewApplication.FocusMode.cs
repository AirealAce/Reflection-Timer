using ReflectionTimer.Core;
using ReflectionTimer.Desktop;

namespace ReflectionTimer.Accessible;

internal sealed partial class PreviewApplication
{
    private readonly IFocusTargetSource focusTargets = new WindowsFocusTargets();
    private FocusModeMonitor focusMonitor = null!;
    private FocusScreenGlow focusGlow = null!;
    private readonly System.Windows.Forms.Timer focusPulse = new() { Interval = 250 };
    private readonly Dictionary<Guid, FocusTarget> focusChoices = [];
    private Task<IReadOnlyList<FocusTarget>>? listingBrowserTargets;
    private int focusListRevision;
    private void InitializeFocusMode()
    {
        focusGlow = new(() => Services.Log.Record("focus.glowUnavailable"));
        focusMonitor = new(Session.Engine, focusTargets, Services.SetFocusAlert, value => focusGlow.SetActive(value && !closing, Session.Engine.SettingsSnapshot.FocusMode.ScreenEdgeGlowStyle));
        focusMonitor.StatusChanged += status => Broadcast(new { type = "focusStatus", status });
        focusPulse.Tick += (_, _) => { focusMonitor.Poll(); focusGlow.SetActive(focusMonitor.ScreenEdgeGlow && !closing, Session.Engine.SettingsSnapshot.FocusMode.ScreenEdgeGlowStyle); }; focusPulse.Start();
    }
    internal string FocusStatus => focusMonitor.Status;
    private void ToggleFocusModeFromGlobalShortcut()
    {
        compactPresses.Reset();
        var current=Session.Engine.SettingsSnapshot.FocusMode;
        if(!current.Enabled&&current.SelectedTargets.Length==0&&!current.IdleEnabled){
            Open("main",timerPage:true);
            if(WindowActivation.CanReceiveFocus(MainForm!)){
                ((PreviewWindow)MainForm!).ChooseFocusTarget();
                Services.AnnounceFeedback("Choose a window, browser tab or group, or enable idle detection to turn Focus mode on.");
            } else Services.AnnounceFeedback("Finish or close the open dialog before choosing a Focus target.");
            return;
        }
        Session.Engine.SetFocusMode(current with{Enabled=!current.Enabled});
        Broadcast(new{type="focusToggled",enabled=!current.Enabled});
        // SessionVoice publishes the committed change to both feedback paths.
    }
    internal async Task<object> ListFocusTargetsAsync(FocusTargetKind kind, bool reset = false)
    {
        if (listingBrowserTargets is { IsCompleted:false } && kind!=FocusTargetKind.Window) throw new ArgumentException("The browser target list is still loading. Try choosing a window instead.");
        var revision = ++focusListRevision;
        var listingTargets = focusTargets.ListAsync(kind);
        if (kind!=FocusTargetKind.Window) listingBrowserTargets = listingTargets;
        IReadOnlyList<FocusTarget> list;
        try { list = await listingTargets.WaitAsync(TimeSpan.FromSeconds(6)); }
        catch { throw new ArgumentException("The target list could not finish loading. Try choosing a window, or reopen the app if its browser tab list remains unavailable."); }
        if (revision != focusListRevision) throw new ArgumentException("That target list expired. Refresh it and choose again.");
        if (reset) focusChoices.Clear();
        if(kind==FocusTargetKind.Window)focusMonitor.RestoreWindows(list);
        var saved = Session.Engine.SettingsSnapshot.FocusMode.SelectedTargets;
        foreach (var target in saved) focusChoices[target.Id] = target;
        if (focusChoices.Count > 4096) throw new ArgumentException("Reopen the target chooser to refresh its saved choices.");
        var choices = FocusChoices(kind, list);
        foreach (var target in choices) focusChoices[target.Id] = target;
        return choices.Select(t => new { id = t.Id, key = FocusTargetKey(t), kind=(int)t.Kind,name = t.Name, app = t.App, windowName=t.WindowName,tabPosition=t.TabPosition,
            t.UseFocused,captureScope=(int)t.CaptureScope, replacesKeys=focusMonitor.PreviousWindowKeys(t).Select(HashFocusKey).ToArray(), selected = saved.Any(s => MatchesSavedFocusTarget(t,s)), current = !t.UseFocused && focusTargets.IsCurrent(t) }).ToArray();
    }
    internal static FocusTarget[] FocusChoices(FocusTargetKind kind, IReadOnlyList<FocusTarget> list) => Enum.GetValues<FocusCaptureScope>()
        .Where(scope=>FocusTarget.ValidScope(kind,scope)).Select(scope=>FocusTarget.Focused(kind,scope)).Concat(list)
        .DistinctBy(t => t.Key).Select(t => t with { Id = new Guid(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(t.Key)).AsSpan(0,16)) }).ToArray();
    internal static string FocusTargetKey(FocusTarget target) => HashFocusKey(target.Key);
    private static string HashFocusKey(string key)=>Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key)));
    internal static bool MatchesSavedFocusTarget(FocusTarget listed, FocusTarget? saved) => saved is not null
        && listed.Kind==saved.Kind && listed.UseFocused==saved.UseFocused && (listed.UseFocused ? listed.CaptureScope==saved.CaptureScope : (listed.WindowHandle==saved.WindowHandle
        && listed.ProcessId==saved.ProcessId && listed.ProcessStartedAt==saved.ProcessStartedAt
        && (listed.Kind==FocusTargetKind.Window || listed.TabRuntimeId==saved.TabRuntimeId)));
    internal void SelectFocusTarget(Guid id, bool enable)
    {
        if (!focusChoices.TryGetValue(id, out var target)) throw new ArgumentException("That target list expired. Refresh it and choose again.");
        var current = Session.Engine.SettingsSnapshot.FocusMode;
        Session.Engine.SetFocusMode(enable || current.Enabled, current.DelaySeconds, focusMonitor.CurrentWindow(target));
    }
    internal void SelectFocusTargets(IReadOnlyList<Guid> ids, bool enable, bool multiple, bool idle, int idleSeconds)
    {
        var selected = ids.Distinct().Select(id => focusChoices.TryGetValue(id, out var target) ? target
            : throw new ArgumentException("That target list expired. Refresh it and choose again.")).Select(focusMonitor.CurrentWindow).DistinctBy(t => t.Key).ToArray();
        var current = Session.Engine.SettingsSnapshot.FocusMode;
        Session.Engine.SetFocusMode(current with { Enabled = enable || current.Enabled, MultipleTargets = multiple, IdleEnabled = idle,
            IdleSeconds = idleSeconds, Target = selected.FirstOrDefault(), Targets = System.Collections.Immutable.ImmutableArray.CreateRange(selected) });
    }
}
