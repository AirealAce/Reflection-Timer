using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using ReflectionTimer.Accessible;
using ReflectionTimer.Core;
using ReflectionTimer.Desktop;

static class DeliveryPreflightTests
{
    internal static async Task Run(Action<bool, string> check)
    {
        using (var test = new Fixture([
            Entry(), Entry() with { IsCheckIn = true }, Entry() with { Mode = SessionMode.Stopwatch },
            Entry() with { AutoSent = true, Message = new string('x', 5000) }
        ])) {
            await test.Services.Sync();
            check(test.Receiver.Pings == 1 && test.Receiver.Writes.Count == 4 && test.Items.All(x => x.Status == DeliveryStatus.Sent),
                "A mixed timer, check-in, stopwatch and full-length auto-send batch uses one capability check");
            check(test.Receiver.Writes.All(x => x.GetProperty("deliveryProtocol").GetString() == SheetsClient.DeliveryProtocol)
                && test.Items.All(x => x.Attempts == 1 && x.RetryProtected), "Batch capability reuse preserves protected request IDs and attempt counts");
        }

        using (var test = new Fixture(Enumerable.Range(0, 21).Select(_ => Entry() with { Mode = SessionMode.Stopwatch }).ToArray())) {
            await test.Services.Sync();
            check(test.Receiver.Pings == 1 && test.Receiver.Writes.Count == 20, "A capability check covers the bounded twenty-entry batch");
            await test.Services.Sync();
            check(test.Receiver.Pings == 2 && test.Receiver.Writes.Count == 21, "The next sync re-verifies instead of keeping a stale capability cache");
        }

        using (var test = new Fixture()) {
            var combined = Entry() with { IsCheckIn = true, Mode = SessionMode.Stopwatch, AutoSent = true, Message = new string('x', 5000) };
            var reply = await test.Client.Upload(Connection, combined);
            check(reply.Success && test.Receiver.Pings == 1 && test.Receiver.Writes.Count == 1,
                "An independent upload checks all required capabilities with one authenticated ping");
            test.Receiver.Stopwatch = false;
            reply = await test.Client.Upload(Connection, combined);
            check(reply.ErrorKind == "receiver_update_required" && test.Receiver.Pings == 2 && test.Receiver.Writes.Count == 1,
                "Independent callers still verify capabilities on their next upload");
        }

        foreach (var feature in new[] { "check-in", "stopwatch", "auto-sent" }) {
            var entry = feature switch {
                "check-in" => Entry() with { IsCheckIn = true },
                "stopwatch" => Entry() with { Mode = SessionMode.Stopwatch },
                _ => Entry() with { AutoSent = true, Message = new string('x', 5000) }
            };
            using var test = new Fixture([entry]);
            test.Receiver.CheckIns = feature != "check-in";
            test.Receiver.Stopwatch = feature != "stopwatch";
            test.Receiver.AutoSent = feature != "auto-sent";
            await test.Services.Sync();
            check(test.Receiver.Pings == 1 && test.Receiver.Writes.Count == 0
                && test.Items.Single() is { Status: DeliveryStatus.NeedsReview, ErrorKind: "receiver_update_required" },
                "A cached capability cannot bypass the old-receiver guard for " + feature);
        }

        using (var test = new Fixture()) {
            test.Receiver.Failure = "rejected";
            var batch = await test.Client.BeginBatch(Connection);
            var reply = await batch.Upload(Entry());
            check(!reply.Success && test.Receiver.Pings == 1 && test.Receiver.Writes.Count == 0,
                "An unauthenticated batch cannot append reflections");
        }

        using (var test = new Fixture()) {
            var batch = await test.Client.BeginBatch(Connection);
            var local = await batch.Upload(Entry() with { LocalOnly = true });
            var changed = await batch.Upload(Entry() with { ReceiverUrl = "https://script.google.com/macros/s/otherSynthetic/exec" });
            check(local.ErrorKind == "settings_required" && changed.ErrorKind == "receiver_changed" && test.Receiver.Writes.Count == 0,
                "A verified batch still rejects local-only entries and entries belonging to another receiver");
        }

        foreach (var change in new[] { "pause", "same settings", "token", "receiver", "pause and resume" }) {
            using var test = new Fixture();
            test.Receiver.OnPing = () => {
                if (change == "pause and resume") test.Services.SaveConnection(Connection, false);
                var connection = change == "token" ? Connection with { ApiToken = new string('b', 64) }
                    : change == "receiver" ? Connection with { WebAppUrl = "https://script.google.com/macros/s/otherSynthetic/exec" } : Connection;
                test.Services.SaveConnection(connection, change != "pause");
            };
            await test.Services.Sync();
            check(test.Receiver.Writes.Count == 0 && test.Items.Single().Attempts == 0,
                "A connection revision invalidates in-flight verification: " + change);
        }

        using (var test = new Fixture([Entry(), Entry()])) {
            test.Receiver.OnWrite = () => test.Services.SaveConnection(Connection, true);
            await test.Services.Sync();
            check(test.Receiver.Writes.Count == 1 && test.Items.Count(x => x.Status == DeliveryStatus.Sent) == 1
                && test.Items.Count(x => x.Status == DeliveryStatus.Pending && x.Attempts == 0) == 1,
                "A connection revision during an append saves its result but stops the next batch write");
        }

        using (var test = new Fixture()) {
            test.Receiver.PingGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var sync = test.Services.Sync();
            await test.Receiver.PingEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await test.Services.Sync(true);
            check(test.Receiver.Pings == 1 && test.Items.Single().Attempts == 0, "Concurrent manual sync cannot duplicate an in-flight preflight");
            test.Receiver.PingGate.SetResult(); await sync;
            check(test.Receiver.Writes.Count == 1, "A shared in-flight sync appends the reflection once");
        }

        using (var test = new Fixture()) {
            test.Receiver.Failure = "network";
            foreach (var seconds in new[] { 15, 30, 60, 120, 240, 300, 300 }) {
                await test.Services.Sync();
                check(test.Services.DeliveryIssue is { ErrorKind: "network" } issue
                    && issue.NextRetryAt == test.Clock.GetUtcNow().AddSeconds(seconds).ToUnixTimeMilliseconds(),
                    "Connection backoff is bounded and reports its next retry: " + seconds + " seconds");
                var pings = test.Receiver.Pings; var updates = test.HealthUpdates;
                await test.Services.Sync();
                test.Clock.Advance(TimeSpan.FromSeconds(seconds - 1)); await test.Services.Sync();
                check(test.Receiver.Pings == pings && test.HealthUpdates == updates,
                    "Automatic sync leaves a waiting connection alone before its " + seconds + "-second deadline");
                test.Clock.Advance(TimeSpan.FromSeconds(1));
            }
            check(test.Items.Single() is { Status: DeliveryStatus.Pending, Attempts: 0, ErrorKind: "", NextAttemptAt: null }
                && test.Receiver.Writes.Count == 0, "Failed preflight never consumes append attempts or changes the saved reflection");
            check(test.Notices.Count == 0 && test.Audio.Calls.IsEmpty, "Repeated automatic connection failures do not spam announcements or failure sounds");
            var health = JsonSerializer.Serialize(test.Services.Settings(), PreviewSession.Json);
            check(health.Contains("deliveryIssue") && !health.Contains(Connection.ApiToken) && !health.Contains("Synthetic private server detail"),
                "The UI receives persistent delivery health without a token or raw server error");
        }

        using (var test = new Fixture()) {
            test.Receiver.Failure = "network"; await test.Services.Sync();
            test.Clock.Utc = test.Clock.Utc.AddDays(2); await test.Services.Sync();
            test.Clock.Utc = test.Clock.Utc.AddDays(-4); await test.Services.Sync();
            check(test.Receiver.Pings == 1, "Wall-clock changes cannot skip the connection retry delay");
            test.Clock.Advance(TimeSpan.FromSeconds(15)); await test.Services.Sync();
            check(test.Receiver.Pings == 2, "Connection backoff still expires after elapsed time despite a clock correction");
        }

        foreach (var failure in new[] { "network", "timeout", "429", "unexpected", "rejected", "invalid" }) {
            using var test = new Fixture(timeout: failure == "timeout" ? TimeSpan.FromMilliseconds(100) : null);
            test.Receiver.Failure = failure;
            await test.Services.Sync();
            var retryable = failure is "network" or "timeout" or "429" or "unexpected";
            check(test.Services.DeliveryIssue is not null && (test.Services.DeliveryIssue.NextRetryAt is not null) == retryable
                && test.Items.Single().Attempts == 0, "Preflight failure has the right recovery policy: " + failure);
            if (!retryable) {
                test.Clock.Advance(TimeSpan.FromHours(1)); await test.Services.Sync();
                check(test.Receiver.Pings == 1 && test.Services.DeliveryIssue!.Message.Contains("Connection setup"),
                    "Setup/access failures wait for corrective action rather than polling: " + failure);
            }
            test.Receiver.Failure = "";
            await test.Services.Sync(true);
            check(test.Services.DeliveryIssue is null && test.Items.Single().Status == DeliveryStatus.Sent && test.Receiver.Writes.Count == 1,
                "Manual Send pending now can recover immediately from " + failure);
        }

        using (var test = new Fixture()) {
            test.Receiver.Failure = "rejected";
            await test.Services.Sync(true);
            check(test.Notices.Count == 1 && test.Notices[0].Contains("Connection setup") && test.Audio.Calls.IsEmpty,
                "Manual preflight failure gives one actionable explanation without an upload failure sound");
            test.Receiver.Failure = "";
            test.Services.SaveConnection(Connection with { ApiToken = new string('b', 64) }, true);
            check(test.Services.DeliveryIssue is null, "Saving corrected credentials clears the previous connection block");
            await test.Services.Sync();
            check(test.Items.Single().Status == DeliveryStatus.Sent, "Corrected credentials get a fresh check without waiting for old backoff");
        }

        using (var test = new Fixture()) {
            test.Receiver.Failure = "network"; await test.Services.Sync(); test.Receiver.Failure = "";
            await test.Services.CheckConnection(Connection with { ApiToken = new string('b', 64) });
            check(test.Services.DeliveryIssue is not null, "Testing unrelated credentials cannot clear the active connection's failure");
            await test.Services.CheckConnection(Connection);
            check(test.Services.DeliveryIssue is null && test.Receiver.Writes.Count == 0, "A successful read-only connection test clears the current block without appending");
            await test.Services.Sync();
            check(test.Items.Single().Status == DeliveryStatus.Sent, "Delivery resumes automatically after successful verification");
        }

        using (var test = new Fixture()) {
            test.Receiver.Failure = "rejected";
            test.Receiver.OnPing = () => test.Services.SaveConnection(Connection with { ApiToken = new string('b', 64) }, true);
            await test.Services.Sync();
            check(test.Services.DeliveryIssue is null && test.Items.Single().Attempts == 0, "A stale failed check cannot block newer credentials");
        }

        using (var test = new Fixture()) {
            test.Receiver.Failure = "network"; await test.Services.Sync();
            test.Services.SaveConnection(Connection, false); await test.Services.Sync(true);
            check(test.Services.DeliveryIssue is null && test.Receiver.Pings == 1 && test.Items.Single().Attempts == 0,
                "Pausing clears stale retry status and prevents both checks and writes");
        }

        using (var test = new Fixture()) {
            test.Services.SaveConnection(Connection with { ApiToken = "" }, true);
            await test.Services.Sync(); await test.Services.Sync();
            check(test.Services.DeliveryIssue is { ErrorKind: "settings_required", NextRetryAt: null }
                && test.Receiver.Pings == 0 && test.Items.Single().Attempts == 0, "Invalid saved settings surface an actionable block without a network request");
        }

        using (var test = new Fixture()) {
            test.Receiver.PingGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var sync = test.Services.Sync(); await test.Receiver.PingEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            test.Services.Dispose(); await sync;
            check(test.Services.DeliveryIssue is null && test.Receiver.Writes.Count == 0 && test.Notices.Count == 0,
                "Shutdown during preflight produces neither a false failure status nor a late write");
        }
    }

    private static readonly ConnectionSettings Connection = new() {
        SheetUrl = "https://docs.google.com/spreadsheets/d/abcdefghijklmnopqrstuvwxyz/edit",
        WebAppUrl = "https://script.google.com/macros/s/syntheticPreflight/exec", ApiToken = new string('a', 64),
        SheetMode = "fixed", SheetName = "Synthetic only"
    };
    private static OutboxItem Entry() => new() { Message = "Synthetic preflight reflection", SubmittedAt = DateTimeOffset.UtcNow,
        SheetUrl = Connection.SheetUrl, ReceiverUrl = Connection.WebAppUrl, SheetMode = Connection.SheetMode,
        SheetName = Connection.SheetName, DurationSeconds = 300, ActualDurationSeconds = 60 };

    private sealed class Fixture : IDisposable
    {
        public readonly Clock Clock = new();
        public readonly Receiver Receiver = new();
        public readonly Audio Audio = new();
        public readonly SheetsClient Client;
        public readonly TimerEngine Engine;
        public readonly PreviewServices Services;
        public readonly List<string> Notices = [];
        public int HealthUpdates;
        public IReadOnlyList<OutboxItem> Items => Engine.Snapshot.Outbox;
        public Fixture(OutboxItem[]? entries = null, TimeSpan? timeout = null)
        {
            Engine = new(new Store(new() { Connection = Connection, ExtensionDisabledConfirmed = true,
                LoggingEnabled = false, Outbox = (entries ?? [Entry()]).ToList() }), () => Clock.GetUtcNow());
            Client = new(Receiver, timeout);
            Services = new(Engine, Path.Combine(Path.GetTempPath(), "ReflectionTimer-Preflight-" + Guid.NewGuid().ToString("N")), Client, Audio, Clock);
            Services.Announcement += Notices.Add;
            Services.DeliveryIssueChanged += () => HealthUpdates++;
        }
        public void Dispose() => Services.Dispose(); // In-memory state, disabled logs and fake audio only.
    }
    private sealed class Store(AppState state) : IStateStore
    {
        public AppState Load() => DataJson.Clone(state);
        public void Save(AppState value) => state = DataJson.Clone(value);
    }
    private sealed class Clock : TimeProvider
    {
        private long ticks;
        public DateTimeOffset Utc = new(2026, 9, 22, 12, 0, 0, TimeSpan.Zero);
        public override long GetTimestamp() => ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override DateTimeOffset GetUtcNow() => Utc;
        public void Advance(TimeSpan duration) { ticks += duration.Ticks; Utc += duration; }
    }
    private sealed class Audio : IAlertAudioBackend
    {
        public readonly ConcurrentQueue<string> Calls = new();
        public Task PlayAsync(string path, AudioLevel level, CancellationToken cancellationToken) { Calls.Enqueue(path); return Task.CompletedTask; }
    }
    private sealed class Receiver : HttpMessageHandler
    {
        public int Pings;
        public readonly List<JsonElement> Writes = [];
        public bool CheckIns = true, Stopwatch = true, AutoSent = true;
        public string Failure = "";
        public Action? OnPing, OnWrite;
        public TaskCompletionSource? PingGate;
        public readonly TaskCompletionSource PingEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var data = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            if (data.RootElement.GetProperty("action").GetString() == "ping") {
                Pings++; PingEntered.TrySetResult();
                if (PingGate is not null) await PingGate.Task.WaitAsync(cancellationToken);
                OnPing?.Invoke();
                if (Failure == "network") throw new HttpRequestException("Synthetic private server detail");
                if (Failure == "unexpected") throw new InvalidOperationException("Synthetic private server detail");
                if (Failure == "timeout") await Task.Delay(Timeout.Infinite, cancellationToken);
                if (Failure == "429") return new(HttpStatusCode.TooManyRequests);
                if (Failure == "rejected") return Json(new { success = false, message = "Synthetic private server detail", code = "invalid_token" });
                if (Failure == "invalid") return new(HttpStatusCode.OK) { Content = new StringContent("<html>Synthetic private server detail</html>") };
            } else { Writes.Add(data.RootElement.Clone()); OnWrite?.Invoke(); }
            return Json(new { success = true, target = "Synthetic only", sheet = "Synthetic only", deliveryProtocol = SheetsClient.DeliveryProtocol,
                supportsCheckIns = CheckIns, supportsStopwatch = Stopwatch, supportsAutoSent = AutoSent });
        }
        private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value)) };
    }
}
