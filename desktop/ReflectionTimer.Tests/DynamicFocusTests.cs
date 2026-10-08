using System.Text.Json;
using System.Collections.Immutable;
using ReflectionTimer.Accessible;
using ReflectionTimer.Core;

internal static class DynamicFocusTests
{
    internal static async Task Run(Action<bool, string> check, FocusTarget window)
    {
        var kinds = Enum.GetValues<FocusTargetKind>();
        var dynamic = kinds.Select(k => PreviewApplication.FocusChoices(k, [])[0]).ToImmutableArray();
        check(dynamic.Select(t => t.Id).Distinct().Count() == 4 && dynamic.All(t => t.UseFocused && t.Id != Guid.Empty),
            "The four dynamic choices have separate stable identities even with no open targets");
        foreach (var kind in kinds) {
            var first = PreviewApplication.FocusChoices(kind, [window with { Kind = kind, SiteHost = kind == FocusTargetKind.Site ? "study.example" : "" }]);
            var ordinary=first.First(t=>!t.UseFocused);
            check(first.Length == (kind==FocusTargetKind.BrowserTabGroup?4:kind==FocusTargetKind.Site?2:3) && first[0].UseFocused && ordinary.Key != first[0].Key
                && first[0].Id == PreviewApplication.FocusChoices(kind, [])[0].Id, kind + ": dynamic choice is first and stable across refreshes");
            check(PreviewApplication.MatchesSavedFocusTarget(first[0] with { Name = "Display renamed" }, first[0])
                && !PreviewApplication.MatchesSavedFocusTarget(ordinary, first[0]), kind + ": a dynamic choice cannot match an ordinary target");
        }
        var store = new MemoryStore(); var engine = new TimerEngine(store);
        var options = new FocusModeSettings { Enabled = true, MultipleTargets = true, Targets = dynamic, DelaySeconds = 0 };
        engine.SetFocusMode(options with { Targets = dynamic.Add(dynamic[0] with { Id = Guid.NewGuid() }).Add(window) });
        var saved = JsonSerializer.Deserialize<FocusModeSettings>(JsonSerializer.Serialize(engine.Snapshot.FocusMode))!;
        check(saved.SelectedTargets.Length == 5 && saved.SelectedTargets.Count(t => t.UseFocused) == 4,
            "All four dynamic choices persist together with an ordinary target and deduplicate by category");
        check(!JsonSerializer.Deserialize<FocusTarget>("{\"Id\":\"00000000-0000-0000-0000-000000000001\",\"Kind\":0,\"Name\":\"Legacy\",\"App\":\"synthetic\",\"WindowHandle\":123,\"ProcessId\":456,\"ProcessStartedAt\":789}")!.UseFocused,
            "Older profiles retain exact targets rather than becoming dynamic");
        var bridge = JsonSerializer.SerializeToElement(PreviewSession.FocusView(saved), PreviewSession.Json);
        check(bridge.GetProperty("targets").EnumerateArray().Count(t => t.GetProperty("useFocused").GetBoolean()) == 4,
            "The WebView receives the dynamic flag for every saved category");
        BrowserCapture(check, window);
        foreach (var mode in Enum.GetValues<SessionMode>()) {
            engine = new(new MemoryStore()); engine.SwitchMode(mode); engine.SetFocusMode(options with { MultipleTargets = false, Targets = [dynamic[0]] });
            var source = new Source { Foreground = window }; var alerts = new List<bool>();
            using var monitor = new FocusModeMonitor(engine, source, alerts.Add);
            void Start() { if (mode == SessionMode.Stopwatch) engine.StartStopwatch(); else engine.Start(900, false, 50); }
            check(source.Snapshots == 0, mode + ": saving or loading the dynamic choice does not capture a target");
            Start(); source.Foreground = window with { WindowHandle = 124 }; monitor.Poll();
            check(source.Snapshots == 1 && source.Captures == 1 && source.Checked.Single().WindowHandle == window.WindowHandle,
                mode + ": start captures the foreground identity before a later focus change");
            source.Presence = FocusPresence.Away; monitor.Poll();
            check(alerts.Last(), mode + ": leaving the captured target triggers the existing away alert");
            engine.SetAppVolume(40); engine.SetFocusMode(engine.Snapshot.FocusMode with { DelaySeconds = 2 }); engine.Advance();
            check(source.Snapshots == 1, mode + ": volume, delay and clock updates do not recapture the new foreground");
            engine.Pause(); check(!alerts.Last(), mode + ": pause stops the dynamic target's audio synchronously");
            engine.Resume(); monitor.Poll(); check(source.Snapshots == 2 && source.Checked.Last().WindowHandle == 124,
                mode + ": resume captures the newly focused window for the same session");
            engine.Resume(); check(source.Snapshots == 2, mode + ": an already-running resume command does not replace the target");
            engine.SwitchMode(mode == SessionMode.Timer ? SessionMode.Stopwatch : SessionMode.Timer); monitor.Poll();
            engine.SwitchMode(mode); monitor.Poll();
            check(source.Snapshots == 2, mode + ": switching away and back keeps the session capture without recapturing");
            engine.Resume(); monitor.Poll(); check(source.Snapshots == 3, mode + ": resuming after returning to the mode captures again");
            engine.Reset(); Start(); monitor.Poll(); check(source.Snapshots == 4, mode + ": a new session captures its own target");
            check(engine.Snapshot.FocusMode.SelectedTargets.Single().UseFocused && engine.Snapshot.FocusMode.SelectedTargets.Single().WindowHandle == 0,
                mode + ": captured identities never replace the saved dynamic choice");
        }
        engine = new(new MemoryStore()); engine.SetFocusMode(options);
        var mixed = new Source { Foreground = window, ResolveBrowsers = true }; var transitions = new List<bool>();
        using (var monitor = new FocusModeMonitor(engine, mixed, transitions.Add)) {
            engine.Start(900, false, 50); monitor.Poll();
            check(mixed.Captures == 1 && mixed.LastKinds.SequenceEqual(kinds), "Multiple dynamic categories share one activation snapshot");
            mixed.Presence = FocusPresence.Away; mixed.FocusedKind = FocusTargetKind.BrowserTabGroup; monitor.Poll();
            check(!transitions.Contains(true), "Any captured category can satisfy multi-target focus");
            mixed.FocusedKind = null; monitor.Poll(); check(transitions.Last(), "Leaving every resolved dynamic category starts the alert");
            engine.Pause(); mixed.ResolveBrowsers = false; engine.Resume(); monitor.Poll();
            check(transitions.Last(), "A missing dynamic tab or group does not suppress away detection for the successfully captured window");
        }
        await Stalled(check, window, dynamic[0]);
        engine = new(new MemoryStore()); engine.SetFocusMode(options with { IdleEnabled = true });
        var missing = new Source { Foreground = null, IdleMilliseconds = 20000 }; transitions = [];
        using (var monitor = new FocusModeMonitor(engine, missing, transitions.Add)) {
            engine.Start(900, false, 50); monitor.Poll(); check(transitions.Last(), "Unreadable dynamic targets do not disable the independent idle trigger");
            missing.IdleMilliseconds = 0; monitor.Poll(); check(!transitions.Last(), "Unreadable dynamic targets stop away audio instead of guessing a target");
        }
        engine = new(new MemoryStore()); engine.SetFocusMode(options with { MultipleTargets = false, Targets = [dynamic[0]] });
        engine.Start(900, false, 50); var restoredSource = new Source { Foreground = window };
        using (var monitor = new FocusModeMonitor(engine, restoredSource, _ => { })) {
            monitor.Poll(); check(restoredSource.Snapshots == 0 && restoredSource.Checked.Count == 0,
                "An app restart never silently recaptures an already-running session from the startup window");
            engine.Pause(); engine.Resume(); monitor.Poll(); check(restoredSource.Snapshots == 1, "The next explicit resume resolves a restored dynamic choice");
        }
    }
    private static void BrowserCapture(Action<bool, string> check, FocusTarget window)
    {
        FocusTarget Tab(string id, int position) => window with { Kind = FocusTargetKind.BrowserTab, TabRuntimeId = id, TabPosition = position, Name = "Same title" };
        BrowserTabSlot[] strip = [new("group", "strip", "group Work - 2 tabs, • Example - Expanded", true, null),
            new("one", "strip", "Same title - Part of group Work", false, true), new("two", "strip", "Same title - Part of group Work", false, false),
            new("outside", "strip", "Same title", false, false)];
        FocusTargetKind[] kinds = [FocusTargetKind.Window, FocusTargetKind.BrowserTab, FocusTargetKind.BrowserTabGroup];
        BrowserTabChoice[] tabs = [new(Tab("one", 1), true), new(Tab("two", 2), false), new(Tab("outside", 3), false)];
        var captured = FocusedBrowserTargets.Capture(window, kinds, tabs, strip);
        check(captured.Count == 3 && captured[1].TabRuntimeId == "one" && captured[2].TabRuntimeId == "group",
            "The actual selected tab and its group resolve from the same foreground window snapshot");
        check(FocusedBrowserTargets.Capture(window, kinds, [new(Tab("one", 1) with { WindowHandle = 999 }, true)], []).Count == 1,
            "A tab from another window with the same name is never substituted for a focused target");
        var ambiguous = tabs.Select(t => t.Target.TabRuntimeId == "outside" ? t with { Selected = true } : t).ToArray();
        var mixedStrip = strip.Select(t => t.Id == "outside" ? t with { Selected = true } : t).ToArray();
        check(FocusedBrowserTargets.Capture(window, kinds, ambiguous, mixedStrip).Count == 1,
            "Ambiguous browser multi-selection across groups fails quietly");
        ambiguous[0] = ambiguous[0] with { KeyboardFocused = true };
        check(FocusedBrowserTargets.Capture(window, kinds, ambiguous, mixedStrip).Count == 3,
            "Exposed keyboard focus identifies the exact tab and group within a multi-selection");
        var outside = tabs.Select(t => t with { Selected = t.Target.TabRuntimeId == "outside" }).ToArray();
        check(FocusedBrowserTargets.Capture(window, kinds, outside, strip.Select(t => t with { Selected = t.Id == "outside" }).ToArray()).Count == 2,
            "An ungrouped active tab never borrows another group's identity");
        var groupSelected = tabs.Select(t => t with { Selected = t.Target.TabRuntimeId != "outside" }).ToArray();
        var groupStrip = strip.Select(t => t.Id == "two" ? t with { Selected = true } : t).ToArray();
        captured = FocusedBrowserTargets.Capture(window, kinds, groupSelected, groupStrip);
        check(captured.Count == 2 && captured[1].Kind == FocusTargetKind.BrowserTabGroup,
            "Multiple selected tabs in one group can resolve the group without inventing an active tab");
    }
    private static async Task Stalled(Action<bool, string> check, FocusTarget window, FocusTarget choice)
    {
        var engine = new TimerEngine(new MemoryStore()); engine.SetFocusMode(true, 0, choice);
        var stalled = new TaskCompletionSource<IReadOnlyList<FocusTarget>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new Source { Foreground = window, Pending = stalled }; var alerts = new List<bool>();
        using var monitor = new FocusModeMonitor(engine, source, alerts.Add);
        engine.Start(900, false, 50); monitor.Poll();
        for (var i = 0; i < 4; i++) { engine.Pause(); source.Foreground = window with { WindowHandle = 124 + i }; engine.Resume(); monitor.Poll(); }
        check(source.Captures == 1 && source.Snapshots == 5 && !alerts.Contains(true),
            "A stalled capture cannot queue one browser job per resume or generate false away audio");
        source.Pending = null; stalled.SetResult([window]); await stalled.Task;
        monitor.Poll(); monitor.Poll();
        check(source.Captures == 2 && source.Checked.All(t => t.WindowHandle == 127),
            "An obsolete capture is discarded and only the latest pinned resume target is resolved");
        source.Presence = FocusPresence.Away; monitor.Poll(); check(alerts.Last(), "A recovered latest capture can trigger its own away alert");
        engine.Pause(); source.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously); engine.Resume(); source.Pending.SetException(new InvalidOperationException("Synthetic provider failure"));
        monitor.Poll(); check(!alerts.Last(), "A failed provider capture cannot reuse the previous session target or leave audio playing");
    }
    private sealed class Source : IFocusTargetSource
    {
        internal FocusTarget? Foreground;
        internal int Snapshots, Captures;
        internal bool ResolveBrowsers;
        internal FocusPresence Presence = FocusPresence.Focused;
        internal FocusTargetKind? FocusedKind;
        internal FocusTargetKind[] LastKinds = [];
        internal List<FocusTarget> Checked = [];
        internal TaskCompletionSource<IReadOnlyList<FocusTarget>>? Pending;
        public long? IdleMilliseconds { get; set; }
        public FocusTarget? CaptureForeground() { Snapshots++; return Foreground; }
        public Task<IReadOnlyList<FocusTarget>> CaptureAsync(FocusTarget window, IReadOnlyList<FocusTargetKind> kinds)
        {
            Captures++; LastKinds = kinds.ToArray();
            return Pending?.Task ?? Task.FromResult<IReadOnlyList<FocusTarget>>(kinds.Where(k => ResolveBrowsers || k == FocusTargetKind.Window)
                .Select(k => window with { Kind = k, TabRuntimeId = k == FocusTargetKind.Window ? "" : "captured-" + k,
                    SiteHost = k == FocusTargetKind.Site ? "study.example" : "" }).ToArray());
        }
        public Task<FocusPresence> CheckAsync(FocusTarget target) { Checked.Add(target); return Task.FromResult(target.Kind == FocusedKind ? FocusPresence.Focused : Presence); }
        public Task<IReadOnlyList<FocusTarget>> ListAsync(FocusTargetKind kind) => Task.FromResult<IReadOnlyList<FocusTarget>>([]);
        public void Dispose() { }
    }
}
