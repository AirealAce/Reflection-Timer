using System.Text.Json.Nodes;
using ReflectionTimer.Accessible;

static class DesktopUpdateInstallerTests
{
    internal static void Run(Action<bool, string> check)
    {
        using (var fixture = new Fixture()) {
            var originalProfile = File.ReadAllBytes(fixture.Profile);
            var originalHost = File.ReadAllBytes(Path.Combine(fixture.Install, "browser-companion", "native-host.json"));
            var planPath = fixture.Prepare("--tray", "--profile", "update-test");
            var plan = Read(planPath);
            var staging = Text(plan, "Staging");
            check(Path.GetDirectoryName(staging) == Path.GetDirectoryName(fixture.Install), "Updates stage on the installed app's volume");
            check(File.ReadAllText(Path.Combine(staging, "ReflectionTimer.exe")) == "new executable", "Updates stage the complete new executable before closing the app");
            check(File.ReadAllText(Path.Combine(fixture.Install, "ReflectionTimer.exe")) == "old executable", "Preparing an update leaves the running installation unchanged");
            check(File.ReadAllBytes(Path.Combine(staging, "browser-companion", "native-host.json")).SequenceEqual(originalHost), "Updates retain configured companion data byte for byte");
            check(File.ReadAllText(Path.Combine(staging, "custom", "user sound.mp3")) == "private custom audio", "Updates preserve locally added MP3s in their original relative folder");
            check(File.ReadAllText(Path.Combine(staging, "bundled.mp3")) == "new bundled audio", "Updated bundled tracks replace old bundled versions");
            check(!File.Exists(Path.Combine(staging, "unrelated.txt")), "Updates do not publish unrelated old installation files into the new app");
            check(File.ReadAllBytes(fixture.Profile).SequenceEqual(originalProfile), "Preparing an update does not touch the separate saved profile");
            check(plan["Arguments"]!.AsArray().Select(item => item!.GetValue<string>()).SequenceEqual(new[] { "--tray", "--profile", "update-test" }), "Updates preserve tray and isolated profile restart arguments");
            DesktopUpdateInstaller.VerifyPreparedForTests(planPath);
            DesktopUpdateInstaller.SwapPreparedForTests(planPath);
            var backup = Text(plan, "Backup");
            check(File.ReadAllText(Path.Combine(fixture.Install, "ReflectionTimer.exe")) == "new executable", "A verified update is atomically installed");
            check(File.ReadAllText(Path.Combine(backup, "ReflectionTimer.exe")) == "old executable" && File.ReadAllText(Path.Combine(backup, "unrelated.txt")) == "keep in backup", "Updates retain the complete old installation in a recoverable backup");
            check(File.ReadAllBytes(fixture.Profile).SequenceEqual(originalProfile), "Swapping an update leaves the separate saved profile byte for byte unchanged");
            check(File.ReadAllBytes(Path.Combine(fixture.Install, "browser-companion", "native-host.json")).SequenceEqual(originalHost), "Installed updates preserve the browser connection's final executable path");
        }

        using (var fixture = new Fixture()) {
            var planPath = fixture.Prepare();
            var moves = 0;
            Rejected(() => DesktopUpdateInstaller.SwapPreparedForTests(planPath, (from, to) => {
                moves++;
                if (moves == 2) throw new IOException("Synthetic second move failure");
                Directory.Move(from, to);
            }), check, "Updates report a failed replacement instead of silently publishing a partial app");
            check(moves == 3 && File.ReadAllText(Path.Combine(fixture.Install, "ReflectionTimer.exe")) == "old executable", "Updates roll the old folder back when the second rename fails");
            check(Directory.Exists(Text(Read(planPath), "Staging")), "Failed swaps keep the staged update for diagnosis instead of destroying user files");
        }
        using (var fixture = new Fixture()) {
            var planPath = fixture.Prepare();
            Rejected(() => DesktopUpdateInstaller.SwapPreparedForTests(planPath, (_, _) => throw new IOException("Synthetic locked install")), check,
                "A locked installed app cancels the update without killing any process");
            check(File.ReadAllText(Path.Combine(fixture.Install, "ReflectionTimer.exe")) == "old executable", "A failed initial rename leaves the complete old installation in place");
        }

        using (var fixture = new Fixture()) {
            var planPath = fixture.Prepare(); var plan = Read(planPath);
            File.AppendAllText(Path.Combine(Text(plan, "Staging"), "Web", "app.js"), "tampered");
            Rejected(() => DesktopUpdateInstaller.VerifyPreparedForTests(planPath), check, "Updates reject a modified staged file before swapping folders");
            check(File.ReadAllText(Path.Combine(fixture.Install, "ReflectionTimer.exe")) == "old executable", "Rejected staged updates leave the original app available");
        }
        using (var fixture = new Fixture()) {
            var planPath = fixture.Prepare();
            File.AppendAllText(Path.Combine(fixture.Payload, "ReflectionTimer.dll"), "tampered");
            Rejected(() => DesktopUpdateInstaller.VerifyPreparedForTests(planPath), check, "Updates reject a modified downloaded helper package");
        }
        using (var fixture = new Fixture()) {
            var planPath = fixture.Prepare(); var plan = Read(planPath);
            File.WriteAllText(Path.Combine(Text(plan, "Staging"), "injected.dll"), "extra binary");
            Rejected(() => DesktopUpdateInstaller.VerifyPreparedForTests(planPath), check, "Updates reject an unlisted binary injected into staging");
        }
        using (var fixture = new Fixture()) {
            var planPath = fixture.Prepare(); var id = Text(Read(planPath), "Id");
            Rejected(() => DesktopUpdateInstaller.AcknowledgeAfterValidationForTests(planPath, () => {
                using (var launcherSignal = new EventWaitHandle(false, EventResetMode.ManualReset, @"Local\ReflectionTimerUpdateCancelled-" + id)) launcherSignal.Set();
                File.WriteAllText(planPath + ".cancelled", "Synthetic launcher ready timeout while helper was hashing.");
            }), check, "A helper that finishes verification after the launch timeout still rejects the persistent cancellation marker");
            check(File.ReadAllText(Path.Combine(fixture.Install, "ReflectionTimer.exe")) == "old executable", "A delayed cancelled helper leaves the running installation unchanged");
            var ready = false;
            try { using var signal = EventWaitHandle.OpenExisting(@"Local\ReflectionTimerUpdateReady-" + id); ready = signal.WaitOne(0); }
            catch (WaitHandleCannotBeOpenedException) { }
            check(!ready, "A timed-out helper does not acknowledge readiness after the launcher's cancellation event handle has closed");
        }
        using (var fixture = new Fixture()) {
            var planPath = fixture.Prepare(); var moves = 0;
            Rejected(() => DesktopUpdateInstaller.SwapPreparedForTests(planPath, (from, to) => { moves++; Directory.Move(from, to); },
                () => File.WriteAllText(planPath + ".cancelled", "Synthetic cancellation immediately before the swap.")), check,
                "A cancellation received after package verification is rechecked immediately before swapping");
            check(moves == 0 && File.ReadAllText(Path.Combine(fixture.Install, "ReflectionTimer.exe")) == "old executable",
                "Cancelling at the final swap gate performs no folder renames");
        }

        foreach (var forbidden in new[] { "state.dat", "state.dat.bak", "nested/diagnostics.dat", "browser-companion/native-host.json" }) {
            using var fixture = new Fixture();
            Write(fixture.Payload, forbidden, "must not ship");
            Rejected(() => fixture.Prepare(), check, "Updates reject downloaded personal data: " + forbidden);
        }
        using (var fixture = new Fixture()) {
            File.Delete(Path.Combine(fixture.Payload, "coreclr.dll"));
            Rejected(() => fixture.Prepare(), check, "Updates require the self-contained runtime instead of requiring an SDK");
        }
        using (var fixture = new Fixture()) {
            Rejected(() => DesktopUpdateInstaller.Prepare(fixture.Install, fixture.Install, Path.Combine(fixture.Install, "ReflectionTimer.exe"), []), check,
                "Updates reject identical source and installation folders");
            var nested = Path.Combine(fixture.Install, "download"); Directory.CreateDirectory(nested);
            Rejected(() => DesktopUpdateInstaller.Prepare(nested, fixture.Install, Path.Combine(fixture.Install, "ReflectionTimer.exe"), []), check,
                "Updates reject downloaded helpers nested inside the installation being renamed");
            Rejected(() => DesktopUpdateInstaller.Prepare(fixture.Payload, fixture.Install, Path.Combine(fixture.Payload, "ReflectionTimer.exe"), []), check,
                "Updates reject an executable that does not match the destination installation");
            Rejected(() => fixture.Prepare("--check-connection"), check, "Update restart arguments cannot run connection checks instead of opening the app");
            Rejected(() => fixture.Prepare("--profile", "../escape"), check, "Update restart arguments cannot escape the profile name boundary");
        }

        foreach (var field in new[] { "Install", "Staging", "Backup", "Executable", "Payload" }) {
            using var fixture = new Fixture();
            var planPath = fixture.Prepare(); var plan = Read(planPath);
            plan[field] = Path.Combine(fixture.Root, "outside-" + field);
            File.WriteAllText(planPath, plan.ToJsonString());
            Rejected(() => DesktopUpdateInstaller.VerifyPreparedForTests(planPath), check, "Updates reject a plan with a redirected " + field.ToLowerInvariant() + " path");
        }
        using (var fixture = new Fixture()) {
            var planPath = fixture.Prepare(); var plan = Read(planPath);
            plan["PackageFiles"]![0]!["Path"] = "../outside.txt";
            File.WriteAllText(planPath, plan.ToJsonString());
            Rejected(() => DesktopUpdateInstaller.VerifyPreparedForTests(planPath), check, "Updates reject file entries escaping the downloaded package");
        }
        using (var fixture = new Fixture()) {
            var planPath = fixture.Prepare(); var plan = Read(planPath);
            plan["StagedFiles"]![0]!["Path"] = "C:/outside.dll";
            File.WriteAllText(planPath, plan.ToJsonString());
            Rejected(() => DesktopUpdateInstaller.VerifyPreparedForTests(planPath), check, "Updates reject rooted staged file entries");
        }
        using (var fixture = new Fixture()) {
            check(DesktopUpdateInstaller.IsUpdateAllowed(fixture.Install, out var reason) && reason.Length == 0, "A dedicated writable portable app folder supports updates");
            check(!Directory.EnumerateFiles(fixture.Root).Any(path => Path.GetFileName(path).StartsWith(".reflection-timer-write-", StringComparison.Ordinal)), "Checking update support leaves no write probe behind");
            check(!DesktopUpdateInstaller.IsUpdateAllowed(Path.GetPathRoot(fixture.Install)!, out _) &&
                !DesktopUpdateInstaller.IsUpdateAllowed(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), out _), "Updates refuse drive and user-home destinations");
            check(DesktopUpdateInstaller.HostStopEventName(fixture.Install) == DesktopUpdateInstaller.HostStopEventName(fixture.Install.ToUpperInvariant() + Path.DirectorySeparatorChar), "Browser host update signals use normalized installation identities");
            check(!DesktopUpdateInstaller.IsHostStopping(fixture.Install), "Normal installations have no stale browser stop signal");
            using (var signal = new EventWaitHandle(false, EventResetMode.ManualReset, DesktopUpdateInstaller.HostStopEventName(fixture.Install))) {
                check(!DesktopUpdateInstaller.IsHostStopping(fixture.Install), "An untriggered update signal allows normal browser connections");
                signal.Set();
                check(DesktopUpdateInstaller.IsHostStopping(fixture.Install), "A pending update blocks only the matching installation's browser host respawns");
                check(!DesktopUpdateInstaller.IsHostStopping(fixture.Payload), "Updating one installation leaves other installations' browser host signals unchanged");
                signal.Reset();
            }
        }

        // Creating Windows directory links requires Developer Mode or the symbolic-link privilege.
        // If available, exercise the actual OS link behavior rather than mocking file attributes.
        using (var fixture = new Fixture()) {
            var outside = Path.Combine(fixture.Root, "outside"); Directory.CreateDirectory(outside);
            var link = Path.Combine(fixture.Payload, "linked");
            try {
                Directory.CreateSymbolicLink(link, outside);
                Rejected(() => fixture.Prepare(), check, "Updates reject linked payload directories instead of following paths outside the package");
                Directory.Delete(link);
            }
            catch (UnauthorizedAccessException) { }
            catch (IOException) when (!Directory.Exists(link)) { }
        }
    }

    private static JsonObject Read(string path) => JsonNode.Parse(File.ReadAllText(path))!.AsObject();
    private static string Text(JsonObject plan, string name) => plan[name]!.GetValue<string>();
    private static void Rejected(Action action, Action<bool, string> check, string name)
    {
        var rejected = false;
        try { action(); }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or InvalidOperationException) { rejected = true; }
        check(rejected, name);
    }
    private static void Write(string root, string relative, string content)
    {
        var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, content);
    }

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "ReflectionTimer-UpdateInstallTests-" + Guid.NewGuid().ToString("N"));
        internal string Install => Path.Combine(Root, "installed app");
        internal string Payload => Path.Combine(Root, "verified package");
        internal string Profile => Path.Combine(Root, "saved profile", "state.dat");
        internal Fixture()
        {
            foreach (var file in new[] { "ReflectionTimer.exe", "ReflectionTimer.dll", "coreclr.dll", "ReflectionTimer.runtimeconfig.json", "Web/index.html", "Web/app.js", "Web/compact.html", "Web/themes.css", "browser-companion/manifest.json", "browser-companion/background.js", "bundled.mp3" })
                Write(Payload, file, file == "ReflectionTimer.exe" ? "new executable" : file == "bundled.mp3" ? "new bundled audio" : "new package file");
            Write(Install, "ReflectionTimer.exe", "old executable");
            Write(Install, "custom/user sound.mp3", "private custom audio");
            Write(Install, "bundled.mp3", "old bundled audio");
            Write(Install, "browser-companion/native-host.json", "{\"path\":\"synthetic configured original final executable path\"}");
            Write(Install, "unrelated.txt", "keep in backup");
            Write(Root, "saved profile/state.dat", "synthetic encrypted saved profile bytes");
        }
        internal string Prepare(params string[] args) => DesktopUpdateInstaller.Prepare(Payload, Install, Path.Combine(Install, "ReflectionTimer.exe"), args);
        public void Dispose()
        {
            var full = Path.GetFullPath(Root);
            if (!full.StartsWith(Path.GetFullPath(Path.GetTempPath()) + "ReflectionTimer-UpdateInstallTests-", StringComparison.OrdinalIgnoreCase)) throw new IOException("Unexpected update test path.");
            // This unique test-owned tree contains only synthetic fixtures; never the user's installation or profile.
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }
}
