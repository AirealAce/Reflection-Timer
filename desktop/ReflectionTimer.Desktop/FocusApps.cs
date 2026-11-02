using ReflectionTimer.Core;

namespace ReflectionTimer.Accessible;

internal static class FocusApps
{
    internal static FocusTarget? FromWindow(FocusTarget window)
    {
        if (!FocusAppIdentity.IsValid(window.ProcessPath, window.ApplicationId)) return null;
        var name = string.IsNullOrWhiteSpace(window.App) ? Path.GetFileNameWithoutExtension(window.ProcessPath) : window.App;
        if (string.IsNullOrWhiteSpace(name)) name = window.ApplicationId;
        return new(Guid.NewGuid(), FocusTargetKind.App, name, name, 0, 0, 0) {
            ProcessPath = window.ProcessPath, ApplicationId = window.ApplicationId
        };
    }

    internal static IReadOnlyList<FocusTarget> List(IEnumerable<FocusTarget> windows)
        => windows.Select(FromWindow).OfType<FocusTarget>().DistinctBy(target => target.Key)
            .OrderBy(target => target.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();

    internal static bool Matches(FocusTarget saved, FocusTarget current)
        => saved.Kind == FocusTargetKind.App && !saved.UseFocused
            && FocusAppIdentity.IsValid(saved.ProcessPath, saved.ApplicationId)
            && FocusAppIdentity.Key(saved.ProcessPath, saved.ApplicationId) == FocusAppIdentity.Key(current.ProcessPath, current.ApplicationId);

    internal static FocusPresence Presence(FocusTarget saved, IReadOnlyList<FocusTarget> foreground)
    {
        if (!FocusAppIdentity.IsValid(saved.ProcessPath, saved.ApplicationId)) return FocusPresence.Unknown;
        if (foreground.Any(window => Matches(saved, window))) return FocusPresence.Focused;
        // Closed apps remain valid bookmarks. Another identifiable app means
        // away; an unreadable foreground must never cause a false away alarm.
        return foreground.Any(window => FocusAppIdentity.IsValid(window.ProcessPath, window.ApplicationId))
            ? FocusPresence.Away : FocusPresence.Unknown;
    }

    internal static (string Path, string ApplicationId)? ResolveHosted(string applicationId, IReadOnlyList<(string Path, string ApplicationId)> processes)
    {
        var candidates = processes.Where(item => FocusAppIdentity.IsValid(item.Path, item.ApplicationId))
            .DistinctBy(item => FocusAppIdentity.Key(item.Path, item.ApplicationId)).ToArray();
        if (applicationId.Length == 0) return candidates.Length == 1 ? candidates[0] : null;
        var matching = candidates.Where(item => item.ApplicationId == applicationId).ToArray();
        if (matching.Length == 1) return matching[0];
        // A document may embed a separate app process. Its different package ID
        // must never replace the actual frame's explicit AppUserModelID.
        return candidates.Length == 1 && candidates[0].ApplicationId.Length == 0
            ? (candidates[0].Path, applicationId) : null;
    }
}
