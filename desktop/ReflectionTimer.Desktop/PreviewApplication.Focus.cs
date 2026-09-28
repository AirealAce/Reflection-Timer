using ReflectionTimer.Desktop;

namespace ReflectionTimer.Accessible;

internal sealed partial class PreviewApplication
{
    private nint returnFocus;

    private bool IsOutsideTimer(nint candidate)
    {
        // Keep one return destination across App, Compact and reflection views.
        // An owned dialog must not become a route back into our own application.
        return !windows.Any(window => !window.IsDisposed && window.IsHandleCreated
            && WindowActivation.IsWindowOrOwnedBy(candidate, window.Handle));
    }

    internal void RememberReturnFocus(nint previous)
    {
        var candidate = WindowActivation.RootWindow(previous);
        if (WindowActivation.IsReturnTarget(candidate, 0) && IsOutsideTimer(candidate))
            returnFocus = candidate;
    }

    internal void ReleaseFocus(PreviewWindow window)
        => WindowActivation.ReleaseFocus(window, returnFocus, IsOutsideTimer);

    private void ReleaseTimerViewFocus()
    {
        // Cycling a background overlay must leave an unrelated foreground app
        // untouched. Never redirect typing from a reflection or an owned modal.
        var foreground = WindowActivation.Foreground;
        var window = windows.FirstOrDefault(window => window.View is "main" or "compact"
            && WindowActivation.IsForeground(window, foreground));
        if (window is not null) ReleaseFocus(window);
    }
}
