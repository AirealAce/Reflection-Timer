using System.Net;
using System.Text.Json;
using ReflectionTimer.Accessible;
using ReflectionTimer.Core;
using ReflectionTimer.Desktop;

static class RetryAllServicesTests
{
    internal static async Task Run(Action<bool, string> check)
    {
        using (var test = new Fixture()) {
            var savedCsv = test.Entry() with { CsvStatus = CsvDeliveryStatus.Pending, CsvDirectory = test.CsvDirectory };
            var csvReply = new CsvLog().Write(savedCsv);
            check(csvReply.Success, "Retry-all fixture has an actual previously saved CSV row: " + csvReply.Error);
            savedCsv = savedCsv with { CsvStatus = CsvDeliveryStatus.Saved, CsvFile = csvReply.File, CsvAttempts = 1 };
            var originalCsv = File.ReadAllBytes(csvReply.File);
            var sheetFailure = test.Entry() with { CsvStatus = CsvDeliveryStatus.Pending, CsvDirectory = test.CsvDirectory };
            var csvFailure = test.Entry() with { Status = DeliveryStatus.Pending, ErrorKind = "", Attempts = 0,
                CsvStatus = CsvDeliveryStatus.NeedsReview, CsvDirectory = test.CsvDirectory, CsvError = "csv_locked", CsvAttempts = 1 };
            var sheetAlreadySent = test.Entry() with { Status = DeliveryStatus.Sent, ErrorKind = "", SavedTab = "Synthetic only",
                CsvStatus = CsvDeliveryStatus.Pending, CsvDirectory = test.CsvDirectory, CsvError = "csv_locked", CsvAttempts = 1 };
            test.SetEntries([savedCsv, sheetFailure, csvFailure, sheetAlreadySent]);
            await test.Services.RetryAllFailed();
            check(test.Receiver.Writes.Select(x => x.Id).ToHashSet().SetEquals([savedCsv.Id, sheetFailure.Id]),
                "Retry all sends only failed Sheets destinations, including entries whose CSV already succeeded");
            check(test.Item(sheetFailure.Id).CsvStatus == CsvDeliveryStatus.Pending && test.Item(sheetFailure.Id).CsvAttempts == 0,
                "A failed Sheets destination does not export that entry's unrelated fresh CSV request");
            check(test.Item(csvFailure.Id).Status == DeliveryStatus.Pending && test.Item(csvFailure.Id).Attempts == 0,
                "A failed CSV destination does not send that entry's unrelated fresh Sheets request");
            check(test.Item(csvFailure.Id).CsvStatus == CsvDeliveryStatus.Saved && test.Item(sheetAlreadySent.Id).CsvStatus == CsvDeliveryStatus.Saved,
                "Retry all handles failed CSV destinations independently of their Sheets state");
            check(test.Item(savedCsv.Id).CsvAttempts == 1 && test.Item(savedCsv.Id).CsvFile == csvReply.File
                && File.ReadAllBytes(csvReply.File).AsSpan().StartsWith(originalCsv),
                "A successful CSV destination retains its identity and original row during a Sheets retry");
            check(test.Item(sheetAlreadySent.Id).Attempts == 1 && test.Item(sheetAlreadySent.Id).SavedTab == "Synthetic only",
                "A CSV retry preserves the successful Sheets destination and attempt count");
        }

        using (var test = new Fixture()) {
            var fresh = test.Entry() with { Status = DeliveryStatus.Pending, Attempts = 0, ErrorKind = "", RetryProtected = false };
            var completed = test.Entry() with { Status = DeliveryStatus.Sent, ErrorKind = "stale error" };
            var local = test.Entry() with { LocalOnly = true };
            var unrequested = test.Entry() with { SheetsRequested = false };
            var failed = test.Entry() with { Status = DeliveryStatus.Pending, ErrorKind = "network", NextAttemptAt = test.Now.AddHours(1).ToUnixTimeMilliseconds() };
            test.SetEntries([fresh, completed, local, unrequested, failed]);
            await test.Services.RetryAllFailed();
            check(test.Receiver.Writes.Select(x => x.Id).SequenceEqual([failed.Id]) && test.Item(failed.Id).Status == DeliveryStatus.Sent,
                "Retry all bypasses a failed entry's retry delay without sending new, completed, local, or unrequested entries");
            check(test.Item(fresh.Id) == fresh && test.Item(completed.Id) == completed && test.Item(local.Id) == local && test.Item(unrequested.Id) == unrequested,
                "Entries outside the retry-all failure snapshot retain their state");
            var pings = test.Receiver.Pings;
            await test.Services.RetryAllFailed();
            check(test.Receiver.Pings == pings && test.Receiver.Writes.Count == 1,
                "Retry all with no failed destination performs no network requests");
        }

        using (var test = new Fixture()) {
            var entries = Enumerable.Range(0, 27).Select(_ => test.Entry()).ToArray();
            test.SetEntries(entries); test.Receiver.RejectedIds.Add(entries[0].Id); test.Receiver.UnavailableIds.Add(entries[1].Id);
            await test.Services.RetryAllFailed();
            check(test.Receiver.Writes.Count == 27 && test.Receiver.Writes.GroupBy(x => x.Id).All(x => x.Count() == 1),
                "Retry all attempts every failed entry once even when there are more than twenty failures");
            check(test.Item(entries[0].Id).Status == DeliveryStatus.NeedsReview && test.Items.Skip(2).All(x => x.Status == DeliveryStatus.Sent),
                "An item-specific Sheets failure does not prevent retrying the remaining failed entries");
            check(test.Item(entries[1].Id) is { Status: DeliveryStatus.Pending, ErrorKind: "network" } transient
                && transient.NextAttemptAt > test.Engine.Now,
                "A transient item failure gets one attempt and preserves backoff while later failed entries continue");
        }

        using (var test = new Fixture()) {
            var entries = Enumerable.Range(0, 23).Select(_ => test.Entry() with { SheetsRequested = false,
                CsvStatus = CsvDeliveryStatus.Pending, CsvDirectory = test.CsvDirectory, CsvError = "csv_locked", CsvAttempts = 1,
                CsvNextAttemptAt = test.Now.AddHours(1).ToUnixTimeMilliseconds() }).ToArray();
            test.SetEntries(entries);
            await test.Services.RetryAllFailed();
            check(test.Items.All(x => x.CsvStatus == CsvDeliveryStatus.Saved && x.CsvAttempts == 1) && test.Receiver.Pings == 0,
                "Retry all handles more than twenty failed CSV entries without touching Sheets");
            var file = test.Items[0].CsvFile;
            check(File.ReadAllLines(file).Length == 24 && entries.All(x => File.ReadAllText(file).Contains(x.Id.ToString())),
                "The CSV retry creates one header and one row for every failed entry");
        }

        using (var test = new Fixture()) {
            var held = test.Entry(); var retry = test.Entry();
            test.SetEntries([held, retry]);
            test.Engine.OpenUnsentReflection(held.Id);
            await test.Services.RetryAllFailed();
            await test.Services.Sync(true);
            check(test.Engine.IsOutboxBeingEdited(held.Id) && test.Item(held.Id).Status == DeliveryStatus.NeedsReview
                && test.Receiver.Writes.Select(x => x.Id).SequenceEqual([retry.Id]),
                "Retry all and ordinary sync leave an entry open in the reflection editor unsent");
        }

        using (var test = new Fixture()) {
            var failed = test.Entry(); test.SetEntries([failed]);
            test.Receiver.WriteGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var retry = test.Services.RetryAllFailed();
            await test.Receiver.WriteEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var overlappingRetry = test.Services.RetryAllFailed();
            var overlappingSync = test.Services.Sync(true);
            test.Receiver.WriteGate.SetResult();
            await Task.WhenAll(retry, overlappingRetry, overlappingSync).WaitAsync(TimeSpan.FromSeconds(3));
            check(test.Receiver.Writes.Count == 1 && test.Item(failed.Id).Status == DeliveryStatus.Sent,
                "Concurrent retry-all and normal sync requests never duplicate an in-flight append");
        }

        using (var test = new Fixture()) {
            var failed = test.Entry(); test.SetEntries([failed]); test.Store.FailedAcknowledgments = 1;
            await test.Services.RetryAllFailed();
            check(test.Item(failed.Id).Status == DeliveryStatus.Sending && test.Receiver.Writes.Count == 1,
                "A retry-all acknowledgment disk failure preserves the in-flight entry and accepted write");
            await test.Services.RetryAllFailed();
            check(test.Item(failed.Id).Status == DeliveryStatus.Sent && test.Store.Load().Outbox.Single().Status == DeliveryStatus.Sent
                && test.Receiver.Writes.Count == 1 && test.Item(failed.Id).Attempts == 2,
                "Retry all recovers a known acknowledgment locally without resending the reflection");
        }

        using (var test = new Fixture()) {
            var uncertain = test.Entry() with { ErrorKind = "write_uncertain" };
            var ordinary = test.Entry();
            test.SetEntries([ordinary, uncertain]);
            var rejected = false;
            try { await test.Services.RetryAllFailed(); }
            catch (ArgumentException) { rejected = true; }
            check(rejected && test.Receiver.Pings == 0 && test.Item(uncertain.Id) == uncertain && test.Item(ordinary.Id) == ordinary,
                "An uncertain retry-all entry requires confirmation before any state change or network request");
            await test.Services.RetryAllFailed(confirmed: true);
            check(test.Receiver.Writes.Count == 2 && test.Items.All(item => item.Status == DeliveryStatus.Sent),
                "Confirming an uncertain retry-all entry retries its failed destination");
        }

        using (var test = new Fixture()) {
            var pending = test.Entry() with { Status = DeliveryStatus.Pending, Attempts = 0, ErrorKind = "", RetryProtected = false };
            test.SetEntries([pending]); test.Receiver.PingOffline = true;
            await test.Services.Sync();
            check(test.Services.DeliveryIssue is not null && test.Item(pending.Id).Attempts == 0,
                "A preflight connection failure retains an unattempted pending reflection");
            test.Receiver.PingOffline = false;
            await test.Services.RetryAllFailed();
            check(test.Services.DeliveryIssue is null && test.Item(pending.Id).Status == DeliveryStatus.Sent && test.Receiver.Writes.Count == 1,
                "Retry all immediately retries pending reflections blocked by a known connection failure");
        }

        using (var test = new Fixture()) {
            var accepted = test.Entry(); test.SetEntries([accepted]); test.Store.FailedAcknowledgments = 1;
            await test.Services.RetryAllFailed();
            await test.Services.PrepareLatestUnsentReflectionAsync();
            check(test.Item(accepted.Id).Status == DeliveryStatus.Sent && test.Engine.Snapshot.Prompts.Count == 0
                && test.Receiver.Writes.Count == 1,
                "Opening the latest unsent editor first commits a known acknowledgment instead of reopening an already sent reflection");
        }

        using (var test = new Fixture()) {
            var failed = test.Entry(); test.SetEntries([failed], volume: 100);
            var prompt = await test.Services.PrepareLatestUnsentReflectionAsync();
            await Task.Delay(30);
            check(prompt is not null && test.Engine.Snapshot.Prompts.Single().RetryOutboxId == failed.Id && test.Audio.Calls == 0,
                "Opening an unsent message for editing does not replay session-end audio");
        }

        using (var test = new Fixture()) {
            var failed = test.Entry(); test.SetEntries([failed]);
            test.Services.SaveConnection(test.Connection, false);
            await test.Services.RetryAllFailed();
            var remaining = test.Item(failed.Id);
            check(!test.Engine.Snapshot.ExtensionDisabledConfirmed && test.Receiver.Pings == 0 && test.Receiver.Writes.Count == 0,
                "Retry all respects disabled Sheets delivery without enabling it or making a network request");
            check(remaining.DeliveryFailed && test.Services.IsFailedDelivery(remaining)
                && test.Store.Load().Outbox.Single().DeliveryFailed,
                "A failed Sheets message stays durably failed and warning-icon eligible when delivery is disabled");
        }

        using (var test = new Fixture()) {
            var failed = test.Entry(); test.SetEntries([failed]);
            test.Receiver.OnPing = () => test.Services.SaveConnection(test.Connection, false);
            await test.Services.RetryAllFailed();
            check(test.Receiver.Pings == 1 && test.Receiver.Writes.Count == 0 && !test.Engine.Snapshot.ExtensionDisabledConfirmed,
                "Pausing Sheets during retry-all verification stops the append without re-enabling delivery");
            check(test.Item(failed.Id).DeliveryFailed && test.Services.IsFailedDelivery(test.Item(failed.Id))
                && test.Store.Load().Outbox.Single().DeliveryFailed,
                "A message stays durably failed and warning-icon eligible after retry-all verification is interrupted by pausing");
        }

        using (var test = new Fixture()) {
            var failed = test.Entry() with { SubmittedAt = test.Now.AddMinutes(-1) };
            var newestPending = test.Entry() with { SubmittedAt = test.Now, Message = "Newest pending synthetic message",
                Status = DeliveryStatus.Pending, ErrorKind = "", Attempts = 0, RetryProtected = false };
            var completed = test.Entry() with { SubmittedAt = test.Now.AddMinutes(1), Status = DeliveryStatus.Sent, ErrorKind = "" };
            test.SetEntries([failed, completed, newestPending]);
            var promptId = await test.Services.PrepareLatestUnsentReflectionAsync();
            var prompt = test.Engine.Snapshot.Prompts.Single();
            check(prompt.Id == promptId && prompt.RetryOutboxId == newestPending.Id && prompt.Draft == newestPending.Message,
                "The service opens the most recent unsent message instead of the earliest failure or a newer completed message");
            check(test.Services.IsFailedDelivery(test.Item(failed.Id)) && !test.Services.IsFailedDelivery(test.Item(newestPending.Id))
                && test.Item(failed.Id) == failed && test.Receiver.Pings == 0,
                "Opening a newer pending message leaves the failure-only warning scope and older failed entry unchanged");
        }
    }

    private sealed class Fixture : IDisposable
    {
        public readonly DateTimeOffset Now = new(2026, 10, 7, 16, 0, 0, TimeSpan.Zero);
        public readonly string Root = Path.Combine(Path.GetTempPath(), "ReflectionTimer-RetryAll-" + Guid.NewGuid().ToString("N"));
        public string CsvDirectory => Path.Combine(Root, "csv");
        public readonly ConnectionSettings Connection = new() {
            SheetUrl = "https://docs.google.com/spreadsheets/d/abcdefghijklmnopqrstuvwxyz/edit",
            WebAppUrl = "https://script.google.com/macros/s/syntheticRetryAll/exec", ApiToken = new string('a', 64),
            SheetMode = "fixed", SheetName = "Synthetic only"
        };
        public readonly Store Store;
        public TimerEngine Engine = null!;
        public PreviewServices Services = null!;
        public readonly Receiver Receiver = new();
        public readonly Audio Audio = new();
        public IReadOnlyList<OutboxItem> Items => Engine.Snapshot.Outbox;
        public OutboxItem Item(Guid id) => Items.Single(x => x.Id == id);
        public Fixture() { Store = new(new()); }
        public OutboxItem Entry() => new() { Message = "Synthetic retry-all reflection", SubmittedAt = Now,
            SheetUrl = Connection.SheetUrl, ReceiverUrl = Connection.WebAppUrl, SheetMode = Connection.SheetMode,
            SheetName = Connection.SheetName, DurationSeconds = 900, ActualDurationSeconds = 600,
            Status = DeliveryStatus.NeedsReview, ErrorKind = "rejected", Attempts = 1, RetryProtected = true };
        public void SetEntries(OutboxItem[] entries, int volume = 0)
        {
            Store.State = new() { Connection = Connection, ExtensionDisabledConfirmed = true, LoggingEnabled = false,
                Timer = new() { Volume = volume }, Csv = new() { Enabled = true, Directory = CsvDirectory }, Outbox = entries.ToList() };
            Engine = new(Store, () => Now);
            Services = new(Engine, Root, new SheetsClient(Receiver), Audio, speech: new SilentSpeech());
        }
        public void Dispose()
        {
            Services?.Dispose();
            var full = Path.GetFullPath(Root);
            var parent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!full.StartsWith(parent + "ReflectionTimer-RetryAll-", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Unexpected retry-all fixture cleanup path.");
            if (Directory.Exists(full)) Directory.Delete(full, true);
        }
    }
    private sealed class Store(AppState initial) : IStateStore
    {
        public AppState State = initial;
        public int FailedAcknowledgments;
        public AppState Load() => DataJson.Clone(State);
        public void Save(AppState value)
        {
            if (FailedAcknowledgments > 0 && value.Outbox.Any(x => x.Status == DeliveryStatus.Sent
                && State.Outbox.Any(previous => previous.Id == x.Id && previous.Status != DeliveryStatus.Sent))) {
                FailedAcknowledgments--; throw new IOException("Synthetic retry-all disk failure");
            }
            State = DataJson.Clone(value);
        }
    }
    private sealed class Receiver : HttpMessageHandler
    {
        public int Pings;
        public bool PingOffline;
        public readonly List<(Guid Id, string Message)> Writes = [];
        public readonly HashSet<Guid> RejectedIds = [];
        public readonly HashSet<Guid> UnavailableIds = [];
        public Action? OnPing;
        public TaskCompletionSource? WriteGate;
        public readonly TaskCompletionSource WriteEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var root = body.RootElement; var action = root.GetProperty("action").GetString();
            var success = true;
            if (action == "ping") {
                Pings++;
                if (PingOffline) throw new HttpRequestException("Synthetic offline preflight");
                OnPing?.Invoke();
            } else {
                var id = root.GetProperty("requestId").GetGuid();
                Writes.Add((id, root.GetProperty("message").GetString()!)); WriteEntered.TrySetResult();
                if (WriteGate is not null) await WriteGate.Task.WaitAsync(cancellationToken);
                if (UnavailableIds.Contains(id)) return new(HttpStatusCode.ServiceUnavailable);
                success = !RejectedIds.Contains(id);
            }
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new {
                success, code = "rejected", target = "Synthetic only", sheet = "Synthetic only", deliveryProtocol = SheetsClient.DeliveryProtocol,
                supportsCheckIns = true, supportsStopwatch = true, supportsAutoSent = true, supportsPauses = true
            })) };
        }
    }
    private sealed class Audio : IAlertAudioBackend
    {
        public int Calls;
        public Task PlayAsync(string path, AudioLevel level, CancellationToken cancellationToken) { Interlocked.Increment(ref Calls); return Task.CompletedTask; }
    }
}
