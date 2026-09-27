using ReflectionTimer.Accessible;

// Service tests must never use the user's speakers, even when a fixture enables
// speech explicitly. Voice tests use a recording backend.
sealed class SilentSpeech : IVoiceOutput
{
    public void Speak(string text,int volume) { }
    public void SetVolume(int volume) { }
    public void Stop() { }
    public bool TakeFailure()=>false;
    public void Dispose() { }
}
