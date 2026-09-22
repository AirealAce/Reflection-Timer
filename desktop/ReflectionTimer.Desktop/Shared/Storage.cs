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
            var recoveredAt = DateTimeOffset.Now;
            backup.Timer = TimerEngine.PauseRecoveredTimer(backup.Timer, recoveredAt);
            if (backup.ParkedTimer is { } parked) backup.ParkedTimer = TimerEngine.PauseRecoveredTimer(parked, recoveredAt);
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

public sealed class DiagnosticLog : IDisposable
{
    private readonly string path;
    private readonly object gate = new();
    private readonly object writeGate = new();
    private readonly Action<string, IReadOnlyList<Activity>> persist;
    private readonly System.Threading.Timer? writer;
    private long revision, savedRevision;
    private bool disposed;
    private List<Activity> events = [];
    private static readonly HashSet<string> Allowed = new(StringComparer.Ordinal) {
        "app.started", "app.exiting", "app.activated", "app.deactivated", "app.hidden", "system.resume", "system.session",
        "timer.started", "timer.paused", "timer.resumed", "timer.reset", "timer.preferences", "timer.deadline", "timer.autoRestartDisabled", "timer.checkpoint", "timer.clockAdjusted",
        "schedule.saved", "schedule.removed", "prompt.test", "prompt.shown", "prompt.later", "prompt.draftSaved", "prompt.skipped",
        "reflection.queued", "reflection.endedAndQueued", "reflection.autoSent", "upload.started", "upload.sent", "upload.needsReview", "upload.recovered", "upload.retryRequested",
        "upload.confirmedByUser", "settings.saved", "connection.checked", "issue.marked", "error.storage", "error.unexpected",
        "sound.changed", "sound.preview", "sound.played", "sound.fallback", "sound.muted", "sound.stopped", "sound.failed", "display.changed", "theme.changed", "theme.loaded",
        "shortcut.registered", "shortcut.unavailable", "shortcut.used", "timer.endedEarly", "timer.lowTime", "timer.lowTimeOptions", "sound.thresholdChanged", "sound.requested",
        "schedule.policy", "schedule.resolved", "prompt.checkIn", "webview.processFailed", "webview.failureReason", "webview.exitCode",
        "webview.recoveryStarted", "webview.recovered", "webview.recoveryFailed",
        "session.modeChanged", "stopwatch.started", "stopwatch.reviewOpened", "stopwatch.timeReached", "stopwatch.alertChanged"
    };
    public bool Enabled { get; set; } = true;
    private volatile bool storageAvailable = true;
    public bool StorageAvailable { get => storageAvailable; private set => storageAvailable=value; }
    public DiagnosticLog(string directory) : this(directory, (file, entries) => EncryptedStore.Write(file, entries), true) { }
    internal DiagnosticLog(string directory, Action<string, IReadOnlyList<Activity>> persist, bool background)
    {
        this.persist = persist;
        path = Path.Combine(directory, "diagnostics.dat");
        if (File.Exists(path))
        {
            try { events = EncryptedStore.Read<List<Activity>>(path).Where(x => Allowed.Contains(x.Event)).ToList(); }
            catch { StorageAvailable = false; }
        }
        if(background)writer = new(_ => Flush(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }
    public void Record(string name, Guid? item = null, long? value = null) => Record(new(DateTimeOffset.Now.ToUnixTimeMilliseconds(), name, item, value));
    public void Record(Activity entry)
    {
        if (!Enabled || !Allowed.Contains(entry.Event)) return;
        lock (gate)
        {
            if(disposed)return;
            events.Add(entry); Prune();
            revision++;
        }
    }
    private void Prune() {
        var cutoff=DateTimeOffset.Now.AddDays(-7).ToUnixTimeMilliseconds();
        events.RemoveAll(x=>x.At<cutoff);
        if(events.Count>1200)events.RemoveRange(0,events.Count-1200);
    }
    public IReadOnlyList<Activity> Recent() { lock (gate) { Prune(); return events.ToArray(); } }
    // Disk I/O is serialized separately from Record, so a slow diagnostic disk
    // write never holds the timer/UI path. Only diagnostic events are buffered.
    public void Flush() => FlushCore(false);
    private void FlushCore(bool final) {
        lock(writeGate) {
            Activity[] batch; long captured;
            lock(gate) {
                if(disposed&&!final)return;
                if(revision==savedRevision)return;
                Prune(); batch=events.ToArray(); captured=revision;
            }
            try { persist(path,batch);lock(gate){savedRevision=captured;StorageAvailable=true;} }
            catch { lock(gate)StorageAvailable=false; } // Keep the batch dirty for retry.
        }
    }
    public void Clear() {
        lock(writeGate)lock(gate) {
            ObjectDisposedException.ThrowIf(disposed,this);
            try {persist(path,Array.Empty<Activity>());events.Clear();savedRevision=++revision;StorageAvailable=true;}
            catch {StorageAvailable=false;throw;}
        }
    }
    public void Dispose() {
        lock(gate){if(disposed)return;disposed=true;}
        writer?.Dispose();FlushCore(true);
    }
    public object Report(AppState state, long? elapsedAt = null, long? calendarAt = null) => new {
        FormatVersion = 1, AppVersion = typeof(DiagnosticLog).Assembly.GetName().Version?.ToString(3), ExportedAt = DateTimeOffset.Now,
        TimerClock = new { ElapsedAt = elapsedAt, CalendarAt = calendarAt,
            Note = "Runtime Timer/ParkedTimer EndTime and RunningSince use ElapsedAt, not calendar time. Event, schedule, cutoff and submission dates use calendar time." },
        Privacy = "No reflection text, drafts, connection credentials, browsing URLs, window titles, audio filenames/paths, or other-app activity.",
        CustomAlertSound = !string.IsNullOrEmpty(state.AlertSoundPath),
        PopupPosition = state.PopupPosition.ToString(),
        Theme = state.Theme.ToString(),
        ScheduleOverlap = state.ScheduleOverlap.ToString(), state.ShowFloatingTimer, FloatingPlacement = state.FloatingPlacement.ToString(),
        Audio = Enum.GetValues<SoundEvent>().Select(kind => new { Event = kind.ToString(), AudioSettings.From(state).For(kind).Behavior,
            AudioSettings.From(state).For(kind).Track, Custom = AudioSettings.From(state).For(kind).Mp3Path.Length > 0 }),
        AudioSettings.From(state).LowTimeThresholdSeconds,
        AudioSettings.From(state).TimeReachedEnabled,AudioSettings.From(state).TimeReachedSeconds,
        Enabled, StorageAvailable, Events = Recent(), Timer = state.Timer with { LowTime = state.Timer.LowTime with { Mp3Path = "" } },
        TimerLowTimeCustom = state.Timer.LowTime.Mp3Path.Length > 0,
        Schedules = state.Schedules.Select(x => x with { LowTime = x.LowTime with { Mp3Path = "" } }), PendingPrompts = state.Prompts.Count,
        ParkedTimer = state.ParkedTimer is {} parked ? parked with { LowTime=parked.LowTime with { Mp3Path="" } } : null,
        Outbox = state.Outbox.Select(x => new { x.Id, Mode=x.Mode.ToString(), x.SubmittedAt, x.Status, x.IsTest, x.Attempts, x.RetryProtected,
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
