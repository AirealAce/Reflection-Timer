using System.ComponentModel;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using ReflectionTimer.Core;

namespace ReflectionTimer.Accessible;

// Decorative native edges follow the desktop, even when every timer view is
// hidden. They have no input, focus, task-switcher entry or screen-reader text.
internal sealed class FocusScreenGlow : IDisposable
{
    internal const int ExtendedStyles = 0x08000000 | 0x00080000 | 0x00000020 | 0x00000080;
    private readonly System.Windows.Forms.Timer pulse = new() { Interval = 80 };
    private readonly List<Edge> edges = [];
    private readonly Action? unavailable;
    private readonly Func<Rectangle[]> screens;
    private Rectangle[] bounds = [];
    private GlowMetrics[] geometry = [];
    private FocusGlowStyle style = FocusGlowStyle.CrimsonHalo;
    private bool active, disposed, animate;
    private long started, nextDisplayCheck;
    internal IReadOnlyList<nint> WindowHandles => edges.Select(edge => edge.Handle).ToArray();

    internal FocusScreenGlow(Action? unavailable = null, Func<Rectangle[]>? screens = null)
    {
        this.unavailable = unavailable;
        this.screens = screens ?? (() => Screen.AllScreens.Select(s => s.Bounds).ToArray());
        pulse.Tick += (_, _) => Refresh();
    }
    internal void SetActive(bool value, FocusGlowStyle selectedStyle = FocusGlowStyle.CrimsonHalo)
    {
        if (disposed) return;
        if (!Enum.IsDefined(selectedStyle)) selectedStyle = FocusGlowStyle.CrimsonHalo;
        var changedStyle = style != selectedStyle;
        if (active == value && !changedStyle) return;
        style = selectedStyle;
        active = value;
        if (!value) { Clear(); return; }
        if (changedStyle) Clear();
        started = Environment.TickCount64; nextDisplayCheck = 0;
        Refresh();
        if (edges.Count > 0) pulse.Start();
    }
    private void Refresh()
    {
        if (!active || disposed) return;
        try {
            var now = Environment.TickCount64;
            if (now >= nextDisplayCheck) {
                nextDisplayCheck = now + 1000;
                // Windows' reduced-motion preference also governs this glow.
                animate = SystemParametersInfo(0x1042, 0, out var enabled, 0) && enabled;
                var current = screens();
                var scaled = current.Select(screen => Metrics(screen, style, DpiScale(screen))).ToArray();
                if (!bounds.SequenceEqual(current) || !geometry.SequenceEqual(scaled)) {
                    Clear(); bounds = current; geometry = scaled;
                    for (var i = 0; i < bounds.Length; i++)
                        foreach (var edge in Layout(bounds[i], geometry[i].SurfaceThickness)) edges.Add(new(edge, bounds[i], style, geometry[i]));
                    if (edges.Count > 0) pulse.Start();
                }
            }
            var alpha = Opacity(now - started, animate, style);
            foreach (var edge in edges) edge.Show(alpha);
        } catch {
            // A graphics/display failure must never close the timer, steal
            // focus, or interfere with the independently configured audio.
            Clear(); try { unavailable?.Invoke(); } catch { }
        }
    }
    private static double DpiScale(Rectangle screen)
    {
        try {
            var monitor = MonitorFromPoint(new Point(screen.Left + screen.Width / 2, screen.Top + screen.Height / 2), 2);
            if (GetDpiForMonitor(monitor, 0, out var x, out _) == 0) return x / 96d;
        } catch { }
        return 1;
    }
    internal readonly record struct GlowMetrics(int Reach, int CornerRadius, int SurfaceThickness);
    internal static GlowMetrics Metrics(Rectangle screen, FocusGlowStyle style, double scale = 1)
    {
        var limit = Math.Max(1, Math.Min(screen.Width, screen.Height) / 2);
        if (style == FocusGlowStyle.Classic) {
            var thickness = Math.Clamp((int)Math.Round(48 * scale), 1, limit);
            return new(thickness, 0, thickness);
        }
        var reach = Math.Clamp((int)Math.Round(112 * scale), 1, Math.Max(1, limit / 2));
        var radius = Math.Min((int)Math.Round(32 * scale), Math.Max(0, limit - reach));
        // A rounded inner opening reaches farther into diagonal corners than
        // the straight edges. Include its entire fade in these narrow strips.
        var surface = Math.Min(limit, (int)Math.Ceiling(reach + (1 - 1 / Math.Sqrt(2)) * radius) + 1);
        return new(reach, radius, surface);
    }
    internal static Rectangle[] Layout(Rectangle screen, int thickness)
    {
        if (screen.Width < 2 || screen.Height < 2) return [];
        var t = Math.Clamp(thickness, 1, Math.Min(screen.Width, screen.Height) / 2);
        Rectangle[] result = [new(screen.Left, screen.Top, screen.Width, t), new(screen.Left, screen.Bottom - t, screen.Width, t),
            new(screen.Left, screen.Top + t, t, screen.Height - 2 * t), new(screen.Right - t, screen.Top + t, t, screen.Height - 2 * t)];
        return result.Where(r => r.Width > 0 && r.Height > 0).ToArray();
    }
    internal static byte Opacity(long elapsed, bool animate, FocusGlowStyle style = FocusGlowStyle.Classic) => animate
        ? (byte)Math.Round((style == FocusGlowStyle.Classic ? 210 : 170) + (style == FocusGlowStyle.Classic ? 25 : 15) * Math.Cos(Math.Max(0, elapsed) * Math.PI * 2 / 3200))
        : style == FocusGlowStyle.Classic ? (byte)235 : (byte)185;
    internal static byte PixelAlpha(Rectangle edge, Rectangle screen, int x, int y, FocusGlowStyle style = FocusGlowStyle.Classic)
        => PixelAlpha(edge, screen, x, y, style, Metrics(screen, style));
    private static byte PixelAlpha(Rectangle edge, Rectangle screen, int x, int y, FocusGlowStyle style, GlowMetrics metrics)
    {
        if (style != FocusGlowStyle.Classic) {
            // Signed distance to a rounded, transparent inner workspace.
            // Continuous coordinates keep the four native surfaces seamless.
            var qx = Math.Abs(edge.Left + x + .5 - (screen.Left + screen.Width / 2d)) - (screen.Width / 2d - metrics.Reach - metrics.CornerRadius);
            var qy = Math.Abs(edge.Top + y + .5 - (screen.Top + screen.Height / 2d)) - (screen.Height / 2d - metrics.Reach - metrics.CornerRadius);
            var dx = Math.Max(qx, 0); var dy = Math.Max(qy, 0);
            var roundedDistance = Math.Sqrt(dx * dx + dy * dy) + Math.Min(Math.Max(qx, qy), 0) - metrics.CornerRadius;
            var fade = Math.Clamp(roundedDistance / metrics.Reach, 0, 1);
            return (byte)Math.Round(255 * fade * fade * (3 - 2 * fade));
        }
        var distance = Math.Min(Math.Min(edge.Left + x - screen.Left, screen.Right - edge.Left - x - 1),
            Math.Min(edge.Top + y - screen.Top, screen.Bottom - edge.Top - y - 1));
        var thickness = Math.Min(edge.Width, edge.Height);
        return (byte)Math.Round(255 * Math.Pow(Math.Clamp(1 - distance / (double)thickness, 0, 1), 2));
    }
    internal static Bitmap Image(Rectangle edge, Rectangle screen, FocusGlowStyle style = FocusGlowStyle.Classic, GlowMetrics? scaledMetrics = null)
    {
        var metrics = scaledMetrics ?? Metrics(screen, style);
        var bitmap = new Bitmap(edge.Width, edge.Height, PixelFormat.Format32bppPArgb);
        var data = bitmap.LockBits(new(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.WriteOnly, bitmap.PixelFormat);
        try {
            var bytes = new byte[data.Stride * bitmap.Height];
            for (var y = 0; y < bitmap.Height; y++) for (var x = 0; x < bitmap.Width; x++) {
                var offset = y * data.Stride + x * 4;
                var alpha = PixelAlpha(edge, screen, x, y, style, metrics);
                bytes[offset + 3] = alpha;
                // Crimson combines dark blood red with a translucent pulse; Classic
                // retains its original pure-red pixels and lower opacity.
                bytes[offset + 2] = style == FocusGlowStyle.Classic ? alpha : (byte)Math.Round(alpha * 112 / 255d);
                bytes[offset] = style == FocusGlowStyle.Classic ? (byte)0 : (byte)Math.Round(alpha * 5 / 255d);
            }
            Marshal.Copy(bytes, 0, data.Scan0, bytes.Length);
        } finally { bitmap.UnlockBits(data); }
        return bitmap;
    }
    private void Clear()
    {
        pulse.Stop();
        foreach (var edge in edges) edge.Dispose();
        edges.Clear(); bounds = []; geometry = [];
    }
    public void Dispose() { if (disposed) return; SetActive(false); disposed = true; pulse.Dispose(); }

    private sealed class Edge : NativeWindow, IDisposable
    {
        private readonly Rectangle bounds;
        private nint bitmap, dc, previous;
        internal Edge(Rectangle bounds, Rectangle screen, FocusGlowStyle style, GlowMetrics metrics)
        {
            this.bounds = bounds;
            try {
                using var image = Image(bounds, screen, style, metrics);
                bitmap = image.GetHbitmap(Color.FromArgb(0));
                dc = CreateCompatibleDC(0);
                if (dc == 0) throw new Win32Exception();
                previous = SelectObject(dc, bitmap);
                CreateHandle(new CreateParams { X = bounds.X, Y = bounds.Y, Width = bounds.Width, Height = bounds.Height,
                    Style = unchecked((int)0x88000000), ExStyle = ExtendedStyles }); // disabled popup
            } catch { Dispose(); throw; }
        }
        internal void Show(byte alpha)
        {
            var location = bounds.Location; var size = bounds.Size; var source = Point.Empty;
            var blend = new Blend { ConstantAlpha = alpha, AlphaFormat = 1 };
            if (!UpdateLayeredWindow(Handle, 0, ref location, ref size, dc, ref source, 0, ref blend, 2)) throw new Win32Exception();
            if (!SetWindowPos(Handle, -1, 0, 0, 0, 0, 0x0053)) throw new Win32Exception(); // topmost, show, no activate/move/resize
        }
        protected override void WndProc(ref Message message)
        {
            if (message.Msg == 0x84) { message.Result = -1; return; } // hit-test transparent
            if (message.Msg == 0x21) { message.Result = 3; return; } // never activate
            if (message.Msg == 0x3D) { message.Result = 0; return; } // decorative, no accessibility object
            base.WndProc(ref message);
        }
        public void Dispose()
        {
            if (Handle != 0) DestroyHandle();
            if (dc != 0) { if (previous != 0) SelectObject(dc, previous); DeleteDC(dc); dc = 0; }
            if (bitmap != 0) { DeleteObject(bitmap); bitmap = 0; }
        }
    }
    [StructLayout(LayoutKind.Sequential, Pack = 1)] private struct Blend { internal byte Operation, Flags, ConstantAlpha, AlphaFormat; }
    [DllImport("user32.dll")] private static extern bool UpdateLayeredWindow(nint window, nint destination, ref Point position, ref Size size, nint source, ref Point sourcePosition, uint color, ref Blend blend, uint flags);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool SystemParametersInfo(uint action, uint parameter, [MarshalAs(UnmanagedType.Bool)] out bool value, uint flags);
    [DllImport("user32.dll")] private static extern nint MonitorFromPoint(Point point, uint flags);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(nint monitor, int type, out uint x, out uint y);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint value);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint value);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint dc);
}
