using ReflectionTimer.Desktop;

namespace ReflectionTimer.Accessible;

internal interface IReflectionShortcutTarget
{
    bool IsForegroundReflection { get; }
    void FocusOrSaveDraft();
    void FocusReflection();
}

internal static class ReflectionShortcut
{
    internal static void Invoke(IEnumerable<IReflectionShortcutTarget> windows, Action openPendingOrCheckIn)
    {
        // Check the real foreground window, not the app's last activated window.
        var reflections = windows.ToArray();
        var reflection = reflections.FirstOrDefault(window => window.IsForegroundReflection);
        if (reflection is not null) reflection.FocusOrSaveDraft();
        else if (reflections.LastOrDefault() is { } background) background.FocusReflection();
        else openPendingOrCheckIn();
    }
}

// Register only the listed chords; retry only registrations another app owns.
internal sealed class PreviewShortcuts : IDisposable
{
    internal static readonly (uint Key, int Id, uint Modifiers)[] Chords = [
        (GlobalShortcut.Key, GlobalShortcut.HotKeyId, GlobalShortcut.Modifiers),
        (GlobalShortcut.EndEarlyKey, GlobalShortcut.EndEarlyId, GlobalShortcut.Modifiers),
        (GlobalShortcut.CompactKey, GlobalShortcut.CompactId, GlobalShortcut.Modifiers),
        (GlobalShortcut.CompactFocusKey, GlobalShortcut.CompactFocusId, GlobalShortcut.Modifiers),
        (GlobalShortcut.ReflectionFocusKey, GlobalShortcut.ReflectionFocusId, GlobalShortcut.Modifiers),
        (GlobalShortcut.TimerToggleKey, GlobalShortcut.TimerToggleId, GlobalShortcut.TimerToggleModifiers),
        (GlobalShortcut.TimerToggleKey, GlobalShortcut.TimerToggleAltId, GlobalShortcut.Modifiers),
        (GlobalShortcut.ModeToggleKey, GlobalShortcut.ModeToggleId, GlobalShortcut.Modifiers),
        (GlobalShortcut.ResetTimerKey, GlobalShortcut.ResetTimerId, GlobalShortcut.Modifiers)
    ];
    private readonly GlobalShortcut?[] registrations = new GlobalShortcut?[Chords.Length];
    private readonly Action<TimeSpan>[] actions;
    private readonly IHotKeyRegistration? backend;
    private readonly Action<int, bool>? statusChanged;
    private bool disposed;
    internal PreviewShortcuts(Action<TimeSpan>[] actions, Action<int, bool>? statusChanged = null, IHotKeyRegistration? backend = null)
    {
        if (actions.Length != Chords.Length) throw new ArgumentException("Provide each shortcut action.");
        this.actions = actions; this.statusChanged = statusChanged; this.backend = backend;
        RetryUnavailable(initial: true);
    }
    internal object Status => registrations.Select((shortcut,id)=>new { id, available = shortcut?.IsRegistered == true }).ToArray();
    internal bool RetryUnavailable(bool initial = false)
    {
        if (disposed) return false;
        var changed = false;
        for (var i = 0; i < Chords.Length; i++) {
            if (registrations[i]?.IsRegistered == true) continue;
            registrations[i]?.Dispose(); registrations[i] = null;
            var index = i;
            try { registrations[i] = new GlobalShortcut(delay=>actions[index](delay), backend, Chords[i].Key, Chords[i].Id, Chords[i].Modifiers); }
            catch { /* A later retry can recover a temporarily unavailable registration. */ }
            var available = registrations[i]?.IsRegistered == true;
            changed |= available;
            if (initial || available) statusChanged?.Invoke(i, available);
        }
        return changed;
    }
    internal bool Dispatch(int message, int id, TimeSpan queueDelay = default) => !disposed && registrations.Any(shortcut => shortcut?.Dispatch(message, id, queueDelay) == true);
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        foreach (var shortcut in registrations) shortcut?.Dispose();
    }
}
