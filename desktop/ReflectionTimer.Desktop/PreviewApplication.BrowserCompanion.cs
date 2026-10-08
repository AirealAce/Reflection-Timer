using System.Diagnostics;
using ReflectionTimer.Desktop;

namespace ReflectionTimer.Accessible;

internal sealed partial class PreviewApplication
{
    private BrowserCompanionHost? browserCompanion;
    private readonly object companionGate=new();
    private bool companionStartFailed;

    private void UpdateBrowserCompanion()
    {
        var enabled=!closing&&!Session.IsolatedProfile&&ProfileName is null
            &&Session.Engine.SettingsSnapshot.FocusMode.BrowserCompanionEnabled;
        if(!enabled){StopBrowserCompanion();companionStartFailed=false;return;}
        if(browserCompanion is not null||companionStartFailed)return;
        try{
            var host=BrowserCompanionHost.StartServer();
            lock(companionGate)browserCompanion=host;
            host.Received+=(id,packet)=>{lock(companionGate)if(ReferenceEquals(browserCompanion,host)&&!closing)browserIndex.Receive(id,packet);};
            host.Disconnected+=browserIndex.Disconnect;
        }catch{
            companionStartFailed=true;
            Services.Log.Record("focus.companionUnavailable");
            Announce("Browser companion could not connect. Native Site tracking remains available. Turn the companion off and on to retry.");
        }
    }
    private void StopBrowserCompanion()
    {
        BrowserCompanionHost? retired;
        lock(companionGate){retired=browserCompanion;browserCompanion=null;}
        retired?.Dispose();
    }
    internal void ConfigureBrowserCompanion()
    {
        if(Session.IsolatedProfile||ProfileName is not null)
            throw new ArgumentException("Configure the companion from your main Reflection Timer installation.");
        var folder=Path.Combine(AppContext.BaseDirectory,"browser-companion");
        BrowserCompanionRegistration.Register(Environment.ProcessPath!,folder);
        Process.Start(new ProcessStartInfo(folder){UseShellExecute=true});
        Services.AnnounceFeedback("Companion folder opened. In Chrome or Edge's extensions page, turn on Developer mode, choose Load unpacked, and select this folder. Then enable Use browser companion here and save.");
    }
}
