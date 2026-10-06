using ReflectionTimer.Core;

namespace ReflectionTimer.Accessible;

internal static class BackgroundFocusTargets
{
    // Selected tabs in each browser window are included even when that window
    // is behind another app. Open groups include every readable group header.
    internal static IReadOnlyList<FocusTarget> Capture(FocusTarget window,IReadOnlyList<FocusTarget> choices,
        IReadOnlyList<BrowserTabChoice> tabs,IReadOnlyList<BrowserTabSlot> strip)
    {
        var current=FocusedBrowserTargets.Capture(window,[FocusTargetKind.BrowserTab,FocusTargetKind.BrowserTabGroup],tabs,strip);
        var groups=BrowserTabGroups.Read(strip);
        var result=new List<FocusTarget>();
        foreach(var choice in choices.Where(t=>t.Kind!=FocusTargetKind.Window)){
            if(choice.CaptureScope==FocusCaptureScope.OpenIncludingBackground){
                result.AddRange(groups.Select((g,i)=>window with{Kind=FocusTargetKind.BrowserTabGroup,Name=g.Name,TabRuntimeId=g.Id,TabPosition=i+1,CaptureScope=choice.CaptureScope}));
                if(groups.Count==0&&strip.Any(s=>s.GroupHeader))result.Add(Unknown(window,choice));
                continue;
            }
            var selected=current.FirstOrDefault(t=>t.Kind==choice.Kind);
            if(selected is not null)result.Add(selected with{CaptureScope=choice.CaptureScope});
            else if(choice.Kind==FocusTargetKind.BrowserTab || strip.Any(s=>s.GroupHeader)
                && (groups.Count==0||!current.Any(t=>t.Kind==FocusTargetKind.BrowserTab)))result.Add(Unknown(window,choice));
        }
        return result;
    }
    private static FocusTarget Unknown(FocusTarget window,FocusTarget choice)=>window with{Kind=choice.Kind,CaptureScope=choice.CaptureScope,CaptureUnknown=true};
}
