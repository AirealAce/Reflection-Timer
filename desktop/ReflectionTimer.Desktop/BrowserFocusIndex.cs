using System.Drawing;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ReflectionTimer.Core;

namespace ReflectionTimer.Accessible;

// Optional companion metadata. This class never reads UIA, calls native APIs,
// opens a page, or infers a visit from browser history/opener snapshot fields.
internal sealed class BrowserFocusIndex(TimeProvider? clock = null)
{
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private readonly object gate = new();
    private readonly Dictionary<string,Connection> connections = [];
    private readonly Dictionary<string,Binding> bindings = [];
    private Guid? session;
    private string? selection;
    private bool running;
    private bool enabled, tracking;
    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(5);
    private sealed class Connection(string id,string browser,Guid epoch,long seq,long seen)
    {
        internal readonly string Id=id, Browser=browser;
        internal readonly Guid Epoch=epoch;
        internal long Sequence=seq, Seen=seen, SnapshotAt=seen;
        internal bool Readable;
        internal int? CurrentWindow;
        internal Dictionary<int,Window> Windows=[];
        internal Dictionary<int,Tab> Tabs=[];
        internal Dictionary<int,Group> Groups=[];
        internal readonly Dictionary<int,Node> Nodes=[];
    }
    private sealed record Window(int Id,bool Focused,string State,Rectangle Bounds);
    private sealed record Tab(int Id,int WindowId,int Index,bool Active,string Title,string Host,int GroupId);
    private sealed record Group(int Id,int WindowId,string Title);
    private sealed class Node(string host)
    {
        internal string Host=host;
        internal string? FirstHost;
        internal readonly HashSet<string> OwnKeys=[], Hosts=[], Keys=[];
    }
    private sealed record Binding(string Connection,Guid Epoch,int WindowId);
    private sealed record Located(Connection Connection,Window Window,Tab Tab);

    internal bool Connected { get { lock(gate) return enabled&&connections.Values.Any(Fresh); } }
    internal void Configure(FocusModeSettings settings,TimerState timer)
    {
        lock(gate){
            if(!settings.BrowserCompanionEnabled){connections.Clear();bindings.Clear();}
            if(session!=timer.SessionId||selection!=settings.SelectionKey||running!=timer.IsRunning
                ||!settings.BrowserCompanionEnabled||!settings.Enabled||!settings.TargetOnSiteLinks)
                foreach(var connection in connections.Values)foreach(var node in connection.Nodes.Values){node.Hosts.Clear();node.Keys.Clear();node.FirstHost=null;}
            session=timer.SessionId;
            selection=settings.SelectionKey;running=timer.IsRunning;
            enabled=settings.BrowserCompanionEnabled;
            tracking=enabled&&settings.Enabled&&settings.TargetOnSiteLinks&&timer.IsRunning&&timer.SessionId is not null;
        }
    }
    internal void Receive(string connectionId,JsonElement packet)
    {
        lock(gate){
            if(!enabled)return;
            if(string.IsNullOrWhiteSpace(connectionId)||connectionId.Length>128)return;
            try{
                if(packet.ValueKind!=JsonValueKind.Object||packet.GetRawText().Length>1_048_576
                    ||Int(packet,"version",1,1)!=1)return;
                var type=Text(packet,"type",16);var browser=Text(packet,"browser",16);
                if(browser is not ("chrome" or "msedge"))return;
                if(!Guid.TryParseExact(Text(packet,"epoch",36),"D",out var epoch)||epoch==Guid.Empty)return;
                var seq=Long(packet,"seq",0,9_007_199_254_740_991);
                if(type=="hello"){
                    if(connections.TryGetValue(connectionId,out var previous)&&previous.Epoch==epoch){
                        if(previous.Browser!=browser||seq<=previous.Sequence)return;
                        if(Gap(previous,seq))ResetEvidence(previous);
                        previous.Sequence=seq;previous.Seen=time.GetTimestamp();return;
                    }
                    if(!connections.ContainsKey(connectionId)&&connections.Count>=8)return;
                    DisconnectCore(connectionId);
                    connections[connectionId]=new(connectionId,browser,epoch,seq,time.GetTimestamp());return;
                }
                if(!connections.TryGetValue(connectionId,out var connection)||connection.Epoch!=epoch
                    ||connection.Browser!=browser||seq<=connection.Sequence)return;
                if(Gap(connection,seq))ResetEvidence(connection);
                if(type=="snapshot")Snapshot(connection,packet);
                else if(type=="navigation")Navigation(connection,packet);
                else throw new FormatException();
                connection.Sequence=seq;connection.Seen=time.GetTimestamp();
            }catch(Exception error)when(error is JsonException or FormatException or InvalidOperationException or ArgumentException or OverflowException or KeyNotFoundException){
                if(connections.TryGetValue(connectionId,out var failed))failed.Readable=false;
            }
        }
    }
    internal void Disconnect(string connectionId){lock(gate)DisconnectCore(connectionId);}
    internal void DisconnectAll(){lock(gate){connections.Clear();bindings.Clear();}}
    private void DisconnectCore(string id)
    {
        connections.Remove(id);
        foreach(var key in bindings.Where(pair=>pair.Value.Connection==id).Select(pair=>pair.Key).ToArray())bindings.Remove(key);
    }
    private bool Fresh(Connection connection)=>time.GetElapsedTime(connection.Seen)<=Lifetime;
    private bool Readable(Connection connection)=>connection.Readable&&Fresh(connection)&&time.GetElapsedTime(connection.SnapshotAt)<=Lifetime;
    private bool Gap(Connection connection,long next)=>next!=connection.Sequence+1||!Fresh(connection)
        ||connection.Readable&&time.GetElapsedTime(connection.SnapshotAt)>Lifetime;
    private void ResetEvidence(Connection connection)
    {
        connection.Nodes.Clear();connection.Readable=false;
        foreach(var key in bindings.Where(pair=>pair.Value.Connection==connection.Id).Select(pair=>pair.Key).ToArray())bindings.Remove(key);
    }

    internal IReadOnlyList<FocusTarget> ListSites()
    {
        lock(gate)return (enabled?connections.Values.Where(Readable):Enumerable.Empty<Connection>()).SelectMany(connection=>connection.Tabs.Values
            .Where(tab=>tab.Host.Length>0).Select(tab=>(tab.Host,connection.Browser)))
            .GroupBy(item=>item.Host).OrderBy(group=>group.Key,StringComparer.Ordinal)
            .Select(group=>new FocusTarget(SiteId(group.Key),FocusTargetKind.Site,group.Key,
                string.Join(", ",group.Select(item=>item.Browser).Distinct().Order()),0,0,0){SiteHost=group.Key}).ToArray();
    }
    internal bool IsCurrentSite(FocusTarget target)
    {
        lock(gate)return enabled&&target.Kind==FocusTargetKind.Site&&!target.UseFocused&&connections.Values.Where(Readable).Any(connection=>{
            var current=connection.CurrentWindow??UniqueFocused(connection);
            return current is {} window&&Active(connection,window) is {} tab&&tab.Host.Length>0&&FocusSites.Matches(target.SiteHost,tab.Host);
        });
    }
    internal FocusTarget? CaptureSite(FocusTarget nativeWindow,Rectangle? bounds=null)
    {
        lock(gate){
            var located=Locate(nativeWindow,bounds);
            if(located is null||located.Tab.Host.Length==0)return null;
            BindOwnWindow(located,nativeWindow);
            return nativeWindow with{Id=SiteId(located.Tab.Host),Kind=FocusTargetKind.Site,Name=located.Tab.Host,
                SiteHost=located.Tab.Host,TabRuntimeId="",TabPosition=0,UseFocused=false};
        }
    }
    internal bool CanLocate(FocusTarget nativeWindow,Rectangle bounds)
    {
        lock(gate)return enabled&&Locate(nativeWindow,bounds) is not null;
    }
    internal void BindWindow(FocusTarget nativeWindow,IReadOnlyList<BrowserTabSlot> slots,Rectangle? bounds=null)
    {
        lock(gate){
            bindings.Remove(nativeWindow.Key);
            var identity=$":{nativeWindow.ProcessId}:{nativeWindow.ProcessStartedAt}:{nativeWindow.WindowHandle}:";
            foreach(var connection in connections.Values)foreach(var node in connection.Nodes.Values)
                node.OwnKeys.RemoveWhere(key=>key=="0"+identity||key.StartsWith("1"+identity,StringComparison.Ordinal)
                    ||key.StartsWith("2"+identity,StringComparison.Ordinal));
            var located=Locate(nativeWindow,bounds);
            if(located is null)return;
            BindOwnWindow(located,nativeWindow);
            if(slots.Count>4096||slots.Any(slot=>slot.Id.Length is 0 or >512||slot.Name.Length>2048))return;
            var nativeTabs=slots.Where(slot=>!slot.GroupHeader).ToArray();
            var tabs=located.Connection.Tabs.Values.Where(tab=>tab.WindowId==located.Window.Id).OrderBy(tab=>tab.Index).ToArray();
            if(nativeTabs.Length!=tabs.Length||nativeTabs.Select(slot=>slot.Id).Distinct().Count()!=nativeTabs.Length
                ||tabs.Where((tab,index)=>tab.Index!=index||!TabTitle(nativeTabs[index].Name,tab.Title)).Any())return;
            var map=new Dictionary<string,Tab>();
            for(var index=0;index<tabs.Length;index++){
                map[nativeTabs[index].Id]=tabs[index];
                NodeFor(located.Connection,tabs[index]).OwnKeys.Add((nativeWindow with{
                    Kind=FocusTargetKind.BrowserTab,TabRuntimeId=nativeTabs[index].Id}).Key);
            }
            IReadOnlyList<BrowserTabGroup> nativeGroups;
            try{nativeGroups=BrowserTabGroups.Read(slots);}catch(System.Text.RegularExpressions.RegexMatchTimeoutException){return;}
            foreach(var group in nativeGroups){
                var members=group.Members.Where(member=>map.ContainsKey(member.Id)).Select(member=>map[member.Id]).ToArray();
                if(members.Length==0||members.Length!=group.Members.Count||members.Any(tab=>tab.GroupId<0))continue;
                var ids=members.Select(tab=>tab.GroupId).Distinct().ToArray();
                if(ids.Length!=1||!located.Connection.Groups.TryGetValue(ids[0],out var remote)
                    ||(remote.Title.Length==0?"Unnamed group":remote.Title)!=group.Name)continue;
                var remoteMembers=tabs.Where(tab=>tab.GroupId==remote.Id).Select(tab=>tab.Id).ToHashSet();
                if(!remoteMembers.SetEquals(members.Select(tab=>tab.Id)))continue;
                var key=(nativeWindow with{Kind=FocusTargetKind.BrowserTabGroup,TabRuntimeId=group.Id}).Key;
                foreach(var member in members)NodeFor(located.Connection,member).OwnKeys.Add(key);
            }
        }
    }
    internal FocusPresence Check(IReadOnlyList<FocusTarget> targets,FocusTarget? foreground,bool targetOnSiteLinks,Rectangle? bounds=null)
    {
        lock(gate){
            if(!enabled)return FocusPresence.Unavailable;
            var sites=targets.Where(target=>target.Kind==FocusTargetKind.Site&&!target.UseFocused).ToArray();
            var anySite=sites.Length>0;
            if(!connections.Values.Any(Readable))return anySite?FocusPresence.Unknown:FocusPresence.Unavailable;
            if(foreground is null)return anySite?FocusPresence.Unknown:FocusPresence.Unavailable;
            if(Browser(foreground.App) is null)return anySite?FocusPresence.Away:FocusPresence.Unavailable;
            var located=Locate(foreground,bounds);
            if(located is null)return FocusPresence.Unknown;
            if(located.Tab.Host.Length==0)return anySite?FocusPresence.Unknown:FocusPresence.Unavailable;
            BindOwnWindow(located,foreground);
            if(sites.Any(site=>FocusSites.Matches(site.SiteHost,located.Tab.Host)))return FocusPresence.Focused;
            if(targetOnSiteLinks&&tracking&&located.Connection.Nodes.TryGetValue(located.Tab.Id,out var node)
                &&(node.FirstHost is null||node.FirstHost.Length>0&&node.Host==node.FirstHost)
                &&targets.Any(target=>!target.UseFocused&&(target.Kind==FocusTargetKind.Site
                    ?node.Hosts.Any(host=>FocusSites.Matches(target.SiteHost,host)):node.Keys.Contains(target.Key))))
                return FocusPresence.Focused;
            return anySite?FocusPresence.Away:FocusPresence.Unavailable;
        }
    }
    private void BindOwnWindow(Located located,FocusTarget native)
    {
        bindings[native.Key]=new(located.Connection.Id,located.Connection.Epoch,located.Window.Id);
        foreach(var tab in located.Connection.Tabs.Values.Where(tab=>tab.WindowId==located.Window.Id))
            NodeFor(located.Connection,tab).OwnKeys.Add(native.Key);
    }
    private Located? Locate(FocusTarget native,Rectangle? bounds)
    {
        if(!enabled||native.Kind!=FocusTargetKind.Window||native.UseFocused)return null;
        var browser=Browser(native.App);if(browser is null)return null;
        var caption=WindowTitle(native.WindowName.Length>0?native.WindowName:native.Name);
        var candidates=connections.Values.Where(connection=>Readable(connection)&&connection.Browser==browser)
            .SelectMany(connection=>connection.Windows.Values.Where(window=>window.State!="minimized")
                .Select(window=>(connection,window,tab:Active(connection,window.Id))))
            .Where(item=>item.tab is not null&&item.tab.Title.Length>0&&caption==item.tab.Title)
            .Select(item=>new Located(item.connection,item.window,item.tab!)).ToArray();
        if(bounds is {} rectangle)candidates=candidates.Where(candidate=>Near(rectangle,candidate.Window.Bounds)).ToArray();
        return candidates.Length==1?candidates[0]:null;
    }
    private static bool Near(Rectangle a,Rectangle b)=>Math.Abs((long)a.X-b.X)<=20&&Math.Abs((long)a.Y-b.Y)<=20
        &&Math.Abs((long)a.Width-b.Width)<=24&&Math.Abs((long)a.Height-b.Height)<=24;
    private static string? Browser(string app)=>app.ToLowerInvariant() switch{"chrome"=>"chrome","msedge"=>"msedge",_=>null};
    private static string WindowTitle(string title)
    {
        foreach(var suffix in new[]{" - Google Chrome"," — Google Chrome"," - Microsoft Edge"," — Microsoft Edge"})
            if(title.EndsWith(suffix,StringComparison.Ordinal))return title[..^suffix.Length];
        return title;
    }
    private static bool TabTitle(string label,string title)
    {
        if(title.Length==0)return false;
        return label==title||label.StartsWith(title+" - Part of group ",StringComparison.Ordinal)
            ||label.StartsWith(title+" - Part of unnamed group",StringComparison.Ordinal)
            ||label.StartsWith(title+" - Memory usage",StringComparison.Ordinal)
            ||label.StartsWith(title+" - High memory usage",StringComparison.Ordinal);
    }
    private static int? UniqueFocused(Connection connection)
    {
        var focused=connection.Windows.Values.Where(window=>window.Focused).ToArray();
        return focused.Length==1?focused[0].Id:null;
    }
    private static Tab? Active(Connection connection,int window)
    {
        var active=connection.Tabs.Values.Where(tab=>tab.WindowId==window&&tab.Active).ToArray();
        return active.Length==1?active[0]:null;
    }
    private static Guid SiteId(string host)=>new(SHA256.HashData(Encoding.UTF8.GetBytes("ReflectionTimer.Site:"+host)).AsSpan(0,16));
    private static Node NodeFor(Connection connection,Tab tab)
    {
        if(!connection.Nodes.TryGetValue(tab.Id,out var node))connection.Nodes[tab.Id]=node=new(tab.Host);
        return node;
    }
    private void Snapshot(Connection connection,JsonElement packet)
    {
        var windows=new Dictionary<int,Window>();var tabs=new Dictionary<int,Tab>();var groups=new Dictionary<int,Group>();
        foreach(var item in Array(packet,"windows",128)){
            var id=Int(item,"id",0,int.MaxValue);var state=Text(item,"state",24);
            if(state is not ("normal" or "minimized" or "maximized" or "fullscreen" or "locked-fullscreen"))throw new FormatException();
            windows.Add(id,new(id,Bool(item,"focused"),state,new(
                Int(item,"left",-131072,131072),Int(item,"top",-131072,131072),
                Int(item,"width",1,32768),Int(item,"height",1,32768))));
        }
        foreach(var item in Array(packet,"groups",512)){
            var id=Int(item,"id",0,int.MaxValue);var window=Int(item,"windowId",0,int.MaxValue);
            if(!windows.ContainsKey(window))throw new FormatException();
            groups.Add(id,new(id,window,Text(item,"title",2048)));
        }
        foreach(var item in Array(packet,"tabs",4096)){
            var id=Int(item,"id",0,int.MaxValue);var window=Int(item,"windowId",0,int.MaxValue);
            var group=OptionalInt(item,"groupId",-1,int.MaxValue)??-1;
            if(!windows.ContainsKey(window)||group>=0&&(!groups.TryGetValue(group,out var membership)||membership.WindowId!=window))throw new FormatException();
            _=OptionalInt(item,"openerTabId",-1,int.MaxValue); // Validate only; never a provenance signal.
            tabs.Add(id,new(id,window,Int(item,"index",0,4095),Bool(item,"active"),Text(item,"title",2048),Host(item,"siteHost"),group));
        }
        if(tabs.Values.GroupBy(tab=>tab.WindowId).Any(group=>group.Count(tab=>tab.Active)>1
            ||group.Select(tab=>tab.Index).Distinct().Count()!=group.Count()))throw new FormatException();
        var current=OptionalInt(packet,"currentWindowId",-1,int.MaxValue);
        if(current>=0&&!windows.ContainsKey(current.Value))throw new FormatException();
        var overflow=packet.TryGetProperty("overflow",out _)?Bool(packet,"overflow"):false;
        foreach(var tab in tabs.Values){
            if(connection.Tabs.TryGetValue(tab.Id,out var previousTab)&&connection.Nodes.TryGetValue(tab.Id,out var previousNode)){
                if(previousTab.WindowId!=tab.WindowId)previousNode.OwnKeys.Clear();
                else if(previousTab.GroupId!=tab.GroupId)previousNode.OwnKeys.RemoveWhere(key=>key.StartsWith("2:",StringComparison.Ordinal));
            }
            if(connection.Nodes.TryGetValue(tab.Id,out var prior)&&prior.Host!=tab.Host){
                if(prior.FirstHost is not null)prior.Host=tab.Host;
                else{var fresh=new Node(tab.Host);fresh.OwnKeys.UnionWith(prior.OwnKeys);connection.Nodes[tab.Id]=fresh;}
            }else NodeFor(connection,tab);
        }
        // Descendants own their ancestor evidence; removing a source cannot
        // erase it, and stale current tabs cannot leak back into later sessions.
        foreach(var id in connection.Nodes.Keys.Where(id=>connection.Tabs.ContainsKey(id)&&!tabs.ContainsKey(id)).ToArray())connection.Nodes.Remove(id);
        connection.Windows=windows;connection.Tabs=tabs;connection.Groups=groups;
        connection.CurrentWindow=current>=0?current:null;
        connection.Readable=!overflow;connection.SnapshotAt=time.GetTimestamp();
    }
    private void Navigation(Connection connection,JsonElement packet)
    {
        var kind=Text(packet,"kind",16);var id=Int(packet,"tabId",0,int.MaxValue);
        var source=OptionalInt(packet,"sourceTabId",0,int.MaxValue);
        var replaced=OptionalInt(packet,"replacedTabId",0,int.MaxValue);
        var previous=Host(packet,"previousSiteHost");var host=Host(packet,"siteHost");
        var transition=Text(packet,"transitionType",40);
        var qualifiers=Array(packet,"qualifiers",16).Select(value=>String(value,40)).ToArray();
        if(kind=="removed"){connection.Tabs.Remove(id);connection.Nodes.Remove(id);return;}
        if(kind=="replaced"){
            if(replaced is not {} old)throw new FormatException();
            connection.Nodes.TryGetValue(old,out var prior);
            connection.Nodes.TryGetValue(id,out var existing);
            Node next;
            if(tracking&&Readable(connection)&&host.Length>0&&existing is not null&&existing.Host==host)next=existing;
            else if(tracking&&Readable(connection)&&host.Length>0&&prior is not null&&previous==prior.Host&&host==prior.Host)
                next=Descendant(prior,host);
            else next=new(host);
            if(prior is not null)next.OwnKeys.UnionWith(prior.OwnKeys);
            connection.Nodes.Remove(old);connection.Tabs.Remove(old);connection.Nodes[id]=next;
            if(connection.Tabs.TryGetValue(id,out var replacement))connection.Tabs[id]=replacement with{Host=host};
            return;
        }
        if(kind is not ("created" or "committed"))throw new FormatException();
        connection.Nodes.TryGetValue(kind=="created"?source??-1:id,out var parent);
        var redirects=qualifiers.Any(qualifier=>qualifier is "server_redirect" or "client_redirect");
        var firstCommit=kind=="committed"&&parent?.FirstHost is {} expected&&(previous.Length==0||previous==expected)
            &&(expected.Length==0||expected==host||redirects);
        var samePrior=parent is not null&&(parent.Host.Length>0&&previous==parent.Host||firstCommit);
        var independent=transition is "typed" or "auto_bookmark" or "generated" or "keyword" or "keyword_generated" or "start_page"
            ||qualifiers.Any(qualifier=>qualifier is "from_address_bar" or "forward_back");
        var follows=kind=="created"&&source is not null||transition is "link" or "form_submit"
            ||!independent&&redirects;
        Node node;
        if(tracking&&Readable(connection)&&samePrior&&follows&&!independent)node=Descendant(parent!,host);
        else if(tracking&&Readable(connection)&&samePrior&&transition=="reload"){
            node=new(host);node.Hosts.UnionWith(parent!.Hosts);node.Keys.UnionWith(parent.Keys);
        }else node=new(host);
        if(kind=="created"&&tracking&&Readable(connection)&&samePrior&&follows&&!independent)node.FirstHost=host;
        if(kind=="committed"&&parent is not null)node.OwnKeys.UnionWith(parent.OwnKeys);
        connection.Nodes[id]=node;
        if(connection.Tabs.TryGetValue(id,out var tab))connection.Tabs[id]=tab with{Host=host};
        if(connection.Nodes.Count>8192){connection.Nodes.Clear();connection.Readable=false;}
    }
    private static Node Descendant(Node parent,string host)
    {
        var node=new Node(host);
        node.Hosts.UnionWith(parent.Hosts);if(parent.Host.Length>0)node.Hosts.Add(parent.Host);
        node.Keys.UnionWith(parent.Keys);node.Keys.UnionWith(parent.OwnKeys);
        if(node.Hosts.Count>512||node.Keys.Count>2048){node.Hosts.Clear();node.Keys.Clear();}
        return node;
    }
    private static string Host(JsonElement packet,string name)
    {
        var value=Text(packet,name,512);
        if(value.Length==0)return "";
        return FocusSites.TryCanonicalHost(value,out var canonical)?canonical:throw new FormatException();
    }
    private static string Text(JsonElement packet,string name,int max)=>String(packet.GetProperty(name),max);
    private static string String(JsonElement value,int max)
    {
        if(value.ValueKind!=JsonValueKind.String)throw new FormatException();
        var text=value.GetString()!;
        return text.Length<=max&&!text.Any(char.IsControl)?text:throw new FormatException();
    }
    private static long Long(JsonElement packet,string name,long min,long max)
    {
        var value=packet.GetProperty(name);
        if(value.ValueKind!=JsonValueKind.Number||!value.TryGetInt64(out var number)||number<min||number>max)throw new FormatException();
        return number;
    }
    private static int Int(JsonElement packet,string name,int min,int max)=>(int)Long(packet,name,min,max);
    private static int? OptionalInt(JsonElement packet,string name,int min,int max)=>!packet.TryGetProperty(name,out var value)
        ||value.ValueKind==JsonValueKind.Null?null:Int(packet,name,min,max);
    private static bool Bool(JsonElement packet,string name)=>packet.GetProperty(name).ValueKind switch{
        JsonValueKind.True=>true,JsonValueKind.False=>false,_=>throw new FormatException()};
    private static JsonElement.ArrayEnumerator Array(JsonElement packet,string name,int max)
    {
        var array=packet.GetProperty(name);
        if(array.ValueKind!=JsonValueKind.Array||array.GetArrayLength()>max)throw new FormatException();
        return array.EnumerateArray();
    }
}
