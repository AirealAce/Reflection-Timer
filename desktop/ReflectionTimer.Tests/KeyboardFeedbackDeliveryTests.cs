using System.Windows.Forms;
using ReflectionTimer.Accessible;
using ReflectionTimer.Core;

// Synthetic clocks, mock readers and memory-only state. These tests do not
// speak through an installed reader, send physical keys or open user windows.
static class KeyboardFeedbackDeliveryTests
{
    internal static void Run(Action<bool, string> check)
    {
        // The fake owner is never shown or pumped. Do not let its creation
        // install a WinForms context on the console suite's async test thread.
        var context = SynchronizationContext.Current;
        var autoInstall = WindowsFormsSynchronizationContext.AutoInstall;
        try {
            WindowsFormsSynchronizationContext.AutoInstall = false;
            Routing(check);
        } finally {
            WindowsFormsSynchronizationContext.AutoInstall = autoInstall;
            SynchronizationContext.SetSynchronizationContext(context);
        }
        JawsLifecycle(check);
        ReleaseSettling(check);
        PendingSelection(check);
        LongHeldShortcut(check);
        DispatchAndDisposal(check);
        DeferredSessionVoice(check);
    }

    private static void JawsLifecycle(Action<bool, string> check)
    {
        nint window = 0; var probeThrows = false; var factoryThrows = false; var factoryNull = false;
        var factoryCalls = 0; var bridges = new List<JawsSpeech>();
        using var jaws = new JawsScreenReaderOutput(() => {
            if (probeThrows) throw new IOException("Synthetic reader probe failure");
            return window;
        }, () => {
            factoryCalls++;
            if (factoryThrows) throw new IOException("Synthetic reader bridge activation failure");
            if (factoryNull) return null;
            var bridge = new JawsSpeech(); bridges.Add(bridge); return bridge;
        });
        check(!jaws.TryAnnounce("Timer ready.", false) && factoryCalls == 0,
            "An absent JAWS window never activates its speech API or starts a reader");
        window = 100;
        check(jaws.TryAnnounce("Timer paused.", false) && factoryCalls == 1
            && bridges.Single().Messages.SequenceEqual(new[] { ("Timer paused.", true) }),
            "A running JAWS reader receives primary feedback with its interrupt flag enabled");
        check(jaws.TryAnnounce("Compact view.", true) && factoryCalls == 1
            && bridges.Single().Messages.Last() == ("Compact view.", false),
            "Supplementary JAWS feedback queues behind current speech and reuses the live connection");
        jaws.TryAnnounce("Timer paused.", false); jaws.TryAnnounce("Timer paused.", false);
        check(bridges.Single().Messages.Count(m => m == ("Timer paused.", true)) == 3,
            "The JAWS bridge accepts each repeated unchanged timer status request");
        window = 200; var previous = bridges.Last();
        check(jaws.TryAnnounce("Timer resumed.", false) && previous.Disposals == 1 && factoryCalls == 2,
            "A restarted JAWS window releases the old bridge and reconnects for the next message");
        window = 0; previous = bridges.Last();
        check(!jaws.TryAnnounce("Timer paused.", false) && previous.Disposals == 1 && factoryCalls == 2,
            "Stopping JAWS disposes the stale bridge without starting a replacement reader");
        window = 300;
        check(jaws.TryAnnounce("Stopwatch ready.", false) && factoryCalls == 3,
            "JAWS can reconnect after being stopped and restarted while the timer app remains open");
        previous = bridges.Last(); previous.Accepts = false;
        check(!jaws.TryAnnounce("Declined feedback.", false) && previous.Disposals == 1,
            "A declining speech API returns failure and releases its cached connection for native fallback");
        check(jaws.TryAnnounce("Recovered feedback.", false) && factoryCalls == 4,
            "The next request retries a failed JAWS connection instead of caching its failure permanently");
        previous = bridges.Last(); previous.SayThrows = true; previous.DisposeThrows = true;
        check(!jaws.TryAnnounce("Disconnected feedback.", false) && previous.Disposals == 1,
            "Speech and cleanup exceptions are contained so disconnected JAWS permits native fallback");
        check(jaws.TryAnnounce("Reconnected feedback.", false) && factoryCalls == 5,
            "A thrown JAWS speech request cannot prevent a later successful reconnection");
        previous = bridges.Last(); probeThrows = true;
        check(!jaws.TryAnnounce("Unavailable reader probe.", false) && previous.Disposals == 1,
            "A failed running-reader probe releases its bridge and permits native fallback");
        probeThrows = false; factoryThrows = true;
        check(!jaws.TryAnnounce("Unavailable reader bridge.", false),
            "COM activation failure stays local to JAWS and does not escape the announcement route");
        factoryThrows = false; factoryNull = true;
        check(!jaws.TryAnnounce("Missing reader API.", false),
            "A running reader without an available speech API safely permits native feedback");
        factoryNull = false;
        check(jaws.TryAnnounce("Reader available again.", false),
            "Missing or failed API activation is retried when the reader becomes usable again");
        var count = factoryCalls; var messages = bridges.Last().Messages.Count;
        check(!jaws.TryAnnounce(" \t", false) && factoryCalls == count && bridges.Last().Messages.Count == messages,
            "Blank JAWS requests neither activate the bridge nor invoke speech");
        previous = bridges.Last(); jaws.Dispose(); jaws.Dispose();
        check(previous.Disposals == 1 && !jaws.TryAnnounce("After disposal.", false) && factoryCalls == count,
            "JAWS output disposal is idempotent and no later request can speak or reactivate its API");
    }

    private static void Routing(Action<bool, string> check)
    {
        using var owner = new Control();
        var calls = new List<(string Route, Control? Owner, string Message, bool Supplementary)>();
        var jawsAccepts = true; var nativeAccepts = true; var jawsThrows = false; var nativeThrows = false;
        var router = new ScreenReaderAnnouncementRouter((message, extra) => {
            calls.Add(("JAWS", null, message, extra));
            if (jawsThrows) throw new IOException("Synthetic JAWS failure");
            return jawsAccepts;
        }, (provider, message, extra) => {
            calls.Add(("UIA", provider, message, extra));
            if (nativeThrows) throw new IOException("Synthetic native reader failure");
            return nativeAccepts;
        });
        check(router.TryAnnounce(owner, "Timer paused, 2 minutes remaining.", false)
            && calls.SequenceEqual(new[] { ("JAWS", (Control?)null, "Timer paused, 2 minutes remaining.", false) }),
            "Accepted JAWS status bypasses UIA so one request does not speak twice");
        calls.Clear();
        check(router.TryAnnounce(owner, "Time-only view.", true)
            && calls.Single() == ("JAWS", null, "Time-only view.", true),
            "JAWS routing retains supplementary queue policy and exact view text");
        jawsAccepts = false; calls.Clear();
        check(router.TryAnnounce(owner, "Stopwatch ready, 0 seconds elapsed.", false)
            && calls.Select(c => c.Route).SequenceEqual(new[] { "JAWS", "UIA" })
            && calls.Last().Owner == owner && calls.Last().Message == calls.First().Message,
            "Absent or declining JAWS falls back to the supplied native owner");
        calls.Clear(); jawsThrows = true;
        check(router.TryAnnounce(owner, "App view hidden.", true)
            && calls.Select(c => c.Route).SequenceEqual(new[] { "JAWS", "UIA" }) && calls.Last().Supplementary,
            "A failed JAWS bridge cannot suppress native supplementary feedback");
        jawsThrows = false; nativeAccepts = false; calls.Clear();
        check(!router.TryAnnounce(owner, "Synthetic unavailable feedback.", false) && calls.Count == 2,
            "Both channels declining returns false for existing live-region fallback");
        nativeThrows = true; calls.Clear();
        check(!router.TryAnnounce(owner, "Synthetic unavailable feedback.", false) && calls.Count == 2,
            "Native reader failures are contained when JAWS is unavailable");
        calls.Clear(); nativeThrows = false; nativeAccepts = true; jawsAccepts = true;
        foreach (var message in new[] { "", " ", "\r\n\t" })
            check(!router.TryAnnounce(owner, message, false), "Blank reader feedback is rejected before any channel: " + message.Length);
        check(!router.TryAnnounce(null, "Synthetic message.", false) && calls.Count == 0,
            "Unavailable owners cannot invoke either screen-reader channel");
        router.TryAnnounce(owner, "Timer paused, 2 minutes remaining.", false);
        router.TryAnnounce(owner, "Timer paused, 2 minutes remaining.", false);
        check(calls.Count == 2 && calls.All(c => c.Route == "JAWS"),
            "Separate requests for unchanged status are both delivered without text deduplication");
        owner.Dispose(); calls.Clear();
        check(!router.TryAnnounce(owner, "After owner disposal.", false) && calls.Count == 0,
            "Disposed owners cannot invoke JAWS or native feedback");
    }

    private static void ReleaseSettling(Action<bool, string> check)
    {
        using var fixture = new QueueFixture();
        fixture.Pressed = true;
        fixture.Send("Timer paused.");
        fixture.Pulse(0);
        fixture.Pulse(300);
        check(fixture.Messages.Count == 0, "A held playback chord defers speech without invoking the delivery action");
        fixture.Pressed = false; fixture.Pulse(320);
        fixture.Pulse(399);
        check(fixture.Messages.Count == 0, "Speech waits for the complete 80 ms modifier-release settling interval");
        fixture.Pulse(400);
        check(fixture.Messages.SequenceEqual(new[] { "Timer paused." }),
            "Feedback is delivered once after shortcut keys have remained released for 80 ms");
        fixture.Pulse(600);
        check(fixture.Messages.Count == 1, "Later polls cannot repeat an already-delivered action");
        fixture.Send("Timer paused."); fixture.Pulse(600); fixture.Pulse(680);
        check(fixture.Messages.SequenceEqual(new[] { "Timer paused.", "Timer paused." }),
            "A repeated status chord remains announceable even when its text is unchanged");

        using var noKeys = new QueueFixture();
        noKeys.Send("Timer ready."); noKeys.Pulse(0); noKeys.Pulse(79);
        check(noKeys.Messages.Count == 0, "A chord received after release still gives keyboard echo 80 ms to settle");
        noKeys.Pulse(80);
        check(noKeys.Messages.SequenceEqual(new[] { "Timer ready." }), "Already-released shortcuts deliver at the settling boundary");

        using var interrupted = new QueueFixture();
        interrupted.Send("Timer resumed."); interrupted.Pulse(0); interrupted.Pulse(60);
        interrupted.Pressed = true; interrupted.Pulse(79);
        interrupted.Pressed = false; interrupted.Pulse(90); interrupted.Pulse(169);
        check(interrupted.Messages.Count == 0, "A re-pressed modifier restarts the entire release settling interval");
        interrupted.Pulse(170);
        check(interrupted.Messages.SequenceEqual(new[] { "Timer resumed." }), "Feedback follows the final continuous release interval");
    }

    private static void PendingSelection(Action<bool, string> check)
    {
        using var fixture = new QueueFixture { Pressed = true };
        fixture.Send("Old start status."); fixture.Pulse(0);
        fixture.Send("Old Compact view.", true); fixture.Pulse(20);
        fixture.Send("Latest pause status."); fixture.Pulse(40);
        fixture.Send("Latest Time-only view.", true); fixture.Pulse(60);
        fixture.Pressed = false; fixture.Pulse(80); fixture.Pulse(160);
        check(fixture.Messages.SequenceEqual(new[] { "Latest pause status.", "Latest Time-only view." }),
            "Rapid shortcut actions retain the latest primary and latest supplementary feedback in that order");

        using var supplementaryFirst = new QueueFixture { Pressed = true };
        supplementaryFirst.Send("Compact view.", true); supplementaryFirst.Pulse(0);
        supplementaryFirst.Send("Timer started."); supplementaryFirst.Pulse(20);
        supplementaryFirst.Pressed = false; supplementaryFirst.Pulse(30); supplementaryFirst.Pulse(110);
        check(supplementaryFirst.Messages.SequenceEqual(new[] { "Timer started.", "Compact view." }),
            "A supplementary view cannot interrupt or displace the primary timer action even when queued first");

        using var onlyView = new QueueFixture();
        onlyView.Send("App view hidden.", true); onlyView.Pulse(0); onlyView.Pulse(80);
        check(onlyView.Messages.SequenceEqual(new[] { "App view hidden." }),
            "Standalone supplementary feedback does not require an accompanying primary action");
    }

    private static void LongHeldShortcut(Action<bool, string> check)
    {
        using var fixture = new QueueFixture { Pressed = true };
        fixture.Send("Timer paused."); fixture.Pulse(0); fixture.Pulse(1499);
        check(fixture.Messages.Count == 0, "A held chord waits for release rather than speaking into JAWS's interrupt key");
        fixture.Pulse(1500);
        fixture.Send("Latest timer resumed."); fixture.Send("Latest Time-only view.", true); fixture.Pulse(5000);
        check(fixture.Messages.Count == 0 && fixture.Scheduled.Count == 1,
            "Long-held shortcuts keep feedback silent and polling bounded while retaining the latest requested status");
        fixture.Pressed = false; fixture.Pulse(5020); fixture.Pulse(5099);
        check(fixture.Messages.Count == 0, "Long-held shortcuts still require a full release settling interval");
        fixture.Pulse(5100); fixture.Pulse(5200);
        check(fixture.Messages.SequenceEqual(new[] { "Latest timer resumed.", "Latest Time-only view." }),
            "Release after a long hold announces the latest primary and supplementary feedback exactly once");
    }

    private static void DispatchAndDisposal(Action<bool, string> check)
    {
        using var fixture = new QueueFixture();
        fixture.Send("Pending feedback.");
        check(fixture.Messages.Count == 0 && fixture.Scheduled.Count > 0,
            "Publishing schedules delivery instead of synchronously speaking inside the state transition");
        fixture.Dispose(); fixture.Pulse(2000);
        fixture.Send("After disposal."); fixture.Pulse(2100);
        check(fixture.Messages.Count == 0, "Queued scheduler callbacks and later publications remain silent after disposal");
        fixture.Dispose();
        check(true, "Keyboard release dispatcher disposal is idempotent");
    }

    private static void DeferredSessionVoice(Action<bool, string> check)
    {
        var now = DateTimeOffset.Parse("2026-10-13T10:00:00-04:00");
        var store = new MemoryStore { State = new() { VoiceAnnouncements = true, FloatingTimeOnly = false,
            ShowAppView = false, Timer = new() { Volume = 55 } } };
        var engine = new TimerEngine(store, () => now);
        var reader = new List<(string Message, bool Supplementary)>();
        var actions = new Queue<(Action Deliver, bool Supplementary)>(); var speech = new Speech();
        using var feedback = new SessionVoice(engine, speech, (text, extra) => reader.Add((text, extra)),
            (deliver, extra) => actions.Enqueue((deliver, extra)));
        engine.Start(90, false, 55);
        check(engine.CurrentTimer.IsRunning && store.State.Timer.IsRunning && actions.Count == 1
            && reader.Count == 0 && speech.Messages.Count == 0,
            "Starting persists and runs the timer immediately while both reader and optional voice wait for keyboard release");
        actions.Dequeue().Deliver();
        check(reader.SequenceEqual(new[] { ("Timer started with 1 minute 30 seconds remaining.", false) })
            && speech.Messages.SequenceEqual(new[] { ("Timer started with 1 minute 30 seconds remaining.", 55, false) }),
            "Delayed delivery uses the full committed action text in both feedback channels");

        now = now.AddSeconds(5); engine.Pause(); engine.SetVoiceAnnouncements(false);
        check(!engine.CurrentTimer.IsRunning && store.State.Timer.RemainingSeconds == 85 && actions.Count == 1,
            "Pause accounting and the saved state are committed before reader delivery");
        actions.Dequeue().Deliver();
        check(reader.Last().Message == "Timer paused with 1 minute 25 seconds remaining." && speech.Messages.Count == 1,
            "Turning app voice off while a shortcut is queued cancels optional speech without suppressing the reader");

        engine.SetVoiceAnnouncements(true); engine.Resume(); engine.SetAppVolume(0);
        actions.Dequeue().Deliver();
        check(reader.Last().Message == "Timer resumed with 1 minute 25 seconds remaining." && speech.Messages.Count == 1,
            "Muting while delivery is queued suppresses optional voice and retains screen-reader feedback");
        engine.SetAppVolume(55); feedback.Feedback("Current volume."); engine.SetAppVolume(72);
        actions.Dequeue().Deliver();
        check(speech.Messages.Last() == ("Current volume.", 72, false),
            "Deferred optional voice uses the current saved volume at delivery");

        engine.SetFloatingTimeOnly(true);
        check(actions.Single().Supplementary, "Session view transitions keep their supplementary designation through deferred dispatch");
        actions.Dequeue().Deliver();
        check(reader.Last() == ("Time-only view.", true) && speech.Messages.Last() == ("Time-only view.", 72, true),
            "Deferred view delivery retains supplementary speech without duplicating action status");

        store.Fail = true; var pending = actions.Count;
        try { engine.Pause(); throw new Exception("Expected failed persistence"); } catch (IOException) { }
        check(engine.CurrentTimer.IsRunning && actions.Count == pending,
            "A failed timer save cannot queue a false delayed pause confirmation");
        store.Fail = false;

        speech.Throws = true; feedback.Feedback("Optional speech failed.");
        actions.Dequeue().Deliver();
        check(reader.Last().Message == "Optional speech failed." && feedback.PollFailure() is not null && engine.CurrentTimer.IsRunning,
            "Delayed optional speech failure does not suppress reader feedback or undo the committed timer");
        speech.Throws = false;

        var readerCount = reader.Count; var speechCount = speech.Messages.Count;
        feedback.Feedback("Native control owns this announcement.", nativeControlAnnounces: true);
        actions.Dequeue().Deliver();
        check(reader.Count == readerCount && speech.Messages.Count == speechCount + 1,
            "Deferred feedback still suppresses a duplicate reader announcement for a native announcing control");

        feedback.Feedback("Pending during shutdown."); feedback.Dispose();
        readerCount = reader.Count; speechCount = speech.Messages.Count;
        actions.Dequeue().Deliver(); engine.Pause(); feedback.Feedback("After shutdown.");
        check(reader.Count == readerCount && speech.Messages.Count == speechCount && actions.Count == 0,
            "Disposal cancels deferred reader and optional voice closures and detaches future timer transitions");
    }

    private sealed class QueueFixture : IDisposable
    {
        internal long Now; internal bool Pressed;
        internal readonly Queue<Action> Scheduled = new();
        internal readonly List<string> Messages = [];
        private readonly KeyboardReleasedFeedback feedback;
        internal QueueFixture() => feedback = new KeyboardReleasedFeedback(Scheduled.Enqueue, () => Pressed, () => Now);
        internal void Send(string message, bool supplementary = false) => feedback.Publish(() => Messages.Add(message), supplementary);
        internal void Pulse(long now)
        {
            Now = now;
            // Only run the callbacks already pending. Rescheduled polling is a
            // later timer turn; draining recursively would spin a fixed clock.
            var count = Scheduled.Count;
            for (var index = 0; index < count; index++) Scheduled.Dequeue()();
        }
        public void Dispose() => feedback.Dispose();
    }

    private sealed class Speech : IVoiceOutput
    {
        internal bool Throws;
        internal readonly List<(string Message, int Volume, bool Supplementary)> Messages = [];
        public void Speak(string text, int volume)
        {
            if (Throws) throw new IOException("Synthetic optional voice failure");
            Messages.Add((text, volume, false));
        }
        public void SpeakSupplement(string text, int volume)
        {
            if (Throws) throw new IOException("Synthetic optional voice failure");
            Messages.Add((text, volume, true));
        }
        public void SetVolume(int volume) { }
        public void Stop() { }
        public bool TakeFailure() => false;
        public void Dispose() { }
    }

    private sealed class JawsSpeech : IJawsSpeech
    {
        internal bool Accepts = true, SayThrows, DisposeThrows;
        internal int Disposals;
        internal readonly List<(string Message, bool Interrupt)> Messages = [];
        public bool Say(string text, bool interrupt)
        {
            Messages.Add((text, interrupt));
            if (SayThrows) throw new IOException("Synthetic JAWS speech failure");
            return Accepts;
        }
        public void Dispose()
        {
            Disposals++;
            if (DisposeThrows) throw new IOException("Synthetic JAWS bridge cleanup failure");
        }
    }
}
