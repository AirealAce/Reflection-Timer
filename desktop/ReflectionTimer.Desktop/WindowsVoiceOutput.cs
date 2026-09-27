using System.Runtime.InteropServices;

namespace ReflectionTimer.Accessible;

internal interface ISystemVoice : IDisposable
{
    int Volume { set; }
    bool IsSpeaking { get; }
    void Speak(string text);
}

// Windows' installed SAPI voice: local text synthesis; no microphone or network.
internal sealed class SapiVoice : ISystemVoice
{
    private object? instance;
    private string culture="en-GB";
    // The supplied test instance redirects sound to memory, never the speakers.
    internal SapiVoice(object? instance=null)
    {
        this.instance=instance ?? Activator.CreateInstance(
            Type.GetTypeFromProgID("SAPI.SpVoice") ?? throw new InvalidOperationException("Windows speech is unavailable."))!;
        try { SelectPreset(); ((dynamic)this.instance).Rate=VoicePreset.Rate; }
        catch {Dispose();throw;}
    }
    private static string Attribute(object token,string name)
    {
        try{return (string)((dynamic)token).GetAttribute(name);}catch(COMException){return "";}
    }
    private void SelectPreset()
    {
        object? collection=null;object? selected=null;var tokens=new List<object>();
        try {
            collection=((dynamic)instance!).GetVoices();
            var voices=new List<InstalledVoice>();
            for(var i=0;i<(int)((dynamic)collection).Count;i++) {
                object token=((dynamic)collection).Item(i);tokens.Add(token);
                voices.Add(new(Attribute(token,"Name"),Attribute(token,"Gender"),Attribute(token,"Language")));
            }
            var index=VoicePreset.Select(voices);
            if(index>=0)((dynamic)instance!).Voice=tokens[index];
            selected=((dynamic)instance!).Voice;
            culture=VoicePreset.Culture(Attribute(selected,"Language"));
        } finally {
            // Release one owned COM reference per acquisition, including the
            // selected token when it aliases a token already in the list.
            if(selected is not null)Marshal.ReleaseComObject(selected);
            foreach(var token in tokens)Marshal.ReleaseComObject(token);
            if(collection is not null)Marshal.ReleaseComObject(collection);
        }
    }
    public int Volume { set { ((dynamic)instance!).Volume=Math.Clamp(value,0,100); } }
    public bool IsSpeaking {
        get {
            object status=((dynamic)instance!).Status;
            try{return (int)((dynamic)status).RunningState==2;}
            finally{Marshal.ReleaseComObject(status);}
        }
    }
    public void Speak(string text) {
        // Empty plain text cancels. Otherwise supply only our own escaped SSML
        // for pitch, with async + purge + XML + explicit SSML parsing flags.
        // Never accept raw markup or interpret text as a file path.
        ((dynamic)instance!).Speak(text.Length==0?"":VoicePreset.Ssml(text,culture),text.Length==0?1|2|16:1|2|8|0x100);
    }
    public void Dispose()
    {
        var current=instance;instance=null;
        if(current is null)return;
        try{((dynamic)current).Speak("",1|2|16);}finally{Marshal.FinalReleaseComObject(current);}
    }
}

// COM creation and playback stay off the timer/UI thread. One replaceable
// message prevents a backlog during rapid start/pause/resume operations.
internal sealed class WindowsVoiceOutput : IVoiceOutput
{
    private readonly object gate=new();
    private readonly Func<ISystemVoice> create;
    private Thread? worker;
    private string? pending;
    private string? supplement;
    private int volume=50,revision,supplementRevision;
    private bool dirty,disposed,failed;
    internal WindowsVoiceOutput(Func<ISystemVoice>? create=null)=>this.create=create??(()=>new SapiVoice());
    public void Speak(string text,int volume)
    {
        lock(gate) {
            if(disposed)return;
            this.volume=Math.Clamp(volume,0,100);
            pending=this.volume>0?text:"";revision++;supplement=null;supplementRevision++;dirty=true;
            if(pending.Length>0)StartWorker();
            Monitor.PulseAll(gate);
        }
    }
    public void SpeakSupplement(string text,int volume)
    {
        lock(gate) {
            if(disposed)return;
            this.volume=Math.Clamp(volume,0,100);
            if(this.volume==0)return;
            supplement=text;supplementRevision++;dirty=true;StartWorker();Monitor.PulseAll(gate);
        }
    }
    private void StartWorker()
    {
        if(worker is not null)return;
        worker=new Thread(Run){IsBackground=true,Name="Reflection Timer voice"};
        worker.SetApartmentState(ApartmentState.STA);worker.Start();
    }
    public void SetVolume(int volume)
    {
        lock(gate) {
            if(disposed)return;
            this.volume=Math.Clamp(volume,0,100);
            if(this.volume==0){pending="";revision++;supplement=null;supplementRevision++;}
            dirty=true;Monitor.PulseAll(gate);
        }
    }
    public void Stop(){lock(gate){if(disposed)return;pending="";revision++;supplement=null;supplementRevision++;dirty=true;Monitor.PulseAll(gate);}}
    public bool TakeFailure(){lock(gate){var result=failed;failed=false;return result;}}
    private void Run()
    {
        ISystemVoice? voice=null;var playingSupplement=false;
        try {
            while(true) {
                string? text,extra;int request,extraRequest;
                lock(gate) {
                    while(!dirty&&!disposed)Monitor.Wait(gate);
                    if(disposed)return;
                    text=pending;pending=null;dirty=false;request=revision;extra=supplement;extraRequest=supplementRevision;
                }
                try {
                    if(voice is null && (!string.IsNullOrEmpty(text)||extra is not null))voice=create();
                    if(voice is null)continue;
                    int gain;
                    lock(gate){if(disposed)return;if(request!=revision)continue;gain=volume;}
                    voice.Volume=gain;
                    if(text is not null){voice.Speak(gain>0?text:"");playingSupplement=false;}
                    else if(extra is not null&&(playingSupplement||!voice.IsSpeaking)) {
                        lock(gate){if(disposed)return;if(extraRequest!=supplementRevision||request!=revision)continue;supplement=null;}
                        voice.Speak(gain>0?extra:"");playingSupplement=true;
                    }
                } catch {
                    lock(gate){failed=true;if(extraRequest==supplementRevision)supplement=null;}
                    try{voice?.Dispose();}catch{}voice=null;
                }
                lock(gate) {
                    if(supplement is not null) {
                        dirty=true;
                        // Poll only while a supplementary view announcement is
                        // waiting; new actions/cancel wake this immediately.
                        if(pending is null&&!disposed)Monitor.Wait(gate,50);
                    }
                }
            }
        } finally {try{voice?.Dispose();}catch{}}
    }
    public void Dispose(){lock(gate){if(disposed)return;disposed=true;pending=null;supplement=null;Monitor.PulseAll(gate);}}
}
