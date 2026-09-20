using System.Text.Json;
using ReflectionTimer.Core;
using ReflectionTimer.Desktop;

namespace ReflectionTimer.Accessible;

public sealed class PreviewServices : IDisposable
{
    private readonly TimerEngine engine;
    private readonly SheetsClient sheets;
    private readonly AlertSoundPlayer sounds;
    private readonly CancellationTokenSource stop = new();
    private bool syncing;
    private bool disposed;
    private int connectionRevision;
    private readonly HashSet<Guid> sounded = [];
    public DiagnosticLog Log { get; }
    public event Action<string>? Announcement;
    public PreviewServices(TimerEngine engine, string directory, SheetsClient? sheets = null, IAlertAudioBackend? audio = null)
    {
        this.engine = engine; this.sheets = sheets ?? new(); sounds = new(audio);
        Log = new(directory) { Enabled = engine.Snapshot.LoggingEnabled };
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
    private void LowTime(TimerState timer) => _ = Play(SoundEvent.LowTime, false, AudioSettings.From(engine.Snapshot).ForLowTime(timer.LowTime),sessionId:timer.SessionId);
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
        var s = engine.Snapshot; var audio = AudioSettings.From(s);
        return new { s.Connection.SheetUrl, s.Connection.WebAppUrl, s.Connection.SheetMode, s.Connection.SheetName,
            hasToken = s.Connection.ApiToken.Length > 0, hasDraft = s.SetupDraft is not null, s.ExtensionDisabledConfirmed, s.LoggingEnabled, s.StartAtLogin,
            connected = SheetsClient.Validate(s.Connection) is null && s.ExtensionDisabledConfirmed,
            volume = s.Timer.Volume, threshold = audio.LowTimeThresholdSeconds, s.ShowFloatingTimer,
            audio.TimeReachedEnabled,audio.TimeReachedSeconds,
            s.CompactAlwaysOnTop, s.TimeOnlyAlwaysOnTop, s.PromptAlwaysOnTop, s.AutoSendIncompleteReflections,
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
    }
    public async Task<string> CheckConnection(ConnectionSettings connection)
    {
        var invalid=SheetsClient.Validate(connection);
        if(invalid is not null)throw new ArgumentException(invalid);
        var reply=await sheets.Ping(connection,stop.Token);
        Log.Record("connection.checked",value:reply.Success?1:0);
        if(!reply.Success)throw new ArgumentException(reply.DisplayMessage);
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
        var settings = engine.Snapshot.Connection;
        if (!engine.Snapshot.ExtensionDisabledConfirmed || SheetsClient.Validate(settings) is not null) {
            if (requested) Announcement?.Invoke("Set up and enable Sheets delivery in Connection setup first."); return;
        }
        syncing = true;
        try {
            if (!engine.Snapshot.Outbox.Any(o => !o.LocalOnly && o.Status == DeliveryStatus.Pending && !(o.NextAttemptAt > engine.Now))) {
                if (requested) Announcement?.Invoke("No connected entries are waiting to send."); return;
            }
            var capability = await sheets.Ping(settings, stop.Token);
            if (!capability.Success) { if (requested) Announcement?.Invoke(capability.DisplayMessage); return; }
            for (var sent = 0; sent < 20 && !stop.IsCancellationRequested; sent++) {
                // A pause or account change during an awaited request stops the next write.
                if (!engine.Snapshot.ExtensionDisabledConfirmed || engine.Snapshot.Connection != settings) break;
                var item = engine.BeginUpload(capability.SupportsSafeRetry);
                if (item is null) break;
                var reply = await sheets.Upload(settings, item, stop.Token);
                engine.FinishUpload(item.Id, reply.Success, reply.ErrorKind, reply.Tab, reply.Retryable);
                Announcement?.Invoke(reply.Success ? $"Reflection sent to {reply.Tab}." : "Reflection retained in Outbox. " + reply.DisplayMessage);
                _ = Play(reply.Success ? SoundEvent.Success : SoundEvent.Failure);
                if (!reply.Success) break;
            }
        }
        catch { if (!stop.IsCancellationRequested) Announcement?.Invoke("Delivery could not be finalized. Check Outbox before retrying."); }
        finally { syncing = false; }
    }
    public async Task Play(SoundEvent kind, bool preview = false, SoundSetting? selected = null,bool announcePreview=true,Guid? sessionId=null)
    {
        if (stop.IsCancellationRequested) return;
        try {
            var state = engine.Snapshot; selected ??= AudioSettings.From(state).For(kind);
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
        stop.Cancel(); sounds.Dispose(); sheets.Dispose(); stop.Dispose();
    }
}
