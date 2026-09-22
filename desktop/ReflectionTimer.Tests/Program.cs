using System.Text.Json;
using ReflectionTimer.Accessible;
using ReflectionTimer.Core;
using ReflectionTimer.Desktop;

if(args.Contains("--native-smoke")){NativeReflectionSmoke.Run();return;}
if(args.Contains("--native-reflection-send")){NativeReflectionSmoke.Run(sendModeOnly:true);return;}
if(args.Contains("--native-clock")){NativeClockSmoke.Run();return;}
if(args.Contains("--native-reset-reload")){NativeResetReloadSmoke.Run();return;}
if(args.Contains("--native-startup")){NativeStartupSmoke.Run();return;}
if(args.Contains("--native-stopwatch")){NativeStopwatchSmoke.Run();return;}
if(args.Contains("--native-stopwatch-focus")){NativeStopwatchSmoke.RunInteractive();return;}
if(args.Contains("--native-settings-save")){NativeSettingsSaveSmoke.Run();return;}
if(args.Contains("--native-reset-confirmation")){NativeResetConfirmationSmoke.Run();return;}
if(args.Contains("--native-reset-dialog")){NativeResetDialogSmoke.Run();return;}
if(args.Contains("--native-reset-dialog-interactive")){NativeResetDialogSmoke.RunInteractive();return;}
if(args.Contains("--upload-recovery")){
    var recoveryPassed=0;
    await UploadRecoveryTests.Run((condition,name)=>{if(!condition)throw new Exception(name);recoveryPassed++;Console.WriteLine("PASS "+name);});
    Console.WriteLine($"{recoveryPassed} upload recovery checks passed.");return;
}
if(args.Contains("--delivery-preflight")){
    var preflightPassed=0;
    await DeliveryPreflightTests.Run((condition,name)=>{if(!condition)throw new Exception(name);preflightPassed++;Console.WriteLine("PASS "+name);});
    Console.WriteLine($"{preflightPassed} delivery preflight checks passed.");return;
}
if(args.Contains("--clock-accuracy")){
    var clockPassed=0;
    ClockAccuracyTests.Run((condition,name)=>{if(!condition)throw new Exception(name);clockPassed++;Console.WriteLine("PASS "+name);});
    Console.WriteLine($"{clockPassed} clock accuracy checks passed.");return;
}

var passed = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception(name); passed++; Console.WriteLine("PASS " + name); }
JsonElement Data(object value) => JsonSerializer.SerializeToElement(value, PreviewSession.Json);
var now = DateTimeOffset.Now;
DefaultsThemeShortcutTests.Run(Check);
TimerToggleShortcutTests.Run(Check);
ResetShortcutTests.Run(Check);
ResetConfirmationTests.Run(Check);
ClockDisplayTests.Run(Check);
ClockAccuracyTests.Run(Check);
PerformanceTests.Run(Check);
SessionDraftTests.Run(Check);
ReflectionSeparatorTests.Run(Check);
ProfileStorageTests.Run(Check);
ConditionalReflectionTests.Run(Check);
EarlyEndGraceTests.Run(Check);
await StopwatchTests.Run(Check);
var store = new MemoryStore { State = PreviewSession.SampleState(now) };
var session = new PreviewSession(store, () => now, isolatedProfile: true);
Check(session.Engine.Snapshot.Timer.DurationSeconds == 900 && session.Engine.Snapshot.Timer.LowTime.Enabled, "Fresh timer and low-time defaults");
Check(session.Engine.Snapshot.FloatingPlacement == FloatingTimerPlacement.BottomLeft && session.Engine.Snapshot.PopupPosition == ReflectionPopupPosition.BottomRight, "Fresh window positions");
var area=new Rectangle(0,0,1920,1040);
Check(ViewPlacement.Calculate(area,new(940,810),1)==new Point(490,115),"App view opens centered in the working area");
var compactPosition=ViewPlacement.Calculate(area,new(356,258),4);var tinyPosition=ViewPlacement.Calculate(area,new(96,40),4);
Check(compactPosition.X==16&&tinyPosition.X==16&&compactPosition.Y+258==1024&&tinyPosition.Y+40==1024,"Compact and time-only share the bottom-left anchor across resizing");
var reflectionPosition=ViewPlacement.Calculate(area,new(560,490),5);
Check(reflectionPosition.X+560==1904&&reflectionPosition.Y+490==1024,"Session-end prompt opens at bottom right");
session.SetDurationDraft(["1","2","3"]);
Check(JsonSerializer.Serialize(session.View(),PreviewSession.Json).Contains("\"durationDraft\":[\"1\",\"2\",\"3\"]")&&session.Engine.Snapshot.Timer.DurationSeconds==900,"Shared duration draft is available to both views without committing a timer change");
session.SetDurationDraft(null);
session.Execute("toggle", Data(new { seconds = 20, threshold = 15, repeat = false, lowTime = true }));
Check(session.Engine.Snapshot.Timer.IsRunning, "Real engine starts from bridge command");
var deadline = session.Engine.Snapshot.Timer.EndTime;
session.Execute("repeat", Data(new { enabled = true }));
Check(session.Engine.Snapshot.Timer.AutoRestart && session.Engine.Snapshot.Timer.EndTime == deadline, "Auto-start edits apply without restarting the timer");
session.Execute("repeat", Data(new { enabled = false }));
session.Execute("lowTime", Data(new { enabled = false, threshold = 10 }));
Check(!session.Engine.Snapshot.Timer.LowTime.Enabled && session.Engine.Snapshot.Timer.EndTime == deadline, "Low-time edits preserve the running session");
session.Execute("lowTime", Data(new { enabled = true, threshold = 15 }));
now = now.AddSeconds(3); session.Execute("toggle", Data(new { }));
Check(!session.Engine.Snapshot.Timer.IsRunning && TimerEngine.Remaining(session.Engine.Snapshot.Timer, session.Engine.Now) == 17, "Pause retains elapsed time");
session.Execute("toggle", Data(new { seconds = 20, threshold = 15, repeat = false, lowTime = true }));
Check(TimerEngine.Remaining(session.Engine.Snapshot.Timer, session.Engine.Now) == 17, "Resume keeps remaining duration");
var announcements = new List<string>(); session.Announcement += announcements.Add;
now = now.AddSeconds(2); session.Tick(); session.Tick();
Check(announcements.Count(a => a.StartsWith("Low time")) == 1, "Low-time event announced once");
now = now.AddSeconds(15); session.Tick(); session.Tick();
Check(announcements.Count(a => a.StartsWith("Session finished")) == 1 && session.Engine.Snapshot.Prompts.Count == 1, "Completion produces one prompt and announcement");
var prompt = session.Engine.Snapshot.Prompts[0];
session.Execute("draft", Data(new { id = prompt.Id, text = "A draft that should survive.", reason = "" }));
var reopened = new PreviewSession(store, () => now);
Check(reopened.Engine.Snapshot.Prompts[0].Draft == "A draft that should survive.", "Draft survives session recreation");
session.Execute("queue", Data(new { id = prompt.Id, text = "Saved local reflection.", reason = "" }));
Check(session.Engine.Snapshot.Prompts.Count == 0 && session.Engine.Snapshot.Outbox.Last().Message == "Saved local reflection.", "Queue atomically moves draft into local outbox");
session.Execute("simulate", Data(new { id = prompt.Id }));
Check(session.Engine.Snapshot.Outbox.Last().Status == DeliveryStatus.Sent, "Local delivery simulation changes the same record");
var serialized = JsonSerializer.Serialize(session.View(), PreviewSession.Json);
Check(serialized.Contains("Simulated success") && !serialized.Contains("apiToken") && !serialized.Contains("receiverUrl") && !serialized.Contains("sheetUrl"), "View exposes simulation truthfully and excludes connection fields");
var start = now.AddHours(1).ToLocalTime().ToString("yyyy-MM-ddTHH:mm");
session.Execute("schedule", Data(new { start, seconds = 60 }));
Check(session.Engine.Snapshot.Schedules.Count == 3, "Schedule saved through engine");
var scheduleId = session.Engine.Snapshot.Schedules.First(s => s.DurationSeconds == 60).Id;
session.Execute("removeSchedule", Data(new { id = scheduleId }));
Check(session.Engine.Snapshot.Schedules.All(s => s.Id != scheduleId), "Schedule removed by stable ID");
session.Execute("lowTime",Data(new{enabled=true,inherit=true,threshold=27,track=3,keepCustom=false}));
Check(session.Engine.Snapshot.Timer.LowTime.ThresholdSeconds is null&&(int)session.Engine.Snapshot.Timer.LowTime.Track==3,"Inherited low-time threshold and selected track remain separate preferences");
session.Execute("schedule",Data(new{start,seconds=4509,fromTimer=true,lowOptions=new{enabled=true,inherit=false,threshold=27,track=3,keepCustom=false}}));
var withLow=session.Engine.Snapshot.Schedules.Single(s=>s.DurationSeconds==4509);
Check(withLow.LowTime.ThresholdSeconds==27&&(int)withLow.LowTime.Track==3,"Scheduled duration and individual low-time sound survive saving");
session.SelectScheduleDraft(withLow.Id);
Check(session.ScheduledLowDraft==withLow.LowTime,"Editing restores the schedule's individual low-time options");
session.Execute("removeSchedule",Data(new{id=withLow.Id}));
var before = JsonSerializer.Serialize(session.Engine.Snapshot);
try { session.Execute("toggle", Data(new { seconds = -10 })); throw new Exception("Invalid duration accepted"); } catch (ArgumentException) { }
Check(JsonSerializer.Serialize(session.Engine.Snapshot) == before, "Invalid bridge input leaves state untouched");
store.Fail = true;
try { session.Execute("reset", Data(new { seconds = 10 })); throw new Exception("Failed save accepted"); } catch (IOException) { }
Check(JsonSerializer.Serialize(session.Engine.Snapshot) == before, "Failed persistence does not change live state");
store.Fail = false;
Check(PreviewWindow.Allowed("https://reflection-timer.invalid/index.html?view=main"), "Local UI origin allowed");
Check(new[] { "https://evil.example/index.html", "file:///C:/private.txt", "https://reflection-timer.invalid:4430/index.html", "https://reflection-timer.invalid/private.txt", "https://reflection-timer.invalid@evil.example/index.html", "http://reflection-timer.invalid/app.js" }.All(address => !PreviewWindow.Allowed(address)), "Unexpected schemes, origins, ports and paths rejected");
var directory = Path.Combine(Path.GetTempPath(), "ReflectionTimer-AccessibleTest-" + Guid.NewGuid().ToString("N"));
try {
    var encrypted = new EncryptedStore(directory); encrypted.Save(store.State);
    Check(!File.ReadAllText(Path.Combine(directory, "state.dat")).Contains("Saved local reflection."), "Preview uses encrypted storage");
    Check(new PreviewSession(encrypted).Engine.Snapshot.Outbox.Last().Message == "Saved local reflection.", "Encrypted preview data survives reopening");
} finally { File.Delete(Path.Combine(directory, "state.dat")); if (Directory.Exists(directory)) Directory.Delete(directory); }
var launch=PreviewStartup.Parse(["--profile","review-test","--tray"]);
Check(launch.Profile=="review-test"&&launch.Tray&&PreviewStartup.Parse([]).Profile is null,"Startup arguments preserve the selected preview profile");
Check(PreviewStartup.ValueName("review-test")!="Reflection Timer Desktop"&&PreviewStartup.Command(@"C:\Preview Test\Timer.exe","review-test")=="\"C:\\Preview Test\\Timer.exe\" --profile review-test --tray","Startup targets only the preview and quotes executable paths");
foreach(var invalid in new[]{new[]{"--profile","../other"},new[]{"--tray","--tray"},new[]{"--unknown"}}){try{PreviewStartup.Parse(invalid);throw new Exception("Invalid startup arguments accepted");}catch(ArgumentException){}}
Check(true,"Startup rejects unsafe or unrecognized arguments");
var shortcutSession=new PreviewSession(new MemoryStore{State=PreviewSession.SampleState(now)},()=>now);
shortcutSession.SetDurationDraft(["0","2","3"]);shortcutSession.Execute("startOrEnd",Data(new{}));
Check(shortcutSession.Engine.Snapshot.Timer.IsRunning&&shortcutSession.Engine.Snapshot.Timer.DurationSeconds==123,"Start/end shortcut starts with the shared duration draft");
shortcutSession.Engine.Pause();shortcutSession.Execute("startOrEnd",Data(new{}));
Check(shortcutSession.Engine.Snapshot.Timer.IsRunning,"Start/end shortcut resumes a paused timer");
var shortcutEnd=shortcutSession.Execute("startOrEnd",Data(new{}));
Check(!shortcutSession.Engine.Snapshot.Timer.IsRunning&&shortcutEnd.OpenReflection.HasValue,"Start/end shortcut opens an early-end reflection");
await PromotionTests.Run(Check);
await PromptPolicyTests.Run(Check);
await ServiceTests.Run(Check);
await UploadRecoveryTests.Run(Check);
await DeliveryPreflightTests.Run(Check);
await SuccessAudioTests.Run(Check);
await AudioBehaviorTests.Run(Check);
await AudioPreviewTests.Run(Check);
await MessageSentFadeTests.Run(Check);
await ReflectionSendModeTests.Run(Check);
await QuietCompletionTests.Run(Check);
Console.WriteLine($"{passed} tests passed.");

sealed class MemoryStore : IStateStore
{
    public AppState State = new(); public bool Fail;
    public AppState Load() => DataJson.Clone(State);
    public void Save(AppState state) { if (Fail) throw new IOException("Simulated disk failure"); State = DataJson.Clone(state); }
}
