using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32;
using ReflectionTimer.Core;

namespace ReflectionTimer.Desktop;

public sealed class EncryptedStore(string directory) : IStateStore
{
    private readonly string path = Path.Combine(directory, "state.dat");
    public string? RecoveryNotice { get; private set; }
    public AppState Load()
    {
        if (!File.Exists(path)) return AppState.CreateDefault();
        try { return Read<AppState>(path); }
        catch (Exception) when (File.Exists(path + ".bak"))
        {
            var backup = Read<AppState>(path + ".bak");
            RecoveryNotice = "A backup was recovered. Timers are paused and unsent entries need review to avoid duplicate alerts or uploads. Review your timer, schedules, and Outbox, then confirm the switch in Settings again.";
            // Preserve the unreadable file for recovery, rather than overwriting it.
            File.Copy(path, path + ".unreadable-" + Guid.NewGuid().ToString("N"), false);
            backup.Timer = backup.Timer with { IsRunning = false, EndTime = null,
                RemainingSeconds = TimerEngine.Remaining(backup.Timer, DateTimeOffset.Now.ToUnixTimeMilliseconds()) };
            backup.ExtensionDisabledConfirmed = false;
            backup.Outbox = backup.Outbox.Select(x => x.Status != DeliveryStatus.Sent
                ? x with { Status = DeliveryStatus.NeedsReview, ErrorKind = "interrupted" } : x).ToList();
            // Replace only after the original is preserved. The repaired state must
            // survive another restart without reusing an older Pending snapshot.
            Write(path, backup);
            return backup;
        }
    }
    public void Save(AppState state) => Write(path, state);
    public static T Read<T>(string path)
    {
        var plain = ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser);
        try { return JsonSerializer.Deserialize<T>(plain, DataJson.Options) ?? throw new InvalidDataException("The saved data is empty."); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    public static void Write<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var plain = JsonSerializer.SerializeToUtf8Bytes(value, DataJson.Options);
        byte[] encrypted;
        try { encrypted = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser); }
        finally { CryptographicOperations.ZeroMemory(plain); }
        var temporary = path + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        { stream.Write(encrypted); stream.Flush(true); }
        if (File.Exists(path)) File.Replace(temporary, path, path + ".bak", true);
        else File.Move(temporary, path);
    }
}

public sealed class DiagnosticLog
{
    private readonly string path;
    private readonly object gate = new();
    private List<Activity> events = [];
    private static readonly HashSet<string> Allowed = new(StringComparer.Ordinal) {
        "app.started", "app.exiting", "app.activated", "app.deactivated", "app.hidden", "system.resume", "system.session",
        "timer.started", "timer.paused", "timer.resumed", "timer.reset", "timer.preferences", "timer.deadline", "timer.autoRestartDisabled",
        "schedule.saved", "schedule.removed", "prompt.test", "prompt.shown", "prompt.later", "prompt.draftSaved", "prompt.skipped",
        "reflection.queued", "reflection.endedAndQueued", "reflection.autoSent", "upload.started", "upload.sent", "upload.needsReview", "upload.recovered", "upload.retryRequested",
        "upload.confirmedByUser", "settings.saved", "connection.checked", "issue.marked", "error.storage", "error.unexpected",
        "sound.changed", "sound.preview", "sound.played", "sound.fallback", "sound.muted", "sound.stopped", "sound.failed", "display.changed", "theme.changed",
        "shortcut.registered", "shortcut.unavailable", "shortcut.used", "timer.endedEarly", "timer.lowTime", "timer.lowTimeOptions", "sound.thresholdChanged", "sound.requested",
        "schedule.policy", "schedule.resolved", "prompt.checkIn", "webview.processFailed", "webview.failureReason", "webview.exitCode",
        "webview.recoveryStarted", "webview.recovered", "webview.recoveryFailed"
    };
    public bool Enabled { get; set; } = true;
    public bool StorageAvailable { get; private set; } = true;
    public DiagnosticLog(string directory)
    {
        path = Path.Combine(directory, "diagnostics.dat");
        if (File.Exists(path))
        {
            try { events = EncryptedStore.Read<List<Activity>>(path).Where(x => Allowed.Contains(x.Event)).ToList(); }
            catch { StorageAvailable = false; }
        }
    }
    public void Record(string name, Guid? item = null, long? value = null) => Record(new(DateTimeOffset.Now.ToUnixTimeMilliseconds(), name, item, value));
    public void Record(Activity entry)
    {
        if (!Enabled || !Allowed.Contains(entry.Event)) return;
        lock (gate)
        {
            events.Add(entry); Prune();
            try { EncryptedStore.Write(path, events); StorageAvailable = true; }
            catch { StorageAvailable = false; } // Never interrupt a timer to write diagnostics.
        }
    }
    private void Prune() => events = events.Where(x => x.At >= DateTimeOffset.Now.AddDays(-7).ToUnixTimeMilliseconds()).TakeLast(1200).ToList();
    public IReadOnlyList<Activity> Recent() { lock (gate) { Prune(); return events.ToArray(); } }
    public void Clear() { lock (gate) { EncryptedStore.Write(path, new List<Activity>()); events = []; StorageAvailable = true; } }
    public object Report(AppState state) => new {
        FormatVersion = 1, AppVersion = typeof(DiagnosticLog).Assembly.GetName().Version?.ToString(3), ExportedAt = DateTimeOffset.Now,
        Privacy = "No reflection text, drafts, connection credentials, browsing URLs, window titles, audio filenames/paths, or other-app activity.",
        CustomAlertSound = !string.IsNullOrEmpty(state.AlertSoundPath),
        PopupPosition = state.PopupPosition.ToString(),
        Theme = state.Theme.ToString(),
        ScheduleOverlap = state.ScheduleOverlap.ToString(), state.ShowFloatingTimer, FloatingPlacement = state.FloatingPlacement.ToString(),
        Audio = Enum.GetValues<SoundEvent>().Select(kind => new { Event = kind.ToString(), AudioSettings.From(state).For(kind).Behavior,
            AudioSettings.From(state).For(kind).Track, Custom = AudioSettings.From(state).For(kind).Mp3Path.Length > 0 }),
        AudioSettings.From(state).LowTimeThresholdSeconds,
        Enabled, StorageAvailable, Events = Recent(), Timer = state.Timer with { LowTime = state.Timer.LowTime with { Mp3Path = "" } },
        TimerLowTimeCustom = state.Timer.LowTime.Mp3Path.Length > 0,
        Schedules = state.Schedules.Select(x => x with { LowTime = x.LowTime with { Mp3Path = "" } }), PendingPrompts = state.Prompts.Count,
        Outbox = state.Outbox.Select(x => new { x.Id, x.SubmittedAt, x.Status, x.IsTest, x.Attempts, x.RetryProtected,
            x.NextAttemptAt, x.DurationSeconds, x.ActualDurationSeconds, x.EndedEarly, x.IsCheckIn, x.AutoSent, ErrorKind = TimerEngine.SafeError(x.ErrorKind) }),
        state.ExtensionDisabledConfirmed
    };
}

public static class StartupRegistration
{
    private const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Name = "Reflection Timer Desktop";
    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(Key);
        if (enabled) key.SetValue(Name, $"\"{Environment.ProcessPath}\" --tray");
        else key.DeleteValue(Name, false);
    }
}
