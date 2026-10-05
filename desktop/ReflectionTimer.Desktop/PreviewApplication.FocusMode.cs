using ReflectionTimer.Core;
using ReflectionTimer.Desktop;

namespace ReflectionTimer.Accessible;

internal sealed partial class PreviewApplication
{
    private readonly IFocusTargetSource focusTargets = new WindowsFocusTargets();
    private FocusModeMonitor focusMonitor = null!;
    private readonly System.Windows.Forms.Timer focusPulse = new() { Interval = 250 };
    private readonly Dictionary<Guid, FocusTarget> focusChoices = [];
    private Task<IReadOnlyList<FocusTarget>>? listingBrowserTargets;
    private int focusListRevision;
    private void InitializeFocusMode()
    {
        focusMonitor = new(Session.Engine, focusTargets, Services.SetFocusAlert);
        focusMonitor.StatusChanged += status => Broadcast(new { type = "focusStatus", status });
        focusPulse.Tick += (_, _) => focusMonitor.Poll(); focusPulse.Start();
    }
    internal string FocusStatus => focusMonitor.Status;
    private void ToggleFocusModeFromGlobalShortcut()
    {
        compactPresses.Reset();
        var current=Session.Engine.SettingsSnapshot.FocusMode;
        if(!current.Enabled&&current.SelectedTargets.Length==0&&!current.IdleEnabled){
            Open("main",timerPage:true);
            if(WindowActivation.CanReceiveFocus(MainForm!))((PreviewWindow)MainForm!).ChooseFocusTarget();
            return;
        }
        Session.Engine.SetFocusMode(current with{Enabled=!current.Enabled});
        Broadcast(new{type="focusToggled",enabled=!current.Enabled});
        // A hidden/background WebView live region cannot reliably speak a
        // global shortcut. Use the same native provider as timer feedback.
        AnnounceSession(current.Enabled?"Focus mode off.":"Focus mode on.",supplementary:false);
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
        var saved = Session.Engine.SettingsSnapshot.FocusMode.SelectedTargets;
        foreach (var target in saved) focusChoices[target.Id] = target;
        if (focusChoices.Count > 4096) throw new ArgumentException("Reopen the target chooser to refresh its saved choices.");
        var choices = FocusChoices(kind, list);
        foreach (var target in choices) focusChoices[target.Id] = target;
        return choices.Select(t => new { id = t.Id, key = FocusTargetKey(t), kind=(int)t.Kind,name = t.Name, app = t.App, windowName=t.WindowName,tabPosition=t.TabPosition,
            t.UseFocused, selected = saved.Any(s => MatchesSavedFocusTarget(t,s)), current = !t.UseFocused && focusTargets.IsCurrent(t) }).ToArray();
    }
    internal static FocusTarget[] FocusChoices(FocusTargetKind kind, IReadOnlyList<FocusTarget> list) => new[] { FocusTarget.Focused(kind) }.Concat(list)
        .DistinctBy(t => t.Key).Select(t => t with { Id = new Guid(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(t.Key)).AsSpan(0,16)) }).ToArray();
    internal static string FocusTargetKey(FocusTarget target) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(target.Key)));
    internal static bool MatchesSavedFocusTarget(FocusTarget listed, FocusTarget? saved) => saved is not null
        && listed.Kind==saved.Kind && listed.UseFocused==saved.UseFocused && (listed.UseFocused || (listed.WindowHandle==saved.WindowHandle
        && listed.ProcessId==saved.ProcessId && listed.ProcessStartedAt==saved.ProcessStartedAt
        && (listed.Kind==FocusTargetKind.Window || listed.TabRuntimeId==saved.TabRuntimeId)));
    internal void SelectFocusTarget(Guid id, bool enable)
    {
        if (!focusChoices.TryGetValue(id, out var target)) throw new ArgumentException("That target list expired. Refresh it and choose again.");
        var current = Session.Engine.SettingsSnapshot.FocusMode;
        Session.Engine.SetFocusMode(enable || current.Enabled, current.DelaySeconds, target);
    }
    internal void SelectFocusTargets(IReadOnlyList<Guid> ids, bool enable, bool multiple, bool idle, int idleSeconds)
    {
        var selected = ids.Distinct().Select(id => focusChoices.TryGetValue(id, out var target) ? target
            : throw new ArgumentException("That target list expired. Refresh it and choose again.")).DistinctBy(t => t.Key).ToArray();
        var current = Session.Engine.SettingsSnapshot.FocusMode;
        Session.Engine.SetFocusMode(current with { Enabled = enable || current.Enabled, MultipleTargets = multiple, IdleEnabled = idle,
            IdleSeconds = idleSeconds, Target = selected.FirstOrDefault(), Targets = System.Collections.Immutable.ImmutableArray.CreateRange(selected) });
    }
}
