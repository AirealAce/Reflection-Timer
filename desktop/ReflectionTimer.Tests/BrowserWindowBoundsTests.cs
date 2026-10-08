using System.Drawing;
using ReflectionTimer.Accessible;
using Display = ReflectionTimer.Accessible.BrowserWindowBounds.Display;

internal static class BrowserWindowBoundsTests
{
    internal static void Run(Action<bool, string> check)
    {
        Display D(int id, int x, int y, int width, int height, float scale) => new(id, new(x, y, width, height), scale);
        Rectangle? N(Rectangle pixels, int monitor, params Display[] displays) => BrowserWindowBounds.Normalize(pixels, monitor, displays);
        var p100 = D(1, 0, 0, 1920, 1080, 1);
        check(N(new(100, 200, 900, 600), 1, p100) == new Rectangle(100, 200, 900, 600), "100% primary browser bounds stay in their original coordinates");
        check(N(new(125, 250, 1000, 500), 1, p100 with { Scale = 1.25f }) == new Rectangle(100, 200, 800, 400), "125% primary browser bounds normalize to Chromium DIP coordinates");
        check(N(new(300, 300, 1500, 900), 1, p100 with { Scale = 1.5f }) == new Rectangle(200, 200, 1000, 600), "150% conversion cannot mistake a larger browser window for the selected physical window");
        check(N(new(400, 400, 1600, 1000), 1, p100 with { Scale = 2 }) == new Rectangle(200, 200, 800, 500), "200% browser bounds normalize consistently");
        check(N(new(180, 180, 1440, 900), 1, p100 with { Scale = 1.5f * 1.2f }) is { } text && Math.Abs(text.X - 100) <= 1 && Math.Abs(text.Width - 800) <= 1, "Accessibility text scaling combines with monitor scaling before conversion");
        check(N(new(-1, -1, 2, 2), 1, p100 with { Scale = 1.5f }) == new Rectangle(-1, -1, 2, 2), "Negative screen coordinates use floor and enclosing size rather than truncation");
        check(N(new(1, 1, 2, 2), 1, p100 with { Scale = 1.5f }) == new Rectangle(0, 0, 2, 2), "Fractional origins preserve the complete enclosing window size");

        var p200 = D(1, 0, 0, 3840, 2160, 2);
        var right = D(2, 3840, 0, 1920, 1080, 1);
        check(N(new(3960, 80, 1200, 700), 2, p200, right) == new Rectangle(2040, 80, 1200, 700), "A 100% secondary right of a 200% primary uses the primary DIP edge, not its physical origin");
        check(N(new(3960, 80, 1200, 700), 2, right, p200) == new Rectangle(2040, 80, 1200, 700), "Supported monitor geometry is independent of enumeration order");
        check(N(new(-1800, 150, 1200, 750), 2, p100, D(2, -1920, 0, 1920, 1080, 1.5f)) == new Rectangle(-1200, 100, 800, 500), "A scaled monitor left of the primary retains a negative DIP origin");
        check(N(new(150, -1290, 1200, 900), 2, p100, D(2, 0, -1440, 2560, 1440, 1.5f)) == new Rectangle(100, -860, 800, 600), "A scaled monitor above the primary normalizes relative to its own origin");
        check(N(new(120, 1200, 1000, 600), 2, p100 with { Scale = 1.5f }, D(2, 0, 1080, 1920, 1080, 1)) == new Rectangle(120, 840, 1000, 600), "A monitor below the primary attaches to the scaled bottom edge");
        check(N(new(2020, 460, 1000, 600), 2, p100 with { Scale = 1.5f }, D(2, 1920, 360, 1280, 720, 1)) == new Rectangle(1380, 100, 1000, 600), "Bottom-aligned secondary monitors keep Chromium's end-aligned placement");
        check(N(new(2070, 300, 900, 600), 2, p100 with { Scale = 1.5f }, D(2, 1920, 150, 1920, 1080, 1.5f)) == new Rectangle(1380, 200, 600, 400), "A secondary's positive perpendicular offset scales with the parent");
        check(N(new(2020, -200, 800, 600), 2, p100 with { Scale = 1.5f }, D(2, 1920, -300, 1280, 720, 1)) == new Rectangle(1380, -340, 800, 600), "Partly above a parent, the child placement is anchored using its end offset");
        check(N(new(2020, -100, 800, 600), 2, p100, D(2, 1920, -300, 1920, 1800, 1.5f)) == new Rectangle(1986, -67, 534, 401), "A child encompassing the parent's perpendicular span uses the child's scale");
        check(N(new(5810, 100, 1000, 600), 3, p200, right, D(3, 5760, 0, 1920, 1080, 1)) == new Rectangle(3890, 100, 1000, 600), "An unambiguous three-monitor chain carries DIP origins through parent placements");
        check(N(new(2020, 1180, 900, 600), 2, p100, D(2, 1920, 1080, 1920, 1080, 1)) == new Rectangle(2020, 1180, 900, 600), "Corner-touching two-monitor placement follows Chromium's bottom-edge choice");

        var cyclic = new[] { p100, D(2, 1920, 0, 1920, 1080, 1), D(3, 0, 1080, 1920, 1080, 1) };
        check(N(new(2020, 100, 900, 600), 2, cyclic) is null, "Secondary layout with competing corner parents declines binding instead of guessing browser display-ID order");
        check(N(new(100, 100, 900, 600), 1, cyclic) == new Rectangle(100, 100, 900, 600), "Primary normalization remains available when secondary layout ordering is ambiguous");
        check(N(new(4100, 100, 900, 600), 2, p100, D(2, 4000, 0, 1920, 1080, 1)) is null, "Disconnected secondary monitor layouts do not use an invented DIP origin");
        check(N(new(1000, 100, 900, 600), 2, p100, D(2, 900, 0, 1920, 1080, 1)) is null, "Mirrored or physically overlapping monitor bounds decline secondary binding");
        // Physical displays touch in a chain, but scaling puts the last display
        // over the primary. Chrome repairs this in display-ID order; do not guess.
        var repair = new[] { D(1, 0, 0, 1000, 1000, 1), D(2, 1000, 0, 1000, 2000, 4), D(3, 0, 2000, 1000, 1000, 1) };
        check(N(new(100, 2100, 800, 600), 3, repair) is null, "A layout needing overlap repair declines secondary binding");
        check(N(new(100, 100, 900, 600), 9, p100) is null, "An unobserved monitor cannot be bound by title alone");
        check(N(new(100, 100, 900, 600), 1, p100, p100) is null, "Duplicate native monitor identities are rejected");
        check(N(new(100, 100, 900, 600), 1, p100 with { Scale = float.NaN }) is null
            && N(new(100, 100, 900, 600), 1, p100 with { Scale = 0 }) is null, "Unknown and invalid scale factors decline conversion");
        check(N(Rectangle.Empty, 1, p100) is null && N(new(int.MaxValue, 0, 100, 100), 1, p100) is null, "Empty or overflowing native bounds cannot enter geometry matching");
        check(N(new(100, 100, 900, 600), 1, p100 with { Pixels = new(1, 0, 1920, 1080) }) is null, "A display snapshot without the primary origin fails closed");
    }
}
