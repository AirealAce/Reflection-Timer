using System.Net;
using System.Text.Json;
using ReflectionTimer.Accessible;
using ReflectionTimer.Core;

static class PauseTests
{
    internal static async Task Run(Action<bool,string> check)
    {
        foreach(var mode in new[]{SessionMode.Timer,SessionMode.Stopwatch}) {
            var clock=new Clock();var store=new MemoryStore();var session=new PreviewSession(store,()=>clock.Wall,true,clock);var engine=session.Engine;
            engine.SwitchMode(mode);engine.Start(120,false,50);clock.Step(5000);engine.Pause();
            var first=engine.Snapshot.Timer.Pauses.Single();var pausedAt=first.PausedAt;
            clock.Step(2000);engine.Pause();clock.Step(3000);engine.Resume();
            check(engine.Snapshot.Timer.Pauses.Single() is {DurationMilliseconds:5000}&&engine.Snapshot.Timer.Pauses[0].PausedAt==pausedAt,
                mode+": repeated Pause cannot create duplicates or move the pause time");
            clock.Step(10000);engine.Pause();var id=engine.CheckIn();var second=engine.PausesFor(engine.Snapshot.Prompts.Single()).Last();
            var reasons=new Dictionary<Guid,string>{{first.Id,"Phone call"},{second.Id,"Water break\nBack shortly"}};
            session.Execute("draft",Data(new{id,text="Work done",reason="",pauseReasons=reasons.Select(p=>new{id=p.Key,reason=p.Value})}));
            check(engine.Snapshot.Timer.Pauses[1].Reason==reasons[second.Id]&&engine.Snapshot.Prompts.Single().Pauses.Count==2,
                mode+": bridge saves optional reasons against their own session and pause IDs");
            var before=JsonSerializer.Serialize(engine.Snapshot);store.Fail=true;
            try{engine.SaveDraft(id,"Changed",pauseReasons:new Dictionary<Guid,string>{{first.Id,"Unsaved"}});throw new Exception("Failed save accepted");}catch(IOException){}
            store.Fail=false;check(JsonSerializer.Serialize(engine.Snapshot)==before,mode+": failed save preserves all pause drafts");
            try{engine.SaveDraft(id,"Changed",pauseReasons:new Dictionary<Guid,string>{{Guid.NewGuid(),"Wrong session"}});throw new Exception("Foreign pause accepted");}catch(ArgumentException){}
            check(JsonSerializer.Serialize(engine.Snapshot)==before,mode+": another session's pause ID cannot be edited");
            clock.Wall=clock.Wall.AddHours(1);clock.Step(7000);engine.Checkpoint();
            var restored=new TimerEngine(store,()=>clock.Wall,clock);clock.Step(3000);
            restored.QueueReflection(id,"Work done",endSession:true);
            var item=restored.Snapshot.Outbox.Single();
            check(item.Pauses.Count==2&&item.Pauses[1].DurationMilliseconds==10000&&item.Pauses[0].PausedAt==pausedAt,
                mode+": pauses survive recovery and wall-clock changes without gaining false runtime duration");
            check(item.ActualDurationSeconds==15&&item.Pauses[1].Reason==reasons[second.Id],mode+": sent work excludes breaks and retains reasons");
            check(DataJson.Clone(restored.Snapshot).Outbox.Single().Pauses.SequenceEqual(item.Pauses),mode+": encrypted-state JSON format round-trips pause metadata");
            restored.Start(30,false,50);check(restored.Snapshot.Timer.Pauses.Count==0,mode+": a new session starts with no old pauses");
        }
        {
            var clock=new Clock();var engine=new TimerEngine(new MemoryStore(),()=>clock.Wall,clock);
            engine.Start(60,false,50);clock.Step(5000);engine.SwitchMode(SessionMode.Stopwatch);
            check(engine.Snapshot.ParkedTimer!.Pauses.Count==1,"Switching modes records the paused session only");
            clock.Step(6000);engine.SwitchMode(SessionMode.Timer);engine.Resume();
            check(engine.Snapshot.Timer.Pauses.Single().DurationMilliseconds==6000,"Returning to a mode closes its pause only upon resume");
            clock.Step(5000);engine.Pause();var id=engine.CheckIn();clock.Step(2000);
            engine.QueueReflection(id,"Check-in",endSession:false);clock.Step(3000);
            var next=engine.CheckIn();engine.SaveDraft(next,"Retain after reset",pauseReasons:new Dictionary<Guid,string>{{engine.Snapshot.Timer.Pauses.Last().Id,"Interrupted"}});
            engine.Reset();clock.Step(10000);engine.QueueReflection(next,"Retain after reset");
            check(engine.Snapshot.Outbox[0].Pauses.Last().DurationMilliseconds==2000&&engine.Snapshot.Outbox[1].Pauses.Last().DurationMilliseconds==5000,
                "Check-ins snapshot open pauses; reset freezes the old draft's pause without counting later idle time");
        }
        {
            var clock=new Clock();var engine=new TimerEngine(new MemoryStore(),()=>clock.Wall,clock);
            engine.Start(1,false,50);clock.Step(1000);engine.Pause();
            check(engine.Snapshot.Prompts.Single().Pauses.Count==0,"Pause after the deadline is completion, not a new pause");
        }
        {
            var clock=new Clock();var engine=new TimerEngine(new MemoryStore(),()=>clock.Wall,clock);
            engine.SwitchMode(SessionMode.Stopwatch);engine.StartStopwatch();clock.Step(1000);var id=engine.ReviewStopwatch();
            var pause=engine.Snapshot.Timer.Pauses.Single();clock.Step(4000);
            engine.SaveReflectionForLater(id,"",pauseReasons:new Dictionary<Guid,string>{{pause.Id,"Planning"}});
            check(engine.Snapshot.Timer.IsRunning&&engine.Snapshot.Timer.Pauses.Single() is {DurationMilliseconds:4000,Reason:"Planning"},
                "Saving a stopwatch review closes its pause and resumes the same session");
            clock.Step(1000);id=engine.ReviewStopwatch();clock.Step(2000);engine.QueueReflection(id,"");
            check(engine.Snapshot.Outbox.Single().Message==""&&engine.Snapshot.Outbox.Single().Pauses.Count==2,
                "A pause-only response can be sent without inventing reflection text");
        }
        {
            var clock=new Clock();var engine=new TimerEngine(new MemoryStore(),()=>clock.Wall,clock);
            engine.Start(30,false,50);clock.Step(1000);engine.Pause();var id=engine.CheckIn();var pause=engine.Snapshot.Timer.Pauses.Single();
            engine.SaveDraft(id,"",pauseReasons:new Dictionary<Guid,string>{{pause.Id,"Temporary"}});
            try{engine.QueueReflection(id,"",pauseReasons:new Dictionary<Guid,string>{{pause.Id,""}});throw new Exception("Empty submission accepted");}catch(ArgumentException){}
            check(engine.Snapshot.Outbox.Count==0&&engine.Snapshot.Prompts.Single().Pauses.Single().Reason=="Temporary","Clearing the last reason cannot validate an empty send using stale draft text");
        }
        var settings=new ConnectionSettings{SheetUrl="https://docs.google.com/spreadsheets/d/synthetic-spreadsheet-id-for-tests/edit",WebAppUrl="https://script.google.com/macros/s/synthetic/exec",ApiToken="synthetic-test-token-long-enough"};
        {
            var clock=new Clock();var engine=new TimerEngine(new MemoryStore(),()=>clock.Wall,clock);
            engine.SwitchMode(SessionMode.Stopwatch);engine.StartStopwatch();clock.Step(1000);var id=engine.ReviewStopwatch();var pause=engine.Snapshot.Timer.Pauses.Single();
            engine.SwitchMode(SessionMode.Timer);engine.Start(60,false,50);clock.Step(4000);
            engine.QueueReflection(id,"Parked work",pauseReasons:new Dictionary<Guid,string>{{pause.Id,"Changed just before send"}});
            check(engine.Snapshot.Timer.IsRunning&&engine.Snapshot.ParkedTimer is {StopwatchCompleted:true,PauseElapsedSince:null}
                &&engine.Snapshot.ParkedTimer.Pauses.Single() is {DurationMilliseconds:4000,Reason:"Changed just before send"}
                &&engine.Snapshot.Outbox.Single().Pauses.Single().Reason=="Changed just before send",
                "Sending a parked stopwatch closes its own pause, retains last-second edits, and leaves the current timer running");
        }
        var upload=new OutboxItem{Message="Work",SheetUrl=settings.SheetUrl,DurationSeconds=60,Pauses=[new(Guid.NewGuid(),DateTimeOffset.Now.ToUnixTimeMilliseconds(),5000,"Break")]};
        var receiver=new Receiver();using var client=new SheetsClient(receiver);
        var reply=await client.Upload(settings,upload);
        check(!reply.Success&&reply.ErrorKind=="receiver_update_required"&&receiver.Writes==0,"Old receiver never silently drops pause details");
        receiver.Supports=true;reply=await client.Upload(settings,upload);
        check(reply.Success&&receiver.Writes==1&&receiver.Last.GetProperty("pauses")[0].GetProperty("durationSeconds").GetInt64()==5
            &&receiver.Last.GetProperty("pauses")[0].GetProperty("reason").GetString()=="Break","Updated receiver gets stable pause IDs, timestamps, durations and reasons");
    }
    private static JsonElement Data(object value)=>JsonSerializer.SerializeToElement(value,PreviewSession.Json);
    private sealed class Clock:TimeProvider {
        public DateTimeOffset Wall=new(2026,9,28,12,0,0,TimeSpan.Zero);private long ticks;
        public override long GetTimestamp()=>ticks;public override long TimestampFrequency=>TimeSpan.TicksPerSecond;
        public override DateTimeOffset GetUtcNow()=>Wall;
        public void Step(long ms){ticks+=ms*TimeSpan.TicksPerMillisecond;Wall=Wall.AddMilliseconds(ms);}
    }
    private sealed class Receiver:HttpMessageHandler {
        public bool Supports;public int Writes;public JsonElement Last;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token){
            using var data=JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));Last=data.RootElement.Clone();
            var ping=Last.GetProperty("action").GetString()=="ping";if(!ping)Writes++;
            return new(HttpStatusCode.OK){Content=new StringContent(JsonSerializer.Serialize(new{success=true,target="Synthetic",sheet="test",supportsPauses=Supports}))};
        }
    }
}
