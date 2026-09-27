using System.Windows.Forms.Automation;

namespace ReflectionTimer.Accessible;

// One stable native provider, even when all WebViews are hidden. A live region
// in a hidden/background browser alone cannot reliably announce global keys.
internal static class ScreenReaderAnnouncements
{
    internal static AutomationNotificationProcessing Processing(bool supplementary) => supplementary
        ? AutomationNotificationProcessing.CurrentThenMostRecent
        : AutomationNotificationProcessing.ImportantMostRecent;

    internal static bool TryAnnounce(Control? owner,string message,bool supplementary)
    {
        if(owner is null||owner.IsDisposed||owner.Disposing||string.IsNullOrWhiteSpace(message))return false;
        try {
            return owner.AccessibilityObject.RaiseAutomationNotification(
                AutomationNotificationKind.ActionCompleted,Processing(supplementary),message);
        } catch { return false; } // The visible live-region path remains available.
    }
}
