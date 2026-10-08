using ReflectionTimer.Core;

namespace ReflectionTimer.Accessible;

internal sealed partial class WindowsFocusTargets
{
    private readonly object siteGate=new();
    private readonly NativeSiteAnchors siteAnchors=new();
    private string? currentSite;
    private Task? warmingSites;

    public void Configure(FocusModeSettings settings,TimerState timer)
    {
        browserSites?.Configure(settings,timer);
        var previous=siteAnchors.Generation;
        var token=siteAnchors.Configure(settings,timer);
        if(token!=previous&&timer.IsRunning&&settings.Enabled&&settings.TargetOnSiteLinks)
            WarmSiteTargets(settings.SelectedTargets,true,token);
    }
    private static Rectangle? Bounds(FocusTarget target)=>BrowserWindowBounds.Read((nint)target.WindowHandle);

    private void BindBrowserWindow(FocusTarget window,IReadOnlyList<BrowserTabSlot> slots)
    {
        // A unique title alone cannot identify a browser/profile window.
        if(browserSites?.Connected==true&&Bounds(window) is {} bounds)browserSites.BindWindow(window,slots,bounds);
    }

    private IReadOnlyList<FocusTarget> ListSites()
    {
        var available=new List<FocusTarget>();string? first=null;
        foreach(var window in OpenWindows().Where(IsBrowser)){
            try{
                var host=NativeBrowserSites.Read((nint)window.WindowHandle);
                if(host is null)continue;
                first??=host;
                available.Add(new(Guid.Empty,FocusTargetKind.Site,host,window.App,0,0,0){SiteHost=host});
            }catch{ /* An inaccessible provider never turns a title into a website. */ }
        }
        if(browserSites?.Connected==true)available.AddRange(browserSites.ListSites());
        Volatile.Write(ref currentSite,first);
        return available.GroupBy(t=>t.Key).Select(g=>g.First() with{App=string.Join(", ",g.Select(t=>t.App).Where(a=>a.Length>0).Distinct(StringComparer.OrdinalIgnoreCase))}).ToArray();
    }
    private FocusTarget? CaptureSite(FocusTarget window)
    {
        if(!IsBrowser(window))return null;
        var fromCompanion=browserSites?.Connected==true&&Bounds(window) is {} bounds?browserSites.CaptureSite(window,bounds):null;
        if(fromCompanion is not null)return fromCompanion;
        var host=NativeBrowserSites.Read((nint)window.WindowHandle);
        return host is null?null:new(Guid.Empty,FocusTargetKind.Site,host,window.App,0,0,0){SiteHost=host};
    }
    private void WarmSiteTargets(IReadOnlyList<FocusTarget> targets,bool links,long token)
    {
        lock(siteGate){
            if(!links||warmingSites is {IsCompleted:false})return;
            warmingSites=Enqueue(()=>{
                WarmSiteTargetsNow(targets,token);
                return true;
            });
        }
    }
    private void WarmSiteTargetsNow(IReadOnlyList<FocusTarget> targets,long token)
    {
        var probes=new Dictionary<(long,int,long),BrowserProbe>();
        foreach(var target in targets.Where(t=>!t.UseFocused&&!t.CaptureUnknown&&t.Kind!=FocusTargetKind.Site&&IsBrowser(t))){
            try{
                if(browserSites?.Connected!=true&&!siteAnchors.CanRead(token,target.Key))continue;
                if(CheckWindow(target)==FocusPresence.Unavailable)continue;
                var window=ReadWindow((nint)target.WindowHandle);
                if(window is null)continue;
                var owned=target.Kind==FocusTargetKind.Window||CheckBrowser(target,probes)==FocusPresence.Focused;
                IReadOnlyList<BrowserTabSlot> slots=[];
                if(browserSites?.Connected==true){
                    var probeKey=(target.WindowHandle,target.ProcessId,target.ProcessStartedAt);
                    if(!probes.TryGetValue(probeKey,out var probe))probes[probeKey]=probe=new(BrowserStrip((nint)target.WindowHandle));
                    slots=probe.Slots.Select(s=>s.Data).ToArray();
                    BindBrowserWindow(window,slots);
                }
                if(!owned||!siteAnchors.CanRead(token,target.Key))continue;
                var host=NativeBrowserSites.Read((nint)target.WindowHandle);
                if(host is null)continue;
                siteAnchors.Record(token,target.Key,host);
            }catch{ }
        }
    }
    private FocusPresence CheckSiteTargets(IReadOnlyList<FocusTarget> targets,bool links)
    {
        var staticSites=targets.Where(t=>!t.UseFocused&&t.Kind==FocusTargetKind.Site).ToArray();
        var foreground=CaptureForeground();
        var companionAvailable=false;
        if(browserSites?.Connected==true&&foreground is not null&&Bounds(foreground) is {} bounds&&browserSites.CanLocate(foreground,bounds)){
            companionAvailable=true;
            var linked=browserSites.Check(targets,foreground,links,bounds);
            if(linked==FocusPresence.Focused)return linked;
        }
        var hosts=links&&!companionAvailable?siteAnchors.Hosts(targets):[];
        if(staticSites.Length==0&&hosts.Length==0)return FocusPresence.Unavailable;
        if(foreground is null)return FocusPresence.Unknown;
        if(!IsBrowser(foreground))return FocusPresence.Away;
        var site=CaptureSite(foreground);
        if(site is null)return FocusPresence.Unknown;
        return staticSites.Any(t=>FocusSites.Matches(t.SiteHost,site.SiteHost))
            ||hosts.Any(host=>FocusSites.Matches(host,site.SiteHost))?FocusPresence.Focused:FocusPresence.Away;
    }
    public Task<FocusPresence> CheckAnyAsync(IReadOnlyList<FocusTarget> targets,bool targetOnSiteLinks)
    {
        var token=siteAnchors.Generation;
        if(targets.Any(t=>!t.UseFocused&&!t.CaptureUnknown&&t.Kind==FocusTargetKind.Window&&CheckWindow(t)==FocusPresence.Focused)){
            WarmSiteTargets(targets,targetOnSiteLinks,token);return Task.FromResult(FocusPresence.Focused);
        }
        if(!targets.Any(t=>t.Kind==FocusTargetKind.Site)&&(!targetOnSiteLinks||!targets.Any(IsBrowser)))return CheckNativeTargetsAsync(targets);
        return Enqueue(()=>{
            var probes=new Dictionary<(long,int,long),BrowserProbe>();var away=false;var unknown=false;
            foreach(var target in targets.Where(t=>t.Kind!=FocusTargetKind.Site)){
                var presence=target.UseFocused?FocusPresence.Unknown:target.CaptureUnknown
                    ?CheckWindow(target)==FocusPresence.Unavailable?FocusPresence.Unavailable:FocusPresence.Unknown
                    :target.Kind==FocusTargetKind.Window?CheckWindow(target):CheckBrowser(target,probes);
                if(presence==FocusPresence.Focused){WarmSiteTargets(targets,targetOnSiteLinks,token);return presence;}
                away|=presence==FocusPresence.Away;unknown|=presence==FocusPresence.Unknown;
            }
            if(targetOnSiteLinks)WarmSiteTargetsNow(targets,token);
            var sites=CheckSiteTargets(targets,targetOnSiteLinks);
            if(sites==FocusPresence.Focused)return sites;
            away|=sites==FocusPresence.Away;unknown|=sites==FocusPresence.Unknown;
            return unknown?FocusPresence.Unknown:away?FocusPresence.Away:FocusPresence.Unavailable;
        });
    }
}
