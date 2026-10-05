using ReflectionTimer.Core;

namespace ReflectionTimer.Accessible;

internal static class FocusedBrowserTargets
{
    internal static IReadOnlyList<FocusTarget> Capture(FocusTarget window, IReadOnlyList<FocusTargetKind> kinds,
        IReadOnlyList<BrowserTabChoice> tabs, IReadOnlyList<BrowserTabSlot> strip)
    {
        // All choices must come from the snapshotted foreground window. Equal
        // titles in another window are never a fallback for an unreadable tab.
        var owned = tabs.Where(t => t.Target.WindowHandle == window.WindowHandle && t.Target.ProcessId == window.ProcessId
            && t.Target.ProcessStartedAt == window.ProcessStartedAt).ToArray();
        var current = BrowserTabListing.Create(owned).Current;
        var result = new List<FocusTarget>();
        if (kinds.Contains(FocusTargetKind.Window)) result.Add(window);
        if (kinds.Contains(FocusTargetKind.BrowserTab) && current is not null) result.Add(current);
        if (kinds.Contains(FocusTargetKind.BrowserTabGroup)) {
            var groups = BrowserTabGroups.Read(strip);
            var focused = groups.Where(group => current is not null ? group.Members.Any(t => t.Id == current.TabRuntimeId)
                : BrowserTabGroups.Check(group.Id, strip, true) == FocusPresence.Focused).ToArray();
            if (focused.Length == 1) result.Add(window with { Kind = FocusTargetKind.BrowserTabGroup, Name = focused[0].Name,
                TabRuntimeId = focused[0].Id, TabPosition = groups.ToList().IndexOf(focused[0]) + 1 });
        }
        return result;
    }
}
