using ReflectionTimer.Core;

namespace ReflectionTimer.Accessible;

internal sealed record BrowserTabChoice(FocusTarget Target, bool? Selected, bool KeyboardFocused = false);
internal sealed record BrowserTabListing(IReadOnlyList<FocusTarget> Targets, FocusTarget? Current)
{
    internal static string Identity(FocusTarget target) => $"{target.ProcessId}:{target.ProcessStartedAt}:{target.WindowHandle}:{target.TabRuntimeId}";

    // Input windows are in desktop stacking order, with minimized windows last.
    // Titles are display text: separate tabs with equal titles remain selectable.
    internal static BrowserTabListing Create(IEnumerable<BrowserTabChoice> choices)
    {
        var unique = choices.DistinctBy(choice => Identity(choice.Target)).ToArray();
        var recentWindow = unique.GroupBy(choice => (choice.Target.ProcessId, choice.Target.ProcessStartedAt, choice.Target.WindowHandle)).FirstOrDefault()?.ToArray() ?? [];
        var focused = recentWindow.Where(choice => choice.Selected == true && choice.KeyboardFocused).ToArray();
        var selected = recentWindow.Where(choice => choice.Selected == true).ToArray();
        var current = focused.Length == 1 ? focused[0].Target
            : selected.Length == 1 && recentWindow.All(choice => choice.Selected.HasValue) ? selected[0].Target : null;
        return new(unique.Select(choice => choice.Target).OrderBy(target => target.Id == current?.Id ? 0 : 1)
            .ThenBy(target => target.App).ThenBy(target => target.WindowName).ThenBy(target => target.TabPosition).ToArray(), current);
    }
}
