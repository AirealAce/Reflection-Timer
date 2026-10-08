using System.Drawing;
using System.Text.Json;
using ReflectionTimer.Accessible;
using ReflectionTimer.Core;

internal static class BrowserSiteTrackingTests
{
    internal static void Run(Action<bool,string> check)
    {
        var site=Site("study.example");var source=Native("Study",100);
        using(var test=new Fixture()){
            check(test.Index.Connected&&test.Index.ListSites().Single().SiteHost=="study.example",
                "The optional companion exposes canonical host choices from a fresh authenticated connection");
            var first=test.Index.ListSites().Single();
            test.Snapshot([new(1,10,0,true,"Study","www.study.example")]);
            check(test.Index.ListSites().Single().Id==first.Id&&test.Index.IsCurrentSite(site),
                "Canonical website choice identity and current marker remain stable across snapshots");
            check(test.Index.CaptureSite(source) is {SiteHost:"study.example",Kind:FocusTargetKind.Site}
                &&test.Index.Check([site],source,true)==FocusPresence.Focused,
                "Dynamic site capture binds the native initiating browser window to its exact active metadata page");
            check(test.Index.CanLocate(source,Box(0))&&!test.Index.CanLocate(source,Box(3000))
                &&!test.Index.CanLocate(Native("Study",100,"firefox"),Box(0)),
                "Companion availability applies only to its exactly matched native browser window, retaining native fallback elsewhere");
            check(test.Index.Check([site],Native("Editor",900,"editor"),true)==FocusPresence.Away
                &&test.Index.Check([source],Native("Editor",900,"editor"),true)==FocusPresence.Unavailable,
                "A background website reports Away without replacing native-only focus decisions");
            check(test.Index.CaptureSite(Native("Unrelated page",101)) is null,
                "The native page title must match the companion active page before site capture");
        }
        using(var test=new Fixture()){
            test.Bind(source);
            test.Event("created",2,1,"study.example","outside.example");
            test.Snapshot([new(1,10,0,true,"Study","study.example"),new(2,20,0,true,"Outside","outside.example")],
                [new(10,false,Box(0)),new(20,true,Box(1000))],current:20);
            var outside=Native("Outside",200);var sourceTab=source with{Kind=FocusTargetKind.BrowserTab,TabRuntimeId="tab-runtime-1"};
            check(test.Index.Check([site],outside,true)==FocusPresence.Focused,
                "A verified link-created external tab in a new browser window inherits its source website target");
            check(test.Index.Check([source],outside,true)==FocusPresence.Focused
                &&test.Index.Check([sourceTab],outside,true)==FocusPresence.Focused,
                "External link descendants retain verified native source window and tab target identities");
            check(test.Index.Check([sourceTab with{TabRuntimeId="different-runtime"}],outside,true)==FocusPresence.Unavailable,
                "A similarly titled native tab with a different runtime ID cannot grant inherited focus");
            test.Event("created",3,2,"outside.example","chain.example");
            test.Snapshot([new(1,10,0,true,"Study","study.example"),new(2,20,0,false,"Outside","outside.example"),
                new(3,20,1,true,"Chain","chain.example")],[new(10,false,Box(0)),new(20,true,Box(1000))],current:20);
            check(test.Index.Check([site],Native("Chain",200),true)==FocusPresence.Focused,
                "A chain of verified external links retains the original targeted website");
            test.Event("removed",1,null,"study.example","");
            test.Snapshot([new(2,20,0,false,"Outside","outside.example"),new(3,20,1,true,"Chain","chain.example")],
                [new(20,true,Box(1000))],current:20);
            check(test.Index.Check([site],Native("Chain",200),true)==FocusPresence.Focused
                &&test.Index.Check([source],Native("Chain",200),true)==FocusPresence.Focused,
                "Closing a source window preserves existing descendant host and native-key provenance");
            test.Event("replaced",4,null,"chain.example","chain.example",replaced:3);
            test.Snapshot([new(2,20,0,false,"Outside","outside.example"),new(4,20,1,true,"Replacement","chain.example")],
                [new(20,true,Box(1000))],current:20);
            check(test.Index.Check([site],Native("Replacement",200),true)==FocusPresence.Focused,
                "A browser-attested tab replacement transfers inherited target provenance");
            test.Configure(session:Guid.NewGuid());
            check(test.Index.Check([site],Native("Replacement",200),true)==FocusPresence.Away,
                "A new timer or stopwatch session cannot inherit the previous session's link graph");
        }
        using(var test=new Fixture()){
            test.Bind(source);
            test.Event("committed",1,null,"study.example","outside.example","link");
            test.Snapshot([new(1,10,0,true,"Outside","outside.example")]);
            var outside=Native("Outside",100);
            check(test.Index.Check([site],outside,true)==FocusPresence.Focused,
                "A verified same-tab external link inherits the previous website target");
            test.Event("committed",1,null,"outside.example","outside.example","reload");
            check(test.Index.Check([site],outside,true)==FocusPresence.Focused,
                "Reloading an inherited page preserves its existing provenance");
            test.Event("committed",1,null,"outside.example","form.example","form_submit");
            test.Snapshot([new(1,10,0,true,"Form","form.example")]);
            check(test.Index.Check([site],Native("Form",100),true)==FocusPresence.Focused,
                "A verified form submission continues an inherited page chain");
            test.Event("committed",1,null,"form.example","redirect.example","link",qualifiers:["server_redirect"]);
            test.Snapshot([new(1,10,0,true,"Redirect","redirect.example")]);
            check(test.Index.Check([site],Native("Redirect",100),true)==FocusPresence.Focused,
                "A redirect attached to a verified link keeps its inherited provenance");
            test.Event("committed",1,null,"redirect.example","typed.example","typed",qualifiers:["server_redirect"]);
            test.Snapshot([new(1,10,0,true,"Typed","typed.example")]);
            check(test.Index.Check([site],Native("Typed",100),true)==FocusPresence.Away,
                "Typed navigation clears lineage even if that independent navigation subsequently redirects");
        }
        using(var test=new Fixture()){
            test.Event("created",2,1,"study.example","outside.example");
            test.Snapshot([new(1,10,0,false,"Study","study.example"),new(2,10,1,true,"Pending","")]);
            check(test.Index.Check([site],Native("Pending",100),true)==FocusPresence.Unknown,
                "A pending linked tab with no committed website remains unknown while retaining its attested source evidence");
            test.Event("committed",2,null,"outside.example","outside.example","link");
            test.Event("committed",2,null,"outside.example","redirect.example","link",qualifiers:["server_redirect"]);
            test.Snapshot([new(2,10,0,true,"Redirect","redirect.example")]);
            check(test.Index.Check([site],Native("Redirect",100),true)==FocusPresence.Focused,
                "Created, pending snapshot, first commit and redirect preserve verified external-link provenance");
        }
        using(var test=new Fixture()){
            test.Event("created",2,1,"study.example","");
            test.Snapshot([new(2,10,0,true,"Pending","")]);
            test.Event("committed",2,null,"","outside.example","link");
            test.Snapshot([new(2,10,0,true,"Outside","outside.example")]);
            check(test.Index.Check([site],Native("Outside",100),true)==FocusPresence.Focused,
                "A browser-attested blank popup can carry its source provenance into its first committed external page");
        }
        using(var test=new Fixture()){
            test.Event("created",2,1,"study.example","outside.example");
            test.Event("committed",3,null,"","typed.example","typed");
            test.Snapshot([new(2,10,0,false,"Outside","outside.example"),new(3,10,1,true,"Typed","typed.example")]);
            test.Event("replaced",3,null,"outside.example","typed.example",replaced:2);
            test.Snapshot([new(3,10,0,true,"Typed","typed.example")]);
            check(test.Index.Check([site],Native("Typed",100),true)==FocusPresence.Away,
                "An independently navigated replacement tab cannot acquire the removed tab's inherited website permission");
        }
        using(var test=new Fixture()){
            test.Event("created",2,1,"study.example","outside.example");
            test.Snapshot([new(2,10,0,true,"Outside","outside.example")]);
            test.Event("replaced",3,null,"outside.example","",replaced:2);
            test.Snapshot([new(3,10,0,true,"Unrelated","unrelated.example")]);
            check(test.Index.Check([site],Native("Unrelated",100),true)==FocusPresence.Away,
                "A replacement with an unknown destination cannot transfer inherited website permission into a later snapshot");
        }
        foreach(var qualifier in new[]{"from_address_bar","forward_back"}){
            using var test=new Fixture();
            test.Event("created",2,1,"study.example","outside.example");
            test.Snapshot([new(2,10,0,true,"Outside","outside.example")]);
            test.Event("committed",2,null,"outside.example","independent.example","link",qualifiers:[qualifier]);
            test.Snapshot([new(2,10,0,true,"Independent","independent.example")]);
            check(test.Index.Check([site],Native("Independent",100),true)==FocusPresence.Away,
                "Independent browser qualifiers clear lineage even when the transition label says link: "+qualifier);
        }
        foreach(var transition in new[]{"auto_bookmark","generated","keyword","keyword_generated","start_page"}){
            using var test=new Fixture();
            test.Event("created",2,1,"study.example","outside.example");
            test.Snapshot([new(1,10,0,false,"Study","study.example"),new(2,10,1,true,"Outside","outside.example")]);
            test.Event("committed",2,null,"outside.example","independent.example",transition);
            test.Snapshot([new(1,10,0,false,"Study","study.example"),new(2,10,1,true,"Independent","independent.example")]);
            check(test.Index.Check([site],Native("Independent",100),true)==FocusPresence.Away,
                "Independent browser navigation cannot retain inherited focus: "+transition);
        }
        using(var test=new Fixture()){
            test.Snapshot([new(1,10,0,false,"Study","study.example"),new(2,10,1,true,"Outside","outside.example",Opener:1)]);
            check(test.Index.Check([site],Native("Outside",100),true)==FocusPresence.Away,
                "A startup or ambient snapshot opener ID never invents a linked page relationship");
            test.Event("created",3,1,"wrong.example","unverified.example");
            test.Snapshot([new(1,10,0,false,"Study","study.example"),new(3,10,1,true,"Unverified","unverified.example",Opener:1)]);
            check(test.Index.Check([site],Native("Unverified",100),true)==FocusPresence.Away,
                "A navigation event with the wrong source website cannot create provenance");
        }
        using(var test=new Fixture()){
            test.Event("created",2,1,"study.example","outside.example");
            test.Snapshot([new(2,10,0,true,"Outside","outside.example")]);
            check(test.Index.Check([site],Native("Outside",100),false)==FocusPresence.Away,
                "The Target on site links option can disable inherited external pages while retaining static site matching");
            test.Configure(links:false);
            check(test.Index.Check([site],Native("Outside",100),true)==FocusPresence.Away,
                "Turning off link targeting discards the optional companion graph");
            test.Configure(companion:false);
            check(!test.Index.Connected&&test.Index.ListSites().Count==0
                &&test.Index.Check([site],Native("Outside",100),true)==FocusPresence.Unavailable,
                "Native website focus remains the default when the companion is not enabled");
            test.Hello(newEpoch:true);
            test.Snapshot([new(2,10,0,true,"Outside","outside.example")]);
            check(!test.Index.Connected&&test.Index.ListSites().Count==0,
                "Late callbacks from a disabled companion cannot repopulate connections or website choices");
        }
        using(var test=new Fixture()){
            test.Configure(running:false);
            test.Event("created",2,1,"study.example","outside.example");
            test.Snapshot([new(2,10,0,true,"Outside","outside.example")]);
            test.Configure(running:true);
            check(test.Index.Check([site],Native("Outside",100),true)==FocusPresence.Away,
                "Links opened while a session is paused do not gain session-scoped companion provenance");
        }
        using(var test=new Fixture()){
            test.Event("created",2,1,"study.example","");
            test.Configure(running:false);test.Configure(running:true);
            test.Event("committed",2,null,"","outside.example","link");
            test.Snapshot([new(2,10,0,true,"Outside","outside.example")]);
            check(test.Index.Check([site],Native("Outside",100),true)==FocusPresence.Away,
                "A delayed first commit cannot restore pending-created provenance from before pause and resume");
        }
        using(var test=new Fixture()){
            var groupSlot=new BrowserTabSlot("group-runtime","strip","Group Work - 2 tabs",true,null);
            var slots=new[]{groupSlot,new BrowserTabSlot("tab-runtime-1","strip","Study - Part of group Work - Memory usage: 20 MB",false,true),
                new BrowserTabSlot("tab-runtime-2","strip","Other - Part of group Work",false,false)};
            test.Snapshot([new(1,10,0,true,"Study","study.example",7),new(2,10,1,false,"Other","other.example",7)],
                groups:[new(7,10,"Work")]);
            test.Index.BindWindow(source,slots);
            test.Event("created",3,1,"study.example","outside.example");
            test.Snapshot([new(1,10,0,false,"Study","study.example",7),new(2,10,1,false,"Other","other.example",7),
                new(3,10,2,true,"Outside","outside.example")],groups:[new(7,10,"Work")]);
            var selected=source with{Kind=FocusTargetKind.BrowserTabGroup,TabRuntimeId="group-runtime"};
            check(test.Index.Check([selected],Native("Outside",100),true)==FocusPresence.Focused,
                "Native group provenance is correlated through verified tab indices, titles and full group membership");
        }
        using(var test=new Fixture()){
            test.Snapshot([new(1,10,0,true,"Study","study.example",7)],groups:[new(7,10,"Work")]);
            test.Index.BindWindow(source,[new("group-runtime","strip","Group Work - 1 tab",true,null),
                new("tab-runtime-1","strip","Study - Part of group Work",false,true)]);
            test.Index.BindWindow(source,[new("tab-runtime-1","strip","Study",false,true)]);
            test.Event("created",2,1,"study.example","outside.example");
            test.Snapshot([new(2,10,0,true,"Outside","outside.example")]);
            check(test.Index.Check([source with{Kind=FocusTargetKind.BrowserTabGroup,TabRuntimeId="group-runtime"}],
                Native("Outside",100),true)==FocusPresence.Unavailable,
                "Rebinding a tab outside its old native group removes obsolete own-group provenance before later links");
        }
        using(var test=new Fixture()){
            test.Snapshot([new(1,10,0,true,"Study","study.example"),new(2,10,1,false,"Other","other.example")]);
            test.Index.BindWindow(source,[new("wrong-index","strip","Other",false,false),new("tab-runtime-1","strip","Study",false,true)]);
            test.Event("created",3,1,"study.example","outside.example");
            test.Snapshot([new(3,10,0,true,"Outside","outside.example")]);
            check(test.Index.Check([source with{Kind=FocusTargetKind.BrowserTab,TabRuntimeId="tab-runtime-1"}],
                Native("Outside",100),true)==FocusPresence.Unavailable,
                "Mismatched native tab order cannot associate a runtime ID with a companion tab");
        }
        using(var test=new Fixture()){
            test.Snapshot([new(1,10,0,true,"Study","study.example"),new(2,20,0,true,"Study","other.example")],
                [new(10,false,Box(0)),new(20,true,Box(1000))],current:20);
            check(test.Index.CaptureSite(source) is null&&test.Index.Check([site],source,true)==FocusPresence.Unknown,
                "Two same-title browser windows cannot be guessed from the title alone");
            check(test.Index.CaptureSite(source,Box(0))?.SiteHost=="study.example"
                &&test.Index.CaptureSite(Native("Study",200),Box(1000))?.SiteHost=="other.example",
                "Native bounds disambiguate otherwise identical active browser page titles");
            check(test.Index.CaptureSite(source,Box(5000)) is null,
                "Contradictory window bounds cannot bind a native window to companion metadata");
            check(test.Index.IsCurrentSite(Site("other.example"))&&!test.Index.IsCurrentSite(site),
                "The last-focused browser window marks its current site while the desktop chooser has focus");
        }
        using(var test=new Fixture()){
            test.Snapshot([new(1,10,0,true,"Study","docs.study.example:8443")]);
            check(test.Index.Check([Site("study.example:8443")],source,true)==FocusPresence.Focused
                &&test.Index.Check([Site("study.example")],source,true)==FocusPresence.Away,
                "Companion site matching preserves subdomain boundaries and non-default ports");
            check(test.Index.Check([Site("study.example.evil")],source,true)==FocusPresence.Away,
                "A similar host suffix never matches an unrelated site target");
        }
        using(var test=new Fixture()){
            var before=test.Sequence;
            test.Raw(new{version=1,type="snapshot",browser="chrome",epoch=test.Epoch,seq=before,windows=new object[0],tabs=new object[0],groups=new object[0]});
            check(test.Index.Check([site],source,true)==FocusPresence.Focused,
                "An older or duplicate sequence cannot overwrite fresh companion metadata");
            test.Raw(new{version=1,type="navigation",browser="chrome",epoch=Guid.NewGuid(),seq=before+100,
                kind="removed",tabId=1,previousSiteHost="study.example",siteHost="",transitionType="",qualifiers=new string[0]});
            check(test.Index.Check([site],source,true)==FocusPresence.Focused,
                "An event from another connection epoch cannot mutate the current browser snapshot");
            test.Time.Advance(TimeSpan.FromSeconds(6));
            check(!test.Index.Connected&&test.Index.ListSites().Count==0
                &&test.Index.Check([site],source,true)==FocusPresence.Unknown
                &&test.Index.Check([source],source,true)==FocusPresence.Unavailable,
                "Stale companion data expires without masking native focus or falsely reporting an off-site visit");
            test.Snapshot([new(1,10,0,true,"Study","study.example")]);
            check(test.Index.Connected&&test.Index.Check([site],source,true)==FocusPresence.Focused,
                "A fresh complete snapshot recovers stale companion metadata");
            test.Index.Disconnect("test");
            check(!test.Index.Connected&&test.Index.Check([site],source,true)==FocusPresence.Unknown,
                "Disconnect removes metadata and leaves companion-only website checks unknown");
        }
        using(var test=new Fixture()){
            test.Event("created",2,1,"study.example","outside.example");
            test.Snapshot([new(2,10,0,true,"Outside","outside.example")]);
            test.Hello(newEpoch:true);
            test.Snapshot([new(2,10,0,true,"Outside","outside.example",Opener:1)]);
            check(test.Index.Check([site],Native("Outside",100),true)==FocusPresence.Away,
                "A companion reconnect cannot restore old lineage or infer it from a historical opener");
            test.Raw(new{version=1,type="snapshot",browser="chrome",epoch=test.Epoch,seq=++test.Sequence,
                windows=new[]{new{id=10,focused=true,state="normal",left=0,top=0,width=900,height=700}},groups=new object[0]});
            check(test.Index.Check([site],source,true)==FocusPresence.Unknown,
                "A malformed incomplete snapshot fails quietly and invalidates readable metadata");
            test.Snapshot([new(1,10,0,true,"Study","study.example")],overflow:true);
            check(test.Index.ListSites().Count==0&&test.Index.Check([site],source,true)==FocusPresence.Unknown,
                "A truncated companion snapshot cannot be used for target binding or focus decisions");
            test.Snapshot([new(1,10,0,true,"Study","https://user:secret@study.example")]);
            check(test.Index.ListSites().Count==0&&test.Index.Check([site],source,true)==FocusPresence.Unknown,
                "Credential-bearing or malformed site metadata cannot create a trusted website node");
        }
        using(var test=new Fixture()){
            test.Bind(source);test.Event("created",2,1,"study.example","outside.example");
            test.Snapshot([new(2,10,0,true,"Outside","outside.example")]);
            test.Sequence++;
            test.Event("committed",2,null,"outside.example","outside.example","reload");
            check(test.Index.Check([site],Native("Outside",100),true)==FocusPresence.Unknown,
                "A companion sequence gap invalidates metadata and provenance until a complete snapshot arrives");
            test.Snapshot([new(2,10,0,true,"Outside","outside.example")]);
            check(test.Index.Check([site],Native("Outside",100),true)==FocusPresence.Away
                &&test.Index.Check([source],Native("Outside",100),true)==FocusPresence.Unavailable,
                "The first snapshot after a sequence gap cannot resurrect host or native-key inheritance");
        }
        using(var test=new Fixture()){
            test.Event("created",2,1,"study.example","outside.example");
            test.Snapshot([new(2,10,0,true,"Outside","outside.example")]);
            test.Time.Advance(TimeSpan.FromSeconds(6));
            test.Snapshot([new(2,10,0,true,"Outside","outside.example")]);
            check(test.Index.Check([site],Native("Outside",100),true)==FocusPresence.Away,
                "A stale connection recovering with the next sequence starts fresh provenance");
            test.Event("created",3,2,"outside.example","chain.example");
            test.Snapshot([new(3,10,0,true,"Chain","chain.example")]);
            test.Sequence+=2;test.Hello();
            check(test.Index.Check([Site("outside.example")],Native("Chain",100),true)==FocusPresence.Unknown,
                "A same-epoch hello with a sequence gap also requires a new complete snapshot");
        }
        using(var test=new Fixture(start:false)){
            test.Snapshot([new(1,10,0,true,"Study","study.example")]);
            check(!test.Index.Connected&&test.Index.ListSites().Count==0,
                "A snapshot from a connection without a valid hello cannot seed site metadata");
        }
    }
    private static Rectangle Box(int x)=>new(x,0,900,700);
    private static FocusTarget Site(string host)=>new(Guid.NewGuid(),FocusTargetKind.Site,host,"",0,0,0){SiteHost=host};
    private static FocusTarget Native(string title,long handle,string app="chrome")=>new(Guid.NewGuid(),FocusTargetKind.Window,
        title+" - Google Chrome",app,handle,(int)handle+1000,handle+2000){WindowName=title+" - Google Chrome"};
    private sealed record Window(int Id,bool Focused,Rectangle Bounds);
    private sealed record Tab(int Id,int WindowId,int Index,bool Active,string Title,string Host,int Group=-1,int Opener=-1);
    private sealed record Group(int Id,int WindowId,string Title);
    private sealed class Fixture : IDisposable
    {
        internal readonly FakeTime Time=new();
        internal readonly BrowserFocusIndex Index;
        internal Guid Epoch=Guid.NewGuid(),Session=Guid.NewGuid();
        internal long Sequence;
        internal Fixture(bool start=true)
        {
            Index=new(Time);Configure();
            if(start){Hello();Snapshot([new(1,10,0,true,"Study","study.example")]);}
        }
        internal void Configure(bool companion=true,bool links=true,bool running=true,Guid? session=null)
        {
            if(session is {} id)Session=id;
            Index.Configure(new(){Enabled=true,BrowserCompanionEnabled=companion,TargetOnSiteLinks=links},
                new(){IsRunning=running,SessionId=Session});
        }
        internal void Bind(FocusTarget window)=>Index.BindWindow(window,[new("tab-runtime-1","strip","Study",false,true)]);
        internal void Hello(bool newEpoch=false)
        {
            if(newEpoch){Epoch=Guid.NewGuid();Sequence=0;}
            Raw(new{version=1,type="hello",browser="chrome",epoch=Epoch,seq=Sequence});
        }
        internal void Snapshot(Tab[] tabs,Window[]? windows=null,Group[]? groups=null,int current=10,bool overflow=false)
            =>Raw(new{version=1,type="snapshot",browser="chrome",epoch=Epoch,seq=++Sequence,currentWindowId=current,overflow,
                windows=(windows??[new(10,true,Box(0))]).Select(window=>new{id=window.Id,focused=window.Focused,state="normal",
                    left=window.Bounds.Left,top=window.Bounds.Top,width=window.Bounds.Width,height=window.Bounds.Height}),
                tabs=tabs.Select(tab=>new{id=tab.Id,windowId=tab.WindowId,index=tab.Index,active=tab.Active,title=tab.Title,
                    siteHost=tab.Host,groupId=tab.Group,openerTabId=tab.Opener}),
                groups=(groups??[]).Select(group=>new{id=group.Id,windowId=group.WindowId,title=group.Title})});
        internal void Event(string kind,int id,int? source,string previous,string host,string transition="",string[]? qualifiers=null,int? replaced=null)
            =>Raw(new{version=1,type="navigation",browser="chrome",epoch=Epoch,seq=++Sequence,kind,tabId=id,
                sourceTabId=source,replacedTabId=replaced,previousSiteHost=previous,siteHost=host,transitionType=transition,qualifiers=qualifiers??[]});
        internal void Raw(object packet)
        {
            using var document=JsonDocument.Parse(JsonSerializer.Serialize(packet));
            Index.Receive("test",document.RootElement);
        }
        public void Dispose()=>Index.Disconnect("test");
    }
    private sealed class FakeTime : TimeProvider
    {
        private long timestamp;
        public override long TimestampFrequency=>TimeSpan.TicksPerSecond;
        public override long GetTimestamp()=>timestamp;
        internal void Advance(TimeSpan amount)=>timestamp+=amount.Ticks;
    }
}
