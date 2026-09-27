using System.Runtime.InteropServices;
using ReflectionTimer.Accessible;

// Actual Windows synthesis redirected to memory: no speaker playback, user
// settings, timer actions, microphone access, or screen-reader tests.
static class NativeVoiceSynthesisSmoke
{
    internal static void Run()
    {
        Exception? failure=null;
        var thread=new Thread(()=>{
            object? stream=null;
            try {
                var native=Activator.CreateInstance(Type.GetTypeFromProgID("SAPI.SpVoice",true)!)!;
                stream=Activator.CreateInstance(Type.GetTypeFromProgID("SAPI.SpMemoryStream",true)!)!;
                ((dynamic)native).AudioOutputStream=stream;
                using var voice=new SapiVoice(native);
                object chosen=((dynamic)native).Voice;
                try {
                    var name=(string)((dynamic)chosen).GetAttribute("Name");
                    object available=((dynamic)native).GetVoices("Name="+VoicePreset.Name);
                    try {if((int)((dynamic)available).Count>0&&name!=VoicePreset.Name)throw new Exception("Installed Hazel was not selected.");}
                    finally{Marshal.ReleaseComObject(available);}
                    if((int)((dynamic)native).Rate!=3)throw new Exception("The default speaking rate was not applied.");
                    Console.WriteLine($"PASS Voice preset selected {name}, rate +3, SSML pitch +20%.");
                } finally{Marshal.ReleaseComObject(chosen);}
                voice.Volume=50;
                voice.Speak("Timer reset to 5 minutes.");
                if(!((dynamic)native).WaitUntilDone(15000))throw new TimeoutException("Windows synthesis timed out.");
                var data=(Array)((dynamic)stream).GetData();
                if(data.Length==0)throw new Exception("Windows did not synthesize audio.");
                Console.WriteLine($"PASS Windows voice synthesized {data.Length} audio bytes to memory using the app's asynchronous speech adapter.");
                if(voice.IsSpeaking)throw new Exception("Finished Windows speech still reports playing.");
                Console.WriteLine("PASS Native speech completion can release a waiting view announcement.");
                voice.Speak("Stopwatch started.");voice.Speak("");
                if(!((dynamic)native).WaitUntilDone(15000))throw new TimeoutException("Speech cancellation timed out.");
                Console.WriteLine("PASS Windows speech can be canceled without blocking timer operations.");
            }catch(Exception error){failure=error;}
            finally{if(stream is not null)Marshal.FinalReleaseComObject(stream);}
        });
        thread.SetApartmentState(ApartmentState.STA);thread.Start();
        if(!thread.Join(TimeSpan.FromSeconds(35)))throw new TimeoutException("Voice synthesis checks timed out.");
        if(failure is not null)throw new Exception("Voice synthesis checks failed.",failure);
    }
}
