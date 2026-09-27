using System.Text.Json;
using ReflectionTimer.Core;

static class ViewPreferenceTests
{
    internal static void Run(Action<bool, string> check)
    {
        var legacy = JsonSerializer.Deserialize<AppState>("{}", DataJson.Options)!;
        check(legacy.ShowAppView is null && legacy.FloatingTimeOnly is null, "Legacy profiles retain their launch/layout defaults until a view changes");
        var store = new MemoryStore { State = legacy };
        var engine = new TimerEngine(store);
        var writes = 0; engine.Changed += () => writes++;
        engine.SetAppViewVisibility(false); engine.SetFloatingTimeOnly(true);
        var reopened = new TimerEngine(store);
        check(reopened.SettingsSnapshot.ShowAppView == false && reopened.SettingsSnapshot.FloatingTimeOnly == true, "Hidden App view and time-only preference survive reload");
        engine.SetAppViewVisibility(false); engine.SetFloatingTimeOnly(true);
        check(writes == 2, "Repeated view/resize notifications do not rewrite the profile");
        var expected = JsonSerializer.Serialize(store.State, DataJson.Options);
        store.Fail = true;
        foreach (var update in new Action[] { () => engine.SetAppViewVisibility(true), () => engine.SetFloatingTimeOnly(false) }) {
            try { update(); throw new Exception("Expected failed save"); } catch (IOException) { }
        }
        check(engine.SettingsSnapshot.ShowAppView == false && engine.SettingsSnapshot.FloatingTimeOnly == true
            && JsonSerializer.Serialize(store.State, DataJson.Options) == expected, "Failed view saves preserve the previous in-memory and disk state");
    }
}
