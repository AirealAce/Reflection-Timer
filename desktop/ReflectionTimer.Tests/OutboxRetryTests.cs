using System.Collections.Immutable;
using System.Text.Json;
using ReflectionTimer.Accessible;
using ReflectionTimer.Core;

static class OutboxRetryTests
{
    internal static void Run(Action<bool,string> check)
    {
        var now = DateTimeOffset.Parse("2026-10-07T11:00:00-04:00");
        var original = new OutboxItem {
            Message = "Original reflection", SubmittedAt = now.AddHours(-1), DurationSeconds = 900,
            ActualDurationSeconds = 720, EndedEarly = true, EarlyEndReason = "An interruption",
            SessionId = Guid.NewGuid(), Status = DeliveryStatus.NeedsReview, ErrorKind = "network",
            Attempts = 1, RetryProtected = true, SheetsRequested = true,
            CsvStatus = CsvDeliveryStatus.NeedsReview, CsvError = "csv_storage", CsvAttempts = 1,
            CsvEntryId = Guid.NewGuid(), CsvDirectory = Path.Combine(Path.GetTempPath(), "reflection-original"),
            SheetUrl = "https://docs.google.com/spreadsheets/d/test/edit",
            ReceiverUrl = "https://script.google.com/macros/s/test/exec",
            SheetMode = "fixed", SheetName = "Historical",
            Pauses = ImmutableList.Create(new SessionPause(Guid.NewGuid(), now.AddHours(-1).ToUnixTimeMilliseconds(), 7000, "A pause"))
        };
        TimerEngine Engine(params OutboxItem[] entries) => new(new MemoryStore { State = new() {
            Outbox = entries.ToList(), Connection = new() { WebAppUrl = original.ReceiverUrl },
            Csv = new() { Directory = Path.Combine(Path.GetTempPath(), "reflection-current") }
        } }, () => now);
        bool Rejects(Action action) { try { action(); return false; } catch (ArgumentException) { return true; } }
        bool SameEntry(OutboxItem left, OutboxItem right) => JsonSerializer.Serialize(left) == JsonSerializer.Serialize(right);

        var newer = original with { Id = Guid.NewGuid(), SubmittedAt = now.AddMinutes(-20) };
        var complete = original with { Id = Guid.NewGuid(), SubmittedAt = now, Status = DeliveryStatus.Sent, CsvStatus = CsvDeliveryStatus.Saved };
        var sending = newer with { Id = Guid.NewGuid(), SubmittedAt = now.AddMinutes(-5), Status = DeliveryStatus.Sending };
        check(OutboxItem.LatestUnsent([complete, newer, original, sending])?.Id == newer.Id,
            "Latest unsent chooses the newest unfinished submission and excludes completed or in-flight entries");
        check(OutboxItem.LatestUnsent([complete, original with { LocalOnly = true }, sending]) is null,
            "Local previews and successful entries are never reopened as unsent");
        check(original.SheetsFailed && original.CsvFailed && original.DeliveryFailed,
            "Failed destinations remain independently identifiable");

        var engine = Engine(original);
        var promptId = engine.OpenUnsentReflection(original.Id);
        var prompt = engine.Snapshot.Prompts.Single();
        check(promptId != original.Id && prompt.RetryOutboxId == original.Id && prompt.CheckInSessionId is null,
            "A retry editor has a distinct durable draft ID without an active session link");
        check(prompt.Draft == original.Message && prompt.EarlyEndReason == original.EarlyEndReason
            && prompt.Pauses.SequenceEqual(original.Pauses) && prompt.SessionId == original.SessionId,
            "Opening an unsent message preserves its text, reason, pauses and original session metadata");
        check(engine.OpenUnsentReflection(original.Id) == promptId && engine.Snapshot.Prompts.Count == 1,
            "Opening the same unfinished message reuses its saved editor");
        check(engine.IsOutboxBeingEdited(original.Id) && engine.BeginUpload(true) is null,
            "An open or saved retry editor holds automatic Sheets delivery");
        check(Rejects(() => engine.RetryDestinations(original.Id, true, true)),
            "An external retry cannot bypass a held editor");
        check(Rejects(() => engine.MarkAlreadySent(original.Id)) && engine.IsOutboxBeingEdited(original.Id),
            "Already in Sheet cannot complete a message while its saved retry editor still owns it");
        check(!engine.AutoSendReflection(promptId) && SameEntry(engine.Snapshot.Outbox.Single(), original),
            "New session completion cannot auto-send an unfinished retry editor");
        engine.SaveDraft(promptId, "Saved retry draft", "Updated reason");
        engine.SaveReflectionForLater(promptId, "Saved retry draft", "Updated reason");
        check(SameEntry(engine.Snapshot.Outbox.Single(), original) && engine.Snapshot.Prompts.Single().ContinuationSeparator is null,
            "Saving retry text keeps the original Outbox record durable and adds no continuation delimiter");
        engine.SkipPrompt(promptId);
        check(!engine.IsOutboxBeingEdited(original.Id) && SameEntry(engine.Snapshot.Outbox.Single(), original),
            "Skipping a retry editor releases the hold without losing the original message");
        var emptySession = new PreviewSession(new MemoryStore { State = new() { Outbox = [original] } }, () => now);
        var emptyId = emptySession.Engine.OpenUnsentReflection(original.Id);
        emptySession.Engine.SaveDraft(emptyId, "", "", new Dictionary<Guid,string> { [original.Pauses[0].Id] = "" });
        var emptyView = JsonSerializer.Serialize(emptySession.View());
        check(emptyView.Length > 0 && emptySession.Engine.RetryReflectionNeedsConfirmation(emptyId, "", ""),
            "An empty retry draft remains renderable and its confirmation state can be read without submission validation");
        check(Rejects(() => emptySession.Engine.QueueReflection(emptyId, "", "")),
            "Allowing an empty retry draft to render does not allow an empty submission");
        emptySession.Engine.SkipPrompt(emptyId);
        check(SameEntry(emptySession.Engine.Snapshot.Outbox.Single(), original),
            "Skipping an emptied retry draft retains the original unsent message");
        var orphan = new PreviewSession(new MemoryStore { State = new() { Prompts = [prompt] } }, () => now);
        check(JsonSerializer.Serialize(orphan.View()).Length > 0
            && !orphan.Engine.RetryReflectionNeedsConfirmation(prompt.Id, prompt.Draft, prompt.EarlyEndReason),
            "A recovered retry draft with a missing Outbox record remains readable without crashing any viewer");
        check(Rejects(() => orphan.Engine.QueueReflection(prompt.Id, prompt.Draft, prompt.EarlyEndReason)),
            "A missing Outbox record cannot be silently recreated or submitted by its orphan editor");
        var normalId = Guid.NewGuid();
        var oldHeld = original with { Status = DeliveryStatus.Sent, CsvStatus = CsvDeliveryStatus.Saved, SubmittedAt = now.AddDays(-5) };
        var recentSent = Enumerable.Range(0, 200).Select(index => oldHeld with {
            Id = Guid.NewGuid(), SubmittedAt = now.AddMinutes(-index)
        }).ToList();
        recentSent.Add(oldHeld);
        var pruneEngine = new TimerEngine(new MemoryStore { State = new() {
            Outbox = recentSent, Prompts = [prompt, new(normalId, now.ToUnixTimeMilliseconds(), 900, 50, false, "New reflection")]
        } }, () => now);
        pruneEngine.QueueReflection(normalId, "New reflection");
        check(pruneEngine.Snapshot.Outbox.Any(item => item.Id == original.Id) && pruneEngine.IsOutboxBeingEdited(original.Id),
            "Sent-history pruning never removes a record still referenced by a retry editor");

        var pending = original with { Status = DeliveryStatus.Pending };
        var scopedOther = pending with { Id = Guid.NewGuid() };
        engine = Engine(pending, scopedOther);
        var held = engine.OpenUnsentReflection(pending.Id);
        check(engine.BeginUpload(true, new HashSet<Guid> { pending.Id }) is null,
            "A scoped Sheets retry excludes held entries");
        check(engine.BeginUpload(true, new HashSet<Guid> { scopedOther.Id })?.Id == scopedOther.Id,
            "A scoped Sheets retry sends only its selected candidate");
        engine.SkipPrompt(held);
        check(engine.BeginUpload(true, new HashSet<Guid> { pending.Id })?.Id == pending.Id,
            "Closing a held editor restores normal delivery eligibility");

        foreach (var delivered in new[] { "csv", "sheets" }) {
            var partial = delivered == "csv" ? original with { CsvStatus = CsvDeliveryStatus.Saved, CsvFile = "saved.csv" }
                : original with { Status = DeliveryStatus.Sent, SavedTab = "Historical" };
            engine = Engine(partial);
            var retried = engine.RetryDestinations(partial.Id, sheets: delivered == "csv", csv: delivered == "sheets");
            check(retried.Id == partial.Id && retried.CsvEntryId == partial.CsvEntryId
                && (delivered == "csv" ? retried.CsvStatus == CsvDeliveryStatus.Saved && retried.CsvFile == "saved.csv"
                    : retried.Status == DeliveryStatus.Sent && retried.SavedTab == "Historical"),
                "Retrying the failed destination retains the already successful " + delivered + " export and identities");
            check(delivered == "csv" ? retried.CsvError == partial.CsvError : retried.ErrorKind == partial.ErrorKind,
                "A destination-scoped retry does not clear another destination's diagnostic state: " + delivered);
        }
        engine = Engine(original);
        var csvOnly = engine.RetryDestinations(original.Id, sheets: false, csv: true);
        check(csvOnly.Status == original.Status && csvOnly.ErrorKind == original.ErrorKind && csvOnly.Attempts == original.Attempts,
            "CSV-only retry does not queue or reset a failed Sheets request");
        var sheetsOnly = engine.RetryDestinations(original.Id, sheets: true, csv: false);
        check(sheetsOnly.CsvAttempts == csvOnly.CsvAttempts && sheetsOnly.CsvDirectory == csvOnly.CsvDirectory,
            "Sheets-only retry does not redirect or reset CSV delivery");
        check(sheetsOnly.SheetsFailed && sheetsOnly.CsvFailed && sheetsOnly.DeliveryFailed,
            "Requesting retry preserves failure visibility until an actual delivery attempt");
        var upload = engine.BeginUpload(true)!;
        check(upload.ErrorKind.Length == 0 && !upload.SheetsFailed && upload.CsvFailed,
            "Beginning a Sheets attempt clears only its previous failure while CSV still needs attention");
        engine.FinishUpload(upload.Id, true, tab: "Historical");
        engine.FinishCsv(upload.Id, new(true, "saved.csv"));
        check(!engine.Snapshot.Outbox.Single().DeliveryFailed && engine.Snapshot.Outbox.Single().DeliveryComplete,
            "Successful destination acknowledgments clear the retained retry warning");
        var notRequested = original with { SheetsRequested = false, CsvStatus = CsvDeliveryStatus.NotRequested };
        engine = Engine(notRequested);
        check(SameEntry(engine.RetryDestinations(notRequested.Id, true, true), notRequested),
            "Retry cannot enable a destination absent from the original request");

        foreach (var error in new[] { "write_uncertain", "id_conflict" }) {
            var uncertain = original with { ErrorKind = error, CsvStatus = CsvDeliveryStatus.Saved, CsvEntryId = null };
            engine = Engine(uncertain);
            check(uncertain.RetryReviewRequired && Rejects(() => engine.RetryDestinations(uncertain.Id, true, false)),
                "An uncertain Sheets request retains explicit review: " + error);
            var confirmed = engine.RetryDestinations(uncertain.Id, true, false, confirmed: true);
            check(confirmed.Id != uncertain.Id && confirmed.CsvEntryId == uncertain.Id && confirmed.CsvStatus == CsvDeliveryStatus.Saved,
                "Confirmed uncertain Sheets retry changes only its request identity and retains legacy CSV identity: " + error);
        }
        var legacy = original with { RetryProtected = false };
        engine = Engine(legacy);
        check(legacy.RetryReviewRequired && Rejects(() => engine.RetryDestinations(legacy.Id, true, false)),
            "An attempted legacy receiver request cannot silently bypass review");
        var conflict = original with { Status = DeliveryStatus.Sent, CsvError = "csv_conflict" };
        engine = Engine(conflict);
        check(conflict.RetryReviewRequired && Rejects(() => engine.RetryDestinations(conflict.Id, false, true)),
            "CSV payload conflicts require review before creating another row");
        var conflictRetry = engine.RetryDestinations(conflict.Id, false, true, confirmed: true);
        check(conflictRetry.Id == conflict.Id && conflictRetry.CsvEntryId != conflict.CsvEntryId && conflictRetry.Status == DeliveryStatus.Sent,
            "Confirmed CSV conflict changes only the CSV identity");

        engine = Engine(original);
        engine.Start(120, false, 50);
        var newerSession = engine.CurrentTimer;
        promptId = engine.OpenUnsentReflection(original.Id);
        check(!engine.RetryReflectionNeedsConfirmation(promptId, original.Message, original.EarlyEndReason),
            "An unchanged protected request can retry with its original delivery identity");
        check(!engine.QueueReflection(promptId, original.Message, original.EarlyEndReason, endSession: true),
            "Retrying an old reflection cannot report completion of the current session");
        var unchanged = engine.Snapshot.Outbox.Single();
        check(unchanged.Id == original.Id && unchanged.CsvEntryId == original.CsvEntryId && engine.Snapshot.Outbox.Count == 1,
            "Unchanged retry replaces its Outbox state without adding a duplicate");
        check(unchanged.SubmittedAt == original.SubmittedAt && unchanged.SheetUrl == original.SheetUrl
            && unchanged.SheetName == original.SheetName && unchanged.CsvDirectory == original.CsvDirectory
            && unchanged.Pauses.SequenceEqual(original.Pauses),
            "Retry editor preserves the historical submission time, destinations and pause metadata");
        check(engine.CurrentTimer == newerSession && engine.Snapshot.Prompts.Count == 0,
            "Retrying removes only its editor and leaves a newer running session untouched");

        foreach (var delivered in new[] { "neither", "csv", "sheets" }) {
            var partial = delivered == "csv" ? original with { CsvStatus = CsvDeliveryStatus.Saved }
                : delivered == "sheets" ? original with { Status = DeliveryStatus.Sent } : original;
            engine = Engine(partial);
            promptId = engine.OpenUnsentReflection(partial.Id);
            check(engine.RetryReflectionNeedsConfirmation(promptId, "Changed reflection", "Updated reason"),
                "Changing an attempted pending payload requires confirmation: prior success=" + delivered);
            check(Rejects(() => engine.QueueReflection(promptId, "Changed reflection", "Updated reason")),
                "An unconfirmed changed retry preserves the original message: prior success=" + delivered);
            check(SameEntry(engine.Snapshot.Outbox.Single(), partial) && engine.IsOutboxBeingEdited(partial.Id),
                "Declining retry confirmation leaves both the original and editable draft available: prior success=" + delivered);
            engine.QueueReflection(promptId, "Changed reflection", "Updated reason", retryConfirmed: true);
            var changed = engine.Snapshot.Outbox.Single();
            check(changed.Message == "Changed reflection" && changed.EarlyEndReason == "Updated reason"
                && changed.SubmittedAt == partial.SubmittedAt && changed.SessionId == partial.SessionId,
                "Confirmed retry edits preserve the original session and timestamp: prior success=" + delivered);
            check((delivered == "sheets" ? changed.Id == partial.Id && changed.Status == DeliveryStatus.Sent : changed.Id != partial.Id)
                && (delivered == "csv" ? changed.CsvEntryId == partial.CsvEntryId && changed.CsvStatus == CsvDeliveryStatus.Saved
                    : changed.CsvEntryId != partial.CsvEntryId),
                "Changed payload obtains identities only for outstanding destinations: prior success=" + delivered);
        }
        var firstAttempt = original with { Attempts = 0, RetryProtected = false, CsvAttempts = 0, Status = DeliveryStatus.Pending, ErrorKind = "" };
        engine = Engine(firstAttempt);
        promptId = engine.OpenUnsentReflection(firstAttempt.Id);
        check(!engine.RetryReflectionNeedsConfirmation(promptId, "Before the first attempt", original.EarlyEndReason),
            "Editing a never-attempted entry needs no duplicate-write confirmation");
        engine.QueueReflection(promptId, "Before the first attempt", original.EarlyEndReason);
        check(engine.Snapshot.Outbox.Single().Message == "Before the first attempt", "A never-attempted edit queues successfully");

        engine = Engine(original);
        promptId = engine.OpenUnsentReflection(original.Id);
        var pauseReason = new Dictionary<Guid,string> { [original.Pauses[0].Id] = "Changed pause reason" };
        check(engine.RetryReflectionNeedsConfirmation(promptId, original.Message, original.EarlyEndReason, pauseReason),
            "Pause-reason edits are part of the delivery payload and require review after an attempt");
        engine.QueueReflection(promptId, original.Message, original.EarlyEndReason, pauseReasons: pauseReason, retryConfirmed: true);
        check(engine.Snapshot.Outbox.Single().Pauses[0].Reason == "Changed pause reason",
            "A confirmed retry preserves edited pause reasons");

        var store = new MemoryStore { State = new() { Outbox = [original] } };
        engine = new(store, () => now);
        promptId = engine.OpenUnsentReflection(original.Id);
        var before = JsonSerializer.Serialize(engine.Snapshot);
        store.Fail = true;
        try { engine.QueueReflection(promptId, original.Message, original.EarlyEndReason); check(false, "Failed retry storage must throw"); }
        catch (IOException) { }
        check(JsonSerializer.Serialize(engine.Snapshot) == before,
            "A failed durable retry save retains the original delivery record and editor atomically");
    }
}
