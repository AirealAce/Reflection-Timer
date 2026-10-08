using ReflectionTimer.Core;

namespace ReflectionTimer.Accessible;

// A saved Site remains in the encrypted profile during an outage. Only new
// selections require a configured, enabled companion with a readable snapshot.
internal static class FocusSitePolicy
{
    internal const string UnavailableMessage = "Site targets require an enabled browser companion with a current, complete browser connection. Configure the companion and wait for it to connect first.";

    internal static bool Available(FocusModeSettings settings, bool browserConnected)
        => settings.BrowserCompanionEnabled && browserConnected;

    internal static void RequireAvailable(FocusModeSettings settings, bool browserConnected)
    {
        if (!Available(settings, browserConnected)) throw new ArgumentException(UnavailableMessage);
    }

    internal static void ValidateSelection(FocusModeSettings current, FocusModeSettings next, bool browserConnected)
    {
        if (Available(next, browserConnected)) return;
        var saved = current.SelectedTargets.Where(t => t.Kind == FocusTargetKind.Site).Select(t => t.Key).ToHashSet();
        if (next.SelectedTargets.Any(t => t.Kind == FocusTargetKind.Site && !saved.Contains(t.Key)))
            throw new ArgumentException(UnavailableMessage);
    }
}
