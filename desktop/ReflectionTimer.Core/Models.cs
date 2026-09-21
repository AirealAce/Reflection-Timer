using System.Text.Json;

namespace ReflectionTimer.Core;

public static class DataJson
{
    public static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    public static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, Options), Options)!;
}

public enum SessionMode { Timer = 0, Stopwatch = 1 }

public record TimerState
{
    public SessionMode Mode { get; init; }
    public long ElapsedMilliseconds { get; init; }
    public long? RunningSince { get; init; }
    public bool StopwatchCompleted { get; init; }
    public bool TimeReachedPlayed { get; init; }
    public const int DefaultDurationSeconds = 15 * 60;
    public Guid? SessionId { get; init; }
    public bool IsRunning { get; init; }
    public int DurationSeconds { get; init; } = DefaultDurationSeconds;
    public int RemainingSeconds { get; init; } = DefaultDurationSeconds;
    public long? PausedRemainingMilliseconds { get; init; }
    public long? EndTime { get; init; }
    public bool AutoRestart { get; init; }
    public long? AutoRestartUntil { get; init; }
    public int Volume { get; init; } = 50;
    public LowTimeOptions LowTime { get; init; } = new();
    public bool LowTimePlayed { get; init; }
}

public record ScheduledSession(Guid Id, long StartTime, int DurationSeconds, bool AutoRestart, int Volume, long? AutoRestartUntil = null)
{
    public LowTimeOptions LowTime { get; init; } = new();
    public bool WaitingForCurrentSession { get; init; }
    public bool AwaitingDecision { get; init; }
}
public enum ScheduleOverlapPolicy { EndWithReflection = 0, Ask = 1, Wait = 2 }
public enum ScheduleDecision { StartNow = 0, Wait = 1, Skip = 2 }
public record ReflectionPrompt(Guid Id, long CompletedAt, int DurationSeconds, int Volume, bool IsTest, string Draft = "")
{
    public SessionMode Mode { get; init; }
    public bool ResumeStopwatchOnSave { get; init; }
    public ReflectionSeparator? ContinuationSeparator { get; init; }
    // Retained independently of the active check-in link, for session-scoped audio.
    public Guid? SessionId { get; init; }
    // Null means an older prompt did not capture actual elapsed time.
    public int? ActualDurationSeconds { get; init; }
    public bool EndedEarly { get; init; }
    public string EarlyEndReason { get; init; } = "";
    public bool IsCheckIn { get; init; }
    public Guid? CheckInSessionId { get; init; }
}

public record ConnectionSettings
{
    public string SheetUrl { get; init; } = "";
    public string WebAppUrl { get; init; } = "";
    public string ApiToken { get; init; } = "";
    public string SheetMode { get; init; } = "date";
    public string SheetName { get; init; } = "Template";
}

public enum DeliveryStatus { Pending, Sending, Sent, NeedsReview }
public enum ReflectionPopupPosition { Center = 0, TopLeft = 1, TopRight = 2, BottomLeft = 3, BottomRight = 4 }
public enum AppColorTheme { Dark = 0, Light = 1, HighContrast = 2, Glamour = 3 }
public enum FloatingTimerPlacement { Custom = 0, Center = 1, TopLeft = 2, TopRight = 3, BottomLeft = 4, BottomRight = 5, TopCenter = 6, BottomCenter = 7 }
public record OutboxItem
{
    public SessionMode Mode { get; init; }
    public Guid? SessionId { get; init; }
    public bool AutoSent { get; init; }
    // Explicitly local records must never be bound to a receiver or uploaded.
    public bool LocalOnly { get; init; }
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Message { get; init; } = "";
    public DateTimeOffset SubmittedAt { get; init; }
    public int DurationSeconds { get; init; }
    public int? ActualDurationSeconds { get; init; }
    public bool EndedEarly { get; init; }
    public bool IsCheckIn { get; init; }
    public string EarlyEndReason { get; init; } = "";
    public bool IsTest { get; init; }
    public string SheetUrl { get; init; } = "";
    public string SheetMode { get; init; } = "date";
    public string SheetName { get; init; } = "Template";
    public DeliveryStatus Status { get; init; }
    public int Attempts { get; init; }
    public bool RetryProtected { get; init; }
    public string ReceiverUrl { get; init; } = "";
    public long? NextAttemptAt { get; init; }
    public string ErrorKind { get; init; } = "";
    public string SavedTab { get; init; } = "";
}

public record AppState
{
    // Only brand-new profiles use these defaults; deserialized legacy audio
    // remains null and retains its original migration behavior.
    public static AppState CreateDefault() => new() {
        Timer = new() { LowTime = new() { Enabled = false, ThresholdSeconds = 13 } },
        Audio = AudioSettings.CreateDefault()
    };
    public int FormatVersion { get; init; } = 1;
    public TimerState Timer { get; set; } = new();
    // Only Timer can be running. The other mode is retained here while paused.
    public TimerState? ParkedTimer { get; set; }
    public List<ScheduledSession> Schedules { get; set; } = [];
    public ScheduleOverlapPolicy ScheduleOverlap { get; set; } = ScheduleOverlapPolicy.EndWithReflection;
    public List<ReflectionPrompt> Prompts { get; set; } = [];
    public List<OutboxItem> Outbox { get; set; } = [];
    public ConnectionSettings Connection { get; set; } = new();
    public ConnectionSettings? SetupDraft { get; set; } // Encrypted; never used for uploads before setup completes.
    public bool? SetupDraftUsesExistingReceiver { get; set; }
    public string AlertSoundPath { get; set; } = ""; // Empty means the bundled extension sound.
    public AudioSettings? Audio { get; set; } // Null migrates the existing session-end MP3 without changing it.
    public ReflectionPopupPosition PopupPosition { get; set; } = ReflectionPopupPosition.BottomRight;
    public bool ShowFloatingTimer { get; set; } = true;
    public bool CompactAlwaysOnTop { get; set; } = true;
    public bool TimeOnlyAlwaysOnTop { get; set; } = true;
    public bool PromptAlwaysOnTop { get; set; } = true;
    public bool AutoSendIncompleteReflections { get; set; } = true;
    public bool ConfirmBeforeReset { get; set; } = true;
    public ReflectionSeparator ReflectionSeparator { get; set; } = ReflectionSeparator.Newline;
    public int? FloatingTimerLeft { get; set; }
    public int? FloatingTimerTop { get; set; }
    public FloatingTimerPlacement FloatingPlacement { get; set; } = FloatingTimerPlacement.BottomLeft;
    public AppColorTheme Theme { get; set; } = AppColorTheme.Dark;
    public bool LoggingEnabled { get; set; } = true;
    public bool StartAtLogin { get; set; }
    public bool ExtensionDisabledConfirmed { get; set; }
}

public interface IStateStore
{
    AppState Load();
    void Save(AppState state);
}

public record Activity(long At, string Event, Guid? ItemId = null, long? Value = null);
