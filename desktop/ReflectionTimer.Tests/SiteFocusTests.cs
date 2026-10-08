using System.Text.Json;
using ReflectionTimer.Core;

internal static class SiteFocusTests
{
    internal static void Run(Action<bool,string> check)
    {
        check((int)FocusTargetKind.Window==0&&(int)FocusTargetKind.BrowserTab==1
            &&(int)FocusTargetKind.BrowserTabGroup==2&&(int)FocusTargetKind.Site==3,
            "Site appends its target kind without renumbering existing saved window, tab or group kinds");
        var canonical=new (string Input,string Host)[]{
            (" https://WWW.Example.COM:443/docs?q=test#section ","example.com"),
            ("http://example.com:80/page","example.com"),
            ("http://example.com:443/page","example.com:443"),
            ("https://example.com:8443/page?q=value#fragment","example.com:8443"),
            ("Example.COM:8443/page?q=value#fragment","example.com:8443"),
            ("example.com:443","example.com:443"),
            ("www.example.com/notes","example.com"),
            ("example.com/notes?next=https://other.example/","example.com"),
            ("www.www.example.com","example.com"),
            ("https://www.com/","www.com"),
            ("https://sub.example.com/","sub.example.com"),
            ("https://www.bücher.example/seite","xn--bcher-kva.example"),
            ("https://example.com./","example.com"),
            ("http://127.0.0.1:8000/","127.0.0.1:8000"),
            ("https://[2001:db8::1]:8443/notes","[2001:db8::1]:8443"),
            ("localhost:3000","localhost:3000")
        };
        foreach(var test in canonical){
            check(FocusSites.TryCanonicalHost(test.Input,out var actual)&&actual==test.Host
                &&FocusSites.CanonicalHost(test.Input)==test.Host,"Website identity canonicalizes "+test.Input.Trim());
            check(FocusSites.CanonicalHost(test.Host)==test.Host,"Canonical website identity remains stable when saved and reread: "+test.Host);
        }
        string?[] invalid=[null,""," ","https://user:password@example.com/","https://@example.com/",
            "https://example.com@evil.example/","javascript:alert(1)","file:///C:/notes.html","chrome://settings",
            "mailto:user@example.com","ftp://example.com","//example.com","https:///example.com","example.com:0",
            "example.com:65536","example.com:-1","example.com:port","example.com:","https://[::1]:",
            "https://-bad.example","https://bad-.example","https://bad_name.example","https://example..com",
            "https://.example.com","https://example.com../",@"https://example.com\@evil.example/",
            "https://example%2ecom/","https://[::1%25eth0]/","https://exam ple.com","https://exam\0ple.com"];
        foreach(var input in invalid)
            check(!FocusSites.TryCanonicalHost(input,out var rejected)&&rejected=="",
                "Invalid or credential-bearing website input is rejected: "+(input??"(null)"));
        var threw=false;try{FocusSites.CanonicalHost("file:///notes.html");}catch(ArgumentException){threw=true;}
        check(threw,"The throwing website parser gives a controlled validation error for unsupported addresses");
        check(FocusSites.Matches("example.com","https://example.com/page?q=test#section")
            &&FocusSites.Matches("www.example.com","http://www.example.com/another")
            &&FocusSites.Matches("example.com","https://docs.example.com/path")
            &&FocusSites.Matches("example.com","https://deep.docs.example.com/"),
            "A website target accepts its host, common www alias and child subdomains regardless of page path");
        check(FocusSites.Matches("bücher.example","https://xn--bcher-kva.example/notes")
            &&FocusSites.Matches("xn--bcher-kva.example","https://www.bücher.example/"),
            "Unicode and IDN ASCII website forms share the same matching identity");
        foreach(var candidate in new[]{"https://example.com.evil.example/","https://badexample.com/",
            "https://example.comevil/","https://example.com@evil.example/","https://evil@example.com/",
            "file://example.com/notes","https://example.com:8443/"} )
            check(!FocusSites.Matches("example.com",candidate),"Website matching rejects unrelated hosts, ports or credentials: "+candidate);
        check(!FocusSites.Matches("docs.example.com","https://example.com/")
            &&!FocusSites.Matches("docs.example.com","https://other.example.com/"),
            "A child website target does not grant its parent or sibling website");
        check(!FocusSites.Matches("www.com","https://other.com/"),
            "A literal www domain is not broadened to every website under its top-level domain");
        check(FocusSites.Matches("example.com:8443","https://docs.example.com:8443/page")
            &&!FocusSites.Matches("example.com:8443","https://docs.example.com:9443/page"),
            "A retained website port must match for both its exact host and subdomains");
        check(FocusSites.Matches("127.0.0.1:8000","http://127.0.0.1:8000/page")
            &&!FocusSites.Matches("127.0.0.1","https://evil.127.0.0.1/")
            &&FocusSites.Matches("[2001:db8::1]:8443","https://[2001:db8::1]:8443/page")
            &&!FocusSites.Matches("[2001:db8::1]:8443","https://[2001:db8::2]:8443/page"),
            "IP website targets require the same exact address and retained port");

        check(new FocusModeSettings().TargetOnSiteLinks
            &&JsonSerializer.Deserialize<FocusModeSettings>("{}")!.TargetOnSiteLinks
            &&JsonSerializer.Deserialize<AppState>("{}")!.FocusMode.TargetOnSiteLinks,
            "New and legacy profiles default Target on site links to enabled");
        check(!new FocusModeSettings().BrowserCompanionEnabled
            &&!JsonSerializer.Deserialize<FocusModeSettings>("{}")!.BrowserCompanionEnabled
            &&!JsonSerializer.Deserialize<AppState>("{}")!.FocusMode.BrowserCompanionEnabled,
            "New and legacy profiles use native website focus without enabling a browser companion");
        var site=new FocusTarget(Guid.NewGuid(),FocusTargetKind.Site,"Example site","chrome",100,200,300,"tab-one"){
            SiteHost="https://www.Example.com/first?q=value"};
        var elsewhere=site with{Id=Guid.NewGuid(),Name="Other page",App="msedge",WindowHandle=101,
            ProcessId=201,ProcessStartedAt=301,TabRuntimeId="other-tab",SiteHost="example.com"};
        check(site.Key=="site:example.com"&&site.Key==elsewhere.Key,
            "Site selection identity is independent of browser, process, window, tab and page title");
        check((site with{SiteHost="other.example.com"}).Key!=site.Key
            &&(site with{SiteHost="example.com:8443"}).Key!=site.Key,
            "Saved site keys keep distinct chosen hosts and retained ports separate");
        var focused=FocusTarget.Focused(FocusTargetKind.Site);
        check(focused.Name=="Use focused site"&&focused.UseFocused&&focused.Kind==FocusTargetKind.Site&&focused.Key=="focused:3",
            "Use focused site is a distinct dynamic choice with no saved native identity");
        check(FocusTarget.ValidScope(FocusTargetKind.Site,FocusCaptureScope.Focused)
            &&!FocusTarget.ValidScope(FocusTargetKind.Site,FocusCaptureScope.FocusedIncludingBackground)
            &&!FocusTarget.ValidScope(FocusTargetKind.Site,FocusCaptureScope.OpenIncludingBackground),
            "Dynamic sites support capture on start and resume only");
        foreach(var scope in new[]{FocusCaptureScope.FocusedIncludingBackground,FocusCaptureScope.OpenIncludingBackground}){
            threw=false;try{FocusTarget.Focused(FocusTargetKind.Site,scope);}catch(ArgumentException){threw=true;}
            check(threw,"Unsupported background site capture is rejected: "+scope);
        }
        var store=new MemoryStore();var engine=new TimerEngine(store);var before=engine.Snapshot;
        engine.SetFocusMode(new(){TargetOnSiteLinks=false});
        check(!engine.Snapshot.FocusMode.TargetOnSiteLinks&&!new TimerEngine(store).Snapshot.FocusMode.TargetOnSiteLinks,
            "Turning off Target on site links persists even when all other Focus settings are unchanged");
        engine.SetFocusMode(engine.SettingsSnapshot.FocusMode with{TargetOnSiteLinks=true});
        check(new TimerEngine(store).Snapshot.FocusMode.TargetOnSiteLinks,
            "Turning Target on site links back on is saved without requiring another setting change");
        engine.SetFocusMode(engine.SettingsSnapshot.FocusMode with{BrowserCompanionEnabled=true});
        check(new TimerEngine(store).Snapshot.FocusMode.BrowserCompanionEnabled,
            "Opting into the browser companion persists even when all other Focus settings are unchanged");
        engine.SetFocusMode(engine.SettingsSnapshot.FocusMode with{BrowserCompanionEnabled=false});
        check(!new TimerEngine(store).Snapshot.FocusMode.BrowserCompanionEnabled,
            "Returning to native website focus persists without requiring another setting change");
        engine.SetFocusMode(new(){Enabled=true,Targets=[site,elsewhere],TargetOnSiteLinks=false});
        var saved=new TimerEngine(store).Snapshot;
        check(saved.FocusMode.SelectedTargets.Length==1&&saved.FocusMode.Target!.SiteHost=="example.com"
            &&saved.FocusMode.Target.Key==site.Key&&!saved.FocusMode.TargetOnSiteLinks,
            "Saving site targets canonicalizes and combines duplicate website choices across native browser identities");
        check(saved.Timer==before.Timer&&saved.Audio==before.Audio&&saved.Connection==before.Connection&&saved.Theme==before.Theme,
            "Saving site choices and link policy preserves timer, audio, Sheets and theme preferences");
        var unchanged=JsonSerializer.Serialize(engine.Snapshot,DataJson.Options);
        foreach(var bad in new[]{site with{SiteHost="file:///notes.html"},site with{Kind=(FocusTargetKind)999},
            focused with{CaptureScope=FocusCaptureScope.OpenIncludingBackground}}){
            threw=false;try{engine.SetFocusMode(new(){Target=bad});}catch(ArgumentException){threw=true;}
            check(threw&&JsonSerializer.Serialize(engine.Snapshot,DataJson.Options)==unchanged,
                "Invalid site or target-type settings cannot replace existing saved Focus choices");
        }
        engine.SetFocusMode(engine.SettingsSnapshot.FocusMode with{Targets=[focused]});
        check(new TimerEngine(store).Snapshot.FocusMode.Target==focused,
            "The dynamic Use focused site choice persists without inventing a static website host");
    }
}
