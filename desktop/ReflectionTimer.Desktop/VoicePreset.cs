using System.Globalization;
using System.Security;

namespace ReflectionTimer.Accessible;

internal sealed record InstalledVoice(string Name,string Gender,string Languages);

// Shared distribution default, matching the hotkey reader's voice preset.
// This selects an installed voice; it never changes Windows settings or copies
// voice packages from one PC to another.
internal static class VoicePreset
{
    internal const string Name="Microsoft Hazel Desktop";
    internal const int Rate=3;
    internal const string Pitch="+20%";
    internal static int Select(IReadOnlyList<InstalledVoice> voices)
    {
        var best=-1;var rank=int.MaxValue;
        for(var i=0;i<voices.Count;i++) {
            var voice=voices[i];
            var candidate=string.Equals(voice.Name,Name,StringComparison.OrdinalIgnoreCase)?0
                : !string.Equals(voice.Gender,"Female",StringComparison.OrdinalIgnoreCase)?int.MaxValue
                : HasLanguage(voice.Languages,0x809)?1:HasLanguage(voice.Languages,0x409)?2:3;
            if(candidate<rank){best=i;rank=candidate;}
        }
        return best; // No suitable preference: retain Windows' default voice.
    }
    private static bool HasLanguage(string languages,int language)=>languages.Split(';').Any(value=>
        int.TryParse(value.Trim(),NumberStyles.HexNumber,CultureInfo.InvariantCulture,out var id)&&id==language);
    internal static string Culture(string languages)
    {
        foreach(var value in languages.Split(';')) {
            if(!int.TryParse(value.Trim(),NumberStyles.HexNumber,CultureInfo.InvariantCulture,out var id))continue;
            try {var name=CultureInfo.GetCultureInfo(id).Name;if(name.Length>0)return name;}catch(CultureNotFoundException){}
        }
        return "en-GB";
    }
    internal static string Ssml(string text,string culture)=>
        $"<speak version=\"1.0\" xmlns=\"http://www.w3.org/2001/10/synthesis\" xml:lang=\"{SecurityElement.Escape(culture)}\"><prosody pitch=\"{Pitch}\">{SecurityElement.Escape(text)}</prosody></speak>";
}
