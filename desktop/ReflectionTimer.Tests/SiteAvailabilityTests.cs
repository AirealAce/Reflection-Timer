using System.Text.Json;
using ReflectionTimer.Accessible;
using ReflectionTimer.Core;

internal static class SiteAvailabilityTests
{
    internal static async Task Run(Action<bool,string> check)
    {
        var site=new FocusTarget(Guid.NewGuid(),FocusTargetKind.Site,"study.example","chrome",0,0,0){SiteHost="study.example"};
        var other=site with {Id=Guid.NewGuid(),SiteHost="other.example"};
        var dynamic=FocusTarget.Focused(FocusTargetKind.Site);
        var window=new FocusTarget(Guid.NewGuid(),FocusTargetKind.Window,"Synthetic window","chrome",1,2,3);
        var current=new FocusModeSettings{BrowserCompanionEnabled=true,MultipleTargets=true,Targets=[site]};
        bool Rejected(FocusModeSettings before,FocusModeSettings next,bool ready){
            try {FocusSitePolicy.ValidateSelection(before,next,ready);return false;}catch(ArgumentException){return true;}
        }
        foreach(var next in new[]{current with{Targets=[site,other]},current with{Targets=[site,dynamic]}}){
            check(Rejected(current,next,false),"Disconnected picker/shortcut selections reject new "+(next.Targets[1].UseFocused?"dynamic":"static")+" Sites");
            check(Rejected(current,next with{BrowserCompanionEnabled=false},true),"A lingering connection cannot bypass a disabled companion toggle for new Site choices");
            check(!Rejected(current,next,true),"A ready companion permits a new "+(next.Targets[1].UseFocused?"dynamic":"static")+" Site selection");
        }
        check(!Rejected(current,current with{BrowserCompanionEnabled=false,DelaySeconds=12},false),
            "Disabling companion and saving unrelated settings retains existing Site choices");
        check(!Rejected(current,current with{Targets=[window],Target=window},false),
            "Unavailable Site choices can be removed while saving ordinary focus targets");
        check(!Rejected(current,current with{Targets=[site with{App="msedge",SiteHost="https://www.study.example/path"}]},false),
            "Saved Site identity remains unchanged across canonical hosts and browser labels during an outage");
        foreach(var ready in new[]{false,true}){
            var rejected=false;try{FocusSitePolicy.RequireAvailable(new(){BrowserCompanionEnabled=false},ready);}catch(ArgumentException){rejected=true;}
            check(rejected,"Manual Site entry cannot bypass a disabled companion, connection="+ready);
        }
        var missing=new BrowserFocusIndex();
        using(var source=new WindowsFocusTargets(missing)){
            source.Configure(current,new());
            check(!source.BrowserConnected&&(await source.ListAsync(FocusTargetKind.Site)).Count==0,
                "Enabled but unconfigured companion exposes no native fallback Site inventory");
            check((await source.CaptureAsync(window,[FocusTargetKind.Site])).Count==0,
                "Dynamic Site capture does not read native URLs without a configured companion");
            check(await source.CheckAsync(site)==FocusPresence.Unknown,
                "A saved Site without a companion is Unknown instead of falsely Away");
        }
        var clock=new FakeTime();var index=new BrowserFocusIndex(clock);index.Configure(current,new());
        var epoch=Guid.NewGuid();long sequence=0;
        void Send(object packet){using var data=JsonDocument.Parse(JsonSerializer.Serialize(packet));index.Receive("test",data.RootElement);}
        void Hello()=>Send(new{version=1,type="hello",browser="chrome",epoch,seq=sequence});
        void Snapshot(bool overflow=false)=>Send(new{version=1,type="snapshot",browser="chrome",epoch,seq=++sequence,currentWindowId=10,overflow,
            windows=new[]{new{id=10,focused=true,state="normal",left=0,top=0,width=900,height=700}},
            tabs=new[]{new{id=1,windowId=10,index=0,active=true,title="Study",siteHost="study.example",groupId=-1}},groups=Array.Empty<object>()});
        Hello();
        check(!index.Connected,"A native host hello alone does not make an incompletely configured companion ready");
        Snapshot();check(index.Connected,"A current complete valid snapshot makes Site targets available");
        clock.Advance(TimeSpan.FromSeconds(6));Hello();
        check(!index.Connected,"A fresh hello cannot revive an expired Site snapshot");
        Snapshot();check(index.Connected,"A complete snapshot restores Site readiness after expiry");
        Send(new{version=1,type="snapshot",browser="chrome",epoch,seq=++sequence,windows=Array.Empty<object>(),groups=Array.Empty<object>()});
        check(!index.Connected,"Malformed incomplete snapshots disable Site selection rather than trusting a live pipe");
        Snapshot(true);check(!index.Connected,"A truncated inventory disables Site selection");
        Snapshot();index.Disconnect("test");check(!index.Connected,"Disconnect disables Site selection immediately");
        Hello();Snapshot();index.Configure(current with{BrowserCompanionEnabled=false},new());
        check(!index.Connected,"Turning companion off clears readiness even after a valid snapshot");
        foreach(var target in new[]{site,dynamic}){
            var engine=new TimerEngine(new MemoryStore());
            engine.SetFocusMode(current with{Enabled=true,DelaySeconds=0,Targets=[target]});
            var provider=new Source{BrowserConnected=false};var alerts=new List<bool>();
            using var monitor=new FocusModeMonitor(engine,provider,alerts.Add);
            engine.Start(900,false,15);monitor.Poll();
            check(!alerts.Contains(true)&&provider.Captures==0&&provider.Reads==0,
                "An unavailable "+(target.UseFocused?"dynamic":"saved")+" Site neither captures nor starts an away alarm");
            check(engine.SettingsSnapshot.FocusMode.SelectedTargets.Single().Key==target.Key,
                "Companion outages preserve the saved "+(target.UseFocused?"dynamic":"static")+" Site preference");
        }
        {
            var engine=new TimerEngine(new MemoryStore());engine.SetFocusMode(current with{Enabled=true,DelaySeconds=0});
            var provider=new Source{BrowserConnected=true};var alerts=new List<bool>();
            using var monitor=new FocusModeMonitor(engine,provider,alerts.Add);
            engine.Start(900,false,15);monitor.Poll();check(alerts.Last(),"A ready saved Site can trigger its normal away alarm");
            provider.BrowserConnected=false;monitor.Poll();check(!alerts.Last(),"Disconnect stops the active Site alarm on the next monitor probe");
            provider.BrowserConnected=true;monitor.Poll();check(alerts.Last(),"Reconnection restores saved Site monitoring without deleting or recapturing the preference");
        }
    }
    private sealed class FakeTime : TimeProvider
    {
        private long timestamp;
        public override long TimestampFrequency=>TimeSpan.TicksPerSecond;
        public override long GetTimestamp()=>timestamp;
        internal void Advance(TimeSpan value)=>timestamp+=value.Ticks;
    }
    private sealed class Source : IFocusTargetSource
    {
        public bool BrowserConnected {get;set;}
        internal int Captures,Reads;
        public FocusTarget? CaptureForeground()=>null;
        public Task<IReadOnlyList<FocusTarget>> CaptureSelectionsAsync(FocusTarget? foreground,IReadOnlyList<FocusTarget> windows,IReadOnlyList<FocusTarget> choices)
        {Captures++;return Task.FromResult<IReadOnlyList<FocusTarget>>([]);}
        public Task<IReadOnlyList<FocusTarget>> ListAsync(FocusTargetKind kind)=>Task.FromResult<IReadOnlyList<FocusTarget>>([]);
        public Task<FocusPresence> CheckAsync(FocusTarget target){Reads++;return Task.FromResult(FocusPresence.Away);}
        public void Dispose(){}
    }
}
