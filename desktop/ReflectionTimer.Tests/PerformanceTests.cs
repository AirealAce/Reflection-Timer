using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using ReflectionTimer.Accessible;
using ReflectionTimer.Core;
using ReflectionTimer.Desktop;
using Activity = ReflectionTimer.Core.Activity;

static class PerformanceTests
{
    public static void Run(Action<bool,string> check)
    {
        // Structural copies are safe only while every nested record is immutable.
        var seen=new HashSet<Type>();
        void Immutable(Type type) {
            if(type.IsValueType||type==typeof(string)||!seen.Add(type))return;
            check(type.Namespace==typeof(AppState).Namespace,"Snapshot reference type is an audited core record: "+type.Name);
            foreach(var property in type.GetProperties(BindingFlags.Public|BindingFlags.Instance)) {
                check(property.SetMethod is null||property.SetMethod.ReturnParameter.GetRequiredCustomModifiers().Contains(typeof(IsExternalInit)),"Snapshot nested property is immutable: "+type.Name+"."+property.Name);
                Immutable(property.PropertyType);
            }
        }
        foreach(var property in typeof(AppState).GetProperties(BindingFlags.Public|BindingFlags.Instance)) {
            if(property.Name is "Schedules" or "Prompts" or "Outbox")Immutable(property.PropertyType.GenericTypeArguments.Single());
            else Immutable(property.PropertyType);
        }
        var store=new MemoryStore {State=new() {
            Prompts=[new(Guid.NewGuid(),1,60,50,false,"retained")],
            Schedules=[new(Guid.NewGuid(),long.MaxValue,60,false,50)],
            Outbox=[new(){Message="retained"}]
        }};
        var engine=new TimerEngine(store);var before=JsonSerializer.Serialize(engine.Snapshot,DataJson.Options);
        var detached=engine.Snapshot;detached.Timer=detached.Timer with{Volume=1};detached.Connection=new(){ApiToken="synthetic"};detached.Theme=AppColorTheme.Glamour;
        detached.Prompts.Clear();detached.Schedules.Clear();detached.Outbox.Clear();
        check(JsonSerializer.Serialize(engine.Snapshot,DataJson.Options)==before,"Editing every mutable snapshot component cannot change engine state");
        check(engine.SettingsSnapshot.Outbox.Count==0&&engine.SettingsSnapshot.Prompts.Count==0&&engine.SettingsSnapshot.Schedules.Count==0,"Settings reads exclude all history collections");
        var old=engine.Snapshot;engine.SetFloatingTimer(false);
        check(old.ShowFloatingTimer&&!engine.Snapshot.ShowFloatingTimer,"Prior snapshots stay stable across a committed mutation");
        store.Fail=true;var durable=JsonSerializer.Serialize(engine.Snapshot,DataJson.Options);
        try{engine.SetFloatingTimer(true);check(false,"Expected save failure");}catch(IOException){}
        check(JsonSerializer.Serialize(engine.Snapshot,DataJson.Options)==durable,"Failed durable write cannot publish structural-copy mutations");

        var session=new PreviewSession(new MemoryStore {State=new(){Outbox=[new(){Message="synthetic history"}]}});
        JsonElement View(bool delta)=>JsonSerializer.SerializeToElement(session.View(delta),PreviewSession.Json);
        check(View(true).GetProperty("outbox").GetArrayLength()==1,"First incremental view carries history");
        session.Engine.SetFloatingTimer(false);
        check(View(true).GetProperty("outbox").ValueKind==JsonValueKind.Null,"Settings updates omit unchanged history");
        check(View(false).GetProperty("outbox").GetArrayLength()==1,"New/recovered viewers always receive complete history");
        session.Engine.TestPrompt();session.Engine.QueueReflection(session.Engine.Snapshot.Prompts.Last().Id,"another synthetic entry",localOnly:true);
        check(View(true).GetProperty("outbox").GetArrayLength()==2,"History mutations invalidate the incremental view cache");

        foreach(var count in new[]{0,200,2000}) {
            var sample=new AppState {Outbox=Enumerable.Range(0,count).Select(_=>new OutboxItem{Message=new string('x',500)}).ToList()};
            var measured=new TimerEngine(new MemoryStore{State=sample});
            var previous=Measure(()=>DataJson.Clone(sample),40);var current=Measure(()=>measured.Snapshot,40);var timer=Measure(()=>measured.CurrentTimer,1000);
            check(current.Bytes<=4096+count*32,"Snapshot allocations scale with references, not message size: "+count);
            check(timer.Bytes<=16,"Clock reads allocate no history: "+count);
            Console.WriteLine($"PERF history={count}: JSON clone {previous.Microseconds:F1} us/{previous.Bytes:F0} bytes; snapshot {current.Microseconds:F1} us/{current.Bytes:F0} bytes; clock {timer.Microseconds:F2} us/{timer.Bytes:F0} bytes");
        }
        Diagnostics(check);
    }
    private static (double Microseconds,double Bytes) Measure(Func<object> action,int count) {
        for(var i=0;i<5;i++)GC.KeepAlive(action());
        var watch=new Stopwatch();var bytes=GC.GetAllocatedBytesForCurrentThread();watch.Start();
        for(var i=0;i<count;i++)GC.KeepAlive(action());watch.Stop();
        return (watch.Elapsed.TotalMicroseconds/count,(GC.GetAllocatedBytesForCurrentThread()-bytes)/(double)count);
    }
    private static void Diagnostics(Action<bool,string> check) {
        var writes=new List<Activity[]>();var fail=false;
        using var log=new DiagnosticLog(Path.Combine(Path.GetTempPath(),"ReflectionTimer-BufferedLog-"+Guid.NewGuid().ToString("N")),(_,items)=>{if(fail)throw new IOException("synthetic failure");writes.Add(items.ToArray());},false);
        for(var i=0;i<1500;i++)log.Record("timer.started",value:i);
        log.Record("not.allowed");
        check(writes.Count==0&&log.Recent().Count==1200,"Diagnostic records are bounded and do not write on the calling thread");
        log.Flush();check(writes.Count==1&&writes[0].First().Value==300,"One diagnostic batch retains the latest allowed events");
        log.Flush();check(writes.Count==1,"Unchanged diagnostics do not rewrite the disk");
        fail=true;log.Record("timer.paused");log.Flush();check(!log.StorageAvailable,"Diagnostic write failure is visible without interrupting a session");
        fail=false;log.Flush();check(log.StorageAvailable&&writes.Last().Last().Event=="timer.paused","Failed diagnostic batch retries without losing its event");
        log.Clear();log.Flush();check(log.Recent().Count==0&&writes.Last().Length==0,"Clear cannot be resurrected by an older buffered diagnostic batch");
        log.Enabled=false;log.Record("timer.started");check(log.Recent().Count==0,"Disabled logging never buffers new events");
        log.Enabled=true;log.Record("app.exiting");log.Dispose();check(writes.Last().Single().Event=="app.exiting","Normal shutdown flushes the final diagnostic batch");
        using var entered=new ManualResetEventSlim();using var release=new ManualResetEventSlim();
        using var slow=new DiagnosticLog(Path.Combine(Path.GetTempPath(),"ReflectionTimer-SlowLog-"+Guid.NewGuid().ToString("N")),(_,_)=>{entered.Set();if(!release.Wait(3000))throw new IOException("timeout");},false);
        slow.Record("timer.started");var flushing=Task.Run(slow.Flush);
        try {
            check(entered.Wait(2000),"Slow diagnostic writer started");
            var record=Task.Run(()=>slow.Record("timer.paused"));
            check(record.Wait(1000),"A blocked diagnostic disk write cannot block recording a timer action");
        } finally {release.Set();flushing.GetAwaiter().GetResult();}
        check(slow.Recent().Last().Event=="timer.paused","Events arriving during a diagnostic flush are retained");
    }
}
