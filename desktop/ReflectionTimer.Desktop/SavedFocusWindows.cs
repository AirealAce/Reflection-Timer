using System.Collections.Immutable;
using System.Text.RegularExpressions;
using ReflectionTimer.Core;

namespace ReflectionTimer.Accessible;

// Reconnect only saved Windows. A captured dynamic target and a browser's
// runtime tab identity must never be replaced by a similarly named target.
internal sealed class SavedFocusWindows(TimerEngine engine, IFocusTargetSource source)
{
    private Task<IReadOnlyList<FocusTarget>>? listing;
    private string? selection;
    private long nextRead;
    private readonly Dictionary<string,FocusTarget> aliases=[];
    internal static bool IsSavedWindow(FocusTarget target)=>target.Kind==FocusTargetKind.Window&&!target.UseFocused;
    private static bool SameApplication(FocusTarget saved,FocusTarget current)=>
        string.Equals(saved.App,current.App,StringComparison.OrdinalIgnoreCase)
        && (saved.ProcessPath.Length==0 || current.ProcessPath.Length>0&&string.Equals(saved.ProcessPath,current.ProcessPath,StringComparison.OrdinalIgnoreCase))
        && (saved.WindowClass.Length==0 || current.WindowClass.Length>0&&ClassKey(saved.WindowClass)==ClassKey(current.WindowClass));
    // WinForms embeds a process/assembly-specific hash in the native class.
    private static string ClassKey(string value)=>value.StartsWith("WindowsForms",StringComparison.Ordinal)
        ? Regex.Replace(value,@"\.app\..*$","") : value;
    private static string TitleKey(FocusTarget target)
    {
        var title=(target.WindowName.Length>0?target.WindowName:target.Name).Trim();
        return target.App.Equals("ReflectionTimer",StringComparison.OrdinalIgnoreCase)
            ? Regex.Replace(title,@" · \d+\.\d+\.\d+$","") : title;
    }
    internal static FocusTarget Resolve(FocusTarget saved,IReadOnlyList<FocusTarget> open,HashSet<string>? reserved=null,bool allowSingleApp=true)
    {
        if(!IsSavedWindow(saved))return saved;
        var windows=open.Where(IsSavedWindow).DistinctBy(t=>t.Key).ToArray();
        // A live identity remains authoritative when the window's title changes.
        var exact=windows.FirstOrDefault(t=>t.Key==saved.Key);
        if(exact is not null)return saved; // Refresh captions in the chooser, not on every poll/save.
        var candidates=windows.Where(t=>SameApplication(saved,t)).ToArray();
        var named=candidates.Where(t=>TitleKey(t)==TitleKey(saved)).ToArray();
        FocusTarget? replacement=named.Length==1?named[0]:null;
        // Shared hosts and folder windows cannot identify their original app/document
        // by process name alone. Never turn one of them into a different selection.
        if(allowSingleApp&&named.Length==0&&candidates.Length==1&&saved.App.ToLowerInvariant() is not ("applicationframehost" or "wwahost" or "rundll32" or "explorer"))replacement=candidates[0];
        // Reservations cannot make an ambiguous title look unique by hiding
        // another live window. Resolve first, then protect an existing selection.
        return replacement is null||reserved?.Contains(replacement.Key)==true?saved:replacement with{Id=saved.Id};
    }
    internal static bool MatchesForToggle(FocusModeSettings settings,FocusTarget saved,FocusTarget target,IReadOnlyList<FocusTarget> open)
        => saved.Key==target.Key||IsSavedWindow(saved)&&IsSavedWindow(target)
            && Resolve(saved,open,allowSingleApp:!settings.SelectedTargets.Where(IsSavedWindow)
                .Any(other=>SameApplication(saved,other)&&TitleKey(other)!=TitleKey(saved))).Key==target.Key;
    internal static FocusModeSettings Reconnect(FocusModeSettings saved,IReadOnlyList<FocusTarget> open)
    {
        var targets=ResolveSelection(saved,open).Select(p=>p.New).DistinctBy(t=>t.Key).ToImmutableArray();
        return saved with{Target=targets.FirstOrDefault(),Targets=targets};
    }
    private static (FocusTarget Old,FocusTarget New)[] ResolveSelection(FocusModeSettings saved,IReadOnlyList<FocusTarget> open)
    {
        var windows=saved.SelectedTargets.Where(IsSavedWindow).ToArray();
        // A still-live selected window must not be stolen by another bookmark.
        var reserved=open.Where(t=>windows.Any(s=>s.Key==t.Key)).Select(t=>t.Key).ToHashSet();
        return saved.SelectedTargets.Select(t=>(t,Resolve(t,open,reserved,
            !windows.Any(other=>SameApplication(t,other)&&TitleKey(other)!=TitleKey(t))))).ToArray();
    }
    internal void Apply(IReadOnlyList<FocusTarget> open)
    {
        var current=engine.SettingsSnapshot.FocusMode;
        var resolved=ResolveSelection(current,open);
        var targets=resolved.Select(p=>p.New).DistinctBy(t=>t.Key).ToImmutableArray();
        var restored=current with{Target=targets.FirstOrDefault(),Targets=targets};
        if(current.SelectedTargets.SequenceEqual(restored.SelectedTargets))return;
        var replacements=resolved.Where(p=>p.Old.Key!=p.New.Key).ToArray();
        engine.SetFocusMode(restored); // Atomic save; failed persistence creates no aliases.
        foreach(var pair in replacements){
            foreach(var key in aliases.Keys.Where(k=>aliases[k].Key==pair.Old.Key).ToArray())aliases[key]=pair.New;
            aliases[pair.Old.Key]=pair.New;
        }
        while(aliases.Count>4096)aliases.Remove(aliases.Keys.First());
    }
    internal FocusTarget Current(FocusTarget target)=>IsSavedWindow(target)&&aliases.TryGetValue(target.Key,out var replacement)
        ? replacement with{Id=target.Id} : target;
    internal string[] PreviousKeys(FocusTarget target)=>aliases.Where(p=>p.Value.Key==target.Key).Select(p=>p.Key).ToArray();
    internal void Poll()
    {
        var settings=engine.SettingsSnapshot.FocusMode;
        if(!settings.SelectedTargets.Any(IsSavedWindow))return;
        if(listing is {IsCompleted:true}){
            var completed=listing;listing=null;
            if(completed.IsCompletedSuccessfully&&selection==settings.SelectionKey){
                try{Apply(completed.Result);}catch{ /* Preserve the saved selection and retry later. */ }
            }
        }
        if(listing is not null||engine.ElapsedNow<nextRead)return;
        selection=engine.SettingsSnapshot.FocusMode.SelectionKey;
        nextRead=engine.ElapsedNow+2000;
        try{listing=source.ListAsync(FocusTargetKind.Window);}catch{listing=null;}
        // A synchronous source (including an isolated test) needn't wait a tick.
        if(listing is {IsCompleted:true}){
            var completed=listing;listing=null;
            if(completed.IsCompletedSuccessfully&&selection==engine.SettingsSnapshot.FocusMode.SelectionKey){
                try{Apply(completed.Result);}catch{ }
            }
        }
    }
}
