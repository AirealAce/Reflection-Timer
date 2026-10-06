using ReflectionTimer.Accessible;
using ReflectionTimer.Core;

internal static class FocusWindowListingTests
{
    internal static void Run(Action<bool,string> check)
    {
        var first = new FocusTarget(Guid.NewGuid(),FocusTargetKind.Window,"ChatGPT","ChatGPT",101,200,300);
        var second = first with {Id=Guid.NewGuid(),WindowHandle=102};
        var labels = FocusWindowLabels.Create([second,first,first],t=>t.WindowHandle==102);
        check(labels.Count==2 && labels[first.Key]=="ChatGPT — Window 1" && labels[second.Key]=="ChatGPT — Window 2 (minimized)",
            "Same-title real windows receive distinct display labels and an explicit minimized state");
        var refreshed = FocusWindowLabels.Create([first,second],_=>false);
        check(refreshed[first.Key]==labels[first.Key] && refreshed[second.Key]=="ChatGPT — Window 2",
            "Window numbers stay attached to their native identity across refresh and minimize changes");
        check(first.Name=="ChatGPT" && second.Name=="ChatGPT" && first.WindowName=="",
            "Display labels leave persisted titles and native target identities unchanged");
        check(FocusWindowLabels.Create([first,first],_=>false)[first.Key]=="ChatGPT",
            "Repeated native identities do not invent another numbered window");
        var differentApp=second with{App="Other app"};
        check(FocusWindowLabels.Create([first,differentApp],_=>false).Values.All(v=>v=="ChatGPT"),
            "Different apps remain distinguished by the App column without extra window numbers");
        check(FocusWindowLabels.Create([FocusTarget.Focused(FocusTargetKind.Window),first with{Kind=FocusTargetKind.BrowserTab}],_=>true).Count==0,
            "Dynamic choices and browser targets keep their original labels");
        check(WindowsFocusTargets.DesktopWindowIdentity(42,42,"Progman",false)==42,
            "The actual shell window remains the canonical Desktop target");
        check(WindowsFocusTargets.DesktopWindowIdentity(43,42,"WorkerW",true)==42,
            "The desktop icon host captures and checks the same saved Desktop identity");
        check(WindowsFocusTargets.DesktopWindowIdentity(43,42,"WorkerW",false)==43
            && WindowsFocusTargets.DesktopWindowIdentity(43,42,"Chrome_WidgetWin_1",true)==43,
            "Wallpaper helpers and ordinary app windows cannot masquerade as Desktop focus");
        check(WindowsFocusTargets.DesktopWindowIdentity(43,0,"WorkerW",true)==43,
            "An unavailable shell never replaces a target with a zero window handle");
        var desktop=first with{App="explorer",Name="Desktop",WindowName="Desktop",WindowClass="Progman"};
        var reopened=desktop with{WindowHandle=103,ProcessId=201,ProcessStartedAt=301};
        var folder=reopened with{WindowHandle=104,WindowClass="CabinetWClass"};
        check(SavedFocusWindows.Resolve(desktop,[reopened,folder]).Key==reopened.Key,
            "A restarted Desktop reconnects to the shell rather than an identically named Explorer folder");
    }
}
