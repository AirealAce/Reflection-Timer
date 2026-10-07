namespace ReflectionTimer.Core;

public sealed partial class TimerEngine
{
    public bool IsOutboxBeingEdited(Guid id)
    {
        lock (gate) return state.Prompts.Any(prompt => prompt.RetryOutboxId == id);
    }

    public Guid OpenUnsentReflection(Guid outboxId)
    {
        lock (gate) {
            var item = state.Outbox.SingleOrDefault(entry => entry.Id == outboxId)
                ?? throw new ArgumentException("That saved message is no longer in Outbox.");
            if (item.LocalOnly || item.DeliveryComplete) throw new ArgumentException("That message has no unfinished delivery.");
            if (item.Status == DeliveryStatus.Sending) throw new ArgumentException("That message is still sending.");
            var existing = state.Prompts.SingleOrDefault(prompt => prompt.RetryOutboxId == outboxId);
            if (existing is not null) return existing.Id;
            var id = Guid.NewGuid();
            Change("prompt.draftSaved", next => next.Prompts.Add(new(id, item.SubmittedAt.ToUnixTimeMilliseconds(),
                item.DurationSeconds, next.Timer.Volume, item.IsTest, item.Message) {
                    RetryOutboxId = item.Id, Mode = item.Mode, SessionId = item.SessionId,
                    ActualDurationSeconds = item.ActualDurationSeconds, EndedEarly = item.EndedEarly,
                    EarlyEndReason = item.EarlyEndReason, IsCheckIn = item.IsCheckIn, Pauses = item.Pauses
                }), id);
            return id;
        }
    }

    // Queue only the destinations named by the caller. A destination already
    // delivered, or never requested for this entry, must remain untouched.
    public OutboxItem RetryDestinations(Guid id, bool sheets, bool csv, bool confirmed = false)
    {
        lock (gate) {
            var item = state.Outbox.SingleOrDefault(entry => entry.Id == id)
                ?? throw new ArgumentException("That saved message is no longer in Outbox.");
            if (item.LocalOnly) throw new ArgumentException("This entry stays local.");
            if (item.Status == DeliveryStatus.Sending) throw new ArgumentException("This reflection is still sending.");
            if (IsOutboxBeingEdited(id)) throw new ArgumentException("Finish editing this message in its reflection window before retrying it.");
            sheets &= item.WantsSheets && item.Status != DeliveryStatus.Sent;
            csv &= item.CsvStatus is CsvDeliveryStatus.Pending or CsvDeliveryStatus.NeedsReview;
            if (!sheets && !csv) return item;
            var updated = RequeueDestinations(state, item, sheets, csv, confirmed);
            Change("upload.retryRequested", next => next.Outbox = next.Outbox
                .Select(entry => entry.Id == id ? updated : entry).ToList(), id);
            return updated;
        }
    }

    private static OutboxItem RequeueDestinations(AppState value, OutboxItem item, bool sheets, bool csv, bool confirmed, bool useCurrentCsvDirectory = true)
    {
        var uncertain = sheets && item.ErrorKind is "write_uncertain" or "id_conflict";
        var unsafeLegacy = sheets && item.Attempts > 0 && !item.RetryProtected;
        var csvConflict = csv && item.CsvError == "csv_conflict";
        if ((uncertain || unsafeLegacy || csvConflict) && !confirmed)
            throw new ArgumentException("Review the existing logging destination and confirm before retrying a message that may already be saved.");
        if (csv) item = item with {
            CsvStatus = CsvDeliveryStatus.Pending, CsvNextAttemptAt = null, CsvAttempts = 0,
            CsvDirectory = useCurrentCsvDirectory ? value.Csv.ResolvedDirectory : item.CsvDirectory,
            CsvEntryId = csvConflict ? Guid.NewGuid() : item.CsvEntryId
        };
        if (sheets) item = item with {
            Status = DeliveryStatus.Pending, NextAttemptAt = null,
            Id = uncertain ? Guid.NewGuid() : item.Id,
            CsvEntryId = uncertain ? item.CsvEntryId ?? item.Id : item.CsvEntryId,
            Attempts = uncertain ? 0 : item.Attempts
        };
        return item;
    }

    public bool RetryReflectionNeedsConfirmation(Guid promptId, string text, string? earlyEndReason = null,
        IReadOnlyDictionary<Guid, string>? pauseReasons = null)
    {
        lock (gate) {
            var prompt = state.Prompts.SingleOrDefault(entry => entry.Id == promptId)
                ?? throw new ArgumentException("That reflection is no longer pending.");
            if (prompt.RetryOutboxId is not { } outboxId) return false;
            var item = state.Outbox.SingleOrDefault(entry => entry.Id == outboxId);
            if (item is null) return false; // Rendering a recovered orphan draft must remain possible.
            var edited = RetryContents(prompt, item, text, earlyEndReason, pauseReasons, validateResponse: false);
            return NeedsRetryConfirmation(item, edited);
        }
    }

    private static OutboxItem RetryContents(ReflectionPrompt prompt, OutboxItem item, string text, string? reason,
        IReadOnlyDictionary<Guid, string>? pauseReasons, bool validateResponse = true)
    {
        text = ReflectionDrafts.Content(prompt, text).Trim();
        reason = prompt.EndedEarly ? (reason ?? prompt.EarlyEndReason).Trim() : item.EarlyEndReason;
        var pauses = MergePauseReasons(prompt.Pauses, pauseReasons);
        if (text.Length > 5000 || reason.Length > 1000)
            throw new ArgumentException("Keep the reflection within 5,000 characters and the reason within 1,000 characters.");
        if (validateResponse && text.Length == 0 && !item.AutoSent && !pauses.Any(pause => !string.IsNullOrWhiteSpace(pause.Reason)))
            throw new ArgumentException("Write a reflection between 1 and 5,000 characters.");
        return item with { Message = text, EarlyEndReason = reason, Pauses = pauses };
    }

    private static bool RetryContentsChanged(OutboxItem original, OutboxItem edited) =>
        original.Message != edited.Message || original.EarlyEndReason != edited.EarlyEndReason
        || !original.Pauses.SequenceEqual(edited.Pauses);

    private static bool NeedsRetryConfirmation(OutboxItem original, OutboxItem edited)
    {
        var sheets = original.WantsSheets && original.Status != DeliveryStatus.Sent;
        var csv = original.CsvStatus is CsvDeliveryStatus.Pending or CsvDeliveryStatus.NeedsReview;
        return original.RetryReviewRequired
            || RetryContentsChanged(original, edited) && (sheets && (original.Attempts > 0 || original.RetryProtected)
                || csv && original.CsvAttempts > 0);
    }

    private void QueueRetryReflection(ReflectionPrompt prompt, string text, string? reason,
        IReadOnlyDictionary<Guid, string>? pauseReasons, bool confirmed)
    {
        var id = prompt.RetryOutboxId!.Value;
        var original = state.Outbox.SingleOrDefault(item => item.Id == id)
            ?? throw new ArgumentException("That saved message is no longer in Outbox.");
        if (original.LocalOnly || original.DeliveryComplete) throw new ArgumentException("That message has no unfinished delivery.");
        if (original.Status == DeliveryStatus.Sending) throw new ArgumentException("That message is still sending.");
        var edited = RetryContents(prompt, original, text, reason, pauseReasons);
        if (NeedsRetryConfirmation(original, edited) && !confirmed)
            throw new ArgumentException("This message may already exist at a logging destination. Review it and confirm before sending these changes.");
        var sheets = original.WantsSheets && original.Status != DeliveryStatus.Sent;
        var csv = original.CsvStatus is CsvDeliveryStatus.Pending or CsvDeliveryStatus.NeedsReview;
        if (RetryContentsChanged(original, edited)) {
            // Each attempted payload owns its identity. A changed outstanding
            // payload cannot reuse a fingerprint or alter a successful export.
            edited = edited with {
                Id = sheets ? Guid.NewGuid() : original.Id,
                Attempts = sheets ? 0 : original.Attempts,
                RetryProtected = sheets ? false : original.RetryProtected,
                CsvEntryId = csv ? Guid.NewGuid() : original.CsvEntryId ?? original.Id,
                ErrorKind = sheets ? "" : original.ErrorKind
            };
        }
        var updated = RequeueDestinations(state, edited, sheets, csv, confirmed, useCurrentCsvDirectory: false);
        Change("upload.retryRequested", next => {
            next.Outbox = next.Outbox.Select(item => item.Id == id ? updated : item).ToList();
            next.Prompts.RemoveAll(entry => entry.Id == prompt.Id);
        }, id);
    }
}
