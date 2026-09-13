using System.Globalization;
using System.Text.Json;
using ReflectionTimer.Core;

namespace ReflectionTimer.Accessible;

// A presentation adapter over the shared engine. Services handle delivery separately.
public sealed class PreviewSession
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public TimerEngine Engine { get; }
    public event Action<string>? Announcement;
    public event Action<string[]?>? DurationDraftChanged;
    private string[]? durationDraft;
    private readonly bool isolatedProfile;
    internal LowTimeOptions ScheduledLowDraft { get; set; }=new();
    internal void SelectScheduleDraft(Guid? id)=>ScheduledLowDraft=id is {} key
        ? Engine.Snapshot.Schedules.SingleOrDefault(s=>s.Id==key)?.LowTime ?? throw new ArgumentException("That schedule is no longer available.") : new();
    internal static object LowView(LowTimeOptions low,int threshold)=>new{low.Enabled,inherit=low.ThresholdSeconds is null,threshold=low.ThresholdSeconds??threshold,track=(int)low.Track,custom=low.Mp3Path.Length>0,customName=Path.GetFileName(low.Mp3Path)};
    internal static LowTimeOptions ReadLow(JsonElement data,LowTimeOptions previous)
    {
        var low=previous with {Enabled=Flag(data,"enabled"),ThresholdSeconds=Flag(data,"inherit")?null:Number(data,"threshold",1,TimerEngine.MaxDuration)};
        if(data.TryGetProperty("track",out _))low=low with{Track=(LibrarySound)Number(data,"track",0,9),Mp3Path=Flag(data,"keepCustom")?previous.Mp3Path:""};
        return low;
    }
    internal void SetDurationDraft(string[]? parts)
    {
        if(parts is not null && (parts.Length!=3 || parts.Any(p=>p is null || p.Length>20)))throw new ArgumentException("Enter three valid duration fields.");
        durationDraft=parts?.ToArray();DurationDraftChanged?.Invoke(durationDraft?.ToArray());
    }
    public PreviewSession(IStateStore store, Func<DateTimeOffset>? clock = null, bool isolatedProfile = false)
    {
        this.isolatedProfile = isolatedProfile;
        var saved = store.Load();
        if(saved.FormatVersion!=1) throw new InvalidDataException("Unsupported data version. Data was not changed.");
        if (isolatedProfile && saved.Outbox.Any(o => o.SheetUrl.Length == 0 && !o.LocalOnly)) {
            saved.Outbox = saved.Outbox.Select(o => o.SheetUrl.Length == 0 ? o with { LocalOnly = true } : o).ToList();
            store.Save(saved);
        }
        Engine = new(store, clock);
        Engine.LowTimeReached += timer => Announcement?.Invoke($"Low time. {SpeakTime(TimerEngine.Remaining(timer, Engine.Now))} remaining.");
    }
    public static AppState SampleState(DateTimeOffset now) => new() {
        ExtensionDisabledConfirmed = false,
        ScheduleOverlap = ScheduleOverlapPolicy.Wait,
        Schedules = [new(Guid.NewGuid(), now.AddDays(7).ToUnixTimeMilliseconds(), 900, false, 50),
                     new(Guid.NewGuid(), now.AddDays(8).ToUnixTimeMilliseconds(), 1200, false, 50)],
        Outbox = [new() { Message = "Sample reflection: I completed my reading.", SubmittedAt = now.AddMinutes(-30), DurationSeconds = 900,
            ActualDurationSeconds = 900, IsTest = true, LocalOnly = true, Status = DeliveryStatus.Pending },
            new() { Message = "Sample reflection: I will take a short break.", SubmittedAt = now.AddMinutes(-15), DurationSeconds = 600,
            ActualDurationSeconds = 360, IsTest = true, LocalOnly = true, Status = DeliveryStatus.NeedsReview, ErrorKind = "sample", Attempts = 1 }]
    };
    public object Clock()
    {
        var timer = Engine.Snapshot.Timer;
        var status = Status(timer);
        // Keep completion and reflection accounting in the engine, but show the
        // duration ready for the next session once the countdown has ended.
        var seconds = status == "Finished" ? timer.DurationSeconds : TimerEngine.Remaining(timer, Engine.Now);
        // Read-time feedback and recovery snapshots use the same edited duration
        // as the idle viewers, including a valid all-zero preview. Never change
        // the running countdown or the completed session's recorded duration.
        if(!timer.IsRunning&&durationDraft is {} values){
            var parts=new long[3];
            if(values.Select((value,index)=>long.TryParse(value.Trim().Length==0?"0":value.Trim(),NumberStyles.None,CultureInfo.InvariantCulture,out parts[index])&&parts[index]<=TimerEngine.MaxDuration).All(valid=>valid)){
                var preview=parts[0]*3600+parts[1]*60+parts[2];
                if(preview<=TimerEngine.MaxDuration)seconds=(int)preview;
            }
        }
        return new { seconds, text = SpeakTime(seconds), status };
    }
    public static string Status(TimerState timer) => timer.IsRunning ? "Running" : TimerEngine.IsPaused(timer) ? "Paused" : timer.RemainingSeconds == 0 ? "Finished" : "Ready";
    internal bool AutoSendReflection(Guid id) => Engine.AutoSendReflection(id, isolatedProfile && SheetsClient.Validate(Engine.Snapshot.Connection) is not null);
    internal Guid? ReflectionForShortcut()
    {
        var state=Engine.Snapshot;
        if(state.Prompts.LastOrDefault() is {} pending)return pending.Id;
        return (state.Timer.IsRunning||TimerEngine.IsPaused(state.Timer))&&TimerEngine.Remaining(state.Timer,Engine.Now)>0
            ? Engine.CheckIn() : null;
    }
    public static string SpeakTime(int total)
    {
        var parts = new List<string>();
        if (total / 3600 > 0) parts.Add($"{total / 3600} hour{(total / 3600 == 1 ? "" : "s")}");
        if (total / 60 % 60 > 0) parts.Add($"{total / 60 % 60} minute{(total / 60 % 60 == 1 ? "" : "s")}");
        if (total % 60 > 0 || parts.Count == 0) parts.Add($"{total % 60} second{(total % 60 == 1 ? "" : "s")}");
        return string.Join(" ", parts);
    }
    // Only fields needed by these views cross the bridge. No connection object or custom paths.
    public object View()
    {
        var state = Engine.Snapshot;
        return new {
            clock = Clock(), durationDraft, theme = (int)state.Theme, state.ShowFloatingTimer, appVolume = state.Timer.Volume, connected = state.ExtensionDisabledConfirmed && SheetsClient.Validate(state.Connection) is null,
            timer = new { state.Timer.DurationSeconds, state.Timer.AutoRestart, state.Timer.LowTime.Enabled, state.Timer.AutoRestartUntil, state.Timer.EndTime,
                threshold = state.Timer.LowTime.ThresholdSeconds ?? AudioSettings.From(state).LowTimeThresholdSeconds,
                low=LowView(state.Timer.LowTime,AudioSettings.From(state).LowTimeThresholdSeconds) },
            prompts = state.Prompts.Select(p => new { p.Id, p.IsCheckIn, p.EndedEarly, draft = ReflectionDrafts.ForEditing(p), p.EarlyEndReason,
                showEarlyEndReason=TimerEngine.ShowEarlyEndReason(p,state.Timer,Engine.Now),
                allotted = SpeakTime(p.DurationSeconds), actual = p.ActualDurationSeconds is { } actual ? SpeakTime(actual) : "Unavailable",
                completed = DateTimeOffset.FromUnixTimeMilliseconds(p.CompletedAt).ToLocalTime().ToString("g") }),
            schedules = state.Schedules.Select(s => new { s.Id, start = DateTimeOffset.FromUnixTimeMilliseconds(s.StartTime).ToLocalTime().ToString("g"),
                duration = SpeakTime(s.DurationSeconds), repeat = s.AutoRestart ? "On" : "Off", lowTime = s.LowTime.Enabled ? s.LowTime.ThresholdSeconds is {} threshold ? $"{threshold/60}:{threshold%60:00}" : "Default" : "Off",
                status = s.AwaitingDecision ? "Needs choice" : s.WaitingForCurrentSession ? "Waiting" : "Scheduled",
                editStart = DateTimeOffset.FromUnixTimeMilliseconds(s.StartTime).ToLocalTime().ToString("yyyy-MM-ddTHH:mm"), s.DurationSeconds, s.AutoRestartUntil, s.Volume }),
            outbox = state.Outbox.Select(o => new { o.Id, saved = o.SubmittedAt.LocalDateTime.ToString("g"),
                localOnly = o.LocalOnly,
                destination = o.LocalOnly ? "Local preview only" : o.IsTest ? "test" : o.SheetMode == "fixed" ? o.SheetName : o.SubmittedAt.ToString("MM/dd/yyyy"),
                status = o.Status == DeliveryStatus.Sent && o.LocalOnly ? "Simulated success" : o.Status.ToString(),
                o.Attempts, o.Message, o.DurationSeconds, o.ActualDurationSeconds, o.EndedEarly, o.EarlyEndReason, o.IsCheckIn, o.AutoSent, o.NextAttemptAt, duration = SpeakTime(o.DurationSeconds), error = o.ErrorKind.Length == 0 ? "" : TimerEngine.SafeError(o.ErrorKind) })
        };
    }
    public void Tick()
    {
        var before = Engine.Snapshot;
        Engine.Advance();
        var after = Engine.Snapshot;
        if (after.Prompts.Any(p => !p.IsCheckIn&&before.Prompts.All(old => old.IsCheckIn||old.Id != p.Id)))
            Announcement?.Invoke("Session finished. A reflection is available under Pending reflections.");
        else if (!before.Timer.IsRunning && after.Timer.IsRunning) Announcement?.Invoke("Scheduled timer started.");
    }
    public CommandResult Execute(string action, JsonElement data)
    {
        var state = Engine.Snapshot;
        switch (action)
        {
            case "startOrEnd":
                if(state.Timer.IsRunning)return Execute("end",data);
                var duration=state.Timer.DurationSeconds;
                if(durationDraft is {} parts){
                    var parsed=parts.Select(p=>p.Length==0?0L:long.TryParse(p,NumberStyles.None,CultureInfo.InvariantCulture,out var value)&&value<=TimerEngine.MaxDuration?value:throw new ArgumentException("Enter valid duration fields.")).ToArray();
                    var total=parsed[0]*3600+parsed[1]*60+parsed[2];
                    if(total<1||total>TimerEngine.MaxDuration)throw new ArgumentException("Enter a duration between one second and one year.");
                    duration=(int)total;
                }
                return Execute("toggle",JsonSerializer.SerializeToElement(new{seconds=duration,repeat=state.Timer.AutoRestart,lowTime=state.Timer.LowTime.Enabled},Json));
            case "toggle":
                if (state.Timer.IsRunning) {
                    Engine.Pause();
                    var completed=Engine.Snapshot.Prompts.FirstOrDefault(p=>!p.IsCheckIn&&state.Prompts.All(old=>old.IsCheckIn||old.Id!=p.Id));
                    return new(completed is null?"Timer paused.":"Session ended. Reflection opened.",completed?.Id,SessionCompleted:completed is not null);
                }
                var seconds = Number(data, "seconds", 1, TimerEngine.MaxDuration);
                if (TimerEngine.IsPaused(state.Timer) && seconds == state.Timer.DurationSeconds) Engine.Resume();
                else Engine.Start(seconds, Flag(data, "repeat"), state.Timer.Volume, state.Timer.AutoRestartUntil, lowTime:state.Timer.LowTime with {Enabled=Flag(data,"lowTime")});
                SetDurationDraft(null);
                return new("Timer running.");
            case "reset":
                Engine.Reset(Number(data, "seconds", 1, TimerEngine.MaxDuration));SetDurationDraft(null);return new("Timer reset.");
            case "repeat":
                Engine.SetPreferences(Flag(data, "enabled"), state.Timer.Volume, Flag(data,"enabled") ? state.Timer.AutoRestartUntil : null);
                return new(Flag(data, "enabled") ? "Auto-start enabled." : "Auto-start disabled.");
            case "lowTime":
                Engine.SetLowTime(ReadLow(data,state.Timer.LowTime));
                return new("Low-time warning saved.");
            case "end":
                var previous = state.Prompts.Where(p=>!p.IsCheckIn).Select(p => p.Id).ToHashSet();
                if (!Engine.EndEarly()) throw new ArgumentException("Start or resume the timer before ending it early.");
                return new("Session ended. Reflection opened.", Engine.Snapshot.Prompts.First(p => !p.IsCheckIn&&!previous.Contains(p.Id)).Id,SessionCompleted:true);
            case "checkIn": return new("Check-in opened.", Engine.CheckIn());
            case "testReflection": return new("Practice reflection opened.", Engine.TestPrompt());
            case "openReflection":
                var prompt = RequiredPrompt(Id(data)); return new("", prompt.Id);
            case "draft":
                var draftId = Id(data); RequiredPrompt(draftId);
                Engine.SaveDraft(draftId, Text(data, "text", 5000), Text(data, "reason", 1000)); return new("");
            case "saveForLater":
                Engine.SaveReflectionForLater(Id(data), Text(data, "text", 5000), Text(data, "reason", 1000));
                return new("Reflection saved locally.", Close: true);
            case "skip":
                var skipped=Id(data);RequiredPrompt(skipped);Engine.SkipPrompt(skipped);return new("Reflection skipped.",Close:true);
            case "queue":
                var localOnly = isolatedProfile && SheetsClient.Validate(state.Connection) is not null;
                var ended = Engine.QueueReflection(Id(data), Text(data, "text", 5000), Text(data, "reason", 1000), localOnly, endSession: Flag(data,"endSession"));
                return new((ended ? "Session ended. " : "") + (localOnly ? "Reflection saved locally in the Outbox." : "Reflection saved in Outbox for Sheets delivery when enabled."), Close: true, SessionCompleted: ended);
            case "schedule":
                var date = Text(data, "start", 40);
                if (!DateTime.TryParseExact(date, "yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var start))
                    throw new ArgumentException("Enter a valid local start date and time.");
                if (TimeZoneInfo.Local.IsInvalidTime(start) || TimeZoneInfo.Local.IsAmbiguousTime(start))
                    throw new ArgumentException("Choose an unambiguous local time outside the daylight-saving clock change.");
                var editId = data.TryGetProperty("id", out var identifier) && identifier.ValueKind == JsonValueKind.String && Guid.TryParse(identifier.GetString(), out var edited) ? edited : (Guid?)null;
                Engine.SaveSchedule(editId, new DateTimeOffset(start), Number(data, "seconds", 1, TimerEngine.MaxDuration), Flag(data,"repeat"),
                    data.TryGetProperty("volume",out _) ? Number(data,"volume",0,100) : 50,
                    data.TryGetProperty("cutoff",out var endAt) && endAt.ValueKind == JsonValueKind.String && endAt.GetString() is { Length: >0 } cutoffText ? ParseLocalTime(cutoffText) : null,
                    data.TryGetProperty("lowOptions",out var lowData)?ReadLow(lowData,Flag(data,"fromTimer")?state.Timer.LowTime:ScheduledLowDraft):new LowTimeOptions { Enabled = !data.TryGetProperty("lowTime",out _) || Flag(data,"lowTime") });
                ScheduledLowDraft=new();
                return new("Session scheduled.");
            case "removeSchedule":
                var id = Id(data);
                if (state.Schedules.All(s => s.Id != id)) throw new ArgumentException("That schedule is no longer available.");
                Engine.RemoveSchedule(id); return new("Scheduled session removed.");
            case "simulate":
                // This exercises row updates with explicitly local records only.
                var item = state.Outbox.SingleOrDefault(o => o.Id == Id(data)) ?? throw new ArgumentException("Entry not found.");
                if (!item.LocalOnly) throw new ArgumentException("Simulation is only available for local preview entries.");
                Engine.FinishUpload(item.Id, true, "", "Preview");
                return new("Simulated success. No data was sent.");
            case "retry":
                var retry = state.Outbox.SingleOrDefault(o => o.Id == Id(data)) ?? throw new ArgumentException("Entry not found.");
                if (retry.LocalOnly) throw new ArgumentException("This preview entry stays local. Save a new reflection after enabling your connection.");
                if (retry.ErrorKind is "write_uncertain" or "id_conflict" && !Flag(data,"confirmed")) throw new ArgumentException("Check your sheet and confirm before retrying this uncertain write.");
                Engine.RetryUpload(retry.Id); return new("Entry queued for retry.");
            case "markSent":
                var reviewed = state.Outbox.SingleOrDefault(o => o.Id == Id(data)) ?? throw new ArgumentException("Entry not found.");
                if (reviewed.LocalOnly || !Flag(data,"confirmed")) throw new ArgumentException("Confirm that this entry is already in your sheet first.");
                Engine.MarkAlreadySent(reviewed.Id); return new("Entry marked already sent.");
            case "resolveSchedule":
                Engine.ResolveSchedule(Id(data),(ScheduleDecision)Number(data,"decision",0,2));
                var resolved=Engine.Snapshot.Prompts.FirstOrDefault(p=>!p.IsCheckIn&&state.Prompts.All(old=>old.IsCheckIn||old.Id!=p.Id));
                return new("Schedule choice saved.",resolved?.Id,SessionCompleted:resolved is not null);
            default: throw new ArgumentException("Unknown preview command.");
        }
    }
    internal static long ParseLocalTime(string value)
    {
        if(!DateTime.TryParseExact(value,"yyyy-MM-ddTHH:mm",CultureInfo.InvariantCulture,DateTimeStyles.None,out var local)
            || TimeZoneInfo.Local.IsInvalidTime(local) || TimeZoneInfo.Local.IsAmbiguousTime(local)) throw new ArgumentException("Choose an unambiguous local date and time.");
        return new DateTimeOffset(local).ToUnixTimeMilliseconds();
    }
    private ReflectionPrompt RequiredPrompt(Guid id) => Engine.Snapshot.Prompts.SingleOrDefault(p => p.Id == id) ?? throw new ArgumentException("That reflection is no longer pending.");
    private static Guid Id(JsonElement data) => Guid.TryParse(Text(data, "id", 36), out var id) ? id : throw new ArgumentException("Invalid record identifier.");
    private static int Number(JsonElement data, string name, int min, int max) => data.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) && number >= min && number <= max
        ? number : throw new ArgumentException($"Enter a valid {name} between {min} and {max}.");
    private static bool Flag(JsonElement data, string name) => data.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True;
    private static string Text(JsonElement data, string name, int max) => data.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { } text && text.Length <= max
        ? text : throw new ArgumentException($"Enter valid {name} (up to {max} characters).");
}
public record CommandResult(string Message, Guid? OpenReflection = null, bool Close = false, bool SessionCompleted = false);
