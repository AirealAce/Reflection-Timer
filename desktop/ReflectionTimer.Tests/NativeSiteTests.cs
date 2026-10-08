using ReflectionTimer.Accessible;
using ReflectionTimer.Core;

internal static class NativeSiteTests
{
    internal static void Run(Action<bool,string> check)
    {
        BrowserDocumentAddress Document(string address,double area=10_000,bool offscreen=false)=>new(address,offscreen,area);
        string? Choose(params BrowserDocumentAddress[] documents)=>NativeBrowserSites.Choose(documents);

        check(Choose(Document("https://smaller.example/page",2_000),Document("https://largest.example/private?token=not-saved#section",20_000))=="largest.example",
            "Native Site identity comes from the largest visible committed document, not a smaller document");
        check(Choose(Document("https://hidden.example/",1_000_000,true),Document("https://visible.example/",10_000))=="visible.example",
            "An offscreen document cannot override the visible website");
        check(Choose(Document("https://example.com/a",10_000),Document("https://WWW.Example.com/b?private=value",9_500))=="example.com",
            "Nearly equal document bounds agree when their canonical website hosts agree");
        check(Choose(Document("https://first.example/",10_000),Document("https://second.example/",9_000)) is null,
            "Nearly equal visible documents on different websites produce unknown instead of a guessed target");
        check(Choose(Document("https://first.example/",10_000),Document("about:blank",9_000)) is null,
            "A similarly sized unreadable document makes the current website unknown");
        check(Choose(Document("https://root.example/",10_000),Document("https://small-frame.example/",8_999))=="root.example",
            "A substantially smaller document does not replace the root website");
        check(Choose(Document("about:blank",10_000),Document("https://embedded.example/",1_000)) is null,
            "An unreadable root does not fall back to an embedded website");
        check(Choose() is null&&Choose(Document("https://example.com/",10_000,true)) is null,
            "Missing and wholly offscreen document metadata remain unknown");
        check(Choose(Document("https://zero.example/",0),Document("https://negative.example/",-1),
            Document("https://nan.example/",double.NaN),Document("https://infinite.example/",double.PositiveInfinity)) is null,
            "Empty, invalid and nonfinite document bounds cannot supply a website");

        var accepted=new (string Address,string Host)[]{
            ("https://WWW.Example.COM:443/private/path?token=fixture#fragment","example.com"),
            ("HTTP://example.com:80/private/path","example.com"),
            ("https://docs.example.com/document/d/fixture/edit?tab=private","docs.example.com"),
            ("https://www.bücher.example/private","xn--bcher-kva.example"),
            ("https://example.com:8443/private","example.com:8443")
        };
        foreach(var (address,expected) in accepted)
            check(NativeBrowserSites.TryCommittedHost(address,out var host)&&host==expected
                &&!host.Contains('/')&&!host.Contains('?')&&!host.Contains('#'),
                "Native document metadata retains only the normalized host: "+expected);

        foreach(var address in new[]{"","example.com","www.example.com/path"," https://example.com/", "about:blank",
            "file:///C:/fixture.html","chrome://settings","edge://settings","javascript:alert(1)",
            "https://name:password@example.com/private","https://example.com@other.example/",
            "https://example.com\\@other.example/","https://exam ple.com/"})
            check(!NativeBrowserSites.TryCommittedHost(address,out var host)&&host=="",
                "Native Site metadata rejects noncommitted, nonweb or unsafe document addresses: "+(address.Length==0?"(empty)":address));
        Anchors(check);
    }

    private static void Anchors(Action<bool,string> check)
    {
        FocusTarget Window(int number)=>new(Guid.NewGuid(),FocusTargetKind.Window,"Fixture "+number,"chrome",number,7,8);
        var first=Window(1);var second=Window(2);
        var settings=new FocusModeSettings{Enabled=true,TargetOnSiteLinks=true,Target=first};
        var timer=new TimerState{SessionId=Guid.NewGuid(),IsRunning=false};
        var anchors=new NativeSiteAnchors();
        var stopped=anchors.Configure(settings,timer);
        anchors.Record(stopped,first.Key,"stopped.example");
        check(!anchors.CanRead(stopped,first.Key)&&anchors.Hosts([first]).Length==0,
            "A stopped session cannot read or seed native website anchors");
        var disabled=anchors.Configure(settings with{Enabled=false},timer with{IsRunning=true});
        anchors.Record(disabled,first.Key,"disabled.example");
        check(!anchors.CanRead(disabled,first.Key)&&anchors.Hosts([first]).Length==0,
            "Disabled Focus mode cannot read or seed native website anchors");
        var noLinks=anchors.Configure(settings with{TargetOnSiteLinks=false},timer with{IsRunning=true});
        anchors.Record(noLinks,first.Key,"links-off.example");
        check(!anchors.CanRead(noLinks,first.Key)&&anchors.Hosts([first]).Length==0,
            "Turning off Target On-Site Links prevents native anchor sampling");
        var noSession=anchors.Configure(settings,timer with{IsRunning=true,SessionId=null});
        anchors.Record(noSession,first.Key,"no-session.example");
        check(!anchors.CanRead(noSession,first.Key)&&anchors.Hosts([first]).Length==0,
            "A running flag without a session does not seed native website anchors");

        timer=timer with{IsRunning=true};
        var started=anchors.Configure(settings,timer);
        check(started==anchors.Generation&&started!=noSession&&anchors.CanRead(started,first.Key),
            "Starting a real session permits its first native website read with the current generation token");
        anchors.Record(stopped,first.Key,"stale.example");
        check(anchors.Hosts([first]).Length==0&&anchors.CanRead(started,first.Key),
            "Late native reads from an earlier stopped generation cannot seed a started session");
        anchors.Record(started,first.Key,"https://name:secret@example.com/private");
        check(anchors.Hosts([first]).Length==0&&anchors.CanRead(started,first.Key),
            "An invalid or credential-bearing native anchor leaves the first-read opportunity available");
        anchors.Record(started,first.Key,"https://WWW.Example.com/private?token=fixture#fragment");
        check(anchors.Hosts([first]).SequenceEqual(["example.com"])&&!anchors.CanRead(started,first.Key),
            "The first committed native anchor stores only its normalized host and stops further sampling for that target");
        anchors.Record(started,first.Key,"https://unrelated-later.example/");
        check(anchors.Hosts([first]).SequenceEqual(["example.com"]),
            "Later unrelated navigation cannot broaden an existing native website anchor");
        check(anchors.Configure(settings,timer)==started&&anchors.Hosts([first]).SequenceEqual(["example.com"]),
            "Ordinary state refreshes preserve the current capture generation and its first website");

        var paused=anchors.Configure(settings,timer with{IsRunning=false});
        anchors.Record(started,first.Key,"late-before-pause.example");
        check(paused!=started&&!anchors.CanRead(started,first.Key)&&anchors.Hosts([first]).Length==0,
            "Pausing clears native anchors and rejects a pending read from before the pause");
        var resumed=anchors.Configure(settings,timer);
        anchors.Record(paused,first.Key,"late-paused.example");
        anchors.Record(resumed,first.Key,"resumed.example");
        check(resumed!=paused&&anchors.Hosts([first]).SequenceEqual(["resumed.example"]),
            "Resuming captures a fresh first website without accepting paused-generation results");
        timer=timer with{SessionId=Guid.NewGuid()};
        var nextSession=anchors.Configure(settings,timer);
        anchors.Record(resumed,first.Key,"late-previous-session.example");
        check(nextSession!=resumed&&anchors.Hosts([first]).Length==0&&anchors.CanRead(nextSession,first.Key),
            "A new session invalidates pending native reads even when its selected targets are unchanged");
        var changed=anchors.Configure(settings with{Target=second},timer);
        anchors.Record(nextSession,first.Key,"late-previous-selection.example");
        anchors.Record(changed,second.Key,"new-selection.example");
        check(changed!=nextSession&&anchors.Hosts([first]).Length==0&&anchors.Hosts([second]).SequenceEqual(["new-selection.example"]),
            "Changing the target selection invalidates prior work and exposes only the new selection's anchors");
        var off=anchors.Configure(settings with{Target=second,Enabled=false},timer);
        anchors.Record(changed,second.Key,"late-before-disabled.example");
        check(off!=changed&&anchors.Hosts([second]).Length==0&&!anchors.CanRead(off,second.Key),
            "Disabling Focus invalidates an active generation and hides its website anchors");

        var multiple=new NativeSiteAnchors();
        var multipleSettings=settings with{Target=null,Targets=[first,second],MultipleTargets=true};
        var token=multiple.Configure(multipleSettings,timer);
        multiple.Record(token,first.Key,"https://www.shared.example/one");
        multiple.Record(token,second.Key,"https://shared.example/two");
        check(multiple.Hosts([first,second]).SequenceEqual(["shared.example"])
            &&multiple.Hosts([Window(3)]).Length==0,
            "Native anchors deduplicate canonical hosts and never return anchors for unrequested targets");
        multiple.Configure(multipleSettings with{TargetOnSiteLinks=false},timer);
        multiple.Record(token,first.Key,"late-links-disabled.example");
        check(multiple.Hosts([first,second]).Length==0&&!multiple.CanRead(token,first.Key),
            "Turning off same-site expansion clears anchors and rejects pending metadata from the enabled generation");

        var bounded=new NativeSiteAnchors();
        var targets=Enumerable.Range(1,513).Select(Window).ToArray();
        var boundToken=bounded.Configure(settings with{Target=null,Targets=[..targets],MultipleTargets=true},timer);
        for(var index=0;index<targets.Length;index++)bounded.Record(boundToken,targets[index].Key,"site-"+index+".example");
        check(bounded.Hosts(targets).Length==512&&bounded.Hosts([targets[^1]]).Length==0&&!bounded.CanRead(boundToken,targets[^1].Key),
            "A native capture keeps at most 512 normalized target anchors and stops sampling once full");
    }
}
