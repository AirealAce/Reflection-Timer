using ReflectionTimer.Core;

namespace ReflectionTimer.Accessible;

public interface IVoiceOutput : IDisposable
{
    void Speak(string text, int volume);
    void SpeakSupplement(string text, int volume) => Speak(text,volume);
    void SetVolume(int volume);
    void Stop();
    bool TakeFailure();
}

// Observe committed transitions, not window clicks. This also covers shortcuts,
// scheduled starts, automatic repeats and saving a paused stopwatch reflection.
internal sealed class SessionVoice : IDisposable
{
    private readonly TimerEngine engine;
    private readonly IVoiceOutput output;
    private readonly Action<string,bool>? screenReader;
    private AppState previous;
    private bool disposed, failed, failureReported;
    internal SessionVoice(TimerEngine engine, IVoiceOutput output, Action<string,bool>? screenReader = null)
    {
        this.engine=engine; this.output=output; this.screenReader=screenReader; previous=engine.SettingsSnapshot;
        engine.ActivityRecorded+=OnActivity;
    }
    private void OnActivity(Activity activity)
    {
        if(disposed)return;
        var before=previous; var after=engine.SettingsSnapshot; previous=after;
        // Optional synthesized speech has its own mute/opt-out. Neither can
        // suppress the committed status messages sent to assistive technology.
        try {
            if(before.Timer.Volume!=after.Timer.Volume)output.SetVolume(after.Timer.Volume);
            if(!after.VoiceAnnouncements) {
                if(before.VoiceAnnouncements)output.Stop();
                failureReported=false;
            }
        } catch { failed=true; }
        var speak=before.VoiceAnnouncements&&after.VoiceAnnouncements&&after.Timer.Volume>0;
        PublishTransitions(activity,before,after,speak);
    }
    private void PublishTransitions(Activity activity,AppState before,AppState after,bool speak)
    {
        var timer=after.Timer;
        string? action=null;
        if(activity.Event=="timer.reset")action="reset";
        else if(timer.IsRunning) {
            if(timer.SessionId!=before.Timer.SessionId || timer.Mode!=before.Timer.Mode)action="started";
            else if(!before.Timer.IsRunning)action="resumed";
        } else if(before.Timer.IsRunning) {
            var paused=timer.SessionId==before.Timer.SessionId && timer.Mode==before.Timer.Mode
                ? timer : after.ParkedTimer;
            if(paused is not null && paused.SessionId==before.Timer.SessionId && TimerEngine.IsPaused(paused)) {
                timer=paused;action="paused";
            }
        }
        var message=action is null?"":Message(timer,action,engine.ElapsedNow);
        if(before.Timer.Mode!=after.Timer.Mode)message+=(message.Length>0?" ":"")+(after.Timer.Mode==SessionMode.Stopwatch?"Stopwatch mode.":"Timer mode.");
        if(message.Length>0)Publish(message,false,speak,after.Timer.Volume);
        // Observe the saved preference, not one particular control/shortcut.
        // Delay/target edits, polling and repeated saves remain silent.
        if(before.FocusMode.Enabled!=after.FocusMode.Enabled)
            Publish(after.FocusMode.Enabled?"Focus mode on.":"Focus mode off.",false,speak,after.Timer.Volume);
        // Layout changes can follow a start/pause asynchronously. Keep the
        // latest view announcement, but let the action and its time finish.
        if(before.ShowFloatingTimer!=after.ShowFloatingTimer || after.ShowFloatingTimer&&before.FloatingTimeOnly.HasValue&&before.FloatingTimeOnly!=after.FloatingTimeOnly)
            Publish(!after.ShowFloatingTimer?"Floating timer hidden.":after.FloatingTimeOnly==true?"Time-only view.":"Compact view.",true,speak,after.Timer.Volume);
        if(before.ShowAppView!=after.ShowAppView&&after.ShowAppView.HasValue)
            Publish(after.ShowAppView.Value?"App view.":"App view hidden.",true,speak,after.Timer.Volume);
    }
    internal void Feedback(string message,bool supplementary=false,bool nativeControlAnnounces=false)
    {
        if(disposed||string.IsNullOrWhiteSpace(message))return;
        var state=engine.SettingsSnapshot;
        Publish(message,supplementary,state.VoiceAnnouncements&&state.Timer.Volume>0,state.Timer.Volume,!nativeControlAnnounces);
    }
    private void Publish(string message,bool supplementary,bool speak,int volume,bool notifyReader=true)
    {
        // Keep failures in either delivery path from affecting the other path
        // or undoing the already-saved timer action.
        try { if(notifyReader)screenReader?.Invoke(message,supplementary); } catch { }
        if(!speak)return;
        try { if(supplementary)output.SpeakSupplement(message,volume);else output.Speak(message,volume); }
        catch { failed=true; }
    }
    internal static string Message(TimerState timer, string action, long now)
    {
        var stopwatch=timer.Mode==SessionMode.Stopwatch;
        var seconds=stopwatch ? TimerEngine.ActualSeconds(timer,now) : TimerEngine.Remaining(timer,now);
        if(action=="reset")return $"{(stopwatch?"Stopwatch":"Timer")} reset" + (seconds>0 ? $" to {PreviewSession.SpeakTime(seconds)}." : ".");
        // Keep the action and time in one phrase, avoiding the speech engine's
        // long sentence-ending pause after started/paused/resumed.
        return $"{(stopwatch?"Stopwatch":"Timer")} {action}" + (seconds>0
            ? $" {(stopwatch?"at":"with")} {PreviewSession.SpeakTime(seconds)} {(stopwatch?"elapsed":"remaining")}." : ".");
    }
    internal string? PollFailure()
    {
        var error=failed;failed=false;
        try {error|=output.TakeFailure();}catch {error=true;}
        if(!error||failureReported)return null;
        failureReported=true;
        return "Voice announcements could not play. Your timer is unaffected. Check that a Windows speech voice is installed, or turn voice announcements off.";
    }
    internal void Stop(){try{output.Stop();}catch{failed=true;}}
    internal void Preview(){try{output.Speak(Message(new(){RemainingSeconds=300},"paused",0),engine.CurrentTimer.Volume);}catch{failed=true;}}
    public void Dispose()
    {
        if(disposed)return;disposed=true;
        engine.ActivityRecorded-=OnActivity;
        try{output.Dispose();}catch{ /* Speech must never prevent app shutdown. */ }
    }
}
