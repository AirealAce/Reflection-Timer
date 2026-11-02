using System.Collections.Concurrent;
using System.Xml.Linq;
using ReflectionTimer.Accessible;

// Every controller is synthetic. These checks never contact an installed reader,
// move keyboard focus, change reader preferences, or use the personal profile.
internal static class NvdaAnnouncementTests
{
    internal static async Task Run(Action<bool, string> check)
    {
        var originThread = Environment.CurrentManagedThreadId;
        var speech = new Controller();
        using (var output = new NvdaScreenReaderOutput(() => speech, () => true)) {
            check(await output.TryAnnounceAsync("Timer paused with 5 minutes remaining.", false).WaitAsync(TimeSpan.FromSeconds(3)),
                "NVDA speech succeeds only after its controller accepts the request");
            await Until(() => speech.Calls.Contains("braille:Timer paused with 5 minutes remaining."));
            check(speech.Calls.ToArray().SequenceEqual(new[] { "test", "important:Timer paused with 5 minutes remaining.", "braille:Timer paused with 5 minutes remaining." }),
                "The NVDA route tests the running reader and requests important action speech with the same braille message");
            check(speech.Threads.All(thread => thread != originThread) && speech.Threads.Distinct().Count() == 1,
                "NVDA native calls share one dedicated worker instead of blocking the caller's UI thread");
            check(await output.TryAnnounceAsync("Time-only view.", true).WaitAsync(TimeSpan.FromSeconds(3)),
                "Supplementary NVDA feedback uses the same existing reader without cancelling its speech");
            await Until(() => speech.Calls.Contains("braille:Time-only view."));
            var before = speech.Calls.Count;
            check(!await output.TryAnnounceAsync(" ", false) && speech.Calls.Count == before,
                "Empty NVDA messages make no native calls");
        }
        await Until(() => speech.Disposals == 1);
        check(speech.Disposals == 1, "The NVDA controller is released once on its owning worker");

        var absent = new Controller { RunningResult = 1722 };
        var replacement = new Controller(); var factories = 0;
        using (var output = new NvdaScreenReaderOutput(() => Interlocked.Increment(ref factories) == 1 ? absent : replacement, () => true)) {
            check(!await output.TryAnnounceAsync("First check", false).WaitAsync(TimeSpan.FromSeconds(3))
                && absent.Calls.ToArray().SequenceEqual(new[] { "test" }) && absent.Disposals == 1,
                "An unavailable NVDA reports failure for the shared fallback without speaking or starting a reader");
            check(await output.TryAnnounceAsync("Reader restarted", false).WaitAsync(TimeSpan.FromSeconds(3)) && factories == 2,
                "The NVDA route reconnects on a later announcement after the reader becomes available");
        }

        var failedSpeech = new Controller { SpeakResult = 5 };
        using (var output = new NvdaScreenReaderOutput(() => failedSpeech, () => true)) {
            check(!await output.TryAnnounceAsync("Rejected speech", false).WaitAsync(TimeSpan.FromSeconds(3)),
                "A nonzero NVDA speech result permits fallback rather than claiming acceptance");
            check(!failedSpeech.Calls.Any(call => call.StartsWith("braille:")) && failedSpeech.Disposals == 1,
                "Rejected NVDA speech releases the connection without a misleading braille success");
        }

        var failedBraille = new Controller { ThrowBraille = true };
        using (var output = new NvdaScreenReaderOutput(() => failedBraille, () => true)) {
            check(await output.TryAnnounceAsync("Speech remains accepted", false).WaitAsync(TimeSpan.FromSeconds(3)),
                "Unavailable NVDA braille cannot negate already accepted speech or trigger duplicates");
            await Until(() => failedBraille.Calls.Any(call => call.StartsWith("braille:")));
            check(await output.TryAnnounceAsync("Next announcement", false).WaitAsync(TimeSpan.FromSeconds(3)),
                "A braille failure does not prevent the next NVDA speech request");
        }

        using (var missing = new NvdaScreenReaderOutput(() => null, () => true))
            check(!await missing.TryAnnounceAsync("No controller", false).WaitAsync(TimeSpan.FromSeconds(3)),
                "A missing controller library permits the existing reader fallback");
        using (var invalid = new NvdaScreenReaderOutput(() => throw new BadImageFormatException(), () => true))
            check(!await invalid.TryAnnounceAsync("Wrong architecture", false).WaitAsync(TimeSpan.FromSeconds(3)),
                "An invalid controller library cannot break a timer command");

        var guarded = new Controller(); var creates = 0;
        using (var output = new NvdaScreenReaderOutput(() => { Interlocked.Increment(ref creates); return guarded; }, () => false))
            check(!await output.TryAnnounceAsync("Private session", false) && creates == 0 && guarded.Calls.IsEmpty,
                "A locked or secure desktop guard stops NVDA output before loading or contacting the controller");
        using (var output = new NvdaScreenReaderOutput(() => guarded, () => throw new InvalidOperationException()))
            check(!await output.TryAnnounceAsync("Unknown desktop", false) && guarded.Calls.IsEmpty,
                "An unreadable desktop guard fails quietly before exposing any announcement");

        await PendingAndShutdown(check);
        await DesktopChange(check);
        ImportantSpeech(check);
        var legacy = new PlainController();
        using (var output = new NvdaScreenReaderOutput(() => legacy, () => true))
            check(await output.TryAnnounceAsync("Legacy action", false).WaitAsync(TimeSpan.FromSeconds(3)) && legacy.Speeches == 1,
                "An older controller implementation inherits important action speech through its v1 plain-text method");
        check(Path.IsPathFullyQualified(NvdaScreenReaderOutput.LibraryPath)
            && Path.GetRelativePath(AppContext.BaseDirectory, NvdaScreenReaderOutput.LibraryPath) == Path.Combine("nvda", "nvdaControllerClient.dll"),
            "NVDA controller discovery uses only the fixed absolute app-local library path");
    }

    private static async Task PendingAndShutdown(Action<bool, string> check)
    {
        using var release = new ManualResetEventSlim();
        var speech = new Controller { BeforeSpeak = text => { if (text == "in flight") release.Wait(); } };
        using (var output = new NvdaScreenReaderOutput(() => speech, () => true)) {
            try {
                var active = output.TryAnnounceAsync("in flight", false);
                await Until(() => speech.Calls.Contains("important:in flight"));
                check(!active.IsCompleted, "A pending RPC is not reported as delivered while its native call is still outstanding");
                var oldPrimary = output.TryAnnounceAsync("obsolete action", false);
                var oldNotice = output.TryAnnounceNoticeAsync("obsolete confirmation");
                var oldSupplement = output.TryAnnounceAsync("obsolete view", true);
                var latestSupplement = output.TryAnnounceAsync("latest view", true);
                var latestNotice = output.TryAnnounceNoticeAsync("latest confirmation");
                var latestPrimary = output.TryAnnounceAsync("latest action", false);
                check(oldPrimary.IsCanceled && oldNotice.IsCanceled && oldSupplement.IsCanceled
                    && !latestPrimary.IsCompleted && !latestNotice.IsCompleted && !latestSupplement.IsCompleted,
                    "A blocked NVDA worker retains the latest action, confirmation and view in three bounded slots; superseded requests cancel");
                release.Set();
                check(await active.WaitAsync(TimeSpan.FromSeconds(3)) && await latestPrimary.WaitAsync(TimeSpan.FromSeconds(3))
                    && await latestNotice.WaitAsync(TimeSpan.FromSeconds(3)) && await latestSupplement.WaitAsync(TimeSpan.FromSeconds(3)),
                    "The NVDA worker resumes queued feedback when the reader responds");
                check(speech.Calls.Where(IsSpeech).SequenceEqual(new[] { "important:in flight", "important:latest action", "speak:latest confirmation", "speak:latest view" }),
                    "Important NVDA actions precede ordinary confirmations and views; obsolete queued messages never reach the reader");
            } finally { release.Set(); }
        }

        using var blocked = new ManualResetEventSlim();
        var stuckSpeech = new Controller { BeforeSpeak = _ => blocked.Wait() };
        var stuck = new NvdaScreenReaderOutput(() => stuckSpeech, () => true);
        try {
            var active = stuck.TryAnnounceAsync("blocked native call", false);
            await Until(() => stuckSpeech.Calls.Contains("important:blocked native call"));
            var queued = stuck.TryAnnounceAsync("queued after block", false);
            await Task.Run(stuck.Dispose).WaitAsync(TimeSpan.FromSeconds(1));
            check(active.IsCanceled && queued.IsCanceled && stuckSpeech.Disposals == 0,
                "Disposal cancels pending feedback and returns without waiting for or unloading a library used by a hung RPC");
            check(stuck.TryAnnounceAsync("after shutdown", false).IsCanceled,
                "A disposed NVDA backend cannot schedule later speech or fallback work");
        } finally { stuck.Dispose(); blocked.Set(); }
        await Until(() => stuckSpeech.Disposals == 1);
        check(stuckSpeech.Calls.Count(IsSpeech) == 1 && !stuckSpeech.Calls.Any(call => call.StartsWith("braille:")),
            "A returned shutdown RPC releases its library without emitting cancelled queued speech or braille");
    }

    private static async Task DesktopChange(Action<bool, string> check)
    {
        using var release = new ManualResetEventSlim(); var desktop = 1;
        var speech = new Controller { BeforeSpeak = text => { if (text == "already started") release.Wait(); } };
        using var output = new NvdaScreenReaderOutput(() => speech, () => Volatile.Read(ref desktop) != 0);
        try {
            var active = output.TryAnnounceAsync("already started", false);
            await Until(() => speech.Calls.Contains("important:already started"));
            var pending = output.TryAnnounceAsync("became private", false);
            Volatile.Write(ref desktop, 0); release.Set();
            await active.WaitAsync(TimeSpan.FromSeconds(3));
            check(!await pending.WaitAsync(TimeSpan.FromSeconds(3)) && !speech.Calls.Contains("important:became private"),
                "The NVDA worker rechecks the input desktop before delivering previously queued private-session feedback");
        } finally { release.Set(); }
    }

    private static async Task Until(Func<bool> ready)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!ready()) await Task.Delay(10, timeout.Token);
    }

    private static bool IsSpeech(string call) => call.StartsWith("speak:") || call.StartsWith("important:");

    private static void ImportantSpeech(Action<bool, string> check)
    {
        const string text = "Timer <ready> & \"quoted\" 'text' — 漢字 😀\r\nNext line.";
        var calls = new List<string>(); string? received = null;
        var accepted = NvdaScreenReaderOutput.SpeakImportant(text, value => { calls.Add("plain"); return 0; },
            value => { calls.Add("ssml"); received = value; return 0; });
        check(accepted == 0 && calls.SequenceEqual(new[] { "ssml" }) && received is not null
            && XDocument.Parse(received).Root is { Name.LocalName: "speak" } root && root.Value == text,
            "Important NVDA markup safely preserves literal Unicode, quotes, XML punctuation and line breaks without a second plain-text request");
        foreach (var unsupported in new[] { 1717, 1745 }) {
            calls.Clear();
            check(NvdaScreenReaderOutput.SpeakImportant(text, value => { calls.Add("plain:" + value); return 0; },
                _ => { calls.Add("unsupported"); return unsupported; }) == 0
                && calls.SequenceEqual(new[] { "unsupported", "plain:" + text }),
                "Unsupported NVDA v2 status " + unsupported + " falls back to the original v1 text exactly once");
        }
        calls.Clear();
        check(NvdaScreenReaderOutput.SpeakImportant(text, value => { calls.Add(value); return 0; }, null) == 0
            && calls.SequenceEqual(new[] { text }), "A missing NVDA SSML export uses v1 text exactly once");
        calls.Clear();
        check(NvdaScreenReaderOutput.SpeakImportant(text, _ => { calls.Add("plain"); return 0; }, _ => 5) == 5 && calls.Count == 0,
            "A genuine NVDA v2 failure is reported without retrying through the same reader's plain speech");
        foreach (var invalid in new[] { "Invalid\u0001control", "Unpaired\ud800surrogate" }) {
            calls.Clear();
            check(NvdaScreenReaderOutput.SpeakImportant(invalid, _ => { calls.Add("plain"); return 0; },
                _ => { calls.Add("ssml"); return 0; }) == 87 && calls.Count == 0,
                "Invalid XML characters are rejected before any NVDA markup or plain-text request");
        }
    }

    private sealed class Controller : INvdaSpeech
    {
        internal readonly ConcurrentQueue<string> Calls = new();
        internal readonly ConcurrentQueue<int> Threads = new();
        internal int RunningResult, SpeakResult, Disposals;
        internal bool ThrowBraille;
        internal Action<string>? BeforeSpeak;
        private void Record(string call) { Calls.Enqueue(call); Threads.Enqueue(Environment.CurrentManagedThreadId); }
        public int TestIfRunning() { Record("test"); return RunningResult; }
        public int SpeakText(string text) { Record("speak:" + text); BeforeSpeak?.Invoke(text); return SpeakResult; }
        public int SpeakImportantText(string text) { Record("important:" + text); BeforeSpeak?.Invoke(text); return SpeakResult; }
        public int BrailleMessage(string text) { Record("braille:" + text); if (ThrowBraille) throw new IOException(); return 0; }
        public void Dispose() { Interlocked.Increment(ref Disposals); }
    }

    private sealed class PlainController : INvdaSpeech
    {
        internal int Speeches;
        public int TestIfRunning() => 0;
        public int SpeakText(string text) { Interlocked.Increment(ref Speeches); return 0; }
        public int BrailleMessage(string text) => 0;
        public void Dispose() { }
    }
}
