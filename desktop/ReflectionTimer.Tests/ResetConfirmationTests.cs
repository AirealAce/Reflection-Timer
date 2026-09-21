using System.Text.Json;
using ReflectionTimer.Accessible;
using ReflectionTimer.Core;

static class ResetConfirmationTests
{
    internal static void Run(Action<bool,string> check)
    {
        check(AppState.CreateDefault().ConfirmBeforeReset,"New profiles confirm before reset by default");
        check(JsonSerializer.Deserialize<AppState>("{}")!.ConfirmBeforeReset,"Existing profiles enable reset confirmation when the setting is absent");
        foreach(var mode in new[]{SessionMode.Timer,SessionMode.Stopwatch})
        foreach(var running in new[]{false,true})
        foreach(var fields in new[]{("",""),(" \n","\t"),("My response",""),("","My reason")}) {
            var state=new AppState{Timer=new(){Mode=mode,IsRunning=running},Prompts=[new(Guid.NewGuid(),0,900,0,false,fields.Item1){EarlyEndReason=fields.Item2}]};
            var text=!string.IsNullOrWhiteSpace(fields.Item1)||!string.IsNullOrWhiteSpace(fields.Item2);
            var warning=ResetWarning.For(state);
            check((warning is not null)==(running||text),$"Reset confirmation follows running/draft conditions: {mode}, running={running}, text={text}");
            state.ConfirmBeforeReset=false;
            check(ResetWarning.For(state) is null,"Disabled reset confirmation bypasses running sessions and drafts");
        }
        var queued=new AppState{Outbox=[new(){Message="Already submitted",Status=DeliveryStatus.Pending}]};
        check(ResetWarning.For(queued) is null,"Already submitted Outbox entries do not count as unfinished reflection inputs");
        var store=new MemoryStore();var engine=new TimerEngine(store);
        engine.SetConfirmBeforeReset(false);
        check(!new TimerEngine(store).Snapshot.ConfirmBeforeReset,"Reset confirmation opt-out persists across app restarts");
        engine.SetConfirmBeforeReset(true);store.Fail=true;
        try{engine.SetConfirmBeforeReset(false);throw new Exception("Failed save accepted");}catch(IOException){}
        check(engine.Snapshot.ConfirmBeforeReset,"Failed preference save retains reset confirmation");
    }
}
