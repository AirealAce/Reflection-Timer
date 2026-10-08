using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ReflectionTimer.Accessible;

/// <summary>Stages a complete self-contained update and swaps it only after the app has saved and exited.</summary>
public static class DesktopUpdateInstaller
{
    private const string ExecutableName = "ReflectionTimer.exe";
    private const string CompanionConfiguration = "browser-companion/native-host.json";
    private static readonly StringComparer Paths = StringComparer.OrdinalIgnoreCase;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private sealed record FileEntry(string Path, string Sha256);
    private sealed record UpdatePlan(int Format, string Id, string Payload, string Install, string Staging,
        string Backup, string Executable, string[] Arguments, FileEntry[] PackageFiles, FileEntry[] StagedFiles);

    public static string HostStopEventName(string installDirectory)
    {
        var normalized = FullDirectory(installDirectory).ToUpperInvariant();
        return @"Local\ReflectionTimerUpdateHosts-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..24];
    }

    /// <summary>Browsers must not reopen the old executable while its installation is being replaced.</summary>
    public static bool IsHostStopping(string installDirectory)
    {
        try { using var signal = EventWaitHandle.OpenExisting(HostStopEventName(installDirectory)); return signal.WaitOne(0); }
        catch (WaitHandleCannotBeOpenedException) { return false; }
    }

    public static bool IsUpdateAllowed(string installDirectory, out string reason)
    {
        try {
            var install = ValidateInstallDirectory(installDirectory);
            EnsureWritableParent(install);
            reason = "";
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) {
            reason = "This app folder cannot be updated. Extract the latest download into a writable folder and run it there.";
            return false;
        }
    }

    /// <summary>The caller verifies the downloaded package's published hashes before invoking this method.</summary>
    public static string Prepare(string payloadRoot, string installDirectory, string executablePath, IReadOnlyList<string> restartArgs)
    {
        var install = ValidateInstallDirectory(installDirectory);
        var payload = FullDirectory(payloadRoot);
        RequireSeparate(payload, install);
        ValidateTree(payload);
        EnsureWritableParent(install);
        if (!Paths.Equals(Path.GetFullPath(executablePath), Path.Combine(install, ExecutableName)))
            throw new InvalidDataException("The update destination does not match this app executable.");
        var parsed = PreviewStartup.Parse(restartArgs.ToArray());
        if (parsed.CheckConnection) throw new InvalidDataException("An update must restart the app, not its connection checker.");
        ValidatePackage(payload);
        var id = Guid.NewGuid().ToString("N");
        var staging = install + "-update-" + id;
        var backup = install + "-backup-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + id;
        // Keep the helper plan beside the verified payload so preparing/retrying cannot
        // add mutable control files to the package's integrity inventory.
        var planPath = Path.Combine(Path.GetDirectoryName(payload)!, "update-plan-" + id + ".json");
        var package = Inventory(payload);
        try {
            CopyTree(payload, staging);
            PreserveLocalFiles(install, staging);
            var staged = Inventory(staging);
            var plan = new UpdatePlan(1, id, payload, install, staging, backup,
                Path.Combine(install, ExecutableName), restartArgs.ToArray(), package, staged);
            File.WriteAllText(planPath, JsonSerializer.Serialize(plan, Json), new UTF8Encoding(false));
            ValidatePlan(planPath, verifyFiles: true);
            return planPath;
        }
        catch {
            // This directory is uniquely generated and is never the installed app or its backup.
            DeleteOwnedStaging(staging, install, id);
            throw;
        }
    }

    /// <summary>Launches the fresh payload's helper; the caller then performs its ordinary saved shutdown.</summary>
    public static void Launch(string planPath, int parentProcessId)
    {
        var plan = ValidatePlan(planPath, verifyFiles: true);
        if (IsHostStopping(plan.Install)) throw new IOException("An update for this app folder is already in progress.");
        using var parent = Process.GetProcessById(parentProcessId);
        if (!Paths.Equals(parent.MainModule?.FileName, plan.Executable))
            throw new InvalidOperationException("The update cannot identify the running timer.");
        using var stopping = new EventWaitHandle(true, EventResetMode.ManualReset, HostStopEventName(plan.Install));
        stopping.Set();
        using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, ReadyEventName(plan.Id));
        ready.Reset();
        using var cancelled = new EventWaitHandle(false, EventResetMode.ManualReset, CancelEventName(plan.Id));
        cancelled.Reset();
        var start = new ProcessStartInfo(Path.Combine(plan.Payload, ExecutableName)) {
            UseShellExecute = false, WorkingDirectory = plan.Payload, CreateNoWindow = true
        };
        start.ArgumentList.Add("--apply-update"); start.ArgumentList.Add(Path.GetFullPath(planPath));
        start.ArgumentList.Add("--wait-for"); start.ArgumentList.Add(parentProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        using var helper = Process.Start(start) ?? throw new IOException("The update helper did not start.");
        if (!ready.WaitOne(TimeSpan.FromSeconds(15))) {
            cancelled.Set();
            File.WriteAllText(Path.GetFullPath(planPath) + ".cancelled", "Update helper startup cancelled.");
            stopping.Reset();
            throw new IOException("The update helper did not become ready. The running app was kept open.");
        }
        // The helper now owns the stop event until the swap and restart are complete.
    }

    /// <summary>Runs before normal app startup, without opening or changing any saved profile.</summary>
    public static int Apply(string planPath, int parentProcessId)
    {
        var plan = ValidatePlan(planPath, verifyFiles: true);
        ThrowIfCancelled(planPath);
        using var exclusive = new Mutex(false, HostStopEventName(plan.Install) + "-Swap");
        var acquired = false;
        try { acquired = exclusive.WaitOne(0); }
        catch (AbandonedMutexException) { acquired = true; }
        if (!acquired) throw new IOException("An update for this app folder is already in progress.");
        try { return ApplyPrepared(planPath, parentProcessId, plan); }
        finally { exclusive.ReleaseMutex(); }
    }

    private static int ApplyPrepared(string planPath, int parentProcessId, UpdatePlan plan)
    {
        if (!Paths.Equals(Environment.ProcessPath, Path.Combine(plan.Payload, ExecutableName)))
            throw new InvalidOperationException("Updates must be applied by the downloaded app helper.");
        using var stopping = new EventWaitHandle(true, EventResetMode.ManualReset, HostStopEventName(plan.Install));
        stopping.Set();
        using var cancelled = new EventWaitHandle(false, EventResetMode.ManualReset, CancelEventName(plan.Id));
        var parentExited = false;
        try {
            AcknowledgeReady(planPath, plan.Id, cancelled);
            WaitForParent(plan, planPath, parentProcessId, cancelled);
            parentExited = true;
            WaitForOtherInstances(plan);
            // Re-read locally added files after saved shutdown in case audio was added during download.
            PreserveLocalFiles(plan.Install, plan.Staging);
            ValidatePackage(plan.Staging, permitCompanionConfiguration: true);
            VerifyFiles(plan.Payload, plan.PackageFiles);
            VerifyFiles(plan.Staging, plan.StagedFiles, allowAdditionalLocalFiles: true);
            ThrowIfCancelled(planPath, cancelled);
            Swap(plan.Install, plan.Staging, plan.Backup, Directory.Move);
            stopping.Reset();
            Restart(plan);
            return 0;
        }
        catch {
            stopping.Reset();
            // A cancelled or failed update retains the original executable and normal saved profile.
            if (parentExited && File.Exists(plan.Executable)) {
                try { Restart(plan); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { }
            }
            throw;
        }
    }

    private static void WaitForParent(UpdatePlan plan, string planPath, int parentProcessId, EventWaitHandle cancelled)
    {
        ThrowIfCancelled(planPath, cancelled);
        if (parentProcessId <= 0 || parentProcessId == Environment.ProcessId) throw new InvalidDataException("Invalid app process.");
        try {
            using var parent = Process.GetProcessById(parentProcessId);
            if (!Paths.Equals(parent.MainModule?.FileName, plan.Executable))
                throw new IOException("The original timer process changed. The update was not applied.");
            var until = Stopwatch.StartNew();
            while (!parent.WaitForExit(150)) {
                ThrowIfCancelled(planPath, cancelled);
                if (until.Elapsed >= TimeSpan.FromSeconds(90)) throw new IOException("The app did not finish saving and closing. The update was not applied.");
            }
        }
        catch (ArgumentException) { /* The app completed its normal saved shutdown before the helper reached this check. */ }
        ThrowIfCancelled(planPath, cancelled);
    }

    private static void WaitForOtherInstances(UpdatePlan plan)
    {
        // Native hosts exit when the stop event is signalled. A second app/profile is never killed.
        var until = Stopwatch.StartNew();
        do {
            var busy = false;
            foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(ExecutableName))) {
                using (process) {
                    if (process.Id == Environment.ProcessId) continue;
                    try { if (Paths.Equals(process.MainModule?.FileName, plan.Executable)) busy = true; }
                    catch (InvalidOperationException) { /* It exited while being inspected. */ }
                    catch (System.ComponentModel.Win32Exception) { throw new IOException("Another timer process could not be checked. Close other timer copies and try again."); }
                }
            }
            if (!busy) return;
            Thread.Sleep(150);
        } while (until.Elapsed < TimeSpan.FromSeconds(15));
        throw new IOException("Another copy of this timer is still open. Close it and try the update again.");
    }

    private static void Restart(UpdatePlan plan)
    {
        var start = new ProcessStartInfo(plan.Executable) { UseShellExecute = false, WorkingDirectory = plan.Install };
        foreach (var argument in plan.Arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("The updated app could not be started. Open the usual app shortcut.");
    }

    private static string ReadyEventName(string id) => @"Local\ReflectionTimerUpdateReady-" + id;
    private static string CancelEventName(string id) => @"Local\ReflectionTimerUpdateCancelled-" + id;

    private static void ThrowIfCancelled(string planPath, EventWaitHandle? cancelled = null)
    {
        // Unlike a named event, this marker survives the launching process and
        // catches a helper whose package verification exceeded the ready timeout.
        if (File.Exists(Path.GetFullPath(planPath) + ".cancelled") || (cancelled?.WaitOne(0) ?? false))
            throw new IOException("This update attempt was cancelled. The running app was kept open.");
    }

    private static void AcknowledgeReady(string planPath, string id, EventWaitHandle cancelled)
    {
        ThrowIfCancelled(planPath, cancelled);
        using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, ReadyEventName(id));
        ready.Set();
    }

    private static UpdatePlan ValidatePlan(string planPath, bool verifyFiles)
    {
        var fullPlan = Path.GetFullPath(planPath);
        ThrowIfCancelled(fullPlan);
        if (!File.Exists(fullPlan) || new FileInfo(fullPlan).Length > 4 * 1024 * 1024) throw new InvalidDataException("Invalid update plan.");
        RejectReparse(fullPlan);
        var plan = JsonSerializer.Deserialize<UpdatePlan>(File.ReadAllText(fullPlan), Json) ?? throw new InvalidDataException("Invalid update plan.");
        if (plan.Format != 1 || !Guid.TryParseExact(plan.Id, "N", out _)) throw new InvalidDataException("Unsupported update plan.");
        var install = ValidateInstallDirectory(plan.Install);
        var payload = FullDirectory(plan.Payload);
        RequireSeparate(payload, install);
        if (!Paths.Equals(fullPlan, Path.Combine(Path.GetDirectoryName(payload)!, "update-plan-" + plan.Id + ".json")) ||
            !Paths.Equals(plan.Executable, Path.Combine(install, ExecutableName)) ||
            !Paths.Equals(plan.Staging, install + "-update-" + plan.Id) ||
            !Paths.Equals(Path.GetDirectoryName(plan.Backup), Path.GetDirectoryName(install)) ||
            !Path.GetFileName(plan.Backup).StartsWith(Path.GetFileName(install) + "-backup-", StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(plan.Backup).EndsWith("-" + plan.Id, StringComparison.Ordinal))
            throw new InvalidDataException("The update plan contains an unsafe path.");
        RejectReparse(payload); RejectReparse(plan.Staging); RejectReparse(plan.Backup);
        var parsed = PreviewStartup.Parse(plan.Arguments);
        if (parsed.CheckConnection) throw new InvalidDataException("Invalid app restart arguments.");
        if (verifyFiles) {
            ValidatePackage(payload);
            VerifyFiles(payload, plan.PackageFiles);
            VerifyFiles(plan.Staging, plan.StagedFiles);
        }
        ThrowIfCancelled(fullPlan);
        return plan;
    }

    private static string ValidateInstallDirectory(string directory)
    {
        var install = FullDirectory(directory);
        if (!Directory.Exists(install) || !File.Exists(Path.Combine(install, ExecutableName)))
            throw new IOException("The installed app folder could not be found.");
        var protectedRoots = new[] {
            Path.GetPathRoot(install), Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs")
        };
        if (protectedRoots.Any(root => !string.IsNullOrWhiteSpace(root) && Paths.Equals(install, FullDirectory(root))))
            throw new IOException("The app must be in its own folder before it can be updated.");
        ValidateTree(install);
        return install;
    }

    private static void EnsureWritableParent(string install)
    {
        var parent = Path.GetDirectoryName(install) ?? throw new IOException("An update needs an app folder.");
        RejectReparse(parent);
        var probe = Path.Combine(parent, ".reflection-timer-write-" + Guid.NewGuid().ToString("N"));
        using var file = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
        file.WriteByte(0);
    }

    private static void ValidatePackage(string root, bool permitCompanionConfiguration = false)
    {
        ValidateTree(root);
        foreach (var required in new[] { ExecutableName, "coreclr.dll", "ReflectionTimer.dll", "ReflectionTimer.runtimeconfig.json", "Web/index.html", "Web/app.js", "Web/compact.html", "Web/themes.css" })
            if (!File.Exists(Child(root, required))) throw new InvalidDataException("The update download is incomplete.");
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) {
            var name = Path.GetFileName(file);
            if (name.StartsWith("state.dat", StringComparison.OrdinalIgnoreCase) || name.StartsWith("diagnostics.dat", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("An update package must not contain saved user data.");
        }
        if (!permitCompanionConfiguration && File.Exists(Child(root, CompanionConfiguration)))
            throw new InvalidDataException("An update package must not contain a user's configured browser companion.");
    }

    private static FileEntry[] Inventory(string root) => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
        .Select(path => new FileEntry(Path.GetRelativePath(root, path).Replace('\\', '/'), Hash(path)))
        .OrderBy(entry => entry.Path, StringComparer.OrdinalIgnoreCase).ToArray();

    private static void VerifyFiles(string root, FileEntry[] entries, string[]? ignore = null, bool allowAdditionalLocalFiles = false)
    {
        ValidateTree(root);
        if (entries is null || entries.Length == 0 || entries.Length > 10000) throw new InvalidDataException("Invalid update file list.");
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries) {
            var path = Child(root, entry.Path);
            if (!known.Add(Path.GetRelativePath(root, path)) || entry.Sha256 is null || entry.Sha256.Length != 64 ||
                !File.Exists(path) || !Paths.Equals(Hash(path), entry.Sha256)) throw new InvalidDataException("The staged update changed. Download it again.");
        }
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) {
            var relative = Path.GetRelativePath(root, file);
            if (known.Contains(relative) || (ignore?.Contains(relative, StringComparer.OrdinalIgnoreCase) ?? false)) continue;
            if (allowAdditionalLocalFiles && (Path.GetExtension(file).Equals(".mp3", StringComparison.OrdinalIgnoreCase) ||
                relative.Replace('\\', '/').Equals(CompanionConfiguration, StringComparison.OrdinalIgnoreCase))) continue;
            throw new InvalidDataException("Unexpected files were found in the update folder.");
        }
    }

    private static void PreserveLocalFiles(string install, string staging)
    {
        ValidateTree(install); ValidateTree(staging);
        foreach (var source in Directory.EnumerateFiles(install, "*", SearchOption.AllDirectories)) {
            var relative = Path.GetRelativePath(install, source).Replace('\\', '/');
            var companion = relative.Equals(CompanionConfiguration, StringComparison.OrdinalIgnoreCase);
            if (!companion && !Path.GetExtension(source).Equals(".mp3", StringComparison.OrdinalIgnoreCase)) continue;
            var destination = Child(staging, relative);
            if (!companion && File.Exists(destination)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(source, destination, overwrite: companion);
            if (!Paths.Equals(Hash(source), Hash(destination))) throw new IOException("Local audio or companion configuration could not be preserved.");
        }
    }

    private static void CopyTree(string source, string destination)
    {
        if (Directory.Exists(destination) || File.Exists(destination)) throw new IOException("The staging folder already exists.");
        Directory.CreateDirectory(destination);
        foreach (var entry in Inventory(source)) {
            var output = Child(destination, entry.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            File.Copy(Child(source, entry.Path), output);
            if (!Paths.Equals(Hash(output), entry.Sha256)) throw new IOException("The update could not be staged completely.");
        }
    }

    private static void Swap(string install, string staging, string backup, Action<string, string> move)
    {
        if (Directory.Exists(backup) || File.Exists(backup)) throw new IOException("The update backup path already exists.");
        move(install, backup);
        try { move(staging, install); }
        catch {
            if (!Directory.Exists(install)) move(backup, install);
            throw;
        }
    }

    private static string FullDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) throw new ArgumentException("A full app path is required.");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    private static string Child(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains(':') ||
            relative.Split('/', '\\').Any(part => part is ".." or "." or "")) throw new InvalidDataException("Invalid update file path.");
        var full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(FullDirectory(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("An update file escapes its folder.");
        RejectReparse(full);
        return full;
    }

    private static void RequireSeparate(string a, string b)
    {
        if (Paths.Equals(a, b) || a.StartsWith(b + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            b.StartsWith(a + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The update download and installed app must be separate folders.");
    }

    private static void RejectReparse(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current)) {
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Linked app folders cannot be updated automatically.");
        }
    }

    private static void ValidateTree(string root)
    {
        RejectReparse(root);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("The app folder was not found.");
        foreach (var item in Directory.EnumerateFileSystemEntries(root)) {
            RejectReparse(item);
            if (Directory.Exists(item)) ValidateTree(item);
        }
    }

    private static string Hash(string path)
    {
        using var file = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(file));
    }

    private static void DeleteOwnedStaging(string staging, string install, string id)
    {
        if (!Paths.Equals(staging, install + "-update-" + id) || !Guid.TryParseExact(id, "N", out _)) return;
        try { ValidateTree(staging); Directory.Delete(staging, recursive: true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    internal static void VerifyPreparedForTests(string planPath) => ValidatePlan(planPath, verifyFiles: true);
    internal static void AcknowledgeAfterValidationForTests(string planPath, Action afterValidation)
    {
        var plan = ValidatePlan(planPath, verifyFiles: true);
        afterValidation();
        // Simulate a helper that opens its event after the launching process has
        // timed out and disposed its handle. The persisted marker must still win.
        using var cancelled = new EventWaitHandle(false, EventResetMode.ManualReset, CancelEventName(plan.Id));
        AcknowledgeReady(planPath, plan.Id, cancelled);
    }
    internal static void SwapPreparedForTests(string planPath, Action<string, string>? move = null, Action? beforeSwap = null)
    {
        var plan = ValidatePlan(planPath, verifyFiles: true);
        beforeSwap?.Invoke();
        ThrowIfCancelled(planPath);
        Swap(plan.Install, plan.Staging, plan.Backup, move ?? Directory.Move);
    }
}
