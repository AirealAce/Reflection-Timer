using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;
using ReflectionTimer.Core;

namespace ReflectionTimer.Accessible;

internal interface IFocusTargetSource : IDisposable
{
    bool BrowserConnected => false;
    void Configure(FocusModeSettings settings, TimerState timer) { }
    Task<IReadOnlyList<FocusTarget>> ListAsync(FocusTargetKind kind);
    Task<FocusPresence> CheckAsync(FocusTarget target);
    FocusTarget? CaptureForeground() => null;
    IReadOnlyList<FocusTarget> CaptureOpenWindows() => [];
    Task<IReadOnlyList<FocusTarget>> CaptureAsync(FocusTarget window, IReadOnlyList<FocusTargetKind> kinds) => Task.FromResult<IReadOnlyList<FocusTarget>>([]);
    Task<IReadOnlyList<FocusTarget>> CaptureSelectionsAsync(FocusTarget? foreground,IReadOnlyList<FocusTarget> windows,IReadOnlyList<FocusTarget> choices)
        => foreground is {} window?CaptureAsync(window,choices.Where(t=>t.CaptureScope==FocusCaptureScope.Focused).Select(t=>t.Kind).Distinct().ToArray())
            : Task.FromResult<IReadOnlyList<FocusTarget>>([]);
    long? IdleMilliseconds => null;
    async Task<FocusPresence> CheckAnyAsync(IReadOnlyList<FocusTarget> targets)
    {
        var away = false; var unknown = false;
        foreach (var target in targets) {
            var presence = target.UseFocused ? FocusPresence.Unknown : await CheckAsync(target).ConfigureAwait(false);
            if (presence == FocusPresence.Focused) return presence;
            away |= presence == FocusPresence.Away; unknown |= presence == FocusPresence.Unknown;
        }
        return unknown ? FocusPresence.Unknown : away ? FocusPresence.Away : FocusPresence.Unavailable;
    }
    Task<FocusPresence> CheckAnyAsync(IReadOnlyList<FocusTarget> targets, bool targetOnSiteLinks) => CheckAnyAsync(targets);
    bool IsCurrent(FocusTarget target) => false;
}

// Read only the visible window chrome and tab strip. Never inspect page bodies,
// browser profiles, keystrokes or history. The optional companion provides only
// website hosts and browser navigation relationships. UIA belongs to one long-lived
// MTA worker; an unresponsive provider cannot block the timer or its WebView.
internal sealed partial class WindowsFocusTargets : IFocusTargetSource
{
    private readonly BrowserFocusIndex? browserSites;
    public bool BrowserConnected => browserSites?.Connected == true;
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
    private readonly Thread worker;
    private string? currentTabKey;
    private bool disposed;
    internal WindowsFocusTargets(BrowserFocusIndex? browserSites = null)
    {
        this.browserSites = browserSites;
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
        var shell = GetShellWindow();
        EnumWindows((handle, _) => {
            if (shell != 0 && DesktopIdentity(handle) == shell) return true;
            if (windows.Count >= 256 || !IsWindowVisible(handle) || Cloaked(handle) || ((long)GetWindowLongPtr(handle,-20) & 0x08000080L) != 0) return true;
            // The App view is a valid target; floating/tool windows were filtered above.
            if (ReadWindow(handle) is {} target) windows.Add(target);
            return true;
        }, 0);
        // Windows marks its desktop as a tool window. It is intentionally
        // included once, separately from floating app/tool windows.
        if (shell != 0 && IsWindowVisible(shell) && ReadWindow(shell) is {} desktop) windows.Add(desktop);
        return windows;
    }
    private static FocusTarget? ReadWindow(nint handle, bool allowUntitled = false)
    {
        GetWindowThreadProcessId(handle, out var pid);
        if (pid == 0) return null;
        var caption = new StringBuilder(2049); GetWindowText(handle, caption, caption.Capacity);
        var name = handle == GetShellWindow() ? "Desktop" : caption.ToString();
        if (name.Length == 0 && !allowUntitled) return null;
        try {
            using var process = Process.GetProcessById((int)pid);
            return new(Guid.NewGuid(), FocusTargetKind.Window, name, process.ProcessName,
                handle.ToInt64(), (int)pid, process.StartTime.ToUniversalTime().Ticks) { WindowName=name, ProcessPath=ProcessPath(process), WindowClass=WindowClass(handle) };
        } catch { return null; }
    }
    internal static nint DesktopWindowIdentity(nint handle, nint shell, string windowClass, bool hasDesktopIcons)
        => shell != 0 && (handle == shell || windowClass == "WorkerW" && hasDesktopIcons) ? shell : handle;
    private static nint DesktopIdentity(nint handle)
    {
        var shell = GetShellWindow();
        if (shell == 0 || handle == shell) return handle;
        var windowClass = WindowClass(handle);
        return DesktopWindowIdentity(handle, shell, windowClass,
            windowClass == "WorkerW" && FindWindowEx(handle, 0, "SHELLDLL_DefView", null) != 0);
    }
    internal static bool IsMinimized(FocusTarget target) => !target.UseFocused && target.Kind == FocusTargetKind.Window && IsIconic((nint)target.WindowHandle);
    public FocusTarget? CaptureForeground()
    {
        // Snapshot the native identity synchronously at start/resume, before a
        // viewer or accessibility announcement can move focus elsewhere.
        var handle = DesktopIdentity(GetAncestor(GetForegroundWindow(), 2));
        if (handle == 0 || !IsWindow(handle)) return null;
        return ReadWindow(handle, allowUntitled:true);
    }
    public IReadOnlyList<FocusTarget> CaptureOpenWindows()=>OpenWindows();
    internal static bool IsBrowser(FocusTarget window)=>window.App.ToLowerInvariant() is "chrome" or "msedge" or "firefox" or "brave" or "vivaldi" or "opera";
    public async Task<IReadOnlyList<FocusTarget>> CaptureSelectionsAsync(FocusTarget? foreground,IReadOnlyList<FocusTarget> windows,IReadOnlyList<FocusTarget> choices)
    {
        var kinds=choices.Where(t=>t.CaptureScope==FocusCaptureScope.Focused&&(t.Kind!=FocusTargetKind.Site||BrowserConnected)).Select(t=>t.Kind).Distinct().ToArray();
        var single=foreground is {} window&&kinds.Length>0?CaptureAsync(window,kinds):Task.FromResult<IReadOnlyList<FocusTarget>>([]);
        var background=choices.Where(t=>t.CaptureScope!=FocusCaptureScope.Focused).ToArray();
        var open=background.Any(t=>t.Kind==FocusTargetKind.Window)
            ? windows.Select(t=>t with{CaptureScope=FocusCaptureScope.OpenIncludingBackground}).ToArray() : [];
        if(background.All(t=>t.Kind==FocusTargetKind.Window))return (await single.ConfigureAwait(false)).Concat(open).ToArray();
        var browser=Enqueue<IReadOnlyList<FocusTarget>>(()=>{
            var captured=new List<FocusTarget>();
            foreach(var target in windows.Where(IsBrowser)){
                if(CheckWindow(target)==FocusPresence.Unavailable)continue;
                try{
                    var strip=BrowserStrip((nint)target.WindowHandle);
                    var position=0;
                    var tabs=strip.Where(s=>!s.Data.GroupHeader&&BelongsToWindow(s.Element,(nint)target.WindowHandle))
                        .Select(s=>new BrowserTabChoice(target with{Kind=FocusTargetKind.BrowserTab,Name=s.Data.Name,TabRuntimeId=s.Data.Id,TabPosition=++position},s.Data.Selected,s.Element.Current.HasKeyboardFocus)).ToArray();
                    if(CheckWindow(target)!=FocusPresence.Unavailable)captured.AddRange(BackgroundFocusTargets.Capture(target,background,tabs,strip.Select(s=>s.Data).ToArray()));
                }catch{
                    captured.AddRange(background.Where(t=>t.Kind!=FocusTargetKind.Window).Select(t=>target with{Kind=t.Kind,CaptureScope=t.CaptureScope,CaptureUnknown=true}));
                }
            }
            return captured;
        });
        return (await single.ConfigureAwait(false)).Concat(open).Concat(await browser.ConfigureAwait(false)).ToArray();
    }
    public Task<IReadOnlyList<FocusTarget>> CaptureAsync(FocusTarget window, IReadOnlyList<FocusTargetKind> kinds)
    {
        var selectedKinds = kinds.Where(kind=>kind!=FocusTargetKind.Site||BrowserConnected).Distinct().ToArray();
        var windows = selectedKinds.Contains(FocusTargetKind.Window) ? new[] { window } : [];
        if(!IsBrowser(window)||selectedKinds.All(k=>k==FocusTargetKind.Window))return Task.FromResult<IReadOnlyList<FocusTarget>>(windows);
        return Enqueue<IReadOnlyList<FocusTarget>>(() => {
            // Never substitute another browser's selected tab when activation
            // happened outside a browser, or focus moved while UIA was queued.
            if (CheckWindow(window) != FocusPresence.Focused) return windows;
            FocusTarget? site=null;
            try {
                // Document and tab-strip providers can fail independently.
                // An unreadable URL must not hide otherwise usable tabs/groups.
                if(selectedKinds.Contains(FocusTargetKind.Site)){
                    try{site=CaptureSite(window);}catch{ }
                }
                if(selectedKinds.All(k=>k is FocusTargetKind.Window or FocusTargetKind.Site))
                    return CheckWindow(window)==FocusPresence.Focused&&site is not null?windows.Append(site).ToArray():windows;
                var position = 0;
                var strip = BrowserStrip((nint)window.WindowHandle);
                var choices = strip.Where(slot => !slot.Data.GroupHeader && BelongsToWindow(slot.Element, (nint)window.WindowHandle))
                    .Select(slot => new BrowserTabChoice(window with { Kind = FocusTargetKind.BrowserTab, Name = slot.Data.Name,
                        TabRuntimeId = slot.Data.Id, TabPosition = ++position }, slot.Data.Selected, slot.Element.Current.HasKeyboardFocus)).ToArray();
                var captured = FocusedBrowserTargets.Capture(window, selectedKinds, choices, strip.Select(s => s.Data).ToArray());
                BindBrowserWindow(window,strip.Select(s=>s.Data).ToArray());
                return CheckWindow(window) == FocusPresence.Focused ? site is null ? captured : captured.Append(site).ToArray() : windows;
            } catch {
                // Site capture is independent of tab-strip accessibility. Keep
                // a verified committed host if tabs/groups cannot be inspected.
                return site is not null&&CheckWindow(window)==FocusPresence.Focused?windows.Append(site).ToArray():windows;
            }
        });
    }
    public Task<IReadOnlyList<FocusTarget>> ListAsync(FocusTargetKind kind) => kind == FocusTargetKind.Site
        ? Task.FromResult<IReadOnlyList<FocusTarget>>(BrowserConnected?browserSites!.ListSites():[]) : kind == FocusTargetKind.Window
        ? Task.Run<IReadOnlyList<FocusTarget>>(() => OpenWindows().OrderBy(w => w.Name == "Desktop" && w.WindowClass == "Progman" ? 0 : 1).ThenBy(w => w.App).ThenBy(w => w.Name).ToArray()) : Enqueue<IReadOnlyList<FocusTarget>>(() => {
        var windows = OpenWindows();
        var choices = new List<FocusTarget>();
        var tabChoices = new List<BrowserTabChoice>();
        foreach (var window in windows.Where(w => w.App is "chrome" or "msedge" or "firefox" or "brave" or "vivaldi" or "opera").OrderBy(w => IsIconic((nint)w.WindowHandle))) {
            try {
                var position=0;
                var strip = BrowserStrip((nint)window.WindowHandle);
                BindBrowserWindow(window,strip.Select(s=>s.Data).ToArray());
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
        return kind == FocusTargetKind.BrowserTab ? choices : choices.OrderBy(t => t.App).ThenBy(t => t.WindowName).ThenBy(t=>t.TabPosition).ToArray();
    });
    public bool IsCurrent(FocusTarget target) => target.Kind == FocusTargetKind.Site ? browserSites?.IsCurrentSite(target)==true
        : target.Kind == FocusTargetKind.BrowserTab && BrowserTabListing.Identity(target) == Volatile.Read(ref currentTabKey);
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
    public Task<FocusPresence> CheckAsync(FocusTarget target) => target.Kind==FocusTargetKind.Site ? CheckAnyAsync([target],false)
        : target.CaptureUnknown?Task.FromResult(CheckWindow(target)==FocusPresence.Unavailable?FocusPresence.Unavailable:FocusPresence.Unknown)
        : target.UseFocused ? Task.FromResult(FocusPresence.Unknown) : target.Kind == FocusTargetKind.Window
        ? Task.FromResult(CheckWindow(target)) : Enqueue(() => CheckBrowser(target, []));
    public Task<FocusPresence> CheckAnyAsync(IReadOnlyList<FocusTarget> targets)=>CheckAnyAsync(targets,false);
    private Task<FocusPresence> CheckNativeTargetsAsync(IReadOnlyList<FocusTarget> targets)
    {
        // Check ordinary windows first so an unrelated hung browser cannot
        // mask a selected window. Read each browser strip once per probe.
        if (targets.Any(t => !t.UseFocused && !t.CaptureUnknown && t.Kind == FocusTargetKind.Window && CheckWindow(t) == FocusPresence.Focused))
            return Task.FromResult(FocusPresence.Focused);
        if (targets.All(t => t.Kind == FocusTargetKind.Window || t.UseFocused || t.CaptureUnknown)) {
            var native = targets.Select(t => t.UseFocused ? FocusPresence.Unknown : t.CaptureUnknown
                ? CheckWindow(t) == FocusPresence.Unavailable ? FocusPresence.Unavailable : FocusPresence.Unknown : CheckWindow(t)).ToArray();
            return Task.FromResult(native.Contains(FocusPresence.Focused) ? FocusPresence.Focused
                : native.Contains(FocusPresence.Unknown) ? FocusPresence.Unknown : native.Contains(FocusPresence.Away) ? FocusPresence.Away : FocusPresence.Unavailable);
        }
        return Enqueue(() => {
            var snapshots = new Dictionary<(long, int, long), BrowserProbe>();
            var away = false; var unknown = false;
            foreach (var target in targets) {
                var presence = target.UseFocused ? FocusPresence.Unknown
                    : target.CaptureUnknown ? CheckWindow(target) == FocusPresence.Unavailable ? FocusPresence.Unavailable : FocusPresence.Unknown
                    : target.Kind == FocusTargetKind.Window ? CheckWindow(target) : CheckBrowser(target, snapshots);
                if (presence == FocusPresence.Focused) return presence;
                away |= presence == FocusPresence.Away; unknown |= presence == FocusPresence.Unknown;
            }
            return unknown ? FocusPresence.Unknown : away ? FocusPresence.Away : FocusPresence.Unavailable;
        });
    }
    private sealed record BrowserProbe(IReadOnlyList<NativeBrowserSlot> Slots)
    {
        internal IReadOnlyList<BrowserTabChoice>? Choices { get; set; }
    }
    private static FocusPresence CheckBrowser(FocusTarget target, Dictionary<(long, int, long), BrowserProbe> snapshots)
    {
        var window = CheckWindow(target);
        if (window == FocusPresence.Unavailable) return window;
        var handle = (nint)target.WindowHandle;
        try {
            var key = (target.WindowHandle, target.ProcessId, target.ProcessStartedAt);
            if (!snapshots.TryGetValue(key, out var probe)) snapshots[key] = probe = new(BrowserStrip(handle));
            if (target.Kind == FocusTargetKind.BrowserTabGroup)
                return BrowserTabGroups.Check(target.TabRuntimeId, probe.Slots.Select(slot => slot.Data).ToArray(), IsTargetForeground(handle));
            probe.Choices ??= probe.Slots.Where(slot => !slot.Data.GroupHeader && BelongsToWindow(slot.Element, handle))
                .Select(slot => new BrowserTabChoice(target with { Kind = FocusTargetKind.BrowserTab, TabRuntimeId = slot.Data.Id }, slot.Data.Selected, slot.Element.Current.HasKeyboardFocus)).ToArray();
            return FocusedBrowserTargets.CheckTab(target, probe.Choices, IsTargetForeground(handle));
        } catch (ElementNotAvailableException) { return FocusPresence.Unavailable; }
        catch { return FocusPresence.Unknown; }
    }
    private static bool BelongsToWindow(AutomationElement element, nint handle)
    {
        for (var i = 0; element is not null && i < 20; i++, element = TreeWalker.ControlViewWalker.GetParent(element)) {
            var nativeHandle = (nint)element.Current.NativeWindowHandle;
            if (nativeHandle != 0) return GetAncestor(nativeHandle, 2) == handle;
        }
        return false;
    }
    private static string ProcessPath(Process process)
    {
        try { return process.MainModule?.FileName ?? ""; } catch { return ""; }
    }
    private static string WindowClass(nint handle)
    {
        var value=new StringBuilder(512);GetClassName(handle,value,value.Capacity);return value.ToString();
    }
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern int GetClassName(nint handle,StringBuilder name,int count);
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
        var foreground = DesktopIdentity(GetAncestor(GetForegroundWindow(), 2));
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
    [DllImport("user32.dll")] private static extern nint GetShellWindow();
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern nint FindWindowEx(nint parent, nint after, string windowClass, string? title);
    [DllImport("user32.dll",EntryPoint="GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint handle,int index);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint handle, int attribute, out int value, int size);
}
