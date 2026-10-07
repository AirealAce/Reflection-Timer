using ReflectionTimer.Core;

namespace ReflectionTimer.Accessible;

public sealed partial class PreviewServices
{
    public bool RetryAllBusy { get; private set; }
    public event Action? RetryAllStateChanged;

    // A failed connection check precedes per-message append attempts. Include
    // the Sheets entries blocked by it without treating their CSV as failed.
    public bool IsFailedDelivery(OutboxItem item) => item.DeliveryFailed
        || DeliveryIssue is not null && !item.LocalOnly && item.WantsSheets && item.Status == DeliveryStatus.Pending;
    public bool RetryReviewRequired(OutboxItem item) => IsFailedDelivery(item) && item.RetryReviewRequired;
    private bool SheetsFailed(OutboxItem item) => item.SheetsFailed
        || DeliveryIssue is not null && item.WantsSheets && item.Status == DeliveryStatus.Pending;

    public async Task<Guid?> PrepareLatestUnsentReflectionAsync()
    {
        await syncGate.WaitAsync(stop.Token);
        try {
            // Commit a received acknowledgment before deciding that a message
            // is unsent. Opening the editor must not undo a successful upload.
            if (!CompletePendingUpload(true)) throw new IOException("Delivery is still being saved locally. Try opening the message again shortly.");
            var item = OutboxItem.LatestUnsent(engine.Snapshot.Outbox);
            return item is null ? null : engine.OpenUnsentReflection(item.Id);
        } finally { syncGate.Release(); }
    }

    public async Task RetryAllFailed(bool confirmed = false)
    {
        if (RetryAllBusy || stop.IsCancellationRequested) return;
        // Freeze the requested destinations at click time. New work queued
        // during this batch is left to the ordinary delivery loop.
        var candidates = engine.Snapshot.Outbox.Where(IsFailedDelivery)
            .Select(item => new { item.Id, Sheets = SheetsFailed(item), Csv = item.CsvFailed }).ToArray();
        if (!confirmed && engine.Snapshot.Outbox.Any(item => candidates.Any(c => c.Id == item.Id)
                && !engine.IsOutboxBeingEdited(item.Id) && RetryReviewRequired(item)))
            throw new ArgumentException("Check the failed entries in your Sheet or CSV and confirm before retrying uncertain deliveries.");
        RetryAllBusy = true; RetryAllStateChanged?.Invoke();
        try {
            await syncGate.WaitAsync(stop.Token);
            try {
                if (!CompletePendingUpload(true)) return;
                var csvIds = new HashSet<Guid>(); var sheetIds = new List<Guid>(); var held = 0;
                foreach (var candidate in candidates) {
                    var item = engine.Snapshot.Outbox.SingleOrDefault(entry => entry.Id == candidate.Id);
                    if (item is null || item.LocalOnly || item.DeliveryComplete || item.Status == DeliveryStatus.Sending) continue;
                    if (engine.IsOutboxBeingEdited(item.Id)) { held++; continue; }
                    var sheets = candidate.Sheets && SheetsFailed(item);
                    var csvFailed = candidate.Csv && item.CsvFailed;
                    if (!sheets && !csvFailed) continue;
                    var retry = engine.RetryDestinations(item.Id, sheets, csvFailed, confirmed);
                    if (csvFailed) csvIds.Add(retry.Id);
                    if (sheets) sheetIds.Add(retry.Id);
                }
                await SyncCsv(true, csvIds);
                if (sheetIds.Count > 0) await RetrySheets(sheetIds);
                var remaining = engine.Snapshot.Outbox.Count(IsFailedDelivery);
                Announcement?.Invoke($"Retry all finished. {remaining} message{(remaining == 1 ? " still needs" : "s still need")} attention."
                    + (held > 0 ? $" {held} message{(held == 1 ? " is" : "s are")} saved for editing; send from the reflection window when ready." : ""));
            } finally { syncGate.Release(); }
        } finally { RetryAllBusy = false; RetryAllStateChanged?.Invoke(); }
    }

    private async Task RetrySheets(IReadOnlyList<Guid> ids)
    {
        var state = engine.Snapshot; var settings = state.Connection; var revision = connectionRevision;
        if (!state.ExtensionDisabledConfirmed) { Announcement?.Invoke("Sheets delivery is disabled. Failed messages remain saved."); return; }
        if (SheetsClient.Validate(settings) is { } invalid) {
            RecordPreflightFailure(settings, new(false, "settings_required", invalid));
            Announcement?.Invoke(DeliveryIssue!.Message); return;
        }
        SheetUploadBatch batch;
        try { batch = await sheets.BeginBatch(settings, stop.Token); }
        catch when (!stop.IsCancellationRequested) {
            if (ConnectionStillCurrent(settings, revision)) RecordPreflightFailure(settings, new(false, "network", "The connection check could not finish.", Retryable: true));
            Announcement?.Invoke("The connection check could not finish. Failed messages remain saved."); return;
        }
        if (stop.IsCancellationRequested || !ConnectionStillCurrent(settings, revision)) return;
        if (!batch.Capability.Success) { RecordPreflightFailure(settings, batch.Capability); Announcement?.Invoke(DeliveryIssue!.Message); return; }
        ClearPreflightFailure();
        // Each selected destination gets one attempt. An item-specific failure
        // cannot prevent a later failed message from being retried.
        foreach (var id in ids) {
            if (stop.IsCancellationRequested || !ConnectionStillCurrent(settings, revision)) return;
            var item = engine.BeginUpload(batch.Capability.SupportsSafeRetry, new HashSet<Guid> { id });
            if (item is null) continue;
            SheetReply reply;
            try { reply = await batch.Upload(item, stop.Token); }
            catch when (!stop.IsCancellationRequested) { reply = new(false, "interrupted", "Delivery could not be confirmed. Check Outbox before retrying.", Retryable: true); }
            if (stop.IsCancellationRequested) return;
            pendingUploadResult = new(item.Id, item.Attempts, reply);
            if (!CompletePendingUpload(true)) return;
        }
    }
}
