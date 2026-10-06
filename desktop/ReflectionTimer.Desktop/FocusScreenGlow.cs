using System.ComponentModel;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

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
    private int[] thicknesses = [];
    private bool active, disposed, animate;
    private long started, nextDisplayCheck;
    internal IReadOnlyList<nint> WindowHandles => edges.Select(edge => edge.Handle).ToArray();

    internal FocusScreenGlow(Action? unavailable = null, Func<Rectangle[]>? screens = null)
    {
        this.unavailable = unavailable;
        this.screens = screens ?? (() => Screen.AllScreens.Select(s => s.Bounds).ToArray());
        pulse.Tick += (_, _) => Refresh();
    }
    internal void SetActive(bool value)
    {
        if (disposed || active == value) return;
        active = value;
        if (!value) { Clear(); return; }
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
                var scaled = current.Select(Thickness).ToArray();
                if (!bounds.SequenceEqual(current) || !thicknesses.SequenceEqual(scaled)) {
                    Clear(); bounds = current; thicknesses = scaled;
                    for (var i = 0; i < bounds.Length; i++)
                        foreach (var edge in Layout(bounds[i], thicknesses[i])) edges.Add(new(edge, bounds[i]));
                    if (edges.Count > 0) pulse.Start();
                }
            }
            var alpha = Opacity(now - started, animate);
            foreach (var edge in edges) edge.Show(alpha);
        } catch {
            // A graphics/display failure must never close the timer, steal
            // focus, or interfere with the independently configured audio.
            Clear(); try { unavailable?.Invoke(); } catch { }
        }
    }
    private static int Thickness(Rectangle screen)
    {
        try {
            var monitor = MonitorFromPoint(new Point(screen.Left + screen.Width / 2, screen.Top + screen.Height / 2), 2);
            if (GetDpiForMonitor(monitor, 0, out var x, out _) == 0) return (int)Math.Round(48 * x / 96d);
        } catch { }
        return 48;
    }
    internal static Rectangle[] Layout(Rectangle screen, int thickness)
    {
        if (screen.Width < 2 || screen.Height < 2) return [];
        var t = Math.Clamp(thickness, 1, Math.Min(screen.Width, screen.Height) / 2);
        Rectangle[] result = [new(screen.Left, screen.Top, screen.Width, t), new(screen.Left, screen.Bottom - t, screen.Width, t),
            new(screen.Left, screen.Top + t, t, screen.Height - 2 * t), new(screen.Right - t, screen.Top + t, t, screen.Height - 2 * t)];
        return result.Where(r => r.Width > 0 && r.Height > 0).ToArray();
    }
    internal static byte Opacity(long elapsed, bool animate) => animate
        ? (byte)Math.Round(210 + 25 * Math.Cos(Math.Max(0, elapsed) * Math.PI * 2 / 3200)) : (byte)235;
    internal static byte PixelAlpha(Rectangle edge, Rectangle screen, int x, int y)
    {
        var distance = Math.Min(Math.Min(edge.Left + x - screen.Left, screen.Right - edge.Left - x - 1),
            Math.Min(edge.Top + y - screen.Top, screen.Bottom - edge.Top - y - 1));
        var thickness = Math.Min(edge.Width, edge.Height);
        return (byte)Math.Round(255 * Math.Pow(Math.Clamp(1 - distance / (double)thickness, 0, 1), 2));
    }
    internal static Bitmap Image(Rectangle edge, Rectangle screen)
    {
        var bitmap = new Bitmap(edge.Width, edge.Height, PixelFormat.Format32bppPArgb);
        var data = bitmap.LockBits(new(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.WriteOnly, bitmap.PixelFormat);
        try {
            var bytes = new byte[data.Stride * bitmap.Height];
            for (var y = 0; y < bitmap.Height; y++) for (var x = 0; x < bitmap.Width; x++) {
                var offset = y * data.Stride + x * 4;
                // Premultiplied pure red: B=G=0, R=A.
                bytes[offset + 2] = bytes[offset + 3] = PixelAlpha(edge, screen, x, y);
            }
            Marshal.Copy(bytes, 0, data.Scan0, bytes.Length);
        } finally { bitmap.UnlockBits(data); }
        return bitmap;
    }
    private void Clear()
    {
        pulse.Stop();
        foreach (var edge in edges) edge.Dispose();
        edges.Clear(); bounds = []; thicknesses = [];
    }
    public void Dispose() { if (disposed) return; SetActive(false); disposed = true; pulse.Dispose(); }

    private sealed class Edge : NativeWindow, IDisposable
    {
        private readonly Rectangle bounds;
        private nint bitmap, dc, previous;
        internal Edge(Rectangle bounds, Rectangle screen)
        {
            this.bounds = bounds;
            try {
                using var image = Image(bounds, screen);
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
