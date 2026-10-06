using ReflectionTimer.Core;

namespace ReflectionTimer.Accessible;

// Display-only labels must never rename native titles or saved bookmarks.
internal static class FocusWindowLabels
{
    internal static IReadOnlyDictionary<string,string> Create(IEnumerable<FocusTarget> targets, Func<FocusTarget,bool> minimized)
    {
        var result = new Dictionary<string,string>();
        foreach (var group in targets.Where(t => t.Kind == FocusTargetKind.Window && !t.UseFocused)
            .DistinctBy(t => t.Key).GroupBy(t => (t.App.ToUpperInvariant(),t.Name))) {
            var windows = group.OrderBy(t => t.WindowHandle).ToArray();
            for (var i = 0; i < windows.Length; i++) {
                var target = windows[i];
                result[target.Key] = target.Name + (windows.Length > 1 ? $" — Window {i + 1}" : "")
                    + (minimized(target) ? " (minimized)" : "");
            }
        }
        return result;
    }
}
