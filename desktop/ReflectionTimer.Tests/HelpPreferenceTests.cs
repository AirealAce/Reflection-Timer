using System.Text.Json;
using ReflectionTimer.Accessible;
using ReflectionTimer.Core;

static class HelpPreferenceTests
{
    internal static void Run(Action<bool,string> check)
    {
        check(!AppState.CreateDefault().ShowAllExplanations&&!JsonSerializer.Deserialize<AppState>("{}")!.ShowAllExplanations,
            "New and existing profiles default to collapsed explanations");
        var store=new MemoryStore();var session=new PreviewSession(store);
        var before=JsonSerializer.Serialize(session.Engine.Snapshot.Timer);
        var audio=JsonSerializer.Serialize(session.Engine.Snapshot.Audio);
        session.Engine.SetShowAllExplanations(true);
        check(new TimerEngine(store).Snapshot.ShowAllExplanations,"Show all explanations survives reopening");
        check(JsonSerializer.SerializeToElement(session.View(),PreviewSession.Json).GetProperty("showAllExplanations").GetBoolean(),
            "View broadcasts include the saved help preference");
        check(JsonSerializer.Serialize(session.Engine.Snapshot.Timer)==before&&JsonSerializer.Serialize(session.Engine.Snapshot.Audio)==audio,
            "Changing guidance visibility leaves timer and audio preferences intact");
        store.Fail=true;
        try{session.Engine.SetShowAllExplanations(false);throw new Exception("Failed save accepted");}catch(IOException){}
        check(session.Engine.Snapshot.ShowAllExplanations,"A failed help-preference save retains the durable value");
        store.Fail=false;session.Engine.SetShowAllExplanations(false);
        check(!new TimerEngine(store).Snapshot.ShowAllExplanations,"Collapsed guidance choice survives reopening");
    }
}
