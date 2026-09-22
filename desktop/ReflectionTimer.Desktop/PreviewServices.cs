using System.Text.Json;
using ReflectionTimer.Core;
using ReflectionTimer.Desktop;

namespace ReflectionTimer.Accessible;

public sealed record SheetDeliveryIssue(string ErrorKind, string Message, long? NextRetryAt = null);

public sealed class PreviewServices : IDisposable
{
    private readonly TimerEngine engine;
    private readonly SheetsClient sheets;
    private readonly AlertSoundPlayer sounds;
    private readonly CancellationTokenSource stop = new();
    private bool syncing;
    private bool disposed;
    private int connectionRevision;
    private readonly TimeProvider time;
    private int preflightFailures;
    private long preflightFailedAt;
    private TimeSpan preflightRetryDelay;
    private ConnectionSettings? failedConnection;
    public SheetDeliveryIssue? DeliveryIssue { get; private set; }
    public event Action? DeliveryIssueChanged;
    private PendingUploadResult? pendingUploadResult;
    private bool uploadResultSaveFailed;
    private sealed record PendingUploadResult(Guid Id, int Attempt, SheetReply Reply);
    private readonly HashSet<Guid> sounded = [];
    public DiagnosticLog Log { get; }
    public event Action<string>? Announcement;
    public PreviewServices(TimerEngine engine, string directory, SheetsClient? sheets = null, IAlertAudioBackend? audio = null, TimeProvider? time = null)
    {
        this.engine = engine; this.sheets = sheets ?? new(); sounds = new(audio);
        this.time = time ?? TimeProvider.System;
        Log = new(directory) { Enabled = engine.SettingsSnapshot.LoggingEnabled };
        foreach (var prompt in engine.Snapshot.Prompts.Where(p=>!p.IsCheckIn)) sounded.Add(prompt.Id);
        engine.ActivityRecorded += Record;
        engine.LowTimeReached += LowTime;
        engine.TimeReached += TimeReached;
        engine.Changed += Changed;
    }
    private void Record(Activity activity)
    {
        Log.Record(activity);
        // Activity is raised only after the skipped prompt is saved to disk.
        if(activity.Event=="prompt.skipped") _ = Play(SoundEvent.Success);
        if(activity.Event=="stopwatch.reviewOpened") _ = Play(SoundEvent.SessionEnd);
        // End-and-send already used the session's reflection window. It leaves
        // no pending prompt and must not replay the completion alert. Delivery
        // still provides its usual success/failure feedback through Sync().
        // Session transitions must let the incoming sound's playback behavior
        // decide whether existing audio is mixed, ducked, or interrupted.
        var state=engine.Snapshot;
        var completed=state.Prompts.Any(p=>!p.IsCheckIn&&!sounded.Contains(p.Id));
        if (!completed&&(activity.Event is "timer.paused" or "timer.reset" or "timer.started" ||
            activity.Event=="timer.lowTimeOptions"&&!state.Timer.LowTime.Enabled)) sounds.Stop(SoundEvent.LowTime);
        if(activity.Event is "timer.paused" or "timer.reset" or "stopwatch.started" or "session.modeChanged")sounds.Stop(SoundEvent.TimeReached);
        if(activity.Event=="session.modeChanged")sounds.Stop(SoundEvent.LowTime);
        if(activity.Event=="stopwatch.alertChanged"&&!AudioSettings.From(state).TimeReachedEnabled)sounds.Stop(SoundEvent.TimeReached);
    }
    internal void ReflectionSendStarted(Guid promptId)
    {
        if(disposed)return;
        var state=engine.Snapshot;
        var prompt=state.Prompts.SingleOrDefault(p=>p.Id==promptId);
        if(prompt is null||prompt.IsTest)return;
        var kind=prompt.Mode==SessionMode.Stopwatch?SoundEvent.TimeReached:SoundEvent.LowTime;
        var low=AudioSettings.From(state).For(kind);
        if(low.FadeOutAfterMessageSent&&(prompt.SessionId??prompt.CheckInSessionId) is {} sessionId)
            sounds.FadeOut(kind,sessionId,low.MessageSentFadeSeconds);
    }
    private void LowTime(TimerState timer) => _ = Play(SoundEvent.LowTime, false, AudioSettings.From(engine.SettingsSnapshot).ForLowTime(timer.LowTime),sessionId:timer.SessionId);
    private void TimeReached(TimerState timer) => _ = Play(SoundEvent.TimeReached,sessionId:timer.SessionId);
    private void Changed()
    {
        var state = engine.Snapshot; Log.Enabled = state.LoggingEnabled;
        sounds.UpdateVolumes(state.Timer.Volume, AudioSettings.From(state));
        var added = state.Prompts.Any(p => !p.IsCheckIn && !sounded.Contains(p.Id));
        sounded.UnionWith(state.Prompts.Where(p=>!p.IsCheckIn).Select(p => p.Id)); sounded.IntersectWith(state.Prompts.Select(p => p.Id));
        if (added) _ = Play(SoundEvent.SessionEnd);
    }
    public object Settings()
    {
        var s = engine.SettingsSnapshot; var audio = AudioSettings.From(s);
        return new { s.Connection.SheetUrl, s.Connection.WebAppUrl, s.Connection.SheetMode, s.Connection.SheetName,
            hasToken = s.Connection.ApiToken.Length > 0, hasDraft = s.SetupDraft is not null, s.ExtensionDisabledConfirmed, s.LoggingEnabled, s.StartAtLogin,
            connected = SheetsClient.Validate(s.Connection) is null && s.ExtensionDisabledConfirmed, deliveryIssue = DeliveryIssue,
            volume = s.Timer.Volume, threshold = audio.LowTimeThresholdSeconds, s.ShowFloatingTimer,
            audio.TimeReachedEnabled,audio.TimeReachedSeconds,
            s.CompactAlwaysOnTop, s.TimeOnlyAlwaysOnTop, s.PromptAlwaysOnTop, s.AutoSendIncompleteReflections, s.ConfirmBeforeReset,
            reflectionSeparator = (int)s.ReflectionSeparator,
            placement = (int)s.FloatingPlacement, popup = (int)s.PopupPosition, theme = (int)s.Theme, overlap = (int)s.ScheduleOverlap,
            tracks = new[] { LibrarySound.Default, LibrarySound.None }.Concat(SoundLibrary.Tracks).Select(t => new { id = (int)t, name = SoundLibrary.Name(t) }),
            sounds = Enum.GetValues<SoundEvent>().Select(kind => new { kind = (int)kind, name = kind.ToString(), track = (int)audio.For(kind).Track,
                behavior = (int)audio.For(kind).Behavior, audio.For(kind).Volume, audio.For(kind).FadeOutEnabled, audio.For(kind).FadeOutAfterSeconds,
                audio.For(kind).FadeOutAfterMessageSent, audio.For(kind).MessageSentFadeSeconds,
                custom = audio.For(kind).Mp3Path.Length > 0, customName=Path.GetFileName(audio.For(kind).Mp3Path), defaultName = SoundLibrary.DefaultName(kind) }) };
    }
    public void SaveConnection(ConnectionSettings connection, bool enabled)
    {
        var state = engine.Snapshot;
        engine.SaveSettings(connection, state.LoggingEnabled, state.StartAtLogin, enabled);
        connectionRevision++;
        ClearPreflightFailure();
    }
    public async Task<string> CheckConnection(ConnectionSettings connection)
    {
        var invalid=SheetsClient.Validate(connection);
        if(invalid is not null)throw new ArgumentException(invalid);
        var revision = connectionRevision;
        var reply=await sheets.Ping(connection,stop.Token);
        Log.Record("connection.checked",value:reply.Success?1:0);
        if(!reply.Success)throw new ArgumentException(reply.DisplayMessage);
        if (ConnectionStillCurrent(connection, revision)) ClearPreflightFailure();
        return "Connection works · "+reply.Target;
    }
    public async Task<string> CheckAndSave(ConnectionSettings connection, bool enabled)
    {
        if (!enabled) { SaveConnection(connection, false); return "Connection saved. Sheets delivery is paused."; }
        var invalid = SheetsClient.Validate(connection);
        if (invalid is not null) throw new ArgumentException(invalid);
        var revision = connectionRevision;
        var reply = await sheets.Ping(connection, stop.Token);
        if (!reply.Success) throw new ArgumentException(reply.DisplayMessage);
        if (revision != connectionRevision) throw new ArgumentException("Connection settings changed during verification. Review them and try again.");
        SaveConnection(connection, true);
        return "Connection verified. Future reflections can be sent to your sheet. Existing local preview entries stay local.";
    }
    public async Task Sync(bool requested = false)
    {
        if (syncing || stop.IsCancellationRequested) return;
        syncing = true;
        try {
            // A receiver result can outlive a failed local disk write. Commit it
            // before considering another request, even while offline/paused.
            // No credentials or reflection text are needed for this local retry.
            var recoveringResult = pendingUploadResult is not null;
            if (!CompletePendingUpload(requested)) return;
            var state = engine.Snapshot;
            var settings = state.Connection;
            var revision = connectionRevision;
            if (failedConnection != settings) ClearPreflightFailure();
            if (!state.ExtensionDisabledConfirmed) {
                ClearPreflightFailure();
                if (requested && !recoveringResult) Announcement?.Invoke("Set up and enable Sheets delivery in Connection setup first."); return;
            }
            if (SheetsClient.Validate(settings) is { } invalid) {
                if (DeliveryIssue is null) RecordPreflightFailure(settings, new(false, "settings_required", invalid));
                if (requested && !recoveringResult) Announcement?.Invoke(DeliveryIssue!.Message);
                return;
            }
            if (!state.Outbox.Any(o => !o.LocalOnly && o.Status == DeliveryStatus.Pending && !(o.NextAttemptAt > engine.Now))) {
                if (requested && !recoveringResult) Announcement?.Invoke("No connected entries are waiting to send."); return;
            }
            // A ping is not an append attempt. Back off at connection level,
            // leaving each reflection's attempt count and retry ID untouched.
            if (!requested && DeliveryIssue is not null && (DeliveryIssue.NextRetryAt is null
                || time.GetElapsedTime(preflightFailedAt) < preflightRetryDelay)) return;
            SheetUploadBatch batch;
            try { batch = await sheets.BeginBatch(settings, stop.Token); }
            catch when (!stop.IsCancellationRequested) {
                if (ConnectionStillCurrent(settings, revision)) {
                    RecordPreflightFailure(settings, new(false, "network", "The connection check could not finish.", Retryable: true));
                    if (requested) Announcement?.Invoke(DeliveryIssue!.Message);
                }
                return;
            }
            if (stop.IsCancellationRequested || !ConnectionStillCurrent(settings, revision)) return;
            var capability = batch.Capability;
            if (!capability.Success) {
                RecordPreflightFailure(settings, capability);
                if (requested) Announcement?.Invoke(DeliveryIssue!.Message);
                return;
            }
            ClearPreflightFailure();
            for (var sent = 0; sent < 20 && !stop.IsCancellationRequested; sent++) {
                // A pause or account change during an awaited request stops the next write.
                if (!ConnectionStillCurrent(settings, revision)) break;
                var item = engine.BeginUpload(capability.SupportsSafeRetry);
                if (item is null) break;
                SheetReply reply;
                try { reply = await batch.Upload(item, stop.Token); }
                catch when (!stop.IsCancellationRequested) {
                    // An unexpected transport failure leaves delivery uncertain.
                    // The engine retries only receiver-protected IDs; legacy
                    // requests become Needs review rather than being sent anew.
                    reply = new(false, "interrupted", "Delivery could not be confirmed. Check Outbox before retrying.", Retryable: true);
                }
                if (stop.IsCancellationRequested) return; // Existing startup recovery handles an interrupted shutdown.
                pendingUploadResult = new(item.Id, item.Attempts, reply);
                if (!CompletePendingUpload(requested)) return;
                if (!reply.Success) break;
            }
        }
        catch { if (!stop.IsCancellationRequested) Announcement?.Invoke("Delivery could not be finalized. Check Outbox before retrying."); }
        finally { syncing = false; }
    }
    private bool ConnectionStillCurrent(ConnectionSettings connection, int revision)
    {
        var state = engine.Snapshot;
        return revision == connectionRevision && state.ExtensionDisabledConfirmed && state.Connection == connection;
    }
    private void RecordPreflightFailure(ConnectionSettings connection, SheetReply reply)
    {
        failedConnection = connection;
        preflightFailures = Math.Min(preflightFailures + 1, 6);
        preflightFailedAt = time.GetTimestamp();
        preflightRetryDelay = TimeSpan.FromSeconds(Math.Min(300, 15 * (1 << (preflightFailures - 1))));
        SetDeliveryIssue(new(TimerEngine.SafeError(reply.ErrorKind), reply.DisplayMessage + (reply.Retryable
            ? " Reflections remain saved. Use Send pending now to retry sooner."
            : " Reflections remain saved. Review Connection setup, then use Save & test connection or Send pending now."),
            reply.Retryable ? time.GetUtcNow().Add(preflightRetryDelay).ToUnixTimeMilliseconds() : null));
    }
    private void ClearPreflightFailure()
    {
        failedConnection = null;
        preflightFailures = 0;
        SetDeliveryIssue(null);
    }
    private void SetDeliveryIssue(SheetDeliveryIssue? issue)
    {
        if (DeliveryIssue == issue) return;
        DeliveryIssue = issue;
        DeliveryIssueChanged?.Invoke();
    }
    private bool CompletePendingUpload(bool requested)
    {
        if (pendingUploadResult is not { } pending) return true;
        var reply = pending.Reply;
        bool completed;
        try {
            completed = engine.FinishUploadAttempt(pending.Id, pending.Attempt, reply.Success, reply.ErrorKind, reply.Tab, reply.Retryable)
                || ResultAlreadySaved(pending);
        }
        catch {
            // Change notifications run after the durable commit. If an observer
            // failed after saving, recognize that result instead of saving twice.
            if (ResultAlreadySaved(pending)) completed = true;
            else {
                if (!uploadResultSaveFailed || requested) {
                    uploadResultSaveFailed = true;
                    Announcement?.Invoke("The delivery result is waiting to be saved on this PC. The app will retry the local save without resending this reflection. Use Send pending now to retry saving sooner.");
                }
                return false;
            }
        }
        pendingUploadResult = null;
        uploadResultSaveFailed = false;
        if (completed) {
            Announcement?.Invoke(reply.Success ? $"Reflection sent to {reply.Tab}." : "Reflection retained in Outbox. " + reply.DisplayMessage);
            _ = Play(reply.Success ? SoundEvent.Success : SoundEvent.Failure);
        }
        return true;
    }
    private bool ResultAlreadySaved(PendingUploadResult pending)
    {
        var item = engine.Snapshot.Outbox.SingleOrDefault(x => x.Id == pending.Id && x.Attempts == pending.Attempt);
        return item is not null && (pending.Reply.Success
            ? item.Status == DeliveryStatus.Sent && item.SavedTab == pending.Reply.Tab
            : (item.Status is DeliveryStatus.Pending or DeliveryStatus.NeedsReview) && item.ErrorKind == TimerEngine.SafeError(pending.Reply.ErrorKind));
    }
    public async Task Play(SoundEvent kind, bool preview = false, SoundSetting? selected = null,bool announcePreview=true,Guid? sessionId=null)
    {
        if (stop.IsCancellationRequested) return;
        try {
            var state = engine.SettingsSnapshot; selected ??= AudioSettings.From(state).For(kind);
            var result = await sounds.PlayAsync(SoundLibrary.Resolve(kind, selected), state.Timer.Volume, selected.Behavior, kind, SoundLibrary.Fallback(kind), preview,
                selected.FadeOutEnabled ? selected.FadeOutAfterSeconds : null, selected.Volume, sessionId);
            if (stop.IsCancellationRequested) return;
            if (result == AlertSoundResult.Failed) Announcement?.Invoke("Audio could not play. The timer and saved reflection are unaffected.");
            else if (preview&&announcePreview) Announcement?.Invoke(result == AlertSoundResult.Muted ? "This audio is muted." : "Audio preview finished.");
        } catch { if (!stop.IsCancellationRequested) Announcement?.Invoke("Audio could not play."); }
    }
    public void StopAudio() => sounds.Stop();
    public void Dispose()
    {
        if(disposed) return; disposed=true;
        engine.ActivityRecorded -= Record; engine.LowTimeReached -= LowTime; engine.TimeReached -= TimeReached; engine.Changed -= Changed;
        stop.Cancel(); sounds.Dispose(); sheets.Dispose(); stop.Dispose(); Log.Dispose();
    }
}
