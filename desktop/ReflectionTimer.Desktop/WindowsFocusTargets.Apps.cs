using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using ReflectionTimer.Core;

namespace ReflectionTimer.Accessible;

internal sealed partial class WindowsFocusTargets
{
    private static readonly ConcurrentDictionary<string, string> appNames = new(StringComparer.OrdinalIgnoreCase);

    public FocusTarget? CaptureApp(FocusTarget window) => FocusApps.FromWindow(AppWindow(window, friendlyName: true));

    private static FocusTarget AppWindow(FocusTarget window, bool friendlyName = false)
    {
        var identity = ReadAppProcess(window.ProcessId);
        var path = identity.Path.Length > 0 ? identity.Path : window.ProcessPath;
        var applicationId = identity.ApplicationId;
        var host = FocusAppIdentity.IsSharedHost(Path.GetFileName(path));
        if (host) {
            // A UWP frame may be owned by ApplicationFrameHost while its content
            // lives in another process. Resolve only a unique real app or its
            // window AppUserModelID; never bookmark the shared frame host.
            applicationId = WindowApplicationId((nint)window.WindowHandle);
            var children = new HashSet<uint>();
            EnumChildWindows((nint)window.WindowHandle, (handle, _) => {
                GetWindowThreadProcessId(handle, out var pid);
                if (pid > 0 && pid != window.ProcessId) children.Add(pid);
                return true;
            }, 0);
            if (FocusApps.ResolveHosted(applicationId, children.Select(pid => ReadAppProcess((int)pid)).ToArray()) is {} resolved) {
                path = resolved.Path;
                applicationId = resolved.ApplicationId;
            }
        }
        var app = window.App;
        if (friendlyName && FocusAppIdentity.IsValid(path, applicationId)) {
            app = AppName(path);
            if (app.Length == 0) app = host && window.Name.Length > 0 ? window.Name : window.App;
        }
        return window with { ProcessPath = path, ApplicationId = applicationId, App = app };
    }

    private static string AppName(string path)
    {
        if (path.Length == 0 || FocusAppIdentity.IsSharedHost(Path.GetFileName(path))) return "";
        if (appNames.TryGetValue(path, out var known)) return known;
        var name = Path.GetFileNameWithoutExtension(path);
        try {
            var info = FileVersionInfo.GetVersionInfo(path);
            if (!string.IsNullOrWhiteSpace(info.FileDescription)) name = info.FileDescription.Trim();
            else if (!string.IsNullOrWhiteSpace(info.ProductName)) name = info.ProductName.Trim();
        } catch { }
        if (name.Length > 256) name = name[..256];
        if (appNames.Count < 512) appNames.TryAdd(path, name);
        return name;
    }

    private static (string Path, string ApplicationId) ReadAppProcess(int pid)
    {
        var process = OpenProcess(0x1000, false, pid); // PROCESS_QUERY_LIMITED_INFORMATION
        if (process == 0) return ("", "");
        try {
            var path = new StringBuilder(32768); var length = path.Capacity;
            var file = QueryFullProcessImageName(process, 0, path, ref length) ? path.ToString() : "";
            uint count = 0;
            var status = GetApplicationUserModelId(process, ref count, null);
            string appId = "";
            if (status == 122 && count is > 1 and <= 513) {
                var value = new StringBuilder((int)count);
                if (GetApplicationUserModelId(process, ref count, value) == 0) appId = value.ToString();
            }
            // This API returns package identity. Ordinary desktop apps use the
            // exe path, so Chrome profile-specific shell IDs cannot narrow an
            // App bookmark to just one profile/window.
            return (file, appId);
        } finally { CloseHandle(process); }
    }

    private static string WindowApplicationId(nint window)
    {
        IAppPropertyStore? store = null;
        try {
            var iid = typeof(IAppPropertyStore).GUID;
            if (SHGetPropertyStoreForWindow(window, ref iid, out store) != 0 || store is null) return "";
            var key = new AppPropertyKey(new("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), 5);
            if (store.GetValue(ref key, out var value) != 0) return "";
            try { return value.Type == 31 && value.Pointer != 0 ? Marshal.PtrToStringUni(value.Pointer) ?? "" : ""; }
            finally { PropVariantClear(ref value); }
        } catch { return ""; }
        finally { if (store is not null) Marshal.ReleaseComObject(store); }
    }

    private static FocusPresence CheckApp(FocusTarget target)
    {
        if (target.UseFocused || !FocusAppIdentity.IsValid(target.ProcessPath, target.ApplicationId)) return FocusPresence.Unknown;
        var foreground = DesktopIdentity(GetAncestor(GetForegroundWindow(), 2));
        var windows = new List<FocusTarget>();
        for (var i = 0; foreground != 0 && i < 16; i++, foreground = GetWindow(foreground, 4))
            if (ReadWindow(foreground, allowUntitled: true) is {} window) windows.Add(AppWindow(window));
        return FocusApps.Presence(target, windows);
    }

    private static FocusPresence CheckNative(FocusTarget target)
        => target.Kind == FocusTargetKind.App ? CheckApp(target) : CheckWindow(target);

    [StructLayout(LayoutKind.Sequential)] private struct AppPropertyKey(Guid format, uint id) { internal Guid Format = format; internal uint Id = id; }
    [StructLayout(LayoutKind.Explicit, Size = 24)] private struct AppPropertyValue { [FieldOffset(0)] internal ushort Type; [FieldOffset(8)] internal nint Pointer; }
    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAppPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out AppPropertyKey key);
        [PreserveSig] int GetValue(ref AppPropertyKey key, out AppPropertyValue value);
        [PreserveSig] int SetValue(ref AppPropertyKey key, ref AppPropertyValue value);
        [PreserveSig] int Commit();
    }
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(nint parent, WindowCallback callback, nint data);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(nint handle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryFullProcessImageName(nint process, uint flags, StringBuilder name, ref int length);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern int GetApplicationUserModelId(nint process, ref uint count, StringBuilder? value);
    [DllImport("shell32.dll", PreserveSig = true)] private static extern int SHGetPropertyStoreForWindow(nint window, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IAppPropertyStore? store);
    [DllImport("ole32.dll")] private static extern int PropVariantClear(ref AppPropertyValue value);
}
