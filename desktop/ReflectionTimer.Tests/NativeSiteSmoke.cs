using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using ReflectionTimer.Accessible;

// Opt-in integration check against this test's own WebView2 and local fixtures.
// It never enumerates other windows, attaches to Chrome, reads a user browser
// profile, activates a timer, or accesses the installed application's settings.
internal static class NativeSiteSmoke
{
    internal static void Run()
    {
        Exception? failure=null;var passed=0;
        var directory=Path.Combine(Path.GetTempPath(),"ReflectionTimer-NativeSite-"+Guid.NewGuid().ToString("N"));
        var content=Path.Combine(directory,"fixture");Directory.CreateDirectory(Path.Combine(content,"private-notes"));
        File.WriteAllText(Path.Combine(content,"private-notes","index.html"),"""
            <!doctype html><html><head><title>https://misleading-title.test/private</title>
            <style>html,body{margin:0;height:100%;font:18px sans-serif}iframe{width:100%;height:75%;border:0}textarea{display:block}</style>
            </head><body><h1>Local Site metadata fixture</h1>
            <textarea aria-label="Document draft">https://uncommitted-input.test/private</textarea>
            <iframe title="Embedded different website" src="https://embedded-frame.test/frame.html" onload="window.frameReady=true"></iframe>
            </body></html>
            """);
        File.WriteAllText(Path.Combine(content,"frame.html"),"<!doctype html><title>Embedded website</title><h1>Different local website</h1>");
        var thread=new Thread(()=>{
            try{
                Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);Application.EnableVisualStyles();
                var primary=Screen.PrimaryScreen!.WorkingArea;
                using var window=new PassiveForm{Text="Native Site metadata fixture",ShowInTaskbar=false,Width=760,Height=540,
                    StartPosition=FormStartPosition.Manual,Location=new Point(primary.Left+50,primary.Top+50)};
                using var address=new TextBox{Dock=DockStyle.Top,Text="https://typed-address.test/private?unsent=1",AccessibleName="Address bar fixture"};
                using var browser=new WebView2{Dock=DockStyle.Fill};
                window.Controls.Add(browser);window.Controls.Add(address);
                var foreground=GetForegroundWindow();
                window.Shown+=async(_,_)=>{
                    try{
                        var environment=await CoreWebView2Environment.CreateAsync(null,Path.Combine(directory,"webview-profile"),
                            new CoreWebView2EnvironmentOptions("--disable-background-networking --disable-component-update --no-first-run"));
                        await browser.EnsureCoreWebView2Async(environment);
                        browser.CoreWebView2.Settings.AreDefaultContextMenusEnabled=false;
                        browser.CoreWebView2.SetVirtualHostNameToFolderMapping("native-site.test",content,CoreWebView2HostResourceAccessKind.Allow);
                        browser.CoreWebView2.SetVirtualHostNameToFolderMapping("embedded-frame.test",content,CoreWebView2HostResourceAccessKind.Allow);
                        var rootAddress="https://native-site.test/private-notes/index.html?token=fixture-secret#hidden-fragment";
                        await Navigate(browser,rootAddress);
                        await Until(async()=>JsonDocument.Parse(await browser.CoreWebView2.ExecuteScriptAsync("window.frameReady===true")).RootElement.ValueKind==JsonValueKind.True);
                        var actual=await Read(window.Handle);
                        var nativeHandle=window.Handle;
                        Check(await Task.Run(()=>BrowserWindowBounds.Read(nativeHandle)) is not null,
                            "The native metadata fixture converts primary-display HWND bounds using actual Windows DPI and text scale");
                        Check(actual=="native-site.test","Native UIA reads the committed host from the test WebView's root Document metadata");
                        Check(actual!=rootAddress&&!actual!.Contains('/')&&!actual.Contains('?')&&!actual.Contains('#'),
                            "Native UIA returns no private page path, query or fragment");
                        Check(actual!="embedded-frame.test","A large cross-site nested iframe cannot override the root Document's website");
                        Check(actual!="typed-address.test"&&actual!="uncommitted-input.test"&&actual!="misleading-title.test",
                            "Native Site reading ignores address edits, document text inputs and misleading window or document titles");
                        Check(GetForegroundWindow()==foreground,"The isolated native Site fixture appears without taking foreground focus");

                        await Navigate(browser,"about:blank");
                        await browser.CoreWebView2.ExecuteScriptAsync("document.title='https://false-title.test/';document.body.innerHTML='<textarea aria-label=\"Draft\">https://false-input.test/</textarea>'");
                        var blank=await Read(window.Handle);
                        Check(blank is null,"An about:blank Document stays unknown even beside an HTTP address edit and URL-shaped page content");
                        browser.Visible=false;
                        Check(await Read(window.Handle) is null,"A window containing only address-bar-like Edit controls supplies no website");
                    }catch(Exception error){failure=error;}
                    finally{window.Close();}
                };
                Application.Run(window);
            }catch(Exception error){failure=error;}
        });
        thread.SetApartmentState(ApartmentState.STA);thread.IsBackground=true;thread.Start();
        if(!thread.Join(TimeSpan.FromSeconds(50)))throw new TimeoutException("Native Site metadata fixture timed out.");
        TryRemoveFixture(directory);
        if(failure is not null)throw new Exception("Native Site metadata checks failed.",failure);
        Console.WriteLine($"{passed} isolated native Site metadata checks passed.");
        void Check(bool condition,string message){if(!condition)throw new Exception(message);passed++;Console.WriteLine("PASS "+message);}
    }
    private static async Task Navigate(WebView2 browser,string address)
    {
        var completed=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Loaded(object? sender,CoreWebView2NavigationCompletedEventArgs e){if(e.IsSuccess)completed.TrySetResult();else completed.TrySetException(new Exception("Local fixture navigation failed: "+e.WebErrorStatus));}
        browser.CoreWebView2.NavigationCompleted+=Loaded;
        try{browser.CoreWebView2.Navigate(address);await completed.Task.WaitAsync(TimeSpan.FromSeconds(15));}
        finally{browser.CoreWebView2.NavigationCompleted-=Loaded;}
    }
    private static Task<string?> Read(nint handle)=>Task.Run(()=>NativeBrowserSites.Read(handle));
    private static async Task Until(Func<Task<bool>> ready)
    {
        using var limit=new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while(!await ready())await Task.Delay(50,limit.Token);
    }
    private static void TryRemoveFixture(string directory)
    {
        var resolved=Path.GetFullPath(directory);var temp=Path.GetFullPath(Path.GetTempPath());
        if(!resolved.StartsWith(temp,StringComparison.OrdinalIgnoreCase)||!Path.GetFileName(resolved).StartsWith("ReflectionTimer-NativeSite-",StringComparison.Ordinal))return;
        try{Directory.Delete(resolved,true);}catch(IOException){}catch(UnauthorizedAccessException){}
    }
    private sealed class PassiveForm:Form{protected override bool ShowWithoutActivation=>true;}
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
}
