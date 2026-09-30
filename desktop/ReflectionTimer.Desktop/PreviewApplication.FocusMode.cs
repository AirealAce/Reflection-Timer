using ReflectionTimer.Core;

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
    internal async Task<object> ListFocusTargetsAsync(FocusTargetKind kind)
    {
        if (listingBrowserTargets is { IsCompleted:false } && kind==FocusTargetKind.BrowserTab) throw new ArgumentException("The browser tab list is still loading. Try choosing a window instead.");
        var revision = ++focusListRevision;
        var listingTargets = focusTargets.ListAsync(kind);
        if (kind==FocusTargetKind.BrowserTab) listingBrowserTargets = listingTargets;
        IReadOnlyList<FocusTarget> list;
        try { list = await listingTargets.WaitAsync(TimeSpan.FromSeconds(6)); }
        catch { throw new ArgumentException("The target list could not finish loading. Try choosing a window, or reopen the app if its browser tab list remains unavailable."); }
        if (revision != focusListRevision) throw new ArgumentException("That target list expired. Refresh it and choose again.");
        focusChoices.Clear(); foreach (var target in list) focusChoices[target.Id] = target;
        return list.Select(t => new { id = t.Id, name = t.Name, app = t.App, windowName=t.WindowName,tabPosition=t.TabPosition });
    }
    internal void SelectFocusTarget(Guid id, bool enable)
    {
        if (!focusChoices.TryGetValue(id, out var target)) throw new ArgumentException("That target list expired. Refresh it and choose again.");
        var current = Session.Engine.SettingsSnapshot.FocusMode;
        Session.Engine.SetFocusMode(enable || current.Enabled, current.DelaySeconds, target);
    }
}
