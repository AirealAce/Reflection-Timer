using System.Text.RegularExpressions;
using ReflectionTimer.Core;

namespace ReflectionTimer.Accessible;

// Chromium exposes group headers as expandable Tab controls beside the TabItems,
// rather than as their parents. Only associate adjacent tabs whose browser label
// explicitly names that group; an ungrouped tab ends the run. Runtime IDs, never
// titles, distinguish duplicate group names. Page documents are excluded upstream.
internal sealed record BrowserTabSlot(string Id, string ParentId, string Name, bool GroupHeader, bool? Selected);
internal sealed record BrowserTabGroup(string Id, string Name, IReadOnlyList<BrowserTabSlot> Members);

internal static class BrowserTabGroups
{
    private static readonly Regex NamedHeader = new(@"^(?:Shared\s+)?[Gg]roup (?<title>.+?) - \d+ tabs?(?:,| - |$)", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly Regex LegacyNamedHeader = new(@"^(?:Shared\s+)?[Gg]roup (?<title>.+?) - .+ - (?:Expanded|Collapsed)$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly Regex UnnamedHeader = new(@"^(?:Shared\s+)?[Uu]nnamed group - ", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    internal static IReadOnlyList<BrowserTabGroup> Read(IReadOnlyList<BrowserTabSlot> slots)
    {
        var groups = new List<BrowserTabGroup>();
        foreach (var siblings in slots.GroupBy(slot => slot.ParentId)) {
            List<BrowserTabSlot>? members = null;
            string? membership = null;
            foreach (var slot in siblings) {
                if (slot.GroupHeader) {
                    members = null; membership = null;
                    var name = slot.Name.Trim();
                    var match = NamedHeader.Match(name);
                    if (!match.Success) match = LegacyNamedHeader.Match(name);
                    var unnamed = UnnamedHeader.IsMatch(name);
                    if (!match.Success && !unnamed) continue; // Unknown/localized labels fail quietly.
                    var title = unnamed ? "Unnamed group" : match.Groups["title"].Value;
                    membership = unnamed ? " - Part of unnamed group" : " - Part of group " + title;
                    members = [];
                    groups.Add(new(slot.Id, title, members));
                } else if (members is not null && membership is not null) {
                    var index = slot.Name.LastIndexOf(membership, StringComparison.Ordinal);
                    var end = index + membership.Length;
                    if (index >= 0 && (end == slot.Name.Length || slot.Name.AsSpan(end).StartsWith(" - ", StringComparison.Ordinal))) members.Add(slot);
                    else { members = null; membership = null; }
                }
            }
        }
        return groups;
    }

    internal static FocusPresence Check(string id, IReadOnlyList<BrowserTabSlot> slots, bool foreground)
    {
        var group = Read(slots).FirstOrDefault(candidate => candidate.Id == id);
        if (group is null) return FocusPresence.Unavailable;
        if (!foreground) return FocusPresence.Away;
        var tabs = slots.Where(slot => !slot.GroupHeader).ToArray();
        // Chromium permits multiple selected tabs. If those selections span
        // groups, selection does not reveal the active tab: do not falsely alert.
        if (tabs.Any(tab => tab.Selected is null)) return FocusPresence.Unknown;
        var selected = tabs.Where(tab => tab.Selected == true).ToArray();
        if (selected.Length == 0) return FocusPresence.Unknown;
        var members = group.Members.Select(tab => tab.Id).ToHashSet();
        if (selected.All(tab => members.Contains(tab.Id))) return FocusPresence.Focused;
        return selected.Any(tab => members.Contains(tab.Id)) ? FocusPresence.Unknown : FocusPresence.Away;
    }
}
