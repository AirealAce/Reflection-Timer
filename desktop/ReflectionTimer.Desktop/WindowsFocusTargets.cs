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
    long? IdleMilliseconds => null;
    async Task<FocusPresence> CheckAnyAsync(IReadOnlyList<FocusTarget> targets)
    {
        var away = false; var unknown = false;
        foreach (var target in targets) {
            var presence = await CheckAsync(target).ConfigureAwait(false);
            if (presence == FocusPresence.Focused) return presence;
            away |= presence == FocusPresence.Away; unknown |= presence == FocusPresence.Unknown;
        }
        return unknown ? FocusPresence.Unknown : away ? FocusPresence.Away : FocusPresence.Unavailable;
    }
    bool IsCurrent(FocusTarget target) => false;
}

// Read only the visible window chrome and tab strip. Never inspect page bodies,
// URLs, browser profiles, keystrokes or history. UIA belongs to one long-lived
// MTA worker; an unresponsive provider cannot block the timer or its WebView.
internal sealed class WindowsFocusTargets : IFocusTargetSource
{
    public long? IdleMilliseconds {
        get {
            var input = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
            return GetLastInputInfo(ref input) ? InputAge(unchecked((uint)Environment.TickCount), input.Time) : null;
        }
    }
    // DWORD ticks wrap every 49.7 days. A future/synthetic timestamp fails quiet.
    internal static long? InputAge(uint now, uint last) { var age = unchecked(now - last); return age <= int.MaxValue ? age : null; }
    [StructLayout(LayoutKind.Sequential)] private struct LastInputInfo { internal uint Size, Time; }
    [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LastInputInfo input);
    private readonly BlockingCollection<Action> jobs = new();
    private readonly Dictionary<string, AutomationElement> tabs = [];
    private readonly Thread worker;
    private string? currentTabKey;
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
            // The App view is a valid target; floating/tool windows were filtered above.
            if (pid == 0) return true;
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
        return windows;
    }
    public Task<IReadOnlyList<FocusTarget>> ListAsync(FocusTargetKind kind) => kind == FocusTargetKind.Window
        ? Task.Run<IReadOnlyList<FocusTarget>>(() => OpenWindows().OrderBy(w => w.App).ThenBy(w => w.Name).ToArray()) : Enqueue<IReadOnlyList<FocusTarget>>(() => {
        var windows = OpenWindows();
        var choices = new List<FocusTarget>();
        var tabChoices = new List<BrowserTabChoice>();
        foreach (var window in windows.Where(w => w.App is "chrome" or "msedge" or "firefox" or "brave" or "vivaldi" or "opera").OrderBy(w => IsIconic((nint)w.WindowHandle))) {
            try {
                var position=0;
                var strip = BrowserStrip((nint)window.WindowHandle);
                if (kind == FocusTargetKind.BrowserTabGroup) {
                    foreach (var group in BrowserTabGroups.Read(strip.Select(slot => slot.Data).ToArray()))
                        choices.Add(window with { Id = Guid.NewGuid(), Kind = kind, Name = group.Name, TabRuntimeId = group.Id, TabPosition = ++position });
                    continue;
                }
                foreach (var slot in strip.Where(slot => !slot.Data.GroupHeader)) {
                    var tab = slot.Element;
                    if (!BelongsToWindow(tab, (nint)window.WindowHandle)) continue;
                    var name = slot.Data.Name;
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    var id = slot.Data.Id;
                    tabs[CacheKey(window, id)] = tab;
                    var target = window with { Id = Guid.NewGuid(), Kind = kind, Name = name.Length > 2048 ? name[..2048] : name, TabRuntimeId = id, TabPosition=++position };
                    tabChoices.Add(new(target, slot.Data.Selected, tab.Current.HasKeyboardFocus));
                }
            } catch { /* Browser does not expose a usable accessible tab strip. */ }
        }
        if (kind == FocusTargetKind.BrowserTab) {
            var listing = BrowserTabListing.Create(tabChoices);
            choices.AddRange(listing.Targets);
            Volatile.Write(ref currentTabKey, listing.Current is {} current ? BrowserTabListing.Identity(current) : null);
        }
        // Bound retained accessibility objects to current choices.
        var keys = choices.Select(t => CacheKey(t, t.TabRuntimeId)).ToHashSet();
        foreach (var key in tabs.Keys.Where(k => !keys.Contains(k)).ToArray()) tabs.Remove(key);
        return kind == FocusTargetKind.BrowserTab ? choices : choices.OrderBy(t => t.App).ThenBy(t => t.WindowName).ThenBy(t=>t.TabPosition).ToArray();
    });
    public bool IsCurrent(FocusTarget target) => target.Kind == FocusTargetKind.BrowserTab && BrowserTabListing.Identity(target) == Volatile.Read(ref currentTabKey);
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
            if (target.Kind == FocusTargetKind.BrowserTabGroup)
                return BrowserTabGroups.Check(target.TabRuntimeId, BrowserStrip(handle).Select(slot => slot.Data).ToArray(), IsTargetForeground(handle));
            var key = CacheKey(target, target.TabRuntimeId);
            if (!tabs.TryGetValue(key, out var tab)) {
                tab = BrowserStrip(handle).Where(slot => !slot.Data.GroupHeader).Select(slot => slot.Element).FirstOrDefault(t => string.Join(",", t.GetRuntimeId()) == target.TabRuntimeId);
                if (tab is null) return FocusPresence.Unavailable;
                tabs[key] = tab;
            }
            if (!BelongsToWindow(tab, handle)) return FocusPresence.Unavailable;
            if (tab.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var pattern)) {
                var selected = ((SelectionItemPattern)pattern).Current.IsSelected;
                return selected && IsTargetForeground(handle) ? FocusPresence.Focused : FocusPresence.Away;
            }
            return FocusPresence.Unknown;
        } catch (ElementNotAvailableException) { tabs.Remove(CacheKey(target, target.TabRuntimeId)); return FocusPresence.Unavailable; }
        catch { return FocusPresence.Unknown; }
    });
    private static bool BelongsToWindow(AutomationElement element, nint handle)
    {
        for (var i = 0; element is not null && i < 20; i++, element = TreeWalker.ControlViewWalker.GetParent(element)) {
            var nativeHandle = (nint)element.Current.NativeWindowHandle;
            if (nativeHandle != 0) return GetAncestor(nativeHandle, 2) == handle;
        }
        return false;
    }
    private static string CacheKey(FocusTarget target, string id) => $"{target.ProcessId}:{target.ProcessStartedAt}:{target.WindowHandle}:{id}";
    private sealed record NativeBrowserSlot(AutomationElement Element, BrowserTabSlot Data);
    private static IReadOnlyList<NativeBrowserSlot> BrowserStrip(nint handle)
    {
        var walker = TreeWalker.ControlViewWalker;
        var slots = new List<NativeBrowserSlot>();
        var identities = new HashSet<string>();
        var pending = new Queue<(AutomationElement Element, int Depth, string Parent)>(); pending.Enqueue((AutomationElement.FromHandle(handle), 0, ""));
        for (var visited = 0; pending.Count > 0 && visited < 600; visited++) {
            var (element, depth, parent) = pending.Dequeue(); var type = element.Current.ControlType;
            var id = string.Join(",", element.GetRuntimeId());
            if (!identities.Add(id)) continue;
            var group = type == ControlType.Tab && element.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out _);
            if (type == ControlType.TabItem || group) {
                bool? selected = null;
                if (!group && element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var selection)) selected = ((SelectionItemPattern)selection).Current.IsSelected;
                slots.Add(new(element, new(id, parent, element.Current.Name, group, selected)));
                continue;
            }
            if (type == ControlType.Document || depth >= 12) continue;
            for (var child = walker.GetFirstChild(element); child is not null && pending.Count < 600; child = walker.GetNextSibling(child)) pending.Enqueue((child, depth + 1, id));
        }
        return slots;
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
    [DllImport("user32.dll")] private static extern bool IsIconic(nint handle);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint handle, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint handle, StringBuilder text, int max);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint handle, uint flags);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint handle, uint command);
    [DllImport("user32.dll",EntryPoint="GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint handle,int index);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint handle, int attribute, out int value, int size);
}
