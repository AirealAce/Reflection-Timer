namespace ReflectionTimer.Core;

public sealed partial class TimerEngine
{
    public void SaveCsvSettings(CsvSettings settings) => Change("settings.saved", s => {
        var directory = CsvSettings.NormalizeDirectory(settings.Directory);
        s.Csv = settings with { Directory = settings.Directory.Trim().Length == 0 ? "" : directory };
    });
    public void FinishCsv(Guid id, CsvWriteReply reply) => Change(reply.Success ? "csv.saved" : "csv.failed", s => {
        s.Outbox = s.Outbox.Select(item => {
            if (item.Id != id || item.LocalOnly || item.CsvStatus == CsvDeliveryStatus.NotRequested || item.CsvStatus == CsvDeliveryStatus.Saved) return item;
            var attempts = item.CsvAttempts + 1;
            var retry = !reply.Success && reply.Retryable && attempts < 8;
            return item with { CsvStatus = reply.Success ? CsvDeliveryStatus.Saved : retry ? CsvDeliveryStatus.Pending : CsvDeliveryStatus.NeedsReview,
                CsvAttempts = attempts, CsvError = reply.Success ? "" : reply.Error,
                CsvFile = reply.Success ? reply.File : "", CsvNextAttemptAt = retry ? Now + Math.Min(300, 15 * (1 << Math.Clamp(attempts - 1, 0, 5))) * 1000L : null };
        }).ToList();
    }, id);
}
