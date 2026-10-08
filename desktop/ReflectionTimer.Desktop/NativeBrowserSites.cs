using System.Windows.Automation;
using ReflectionTimer.Core;

namespace ReflectionTimer.Accessible;

internal sealed record BrowserDocumentAddress(string Address, bool Offscreen, double Area);
internal sealed record BrowserSiteListing(string? Current,IReadOnlyList<string> Hosts);

// Read document metadata, never its text, links, fields, or descendants. The
// address bar can contain an uncommitted edit and is deliberately not used.
internal static class NativeBrowserSites
{
    internal static BrowserSiteListing Listing(IReadOnlyList<BrowserDocumentAddress> documents)
    {
        // Background documents may have empty/offscreen bounds. Their committed
        // URL metadata is still useful for the chooser, but never for deciding
        // which website is currently focused. Do not cache URLs between reads:
        // a background tab can navigate without changing its accessible title.
        var hosts=new List<string>();
        var seen=new HashSet<string>(StringComparer.Ordinal);
        foreach(var document in documents)
            if(TryCommittedHost(document.Address,out var host)&&seen.Add(host))hosts.Add(host);
        return new(Choose(documents),hosts);
    }
    internal static string? Choose(IReadOnlyList<BrowserDocumentAddress> documents)
    {
        var visible=documents.Where(d=>!d.Offscreen&&d.Area>0&&double.IsFinite(d.Area))
            .OrderByDescending(d=>d.Area).ToArray();
        if(visible.Length==0)return null;
        var largest=visible[0];
        if(!TryCommittedHost(largest.Address,out var host))return null;
        if(visible.Skip(1).Any(d=>d.Area>=largest.Area*.9
            &&(!TryCommittedHost(d.Address,out var other)||other!=host)))return null;
        return host;
    }
    internal static bool TryCommittedHost(string address,out string host)
    {
        host="";
        return (address.StartsWith("https://",StringComparison.OrdinalIgnoreCase)
            ||address.StartsWith("http://",StringComparison.OrdinalIgnoreCase))
            &&FocusSites.TryCanonicalHost(address,out host);
    }
    internal static string? Read(nint handle)=>ReadListing(handle).Current;
    internal static BrowserSiteListing ReadListing(nint handle)
    {
        var documents=new List<BrowserDocumentAddress>();
        var walker=TreeWalker.ControlViewWalker;
        var pending=new Queue<(AutomationElement Element,int Depth)>();
        pending.Enqueue((AutomationElement.FromHandle(handle),0));
        var seen=new HashSet<string>();
        for(var visited=0;pending.Count>0&&visited<600;visited++){
            var (element,depth)=pending.Dequeue();
            if(!seen.Add(string.Join(",",element.GetRuntimeId())))continue;
            var current=element.Current;
            if(current.ControlType==ControlType.Document){
                var address="";
                if(element.TryGetCurrentPattern(ValuePattern.Pattern,out var value))address=((ValuePattern)value).Current.Value;
                var rect=current.BoundingRectangle;
                documents.Add(new(address,current.IsOffscreen,rect.IsEmpty?0:rect.Width*rect.Height));
                continue;
            }
            if(depth>=12||current.ControlType==ControlType.TabItem||current.ControlType==ControlType.Edit)continue;
            for(var child=walker.GetFirstChild(element);child is not null&&pending.Count<600;child=walker.GetNextSibling(child))pending.Enqueue((child,depth+1));
        }
        return Listing(documents);
    }
}
