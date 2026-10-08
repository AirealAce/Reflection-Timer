using System.Runtime.InteropServices;

// Coordinate placement adapts Chromium's display geometry (The Chromium Authors).
// BSD-style license and attribution: desktop/THIRD-PARTY-NOTICES.txt.

namespace ReflectionTimer.Accessible;

/// <summary>Converts physical HWND bounds to Chromium's screen DIP coordinates.</summary>
internal static class BrowserWindowBounds
{
    internal readonly record struct Display(nint Id, Rectangle Pixels, float Scale);

    // Chromium lays secondary displays out relative to the primary, rather than
    // dividing virtual-screen coordinates by each monitor's DPI. Reproduce its
    // unambiguous tree placements; decline overlap-repair/ordering-dependent
    // topologies. The primary's origin is invariant even in those topologies.
    // Sources: ui/display/win/{screen_win,scaling_util}.cc and display_layout.cc.
    internal static Rectangle? Normalize(Rectangle pixels, nint monitor, IReadOnlyList<Display> displays)
    {
        if (!Valid(pixels) || displays.Count is < 1 or > 32 || displays.Select(d => d.Id).Distinct().Count() != displays.Count)
            return null;
        var primary = displays.Where(d => d.Pixels.Location == Point.Empty).ToArray();
        var selected = displays.Where(d => d.Id == monitor).ToArray();
        if (primary.Length != 1 || selected.Length != 1 || !ValidDisplay(selected[0]) || !ValidDisplay(primary[0])) return null;
        if (monitor == primary[0].Id) return Convert(pixels, selected[0], Point.Empty);
        if (displays.Any(d => !ValidDisplay(d))) return null;

        var edges = new Dictionary<nint, List<nint>>();
        foreach (var display in displays) edges[display.Id] = [];
        for (var i = 0; i < displays.Count; i++) {
            for (var j = i + 1; j < displays.Count; j++) {
                var a = displays[i]; var b = displays[j];
                if (a.Pixels.IntersectsWith(b.Pixels)) return null;
                if (!Touch(a.Pixels, b.Pixels)) continue;
                edges[a.Id].Add(b.Id); edges[b.Id].Add(a.Id);
            }
        }
        // In a physical touching tree Chromium's traversal and display-ID order
        // cannot change parent selection. Cycles would require reproducing that
        // implementation-specific ordering and are deliberately not guessed.
        if (edges.Values.Sum(e => e.Count) != 2 * (displays.Count - 1)) return null;
        var byId = displays.ToDictionary(d => d.Id);
        var bounds = new Dictionary<nint, Rectangle> { [primary[0].Id] = Enclosing(primary[0].Pixels, primary[0].Scale) };
        var queue = new Queue<nint>(); queue.Enqueue(primary[0].Id);
        while (queue.TryDequeue(out var parentId)) {
            foreach (var childId in edges[parentId]) {
                if (bounds.ContainsKey(childId)) continue;
                bounds[childId] = Place(byId[parentId], bounds[parentId], byId[childId]);
                queue.Enqueue(childId);
            }
        }
        if (bounds.Count != displays.Count) return null;
        var placed = bounds.Values.ToArray();
        for (var i = 0; i < placed.Length; i++)
            for (var j = i + 1; j < placed.Length; j++)
                if (placed[i].IntersectsWith(placed[j])) return null;
        return Convert(pixels, selected[0], bounds[monitor].Location);
    }

    private static bool Valid(Rectangle rect) => rect.Width > 0 && rect.Height > 0
        && Math.Abs((long)rect.Left) <= 1_000_000 && Math.Abs((long)rect.Top) <= 1_000_000
        && Math.Abs((long)rect.Right) <= 1_000_000 && Math.Abs((long)rect.Bottom) <= 1_000_000;
    private static bool ValidDisplay(Display display) => display.Id != 0 && Valid(display.Pixels)
        && float.IsFinite(display.Scale) && display.Scale is >= .5f and <= 16;
    private static Rectangle Enclosing(Rectangle rect, float scale) => Rectangle.FromLTRB(
        (int)MathF.Floor(rect.Left / scale), (int)MathF.Floor(rect.Top / scale),
        (int)MathF.Ceiling(rect.Right / scale), (int)MathF.Ceiling(rect.Bottom / scale));
    private static Rectangle Convert(Rectangle rect, Display display, Point dipOrigin)
    {
        var size = Enclosing(rect, display.Scale).Size;
        // Chromium's newer rounded-point feature differs from floor by at most
        // one DIP; the caller's small geometry tolerance already permits this.
        return new(dipOrigin.X + (int)MathF.Floor((rect.Left - display.Pixels.Left) / display.Scale),
            dipOrigin.Y + (int)MathF.Floor((rect.Top - display.Pixels.Top) / display.Scale), size.Width, size.Height);
    }
    private static bool Touch(Rectangle a, Rectangle b) =>
        (Math.Max(a.Left, b.Left) == Math.Min(a.Right, b.Right) && a.Top <= b.Bottom && b.Top <= a.Bottom)
        || (Math.Max(a.Top, b.Top) == Math.Min(a.Bottom, b.Bottom) && a.Left <= b.Right && b.Left <= a.Right);

    private enum Side { Top, Right, Bottom, Left }
    private static Rectangle Place(Display parent, Rectangle parentDip, Display child)
    {
        var a = parent.Pixels; var b = child.Pixels;
        var x = Math.Max(a.Left, b.Left); var y = Math.Max(a.Top, b.Top);
        Side side;
        if (x == Math.Min(a.Right, b.Right) && y == Math.Min(a.Bottom, b.Bottom))
            side = a.Bottom == y ? Side.Bottom : a.Left == x ? Side.Left : Side.Top;
        else if (x == Math.Min(a.Right, b.Right)) side = a.Left == x ? Side.Left : Side.Right;
        else side = a.Top == y ? Side.Top : Side.Bottom;
        var horizontal = side is Side.Top or Side.Bottom;
        var parentStart = horizontal ? a.Left : a.Top;
        var parentLength = horizontal ? a.Width : a.Height;
        var childStart = (horizontal ? b.Left : b.Top) - parentStart;
        var childEnd = (horizontal ? b.Right : b.Bottom) - parentStart;
        var childDip = Enclosing(b, child.Scale);
        var parentDipLength = horizontal ? parentDip.Width : parentDip.Height;
        var childDipLength = horizontal ? childDip.Width : childDip.Height;
        int offset;
        if (childEnd == parentLength && childStart != 0) offset = parentDipLength - childDipLength;
        else if (childStart >= 0 && childStart <= parentLength) offset = ScaleOffset(parentLength, parent.Scale, childStart);
        else if (childEnd >= 0 && childEnd <= parentLength)
            offset = parentDipLength - ScaleOffset(parentLength, parent.Scale, parentLength - childEnd) - childDipLength;
        else offset = ScaleOffset(childEnd - childStart, child.Scale, childStart);
        offset = Math.Clamp(offset, -childDipLength, parentDipLength);
        var origin = side switch {
            Side.Top => new Point(parentDip.Left + offset, parentDip.Top - childDip.Height),
            Side.Right => new Point(parentDip.Right, parentDip.Top + offset),
            Side.Bottom => new Point(parentDip.Left + offset, parentDip.Bottom),
            _ => new Point(parentDip.Left - childDip.Width, parentDip.Top + offset)
        };
        return new(origin, childDip.Size);
    }
    private static int ScaleOffset(int length, float scale, int offset) => (int)MathF.Floor((length / scale) * ((float)offset / length));

    internal static Rectangle? Read(nint window)
    {
        if (window == 0) return null;
        try {
            // GetWindowRect is DPI-virtualized for unaware callers. The app is
            // PMv2; callers without that context cannot supply physical bounds.
            if (!AreDpiAwarenessContextsEqual(GetThreadDpiAwarenessContext(), (nint)(-4))) return null;
            var monitor = MonitorFromWindow(window, 2);
            if (monitor == 0 || !GetWindowRect(window, out var rect) || !ReadTextScale(out var textScale)) return null;
            var windowDpi = GetDpiForWindow(window);
            var displays = new List<Display>(); var readable = true;
            MonitorCallback callback = (nint id, nint dc, ref NativeRect bounds, nint data) => {
                var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
                if (!GetMonitorInfo(id, ref info) || GetDpiForMonitor(id, 0, out var x, out var y) < 0 || x != y || x is < 48 or > 768) {
                    readable = false; return true;
                }
                displays.Add(new(id, info.Bounds.Rectangle, x / 96f * (float)textScale));
                if (id == monitor && x != windowDpi) readable = false;
                return displays.Count <= 32;
            };
            if (!EnumDisplayMonitors(0, 0, callback, 0) || !readable || MonitorFromWindow(window, 2) != monitor
                || GetDpiForWindow(window) != windowDpi || !GetWindowRect(window, out var after) || after.Rectangle != rect.Rectangle) return null;
            // Secondary DIP origins also depend on other monitors. A topology
            // or scale change during collection invalidates the whole mapping.
            foreach (var display in displays) {
                var current = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
                if (!GetMonitorInfo(display.Id, ref current) || current.Bounds.Rectangle != display.Pixels
                    || GetDpiForMonitor(display.Id, 0, out var x, out var y) < 0 || x != y
                    || x / 96f * (float)textScale != display.Scale) return null;
            }
            if (!ReadTextScale(out var currentTextScale) || (float)currentTextScale != (float)textScale) return null;
            return Normalize(rect.Rectangle, monitor, displays);
        }
        catch { return null; } // An unknown coordinate system never falls back to title-only matching.
    }

    private static bool ReadTextScale(out double value)
    {
        value = 0; nint name = 0, instance = 0, settings = 0;
        var initialized = RoInitialize(1);
        if (initialized < 0 && initialized != unchecked((int)0x80010106)) return false; // Existing STA is valid too.
        try {
            const string type = "Windows.UI.ViewManagement.UISettings";
            if (WindowsCreateString(type, type.Length, out name) < 0 || RoActivateInstance(name, out instance) < 0) return false;
            var iid = new Guid("bad82401-2721-44f9-bb91-2bb228be442f");
            if (Marshal.QueryInterface(instance, in iid, out settings) < 0) return false;
            // IUISettings2 : IInspectable; TextScaleFactor is the first method
            // after IUnknown's three and IInspectable's three ABI slots.
            var method = Marshal.ReadIntPtr(Marshal.ReadIntPtr(settings), 6 * IntPtr.Size);
            var read = Marshal.GetDelegateForFunctionPointer<TextScaleGetter>(method);
            return read(settings, out value) >= 0 && double.IsFinite(value) && value is >= 1 and <= 8;
        }
        finally {
            if (settings != 0) Marshal.Release(settings);
            if (instance != 0) Marshal.Release(instance);
            if (name != 0) WindowsDeleteString(name);
            if (initialized >= 0) RoUninitialize();
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int TextScaleGetter(nint instance, out double value);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate bool MonitorCallback(nint monitor, nint dc, ref NativeRect rect, nint data);
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect {
        internal int Left, Top, Right, Bottom;
        internal readonly Rectangle Rectangle => System.Drawing.Rectangle.FromLTRB(Left, Top, Right, Bottom);
    }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo {
        internal int Size; internal NativeRect Bounds, WorkArea; internal uint Flags;
    }
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint window, uint flags);
    [DllImport("user32.dll")] private static extern nint GetThreadDpiAwarenessContext();
    [DllImport("user32.dll")] private static extern bool AreDpiAwarenessContextsEqual(nint first, nint second);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out NativeRect rect);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetMonitorInfoW")] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(nint dc, nint clip, MonitorCallback callback, nint data);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(nint monitor, int kind, out uint x, out uint y);
    [DllImport("combase.dll")] private static extern int RoInitialize(uint mode);
    [DllImport("combase.dll")] private static extern void RoUninitialize();
    [DllImport("combase.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] private static extern int WindowsCreateString(string source, int length, out nint value);
    [DllImport("combase.dll")] private static extern int WindowsDeleteString(nint value);
    [DllImport("combase.dll")] private static extern int RoActivateInstance(nint name, out nint instance);
}
