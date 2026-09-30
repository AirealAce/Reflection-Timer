using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;
using ReflectionTimer.Core;

namespace ReflectionTimer.Accessible;

internal interface IFocusTargetSource : IDisposable
{
    Task<IReadOnlyList<FocusTarget>> ListAsync(FocusTargetKind kind);
    Task<FocusPresence> CheckAsync(FocusTarget target);
}

// Read only the visible window chrome and tab strip. Never inspect page bodies,
// URLs, browser profiles, keystrokes or history. UIA belongs to one long-lived
// MTA worker; an unresponsive provider cannot block the timer or its WebView.
internal sealed class WindowsFocusTargets : IFocusTargetSource
{
    private readonly BlockingCollection<Action> jobs = new();
    private readonly Dictionary<string, AutomationElement> tabs = [];
    private readonly Thread worker;
    private bool disposed;
    internal WindowsFocusTargets()
    {
        worker = new Thread(() => { foreach (var job in jobs.GetConsumingEnumerable()) job(); }) { IsBackground = true, Name = "Reflection Timer focus targets" };
        worker.SetApartmentState(ApartmentState.MTA); worker.Start();
    }
    private Task<T> Enqueue<T>(Func<T> read)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (disposed) { done.TrySetCanceled(); return done.Task; }
        try { jobs.Add(() => { try { done.TrySetResult(read()); } catch (Exception e) { done.TrySetException(e); } }); }
        catch (InvalidOperationException) { done.TrySetCanceled(); }
        return done.Task;
    }
    private static IReadOnlyList<FocusTarget> OpenWindows()
    {
        var windows = new List<FocusTarget>();
        EnumWindows((handle, _) => {
            if (windows.Count >= 256 || !IsWindowVisible(handle) || Cloaked(handle) || ((long)GetWindowLongPtr(handle,-20) & 0x08000080L) != 0) return true;
            GetWindowThreadProcessId(handle, out var pid);
            if (pid == Environment.ProcessId || pid == 0) return true;
            var caption = new StringBuilder(2049); GetWindowText(handle, caption, caption.Capacity);
            if (caption.Length == 0) return true;
            try {
                using var process = Process.GetProcessById((int)pid);
                if(caption.ToString()=="Program Manager")return true;
                windows.Add(new(Guid.NewGuid(), FocusTargetKind.Window, caption.ToString(), process.ProcessName,
                    handle.ToInt64(), (int)pid, process.StartTime.ToUniversalTime().Ticks) { WindowName=caption.ToString() });
            } catch { /* A closing/inaccessible window is not a selectable target. */ }
            return true;
        }, 0);
        return windows.OrderBy(w => w.App).ThenBy(w => w.Name).ToArray();
    }
    public Task<IReadOnlyList<FocusTarget>> ListAsync(FocusTargetKind kind) => kind == FocusTargetKind.Window
        ? Task.Run(OpenWindows) : Enqueue<IReadOnlyList<FocusTarget>>(() => {
        var windows = OpenWindows();
        var choices = new List<FocusTarget>();
        foreach (var window in windows.Where(w => w.App is "chrome" or "msedge" or "firefox" or "brave" or "vivaldi" or "opera")) {
            try {
                var position=0;
                foreach (var tab in TabStrip((nint)window.WindowHandle)) {
                    var name = tab.Current.Name;
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    var id = string.Join(",", tab.GetRuntimeId());
                    tabs[CacheKey(window, id)] = tab;
                    choices.Add(window with { Id = Guid.NewGuid(), Kind = kind, Name = name.Length > 2048 ? name[..2048] : name, TabRuntimeId = id, TabPosition=++position });
                }
            } catch { /* Browser does not expose a usable accessible tab strip. */ }
        }
        // Bound retained accessibility objects to current choices.
        var keys = choices.Select(t => CacheKey(t, t.TabRuntimeId)).ToHashSet();
        foreach (var key in tabs.Keys.Where(k => !keys.Contains(k)).ToArray()) tabs.Remove(key);
        return choices.OrderBy(t => t.App).ThenBy(t => t.WindowName).ThenBy(t=>t.TabPosition).ToArray();
    });
    private static FocusPresence CheckWindow(FocusTarget target)
    {
        var handle = (nint)target.WindowHandle;
        if (!IsWindow(handle)) return FocusPresence.Unavailable;
        GetWindowThreadProcessId(handle, out var pid);
        if (pid != target.ProcessId) return FocusPresence.Unavailable;
        try { using var process = Process.GetProcessById((int)pid); if (process.StartTime.ToUniversalTime().Ticks != target.ProcessStartedAt) return FocusPresence.Unavailable; }
        catch { return FocusPresence.Unavailable; }
        return IsTargetForeground(handle) ? FocusPresence.Focused : FocusPresence.Away;
    }
    public Task<FocusPresence> CheckAsync(FocusTarget target) => target.Kind == FocusTargetKind.Window
        ? Task.FromResult(CheckWindow(target)) : Enqueue(() => {
        var window = CheckWindow(target);
        if (window == FocusPresence.Unavailable) return window;
        var handle = (nint)target.WindowHandle;
        try {
            var key = CacheKey(target, target.TabRuntimeId);
            if (!tabs.TryGetValue(key, out var tab)) {
                tab = TabStrip(handle).FirstOrDefault(t => string.Join(",", t.GetRuntimeId()) == target.TabRuntimeId);
                if (tab is null) return FocusPresence.Unavailable;
                tabs[key] = tab;
            }
            var ancestor = tab;
            var inWindow = false;
            for (var i = 0; ancestor is not null && i < 20; i++, ancestor = TreeWalker.ControlViewWalker.GetParent(ancestor)) {
                var nativeHandle = (nint)ancestor.Current.NativeWindowHandle;
                if(nativeHandle != 0 && GetAncestor(nativeHandle, 2) == handle) { inWindow = true; break; }
            }
            if (!inWindow) return FocusPresence.Unavailable;
            if (tab.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var pattern)) {
                var selected = ((SelectionItemPattern)pattern).Current.IsSelected;
                return selected && IsTargetForeground(handle) ? FocusPresence.Focused : FocusPresence.Away;
            }
            return FocusPresence.Unknown;
        } catch (ElementNotAvailableException) { tabs.Remove(CacheKey(target, target.TabRuntimeId)); return FocusPresence.Unavailable; }
        catch { return FocusPresence.Unknown; }
    });
    private static string CacheKey(FocusTarget target, string id) => $"{target.ProcessId}:{target.ProcessStartedAt}:{target.WindowHandle}:{id}";
    private static IEnumerable<AutomationElement> TabStrip(nint handle)
    {
        var walker = TreeWalker.ControlViewWalker;
        var pending = new Queue<(AutomationElement Element, int Depth)>(); pending.Enqueue((AutomationElement.FromHandle(handle), 0));
        for (var visited = 0; pending.Count > 0 && visited < 600; visited++) {
            var (element, depth) = pending.Dequeue(); var type = element.Current.ControlType;
            if (type == ControlType.TabItem) { yield return element; continue; }
            if (type == ControlType.Document || depth >= 12) continue;
            for (var child = walker.GetFirstChild(element); child is not null && pending.Count < 600; child = walker.GetNextSibling(child)) pending.Enqueue((child, depth + 1));
        }
    }
    private static bool IsTargetForeground(nint target)
    {
        var foreground = GetAncestor(GetForegroundWindow(), 2);
        for (var i = 0; foreground != 0 && i < 16; i++, foreground = GetWindow(foreground, 4)) if (foreground == target) return true;
        return false;
    }
    private static bool Cloaked(nint handle) => DwmGetWindowAttribute(handle, 14, out var cloaked, 4) == 0 && cloaked != 0;
    public void Dispose() { disposed = true; jobs.CompleteAdding(); /* Do not join a hung external provider. */ }
    private delegate bool WindowCallback(nint handle, nint data);
    [DllImport("user32.dll")] private static extern bool EnumWindows(WindowCallback callback, nint data);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint handle);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint handle);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint handle, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint handle, StringBuilder text, int max);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint handle, uint flags);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint handle, uint command);
    [DllImport("user32.dll",EntryPoint="GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint handle,int index);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint handle, int attribute, out int value, int size);
}
