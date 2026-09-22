using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using ReflectionTimer.Accessible;
using ReflectionTimer.Core;
using ReflectionTimer.Desktop;

static class UploadRecoveryTests
{
    internal static async Task Run(Action<bool, string> check)
    {
        using (var test = new Fixture()) {
            test.Store.FailuresRemaining = 1;
            await test.Services.Sync();
            check(test.Item.Status == DeliveryStatus.Sending && test.Receiver.Writes.Count == 1,
                "A failed local acknowledgment retains the original Sending entry and accepted request");
            check(test.Audio.Calls.IsEmpty, "An undurable upload result does not play success audio");
            await test.Services.Sync(true);
            check(test.Item.Status == DeliveryStatus.Sent && test.Store.Load().Outbox.Single().Status == DeliveryStatus.Sent,
                "The next sync saves the known upload result without restarting the app");
            check(test.Receiver.Actions.SequenceEqual(["ping", "appendReflection"]) && test.Item.Attempts == 1,
                "Local acknowledgment recovery makes no new network request or upload attempt");
            await test.WaitForAudio();
            await test.Services.Sync();
            check(test.Audio.Calls.Count == 1 && test.Notices.Count(x => x.StartsWith("Reflection sent to")) == 1,
                "Recovered success feedback plays once, only after the result is durably saved");
            check(!test.Notices.Any(x => x.StartsWith("No connected entries")),
                "Manual acknowledgment recovery does not overwrite its result with an empty-queue message");
        }

        using (var test = new Fixture()) {
            test.Store.FailuresRemaining = 3;
            await test.Services.Sync(); await test.Services.Sync(); await test.Services.Sync();
            check(test.Item.Status == DeliveryStatus.Sending && test.Item.Message == "Synthetic recovery reflection" && test.Receiver.Writes.Count == 1,
                "Persistent acknowledgment failures preserve the reflection without resending it");
            check(test.Notices.Count == 1 && test.Notices[0].Contains("saved on this PC") && !test.Notices[0].Contains("Synthetic disk failure"),
                "Automatic local-save failures give one privacy-safe explanation instead of repeated notices");
            await test.Services.Sync(true);
            check(test.Item.Status == DeliveryStatus.Sent && test.Receiver.Writes.Count == 1,
                "Manual Send pending now retries the local save once storage recovers");
        }

        using (var test = new Fixture()) {
            test.Store.FailuresRemaining = 1;
            await test.Services.Sync();
            test.Services.SaveConnection(test.Connection, false);
            test.Receiver.Offline = true;
            await test.Services.Sync(true);
            check(test.Item.Status == DeliveryStatus.Sent && !test.Engine.Snapshot.ExtensionDisabledConfirmed && test.Receiver.Actions.Count == 2,
                "A known upload result can be saved while delivery is paused and the network is offline");
        }

        using (var test = new Fixture(false)) {
            test.Store.FailuresRemaining = 1;
            await test.Services.Sync(); await test.Services.Sync();
            check(test.Item.Status == DeliveryStatus.Sent && test.Receiver.Writes.Count == 1 && !test.Item.RetryProtected,
                "A known success from a legacy receiver is saved locally without an unsafe replay");
        }

        using (var test = new Fixture()) {
            var second = test.Engine.TestPrompt(); test.Engine.QueueReflection(second, "Second synthetic reflection");
            test.Store.FailuresRemaining = 2;
            await test.Services.Sync(); await test.Services.Sync();
            var pending = test.Engine.Snapshot.Outbox;
            check(test.Receiver.Writes.Count == 1 && pending[0].Status == DeliveryStatus.Sending && pending[1].Status == DeliveryStatus.Pending,
                "A waiting local acknowledgment blocks later batch uploads until it is saved");
            await test.Services.Sync();
            check(test.Engine.Snapshot.Outbox.All(x => x.Status == DeliveryStatus.Sent) && test.Receiver.Writes.Count == 2 && test.Receiver.Rows.Count == 2,
                "Batch delivery continues after recovery with exactly one append per reflection");
        }

        foreach (var safeRetry in new[] { true, false }) {
            using var test = new Fixture(safeRetry);
            test.Receiver.RejectWrite = true; test.Store.FailuresRemaining = 1;
            await test.Services.Sync(); await test.Services.Sync();
            check(test.Item.Status == DeliveryStatus.NeedsReview && test.Item.ErrorKind == "rejected" && test.Receiver.Writes.Count == 1,
                "A rejected result recovers to Needs review without another append: safe retry=" + safeRetry);
            await test.WaitForAudio();
            check(test.Audio.Calls.Count == 1, "Recovered rejection plays failure feedback once: safe retry=" + safeRetry);
        }

        using (var test = new Fixture()) {
            test.Receiver.TransientWriteFailure = true; test.Store.FailuresRemaining = 1;
            await test.Services.Sync(); await test.Services.Sync();
            check(test.Item.Status == DeliveryStatus.Pending && test.Item.ErrorKind == "network" && test.Item.NextAttemptAt > test.Engine.Now,
                "A retryable result first recovers to its durable Pending/backoff state");
            check(test.Receiver.Writes.Count == 1, "Acknowledgment recovery respects upload backoff");
            test.Receiver.TransientWriteFailure = false; test.Now = test.Now.AddSeconds(16);
            await test.Services.Sync();
            check(test.Item.Status == DeliveryStatus.Sent && test.Receiver.Writes.Count == 2 && test.Receiver.Writes.Distinct().Count() == 1,
                "An actual transport retry retains the same receiver-protected request ID");
        }

        using (var test = new Fixture()) {
            test.Receiver.WriteGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var inFlight = test.Services.Sync();
            await test.Receiver.WriteEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await test.Services.Sync(true);
            check(test.Item.Status == DeliveryStatus.Sending && test.Receiver.Writes.Count == 1,
                "Concurrent Sync does not recover or duplicate a genuinely in-flight request");
            test.Services.SaveConnection(test.Connection, false);
            test.Receiver.WriteGate.SetResult(); await inFlight;
            check(test.Item.Status == DeliveryStatus.Sent && !test.Engine.Snapshot.ExtensionDisabledConfirmed,
                "Pausing during an upload still records its completed result without re-enabling delivery");
        }

        using (var test = new Fixture()) {
            var observerFailed = false;
            test.Engine.Changed += () => {
                if (!observerFailed && test.Item.Status == DeliveryStatus.Sent) { observerFailed = true; throw new InvalidOperationException("Synthetic observer failure"); }
            };
            await test.Services.Sync(); var saves = test.Store.SuccessfulSaves;
            await test.Services.Sync(); await test.WaitForAudio();
            check(observerFailed && test.Item.Status == DeliveryStatus.Sent && test.Store.SuccessfulSaves == saves && test.Receiver.Writes.Count == 1,
                "A post-commit observer failure never repeats the local commit or network upload");
            check(test.Audio.Calls.Count == 1 && test.Notices.Count == 1 && test.Notices[0].StartsWith("Reflection sent to"),
                "An already-durable result receives success feedback, not a false disk-failure notice");
        }

        using (var test = new Fixture()) {
            var first = test.Engine.BeginUpload(true)!;
            test.Engine.FinishUpload(first.Id, false, "rejected"); test.Engine.RetryUpload(first.Id);
            var second = test.Engine.BeginUpload(true)!; var saves = test.Store.SuccessfulSaves;
            check(!test.Engine.FinishUploadAttempt(first.Id, first.Attempts, true, tab: "Stale target")
                && test.Item.Status == DeliveryStatus.Sending && test.Item.Attempts == second.Attempts && test.Store.SuccessfulSaves == saves,
                "A delayed result cannot overwrite a newer upload attempt");
            check(test.Engine.FinishUploadAttempt(second.Id, second.Attempts, true, tab: "Current target") && test.Item.SavedTab == "Current target",
                "Only the matching Sending attempt can commit its result");
            saves = test.Store.SuccessfulSaves;
            check(!test.Engine.FinishUploadAttempt(second.Id, second.Attempts, false, "rejected") && test.Item.Status == DeliveryStatus.Sent && test.Store.SuccessfulSaves == saves,
                "A terminal upload result cannot be overwritten or saved again");
        }

        foreach (var safeRetry in new[] { true, false }) {
            using var test = new Fixture(safeRetry);
            test.Receiver.ThrowAfterWrite = true;
            await test.Services.Sync();
            check(test.Item.Status == (safeRetry ? DeliveryStatus.Pending : DeliveryStatus.NeedsReview) && test.Item.ErrorKind == "interrupted",
                "An unexpected transport exception cannot leave an entry stuck Sending: safe retry=" + safeRetry);
            check(!test.Notices.Any(x => x.Contains("Synthetic transport exception")), "Transport exception details stay out of user feedback");
            test.Receiver.ThrowAfterWrite = false; test.Now = test.Now.AddSeconds(16); await test.Services.Sync();
            check(test.Receiver.Rows.Count == 1 && test.Receiver.Writes.Count == (safeRetry ? 2 : 1),
                "Uncertain transport recovery replays only protected IDs and never blindly resends legacy requests: safe retry=" + safeRetry);
        }

        using (var test = new Fixture()) {
            test.Receiver.WriteGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var inFlight = test.Services.Sync();
            await test.Receiver.WriteEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            test.Services.Dispose(); await inFlight.WaitAsync(TimeSpan.FromSeconds(3));
            check(test.Item.Status == DeliveryStatus.Sending && test.Audio.Calls.IsEmpty,
                "Shutdown during an upload leaves its durable Sending record for normal startup recovery, without late feedback");
        }

        foreach (var safeRetry in new[] { true, false }) {
            using var test = new Fixture(safeRetry);
            test.Store.FailuresRemaining = 1;
            await test.Services.Sync(); test.Services.Dispose();
            var restored = new TimerEngine(test.Store, () => test.Now);
            using var resumed = new PreviewServices(restored, test.Directory, new SheetsClient(test.Receiver), test.Audio);
            test.Now = test.Now.AddSeconds(16); await resumed.Sync();
            var entry = restored.Snapshot.Outbox.Single();
            check(safeRetry ? entry.Status == DeliveryStatus.Sent && test.Receiver.Writes.Count == 2 && test.Receiver.Writes.Distinct().Count() == 1
                    : entry.Status == DeliveryStatus.NeedsReview && test.Receiver.Writes.Count == 1,
                "Restart preserves safe replay IDs and holds uncertain legacy writes for review: safe retry=" + safeRetry);
            check(test.Receiver.Rows.Count == 1, "Acknowledgment recovery never creates a second synthetic sheet row: safe retry=" + safeRetry);
        }
    }

    private sealed class Fixture : IDisposable
    {
        public DateTimeOffset Now = new(2026, 9, 22, 12, 0, 0, TimeSpan.Zero);
        public readonly string Directory = Path.Combine(Path.GetTempPath(), "ReflectionTimer-UploadRecovery-" + Guid.NewGuid().ToString("N"));
        public readonly ConnectionSettings Connection = new() { SheetUrl = "https://docs.google.com/spreadsheets/d/abcdefghijklmnopqrstuvwxyz/edit",
            WebAppUrl = "https://script.google.com/macros/s/syntheticReceiver/exec", ApiToken = new string('a', 64), SheetMode = "fixed", SheetName = "Synthetic only" };
        public readonly FaultStore Store;
        public readonly TimerEngine Engine;
        public readonly Receiver Receiver;
        public readonly RecordingAudio Audio = new();
        public readonly PreviewServices Services;
        public readonly List<string> Notices = [];
        public OutboxItem Item => Engine.Snapshot.Outbox.Single();

        public Fixture(bool safeRetry = true)
        {
            Store = new(new() { Connection = Connection, ExtensionDisabledConfirmed = true, LoggingEnabled = false,
                Outbox = [new() { Message = "Synthetic recovery reflection", SubmittedAt = Now, SheetUrl = Connection.SheetUrl,
                    ReceiverUrl = Connection.WebAppUrl, SheetMode = "fixed", SheetName = Connection.SheetName, DurationSeconds = 300, ActualDurationSeconds = 60 }] });
            Engine = new(Store, () => Now); Receiver = new(safeRetry);
            Services = new(Engine, Directory, new SheetsClient(Receiver), Audio);
            Services.Announcement += Notices.Add;
        }
        public async Task WaitForAudio()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            while (Audio.Calls.IsEmpty) await Task.Delay(10, timeout.Token);
        }
        public void Dispose() => Services.Dispose(); // Logging is disabled; no profile or diagnostic files are created.
    }

    private sealed class FaultStore(AppState state) : IStateStore
    {
        public int FailuresRemaining;
        public int SuccessfulSaves;
        public AppState Load() => DataJson.Clone(state);
        public void Save(AppState value)
        {
            if (FailuresRemaining > 0 && value.Outbox.Any(x => x.Attempts > 0 && x.Status != DeliveryStatus.Sending)) {
                FailuresRemaining--; throw new IOException("Synthetic disk failure: do not disclose raw exceptions");
            }
            state = DataJson.Clone(value);
            SuccessfulSaves++;
        }
    }

    private sealed class Receiver(bool safeRetry) : HttpMessageHandler
    {
        public readonly List<string> Actions = [];
        public readonly List<Guid> Writes = [];
        public readonly HashSet<Guid> Rows = [];
        public bool Offline, RejectWrite, TransientWriteFailure, ThrowAfterWrite;
        public TaskCompletionSource? WriteGate;
        public readonly TaskCompletionSource WriteEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var action = body.RootElement.GetProperty("action").GetString()!; Actions.Add(action);
            if (Offline) throw new HttpRequestException("Synthetic offline transport");
            if (action == "appendReflection") {
                var id = body.RootElement.GetProperty("requestId").GetGuid(); Writes.Add(id); WriteEntered.TrySetResult();
                if (WriteGate is not null) await WriteGate.Task.WaitAsync(cancellationToken);
                if (TransientWriteFailure) return new(HttpStatusCode.ServiceUnavailable);
                if (!RejectWrite) Rows.Add(id);
                if (ThrowAfterWrite) throw new InvalidOperationException("Synthetic transport exception: private details");
            }
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new {
                success = action == "ping" || !RejectWrite, code = "rejected", target = "Synthetic only", sheet = "Synthetic only",
                deliveryProtocol = safeRetry ? SheetsClient.DeliveryProtocol : null, supportsCheckIns = true, supportsStopwatch = true
            })) };
        }
    }

    private sealed class RecordingAudio : IAlertAudioBackend
    {
        public readonly ConcurrentQueue<string> Calls = new();
        public Task PlayAsync(string path, AudioLevel level, CancellationToken cancellationToken) { Calls.Enqueue(path); return Task.CompletedTask; }
    }
}
