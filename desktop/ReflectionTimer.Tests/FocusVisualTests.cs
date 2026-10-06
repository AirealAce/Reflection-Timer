using System.Drawing;
using System.Text.Json;
using ReflectionTimer.Accessible;
using ReflectionTimer.Core;

internal static class FocusVisualTests
{
    internal static void Run(Action<bool,string> check)
    {
        check(JsonSerializer.Deserialize<AppState>("{}")!.FocusMode.ScreenEdgeGlow, "New and older profiles default to the Focus screen-edge glow");
        check(JsonSerializer.Deserialize<AppState>("{}")!.FocusMode.ScreenEdgeGlowStyle == FocusGlowStyle.CrimsonHalo
            && JsonSerializer.Deserialize<FocusModeSettings>("{\"ScreenEdgeGlow\":false}") is {ScreenEdgeGlow:false,ScreenEdgeGlowStyle:FocusGlowStyle.CrimsonHalo},
            "Crimson halo is the default style without re-enabling a previously disabled glow");
        var target = new FocusTarget(Guid.NewGuid(), FocusTargetKind.Window, "Synthetic target", "synthetic", 1, 2, 3);
        var settings = new FocusModeSettings { Enabled = true, Target = target, DelaySeconds = 5 };
        foreach (var mode in Enum.GetValues<SessionMode>()) {
            var gate = new FocusModeGate();
            var timer = new TimerState { Mode = mode, IsRunning = true, SessionId = Guid.NewGuid() };
            var immediate = gate.Evaluate(settings, timer, FocusPresence.Away, 1000);
            check(immediate.ScreenEdgeGlow && !immediate.Alert, mode + ": leaving a target shows the glow immediately while audio still waits");
            check(gate.Evaluate(settings, timer, FocusPresence.Away, 6000) is { Alert: true, ScreenEdgeGlow: true }, mode + ": audio delay does not stop the visual warning");
            foreach (var presence in new[] { FocusPresence.Focused, FocusPresence.Unknown, FocusPresence.Unavailable })
                check(!gate.Evaluate(settings, timer, presence, 6001).ScreenEdgeGlow, mode + ": " + presence + " removes the visual warning");
            check(!gate.Evaluate(settings, timer with { IsRunning = false }, FocusPresence.Away, 6002).ScreenEdgeGlow
                && !gate.Evaluate(settings with { Enabled = false }, timer, FocusPresence.Away, 6002).ScreenEdgeGlow, mode + ": pause, completion and Focus off remove the glow");
            check(!gate.Evaluate(settings with { ScreenEdgeGlow = false }, timer, FocusPresence.Away, 7000).ScreenEdgeGlow, mode + ": disabling Animations retains normal audio gating");
            check(gate.Evaluate(settings with { IdleEnabled = true }, timer, FocusPresence.Focused, 8000, 20000) is { Alert: true, ScreenEdgeGlow: false }, mode + ": idle audio on a selected target does not show an away glow");
            var engine = new TimerEngine(new MemoryStore()); engine.SwitchMode(mode); engine.SetFocusMode(settings);
            var source = new Source(); var visuals = new List<bool>(); var audio = new List<bool>();
            using var monitor = new FocusModeMonitor(engine, source, audio.Add, visuals.Add);
            if (mode == SessionMode.Timer) engine.Start(900, false, 50); else engine.StartStopwatch();
            monitor.Poll(); check(visuals.Last() && !audio.Contains(true), mode + ": monitor publishes immediate visual warning independently of audio");
            engine.SetFocusMode(engine.SettingsSnapshot.FocusMode with { ScreenEdgeGlow = false });
            check(!visuals.Last(), mode + ": changing the checkbox removes visible edges synchronously");
            engine.SetFocusMode(engine.SettingsSnapshot.FocusMode with { ScreenEdgeGlow = true }); monitor.Poll();
            check(visuals.Last(), mode + ": re-enabling the glow during an away session takes effect");
            engine.Pause(); check(!visuals.Last(), mode + ": committed pause removes the glow before another probe");
        }
        var store = new MemoryStore(); var saved = new TimerEngine(store); saved.SetFocusMode(settings with { ScreenEdgeGlow = false });
        check(!new TimerEngine(store).SettingsSnapshot.FocusMode.ScreenEdgeGlow && store.State.FocusMode.Target == target,
            "Animation preference persists without replacing Focus targets");
        foreach (var style in Enum.GetValues<FocusGlowStyle>()) {
            saved.SetFocusMode(saved.SettingsSnapshot.FocusMode with {ScreenEdgeGlowStyle=style});
            check(new TimerEngine(store).SettingsSnapshot.FocusMode.ScreenEdgeGlowStyle == style && store.State.FocusMode.Target == target
                && !store.State.FocusMode.ScreenEdgeGlow, "Selecting " + style + " persists while retaining the enabled preference and targets");
        }
        try { saved.SetFocusMode(saved.SettingsSnapshot.FocusMode with {ScreenEdgeGlowStyle=(FocusGlowStyle)99});throw new Exception("Unsupported style accepted"); } catch(ArgumentException) { }
        check(saved.SettingsSnapshot.FocusMode.ScreenEdgeGlowStyle == FocusGlowStyle.Classic, "Invalid styles cannot replace the saved animation choice");
        foreach (var monitor in new[] { new Rectangle(0, 0, 1920, 1080), new Rectangle(-2560, -200, 2560, 1440), new Rectangle(0, 0, 2, 2) }) {
            var edges = FocusScreenGlow.Layout(monitor, 48);
            check(edges.All(r => monitor.Contains(r) && r.Width > 0 && r.Height > 0), "Native edges stay inside each monitor, including negative coordinates and small displays");
            check(edges.SelectMany((a, i) => edges.Skip(i + 1).Select(b => Rectangle.Intersect(a, b))).All(r => r.Width == 0 || r.Height == 0),
                "Edges do not overlap or paint the central workspace");
        }
        var area = new Rectangle(0, 0, 640, 360); var top = FocusScreenGlow.Layout(area, 48)[0];
        check(FocusScreenGlow.PixelAlpha(top, area, 320, 0) == 255 && FocusScreenGlow.PixelAlpha(top, area, 320, 47) < 2,
            "Bright perimeter fades to transparent toward the workspace");
        check(Enumerable.Range(0, 3200).All(t => FocusScreenGlow.Opacity(t, true) is >= 185 and <= 235)
            && FocusScreenGlow.Opacity(0, true) == FocusScreenGlow.Opacity(3200, true), "The slow breathing glow never flashes on/off");
        check(FocusScreenGlow.Opacity(0, false) == 235 && FocusScreenGlow.Opacity(1600, false) == 235, "Reduced motion uses a steady visible glow");
        using var bitmap = FocusScreenGlow.Image(top, area);
        check(bitmap.GetPixel(320, 0) is { A: 255, R: 255, G: 0, B: 0 } && bitmap.GetPixel(320, 47).A < 2,
            "Native bitmap is red with per-pixel transparency, not an opaque screen cover");
        var screen = new Rectangle(-1920, 0, 1920, 1080);
        var halo = FocusScreenGlow.Metrics(screen, FocusGlowStyle.CrimsonHalo);
        var classic = FocusScreenGlow.Metrics(screen, FocusGlowStyle.Classic);
        var haloEdges = FocusScreenGlow.Layout(screen, halo.SurfaceThickness);
        var classicTop = FocusScreenGlow.Layout(screen, classic.SurfaceThickness)[0];
        check(halo.Reach > classic.Reach * 2 && halo.CornerRadius is > 0 and <= 32 && halo.CornerRadius < halo.Reach / 3,
            "Crimson halo extends more than twice as far with gently rounded, not pill-shaped, inner corners");
        check(FocusScreenGlow.PixelAlpha(haloEdges[0], screen, 960, 72, FocusGlowStyle.CrimsonHalo) > 70
            && FocusScreenGlow.PixelAlpha(classicTop, screen, 960, 72, FocusGlowStyle.Classic) == 0,
            "The new glow remains clearly visible beyond the original fade");
        check(FocusScreenGlow.PixelAlpha(haloEdges[0], screen, halo.Reach, halo.Reach, FocusGlowStyle.CrimsonHalo) > 0
            && FocusScreenGlow.PixelAlpha(haloEdges[0], screen, 960, halo.Reach, FocusGlowStyle.CrimsonHalo) == 0,
            "The rounded halo curves around corners while leaving the central workspace clear");
        check(FocusScreenGlow.PixelAlpha(haloEdges[0], screen, 960, halo.SurfaceThickness - 1, FocusGlowStyle.CrimsonHalo) == 0,
            "The native surface reaches past the halo without clipping its inward fade");
        check(FocusScreenGlow.Metrics(screen, FocusGlowStyle.CrimsonHalo, 2).Reach == halo.Reach * 2,
            "Halo extent follows monitor scaling");
        using var crimson = FocusScreenGlow.Image(haloEdges[0], screen, FocusGlowStyle.CrimsonHalo);
        check(crimson.GetPixel(960, 0) is { A:255, R:112, G:0, B:5 }, "Crimson pixels use dark blood red without warm green/orange");
        check(Enumerable.Range(0, halo.Reach - 1).All(y => crimson.GetPixel(960,y).A >= crimson.GetPixel(960,y+1).A)
            && crimson.GetPixel(960,halo.Reach).A == 0, "The blood-red glow continuously becomes more transparent toward the clear center");
        check(Enumerable.Range(0, 3200).All(t => FocusScreenGlow.Opacity(t, true, FocusGlowStyle.CrimsonHalo) is >=155 and <=185)
            && FocusScreenGlow.Opacity(0, false, FocusGlowStyle.CrimsonHalo) == 185, "Crimson remains translucent through its slow pulse and reduced-motion fallback");
        var small = new Rectangle(0, 0, 640, 360);
        var surfaces = FocusScreenGlow.Layout(small, FocusScreenGlow.Metrics(small, FocusGlowStyle.CrimsonHalo).SurfaceThickness);
        var coversFade = true;
        for(var y=0; y<small.Height && coversFade; y+=3)for(var x=0;x<small.Width;x+=3)
            if(FocusScreenGlow.PixelAlpha(small,small,x,y,FocusGlowStyle.CrimsonHalo)>0 && !surfaces.Any(r=>r.Contains(x,y))){coversFade=false;break;}
        check(coversFade, "The four native strips cover every sampled rounded-corner pixel without gaps");
    }
    private sealed class Source : IFocusTargetSource
    {
        public Task<IReadOnlyList<FocusTarget>> ListAsync(FocusTargetKind kind) => Task.FromResult<IReadOnlyList<FocusTarget>>([]);
        public Task<FocusPresence> CheckAsync(FocusTarget target) => Task.FromResult(FocusPresence.Away);
        public void Dispose() { }
    }
}
