using System.Reflection;
using System.Text.Json;
using ReflectionTimer.Core;
using ReflectionTimer.Desktop;

namespace ReflectionTimer.Accessible;

internal sealed partial class PreviewWindow
{
    private static readonly HashSet<string> SettingsCommands = ["settingsSaveComplete", "settingsLoad", "connectionSave", "connectionPause", "setupImport", "setupExport", "setupScript", "setupRestore", "setupNewToken", "saveAppearance", "displayOption", "saveSound", "browseSound", "previewSound", "stopSound", "volume", "diagnostics", "exportDiagnostics", "markIssue", "sendPending", "setCutoff", "importSchedules", "openSheet", "clearDiagnostics", "editScheduleDraft", "browseLowSound", "previewLowSound", "connectionStore", "setupGuide"];
    private static string ReadString(JsonElement data, string key, int max = 4096) => data.TryGetProperty(key,out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { } text && text.Length <= max
        ? text : throw new ArgumentException($"Enter valid {key}.");
    private static int ReadInt(JsonElement data, string key, int min, int max) => data.TryGetProperty(key,out var value) && value.TryGetInt32(out var result) && result >= min && result <= max
        ? result : throw new ArgumentException($"Enter {key} between {min} and {max}.");
    private static bool ReadFlag(JsonElement data,string key) => data.TryGetProperty(key,out var value) && value.ValueKind == JsonValueKind.True;
    private ConnectionSettings ReadConnection(JsonElement data)
    {
        var token = ReadString(data,"token",512).Trim();
        return new() { SheetUrl = ReadString(data,"sheetUrl").Trim(), WebAppUrl = ReadString(data,"webAppUrl").Trim(),
            ApiToken = token.Length == 0 ? app.Session.Engine.Snapshot.Connection.ApiToken : token,
            SheetMode = ReadString(data,"sheetMode",10), SheetName = ReadString(data,"sheetName",100) };
    }
    private async Task<bool> HandleSettings(string action, JsonElement data, string requestId)
    {
        if (!SettingsCommands.Contains(action) && action!="timeReached") return false;
        if (View != "main") throw new ArgumentException("Open Settings in the main window for this action.");
        var engine = app.Session.Engine; var services = app.Services; var state = engine.Snapshot;
        string message = "";
        switch (action) {
            case "timeReached": engine.SetTimeReached(ReadFlag(data,"enabled"),ReadInt(data,"seconds",1,TimerEngine.MaxDuration));break;
            case "settingsSaveComplete":
                _=services.Play(SoundEvent.Success);Reply(requestId);return true;
            case "settingsLoad": Post(new{type="shortcuts",shortcuts=app.ShortcutState});break;
            case "setupGuide": System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Path.Combine(AppContext.BaseDirectory,"START-HERE.html")){UseShellExecute=true});break;
            case "connectionStore": services.SaveConnection(ReadConnection(data),ReadFlag(data,"enabled"));message="Settings saved.";break;
            case "editScheduleDraft":
                app.Session.SelectScheduleDraft(data.TryGetProperty("id",out var scheduleId)&&scheduleId.ValueKind==JsonValueKind.String?scheduleId.GetGuid():null);break;
            case "browseLowSound":
                var target=ReadString(data,"target",20);
                if(target is not "timer" and not "schedule")throw new ArgumentException("Choose timer or schedule audio.");
                using(var picker=new OpenFileDialog{Title="Choose low-time audio",Filter="MP3 audio (*.mp3)|*.mp3",CheckFileExists=true}){
                    if(picker.ShowDialog(this)==DialogResult.OK){var path=Mp3AudioBackend.ValidateCustomFile(picker.FileName);
                        var countdown=state.Timer.Mode==SessionMode.Timer?state.Timer:state.ParkedTimer??new TimerState();
                        var options=PreviewSession.ReadLow(data.GetProperty("options"),target=="timer"?countdown.LowTime:app.Session.ScheduledLowDraft) with{Mp3Path=path,Track=LibrarySound.Default};
                        if(target=="timer")engine.SetLowTime(options);
                        else app.Session.ScheduledLowDraft=options;
                    }
                }break;
            case "previewLowSound":
                var lowTarget=ReadString(data,"target",20);
                if(lowTarget is not "timer" and not "schedule")throw new ArgumentException("Choose timer or schedule audio.");
                var selected=PreviewSession.ReadLow(data.GetProperty("options"),lowTarget=="timer"?state.Timer.LowTime:app.Session.ScheduledLowDraft);
                _=services.Play(SoundEvent.LowTime,true,AudioSettings.From(state).ForLowTime(selected),!ReadFlag(data,"quiet"));break;
            case "displayOption":
                var option=ReadString(data,"option",40);
                switch(option){
                    case "theme":engine.SetTheme((AppColorTheme)ReadInt(data,"value",0,3));break;
                    case "placement":engine.SetFloatingTimerPlacement((FloatingTimerPlacement)ReadInt(data,"value",0,7));app.ApplyDisplayPreferences();break;
                    case "popup":engine.SetPopupPosition((ReflectionPopupPosition)ReadInt(data,"value",0,4));break;
                    case "overlap":engine.SetScheduleOverlap((ScheduleOverlapPolicy)ReadInt(data,"value",0,2));break;
                    case "showCompact":engine.SetFloatingTimer(ReadInt(data,"value",0,1)==1);app.ApplyDisplayPreferences();break;
                    case "compactAlwaysOnTop":engine.SetAlwaysOnTop(ReadInt(data,"value",0,1)==1,state.TimeOnlyAlwaysOnTop,state.PromptAlwaysOnTop);break;
                    case "timeOnlyAlwaysOnTop":engine.SetAlwaysOnTop(state.CompactAlwaysOnTop,ReadInt(data,"value",0,1)==1,state.PromptAlwaysOnTop);break;
                    case "promptAlwaysOnTop":engine.SetAlwaysOnTop(state.CompactAlwaysOnTop,state.TimeOnlyAlwaysOnTop,ReadInt(data,"value",0,1)==1);break;
                    case "autoSendIncompleteReflections":engine.SetAutoSendIncompleteReflections(ReadInt(data,"value",0,1)==1);break;
                    case "reflectionSeparator":engine.SetReflectionSeparator((ReflectionSeparator)ReadInt(data,"value",0,3));break;
                    default:throw new ArgumentException("Choose an available display preference.");
                }break;
            case "importSchedules":
                using(var picker=new OpenFileDialog{Title="Import extension schedules",Filter="Extension diagnostic report (*.json)|*.json",CheckFileExists=true}){
                    if(picker.ShowDialog(this)==DialogResult.OK){
                        if(new FileInfo(picker.FileName).Length>1024*1024)throw new ArgumentException("Choose a diagnostic report smaller than one megabyte.");
                        using var report=JsonDocument.Parse(File.ReadAllText(picker.FileName));
                        var entries=report.RootElement.GetProperty("snapshot").GetProperty("scheduledSessions");
                        if(entries.GetArrayLength()>1000)throw new ArgumentException("The report contains too many appointments.");
                        var importedSchedules=entries.EnumerateArray().Select(x=>new ScheduledSession(Guid.NewGuid(),x.GetProperty("targetTime").GetInt64(),x.GetProperty("requestedDurationSeconds").GetInt32(),x.GetProperty("autoRestart").GetBoolean(),x.GetProperty("sfxVolume").GetInt32())).ToList();
                        engine.ImportSchedules(importedSchedules);message="Future schedules imported. Past appointments and duplicate start times were skipped.";
                    }
                }break;
            case "openSheet":
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(ConnectionSetup.NormalizeSheetUrl(state.Connection.SheetUrl)){UseShellExecute=true});break;
            case "clearDiagnostics":
                if(!ReadFlag(data,"confirmed"))throw new ArgumentException("Confirm before clearing the diagnostic log.");
                services.Log.Clear();Post(new{type="diagnostics",report=services.Log.Report(state)});message="Local diagnostic log cleared.";break;
            case "connectionSave":
                var checkedConnection=ReadConnection(data);
                if(ReadFlag(data,"enabled"))message=await services.CheckAndSave(checkedConnection,true);
                else{services.SaveConnection(checkedConnection,false);message=await services.CheckConnection(checkedConnection);}
                break;
            case "connectionPause":
                services.SaveConnection(state.Connection,false); message = "Sheets delivery paused. Pending entries stay saved."; break;
            case "setupRestore":
                Post(new { type="setupImported", connection=state.SetupDraft ?? throw new ArgumentException("There is no saved setup draft.") });
                message="Saved setup draft restored. Verify and enable it when ready."; break;
            case "setupNewToken":
                var tokenDraft=ReadConnection(data) with { ApiToken=ConnectionSetup.NewToken() };
                engine.SaveSetupDraft(tokenDraft);Post(new { type="setupImported", connection=tokenDraft });
                message="New token generated in your setup draft. The active connection is unchanged.";break;
            case "setupImport":
                var imported = ConnectionSetup.Import(ReadString(data,"code",16000));
                // Import is a draft only; verification/enabling is a separate explicit action.
                engine.SaveSetupDraft(imported,true);
                Post(new { type = "setupImported", connection = imported, advance=true });
                message = "Private setup code loaded. Verify and enable the connection when ready."; break;
            case "setupExport":
                Post(new { type = "setupCode", code = ConnectionSetup.Export(state.Connection) });
                message = "Private setup code is ready. Keep it private."; break;
            case "setupScript":
                var draft = ReadConnection(data);
                if (draft.ApiToken.Length == 0) draft = draft with { ApiToken = ConnectionSetup.NewToken() };
                using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("ReflectionTimer.Receiver.gs")!)
                using (var reader = new StreamReader(stream)) {
                    var script = ConnectionSetup.BuildScript(await reader.ReadToEndAsync(),draft);
                    using var save = new SaveFileDialog { Title = "Save your private Apps Script receiver", Filter = "Apps Script text (*.gs)|*.gs", FileName = "private-reflection-receiver.gs" };
                    if (save.ShowDialog(this) == DialogResult.OK) { File.WriteAllText(save.FileName,script); engine.SaveSetupDraft(draft); Post(new { type="setupImported", connection=draft }); message = "Private receiver script saved. Follow the setup steps before enabling delivery."; }
                } break;
            case "saveAppearance":
                var theme=(AppColorTheme)ReadInt(data,"theme",0,3);var placement=(FloatingTimerPlacement)ReadInt(data,"placement",0,7);
                var popup=(ReflectionPopupPosition)ReadInt(data,"popup",0,4);var overlap=(ScheduleOverlapPolicy)ReadInt(data,"overlap",0,2);
                app.SetStartup(ReadFlag(data,"startAtLogin"));
                engine.SetTheme(theme); engine.SetFloatingTimerPlacement(placement); engine.SetPopupPosition(popup);
                engine.SetScheduleOverlap(overlap);
                engine.SaveSettings(state.Connection,ReadFlag(data,"logging"),engine.Snapshot.StartAtLogin,state.ExtensionDisabledConfirmed);
                engine.SetFloatingTimer(ReadFlag(data,"showCompact"));
                engine.SetAlwaysOnTop(ReadFlag(data,"compactAlwaysOnTop"),ReadFlag(data,"timeOnlyAlwaysOnTop"),ReadFlag(data,"promptAlwaysOnTop"));
                engine.SetAutoSendIncompleteReflections(ReadFlag(data,"autoSendIncompleteReflections"));
                engine.SetReflectionSeparator((ReflectionSeparator)ReadInt(data,"reflectionSeparator",0,3));
                app.ApplyDisplayPreferences();
                message="Display, schedule policy, and diagnostics preferences saved."; break;
            case "volume": engine.SetAppVolume(ReadInt(data,"volume",0,100)); message="App volume saved."; break;
            case "saveSound":
                var kind = (SoundEvent)ReadInt(data,"kind",0,4); var previous = AudioSettings.From(state).For(kind);
                engine.SetSound(kind,new() { Track = (LibrarySound)ReadInt(data,"track",0,9),
                    Mp3Path = ReadFlag(data,"keepCustom") ? previous.Mp3Path : "", Behavior = (SoundBehavior)ReadInt(data,"behavior",0,2),
                    Volume = ReadInt(data,"volume",0,100), FadeOutEnabled = ReadFlag(data,"fade"), FadeOutAfterSeconds = ReadInt(data,"fadeSeconds",1,TimerEngine.MaxDuration),
                    FadeOutAfterMessageSent = (kind is SoundEvent.LowTime or SoundEvent.TimeReached) && ReadFlag(data,"fadeAfterMessageSent"),
                    MessageSentFadeSeconds = (kind is SoundEvent.LowTime or SoundEvent.TimeReached) && ReadFlag(data,"fadeAfterMessageSent") ? ReadInt(data,"messageSentFadeSeconds",1,TimerEngine.MaxDuration) : previous.MessageSentFadeSeconds });
                message="Audio settings saved."; break;
            case "browseSound":
                using (var picker = new OpenFileDialog { Title="Choose a custom MP3", Filter="MP3 audio (*.mp3)|*.mp3", CheckFileExists=true }) {
                    if(picker.ShowDialog(this)==DialogResult.OK) {
                        var path = Mp3AudioBackend.ValidateCustomFile(picker.FileName);
                        var sound = (SoundEvent)ReadInt(data,"kind",0,4);
                        engine.SetSound(sound,AudioSettings.From(state).For(sound) with { Mp3Path=path,Track=LibrarySound.Default }); message="Custom MP3 saved.";
                    }
                } break;
            case "previewSound": _ = services.Play((SoundEvent)ReadInt(data,"kind",0,4),true,announcePreview:!ReadFlag(data,"quiet")); message="Playing audio preview using your sound and fade settings."; break;
            case "stopSound": services.StopAudio(); message="App audio stopped."; break;
            case "markIssue": services.Log.Record("issue.marked"); message="Issue marked in local diagnostics."; break;
            case "diagnostics": Post(new { type="diagnostics", report=services.Log.Report(state) }); break;
            case "exportDiagnostics":
                using (var save = new SaveFileDialog { Title="Export diagnostic report", Filter="JSON (*.json)|*.json", FileName="reflection-timer-accessibility-diagnostics.json" }) {
                    if(save.ShowDialog(this)==DialogResult.OK) { File.WriteAllText(save.FileName,JsonSerializer.Serialize(services.Log.Report(state),DataJson.Options)); message="Diagnostic report exported."; }
                } break;
            case "sendPending": _ = services.Sync(true); break;
            case "setCutoff":
                long? cutoff = ReadString(data,"cutoff",40) is { Length: >0 } whenText ? PreviewSession.ParseLocalTime(whenText) : null;
                engine.SetPreferences(cutoff.HasValue||(state.Timer.Mode==SessionMode.Timer?state.Timer:state.ParkedTimer??new TimerState()).AutoRestart,state.Timer.Volume,cutoff); message="Auto-start cutoff saved."; break;
        }
        Post(new { type="settings", settings=services.Settings() });
        Post(new{type="scheduledLow",low=PreviewSession.LowView(app.Session.ScheduledLowDraft,AudioSettings.From(engine.Snapshot).LowTimeThresholdSeconds)});
        Reply(requestId); if(message.Length>0&&!ReadFlag(data,"quiet")) app.Announce(message); return true;
    }
}
