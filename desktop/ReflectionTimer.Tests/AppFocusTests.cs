using System.Text.Json;
using ReflectionTimer.Accessible;
using ReflectionTimer.Core;

internal static class AppFocusTests
{
    internal static void Run(Action<bool, string> check)
    {
        var first = new FocusTarget(Guid.NewGuid(), FocusTargetKind.Window, "Document one", "Focus Editor", 101, 201, 301) {
            ProcessPath = @"C:\Apps\FocusEditor\editor.exe", WindowClass = "EditorMain", WindowName = "Document one"
        };
        var second = first with { Id = Guid.NewGuid(), Name = "Different tab or window", WindowHandle = 102, ProcessId = 202, ProcessStartedAt = 302 };
        var app = FocusApps.FromWindow(first)!;
        var restarted = FocusApps.FromWindow(second)!;
        check(app.Kind == FocusTargetKind.App && app.Name == "Focus Editor" && app.App == "Focus Editor",
            "An App target uses an app name rather than one document's window title");
        check(app.Key == restarted.Key && app.Id != restarted.Id,
            "App identity survives process, window and list ID changes");
        check(app.WindowHandle == 0 && app.ProcessId == 0 && app.ProcessStartedAt == 0 && app.WindowName == "" && app.TabRuntimeId == "",
            "An App bookmark contains no session-specific window or tab identity");
        check(FocusApps.List([first, second, first]).Count == 1,
            "Multiple windows and processes for one App create one choice");
        check(FocusApps.Matches(app, second) && FocusApps.Matches(app, second with { Kind = FocusTargetKind.BrowserTab, TabRuntimeId = "other-tab" }),
            "All windows and tabs belonging to the same executable satisfy an App target");
        check(FocusApps.Matches(app, first with { ProcessPath = @"c:\apps\FOCUSEDITOR\EDITOR.EXE", Name = "Renamed" }),
            "Executable App identity is case insensitive and independent of display names");
        check(!FocusApps.Matches(app, first with { ProcessPath = @"D:\Other\editor.exe" })
            && !FocusApps.Matches(app, first with { ProcessPath = "" }),
            "A same-named executable in another installation or an unreadable path cannot impersonate the selected App");
        check(FocusApps.List([first, second with { ProcessPath = @"D:\Other\editor.exe" }]).Count == 2,
            "Different installations remain separate despite equal app names");
        check(FocusApps.Presence(app, [second]) == FocusPresence.Focused
            && FocusApps.Presence(app, [second with { ProcessPath = @"C:\Other\other.exe" }]) == FocusPresence.Away,
            "App presence follows the foreground executable rather than a saved process instance");
        check(FocusApps.Presence(app, [first with { ProcessPath = "" }]) == FocusPresence.Unknown && FocusApps.Presence(app, []) == FocusPresence.Unknown,
            "An unreadable foreground stops App alerts without guessing from a title");
        check(FocusApps.Presence(app, [first with { ProcessPath = @"C:\Windows\explorer.exe", App = "Desktop" }]) == FocusPresence.Away,
            "A closed selected App stays a durable away target while another identifiable app is focused");
        foreach (var path in new[] { "", "editor", @"Apps\editor.exe", @"C:editor.exe", @"C:\Apps\editor.dll", @"C:\Windows\ApplicationFrameHost.exe", @"C:\Windows\System32\rundll32.exe" })
            check(!FocusAppIdentity.IsValid(path, ""), "An unsafe or shared App identity is rejected: " + (path.Length == 0 ? "empty" : Path.GetFileName(path)));
        check(FocusApps.FromWindow(first with { ProcessPath = "" }) is null
            && FocusApps.FromWindow(first with { ProcessPath = @"C:\Windows\ApplicationFrameHost.exe" }) is null,
            "Unknown apps and generic UWP frame hosts are omitted from App choices");
        check(FocusAppIdentity.IsValid(@"\\server\apps\editor.exe", ""), "A fully qualified network installation is a valid App identity");
        check(FocusAppIdentity.Key(@"C:\Apps\Folder\..\editor.exe", "") == FocusAppIdentity.Key(@"C:\Apps\editor.exe", ""),
            "Equivalent fully qualified executable paths share one canonical App identity");

        const string packageOne = @"C:\Program Files\WindowsApps\Contoso.FocusNotes_1.2.3.4_x64__abc123def4567\app\FocusNotes.exe";
        const string packageTwo = @"C:\Program Files\WindowsApps\Contoso.FocusNotes_2.0.0.0_x64__abc123def4567\app\FocusNotes.exe";
        check(FocusAppIdentity.Key(packageOne, "") == FocusAppIdentity.Key(packageTwo, ""),
            "A WindowsApps package version update retains the same App bookmark");
        foreach (var changed in new[] {
            packageTwo.Replace("abc123def4567", "xyz987uvw6543"),
            packageTwo.Replace("Contoso.FocusNotes", "Contoso.OtherNotes"),
            packageTwo.Replace("_x64_", "_arm64_"),
            packageTwo.Replace(@"\app\", @"\bin\"),
            packageTwo.Replace("FocusNotes.exe", "OtherNotes.exe"),
            packageTwo.Replace("C:", "D:") })
            check(FocusAppIdentity.Key(packageOne, "") != FocusAppIdentity.Key(changed, ""),
                "Package App matching retains installation, publisher, architecture and executable identity");
        check(FocusAppIdentity.Key(packageOne.Replace("WindowsApps", "Applications"), "") != FocusAppIdentity.Key(packageTwo.Replace("WindowsApps", "Applications"), ""),
            "Version-looking folders outside WindowsApps retain exact App matching");
        var packaged = first with { ProcessPath = packageOne, ApplicationId = "Contoso.FocusNotes_abc123def4567!App" };
        var packagedApp = FocusApps.FromWindow(packaged)!;
        check(FocusApps.Matches(packagedApp, packaged with { ProcessPath = packageTwo, ProcessId = 999, WindowHandle = 1000 }),
            "A package AppUserModelID reconnects through a package update and new processes");
        check(!FocusApps.Matches(packagedApp, packaged with { ApplicationId = "Contoso.OtherNotes_abc123def4567!App" }),
            "Different hosted apps cannot match merely because they share a process executable");
        check(FocusApps.FromWindow(first with { ProcessPath = @"C:\Windows\ApplicationFrameHost.exe", ApplicationId = packaged.ApplicationId }) is not null,
            "A frame host can represent an App only with its specific resolved AppUserModelID");
        check(FocusApps.ResolveHosted("", [(packageOne, packaged.ApplicationId)]) is {} unique && unique.ApplicationId == packaged.ApplicationId,
            "A shared frame can resolve to its uniquely identified real app process");
        check(FocusApps.ResolveHosted(packaged.ApplicationId, [(packageOne, "Contoso.OtherNotes_abc123def4567!App")]) is null,
            "An unrelated embedded app cannot override a frame's explicit AppUserModelID");
        check(FocusApps.ResolveHosted(packaged.ApplicationId, [(first.ProcessPath, "")]) is {} unassigned && unassigned.ApplicationId == packaged.ApplicationId,
            "An unassigned unique child retains the frame's explicit app identity");
        check(FocusApps.ResolveHosted("", [(first.ProcessPath, ""), (@"D:\Other\editor.exe", "")]) is null,
            "Multiple independent child apps cannot be guessed as one shared frame app");
        foreach (var id in new[] { "with spaces", "bad\nidentity", "bad/id", @"bad\id", new string('a', 513) })
            check(!FocusAppIdentity.IsValid(packageOne, id), "Malformed AppUserModelIDs cannot fall back to a different App identity");

        var windowTarget = first;
        var tabTarget = second with { Kind = FocusTargetKind.BrowserTab, TabRuntimeId = "runtime-one" };
        var siteTarget = new FocusTarget(Guid.NewGuid(), FocusTargetKind.Site, "example.com", "Browser", 0, 0, 0) { SiteHost = "example.com" };
        var store = new MemoryStore(); var engine = new TimerEngine(store);
        engine.SetFocusMode(new FocusModeSettings { Enabled = true, MultipleTargets = true, PickerKind = FocusTargetKind.App,
            Targets = [app, windowTarget, tabTarget, siteTarget], DelaySeconds = 8 });
        var restored = new TimerEngine(store).Snapshot.FocusMode;
        check(restored.PickerKind == FocusTargetKind.App && restored.SelectedTargets.Select(t => t.Key).SequenceEqual(new[] { app.Key, windowTarget.Key, tabTarget.Key, siteTarget.Key }),
            "App bookmarks reload alongside Window, Tab and Site targets without replacing them");
        check(JsonSerializer.Deserialize<FocusTarget>(JsonSerializer.Serialize(app)) == app
            && new TimerEngine(store).Snapshot.FocusMode.SelectedTargets.First().ProcessPath == app.ProcessPath,
            "App installation identity survives encrypted-profile model serialization");
        foreach (var invalid in new[] { app with { ProcessPath = "", ApplicationId = "" }, app with { UseFocused = true }, app with { ProcessPath = @"C:\Windows\ApplicationFrameHost.exe" } }) {
            var beforeInvalid = engine.Snapshot.FocusMode;
            var rejected = false;
            try { engine.SetFocusMode(restored with { Targets = [invalid], Target = invalid }); }
            catch (ArgumentException) { rejected = true; }
            check(rejected && engine.Snapshot.FocusMode == beforeInvalid,
                "An invalid or dynamic App selection is rejected without replacing existing settings");
        }
        var choices = PreviewApplication.FocusChoices(FocusTargetKind.App, [app, restarted]);
        check(choices.Length == 1 && choices.All(t => !t.UseFocused),
            "App choices deduplicate durable identities without adding a dynamic window-style capture option");
        check(PreviewApplication.MatchesSavedFocusTarget(restarted with { Name = "Renamed App" }, app),
            "The chooser keeps an App checked through changed names and fresh list IDs");
        var web = JsonSerializer.Serialize(PreviewSession.FocusView(restored), PreviewSession.Json);
        check(!web.Contains("processPath") && !web.Contains("applicationId") && !web.Contains(app.ProcessPath),
            "Native App paths and AppUserModelIDs stay out of WebView data");
        foreach (var mode in Enum.GetValues<SessionMode>()) {
            var gate = new FocusModeGate(); var options = new FocusModeSettings { Enabled = true, Target = app, DelaySeconds = 2 };
            var timer = new TimerState { Mode = mode, IsRunning = true, SessionId = Guid.NewGuid() };
            check(!gate.Evaluate(options, timer, FocusApps.Presence(app, [second]), 1000).Alert
                && !gate.Evaluate(options, timer, FocusPresence.Away, 2000).Alert
                && gate.Evaluate(options, timer, FocusPresence.Away, 4000).Alert
                && !gate.Evaluate(options, timer, FocusApps.Presence(app, [second]), 4001).Alert,
                mode + ": App focus respects the grace delay and returns immediately after relaunch");
        }
    }

    internal static async Task Scan()
    {
        using var source = new WindowsFocusTargets();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var apps = await source.ListAsync(FocusTargetKind.App).WaitAsync(TimeSpan.FromSeconds(10));
        if (apps.Count == 0 || apps.Any(t => t.Kind != FocusTargetKind.App || !FocusAppIdentity.IsValid(t.ProcessPath, t.ApplicationId)) || apps.Select(t => t.Key).Distinct().Count() != apps.Count)
            throw new Exception("App scan returned an invalid or duplicate native identity.");
        var current = apps.Where(source.IsCurrent).ToArray();
        // No native UI focus moves. This exercises both public overloads plus
        // linked-site dispatch without exposing titles or installation paths.
        var ordinary = await source.CheckAnyAsync(apps).WaitAsync(TimeSpan.FromSeconds(5));
        var links = await source.CheckAnyAsync(apps, true).WaitAsync(TimeSpan.FromSeconds(5));
        var noLinks = await source.CheckAnyAsync(apps, false).WaitAsync(TimeSpan.FromSeconds(5));
        if (current.Length > 0 && (ordinary != FocusPresence.Focused || links != FocusPresence.Focused || noLinks != FocusPresence.Focused))
            throw new Exception("A focused App was not recognized consistently by native focus checks.");
        Console.WriteLine(JsonSerializer.Serialize(new { apps = apps.Count, currentApps = current.Length, ordinary, links, noLinks, elapsedMilliseconds = watch.ElapsedMilliseconds }));
    }
}
