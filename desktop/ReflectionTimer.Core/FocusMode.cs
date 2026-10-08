using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace ReflectionTimer.Core;

public enum FocusTargetKind { Window, BrowserTab, BrowserTabGroup, Site = 3 }
public enum FocusCaptureScope { Focused, FocusedIncludingBackground, OpenIncludingBackground }
public enum FocusGlowStyle { CrimsonHalo, Classic }

// Stored only inside the encrypted profile. Runtime IDs identify the exact tab,
// rather than confusing duplicate titles or a page that changes its title.
public record FocusTarget(Guid Id, FocusTargetKind Kind, string Name, string App,
    long WindowHandle, int ProcessId, long ProcessStartedAt, string TabRuntimeId = "")
{
    public string WindowName { get; init; } = "";
    public int TabPosition { get; init; }
    // Durable window hints stay in the encrypted profile, never in WebView data.
    public string ProcessPath { get; init; } = "";
    public string WindowClass { get; init; } = "";
    public string SiteHost { get; init; } = "";
    // This choice stays in the profile; its captured native target is session-only.
    public bool UseFocused { get; init; }
    public FocusCaptureScope CaptureScope { get; init; }
    [JsonIgnore] public bool CaptureUnknown { get; init; }
    public static FocusTarget Focused(FocusTargetKind kind, FocusCaptureScope scope=FocusCaptureScope.Focused)
    {
        if(!ValidScope(kind,scope))throw new ArgumentException("Choose a supported dynamic target.");
        var name=scope==FocusCaptureScope.Focused?"Use focused "+(kind==FocusTargetKind.Window?"window":kind==FocusTargetKind.BrowserTab?"tab":kind==FocusTargetKind.Site?"site":"tab group")
            : scope==FocusCaptureScope.OpenIncludingBackground?"Use open "+(kind==FocusTargetKind.Window?"windows":"tab groups")+" (including background)"
            : "Use focused "+(kind==FocusTargetKind.BrowserTab?"tabs":"tab groups")+" (including background)";
        return new(Guid.Empty,kind,name,"",0,0,0){UseFocused=true,CaptureScope=scope};
    }
    public static bool ValidScope(FocusTargetKind kind,FocusCaptureScope scope)=>Enum.IsDefined(kind)&&Enum.IsDefined(scope)
        && (scope==FocusCaptureScope.Focused||kind!=FocusTargetKind.Site&&(scope==FocusCaptureScope.FocusedIncludingBackground&&kind!=FocusTargetKind.Window
            ||scope==FocusCaptureScope.OpenIncludingBackground&&kind!=FocusTargetKind.BrowserTab));
    [JsonIgnore] public string Key => UseFocused ? CaptureScope==FocusCaptureScope.Focused?$"focused:{(int)Kind}":$"captured:{(int)Kind}:{(int)CaptureScope}"
        : Kind==FocusTargetKind.Site ? "site:"+FocusSites.CanonicalHost(SiteHost)
        : $"{(int)Kind}:{ProcessId}:{ProcessStartedAt}:{WindowHandle}:{(Kind == FocusTargetKind.Window ? "" : TabRuntimeId)}";
}

public record FocusModeSettings
{
    public bool Enabled { get; init; }
    public int DelaySeconds { get; init; } = 5;
    public FocusTarget? Target { get; init; }
    public ImmutableArray<FocusTarget> Targets { get; init; } = [];
    public bool MultipleTargets { get; init; }
    public bool TargetOnSiteLinks { get; init; } = true;
    public bool BrowserCompanionEnabled { get; init; }
    public bool IdleEnabled { get; init; }
    public int IdleSeconds { get; init; } = 20;
    public bool ScreenEdgeGlow { get; init; } = true;
    public FocusGlowStyle ScreenEdgeGlowStyle { get; init; } = FocusGlowStyle.CrimsonHalo;
    // Older encrypted profiles stored only Target. Keep that choice on upgrade.
    [JsonIgnore] public ImmutableArray<FocusTarget> SelectedTargets => !Targets.IsDefaultOrEmpty ? Targets : Target is {} target ? [target] : [];
    [JsonIgnore] public string SelectionKey => string.Join("|", SelectedTargets.Select(t => t.Key).Order(StringComparer.Ordinal));
}

public enum FocusPresence { Focused, Away, Unavailable, Unknown }
public record FocusModeDecision(bool Alert, string Status, bool ScreenEdgeGlow = false);

// Monotonic elapsed time; no dependence on wall-clock edits or sleep recovery.
public sealed class FocusModeGate
{
    private string? targets;
    private Guid? session;
    private SessionMode mode;
    private int delay;
    private long? awaySince;
    public FocusModeDecision Evaluate(FocusModeSettings settings, TimerState timer, FocusPresence presence, long now, long? idleMilliseconds = null)
    {
        if (targets != settings.SelectionKey || session != timer.SessionId || mode != timer.Mode || delay != settings.DelaySeconds) {
            awaySince = null; targets = settings.SelectionKey; session = timer.SessionId; mode = timer.Mode; delay = settings.DelaySeconds;
        }
        if (!settings.Enabled || !timer.IsRunning) {
            awaySince = null;
            return new(false, !settings.Enabled ? "Off" : "Waiting for the session to run");
        }
        var hasTargets = settings.SelectedTargets.Length > 0;
        if (hasTargets && presence == FocusPresence.Away) awaySince ??= now;
        else awaySince = null;
        var remaining = awaySince is {} since ? Math.Max(0, settings.DelaySeconds - (now - since) / 1000) : (long?)null;
        var idle = settings.IdleEnabled && idleMilliseconds >= settings.IdleSeconds * 1000L;
        // The visual warning is immediate and independent of the audio grace
        // period. Inactivity on a selected target is not leaving that target.
        var glow = settings.ScreenEdgeGlow && hasTargets && presence == FocusPresence.Away;
        if (idle) return new(true, $"Idle for {settings.IdleSeconds} seconds · alert active", glow);
        if (remaining is {} seconds) return new(seconds == 0, seconds == 0 ? "Away from selected targets · delay reached" : $"Away from selected targets · alert in {seconds} seconds", glow);
        return new(false, !hasTargets ? settings.IdleEnabled ? "Watching for inactivity" : "Choose a window or tab" : presence switch {
            FocusPresence.Focused => "A selected target is focused",
            FocusPresence.Unavailable => "Targets closed or moved. Choose them again.",
            _ => "Targets could not be checked. Away alert stopped."
        });
    }
}
