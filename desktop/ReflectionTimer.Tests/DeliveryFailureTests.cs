using ReflectionTimer.Core;

static class DeliveryFailureTests
{
    internal static void Run(Action<bool,string> check)
    {
        var pending=new OutboxItem { Status=DeliveryStatus.Pending, SubmittedAt=DateTimeOffset.Parse("2026-10-07T08:00:00-04:00") };
        check(!pending.DeliveryFailed,"An entry waiting for its first delivery is not a failure");
        var retry=pending with { Id=Guid.NewGuid(), ErrorKind="network", Attempts=1 };
        check(retry.DeliveryFailed,"A failed Sheets delivery remains visible while waiting for automatic retry");
        check((pending with { Status=DeliveryStatus.NeedsReview }).DeliveryFailed,"A Sheets entry needing review appears even without a stored error string");
        check(!(retry with { Status=DeliveryStatus.Sent }).DeliveryFailed,"Successful Sheets delivery clears the warning despite an old error");
        check(!(retry with { Status=DeliveryStatus.Sending }).DeliveryFailed,"A delivery currently retrying is not reported as still failed");
        check(!(retry with { LocalOnly=true }).DeliveryFailed,"Local preview entries never trigger delivery warnings");
        check(!(retry with { SheetsRequested=false }).DeliveryFailed,"An unrequested Sheets destination cannot trigger a warning");
        var csv=pending with { Id=Guid.NewGuid(), SheetsRequested=false, CsvStatus=CsvDeliveryStatus.Pending, CsvError="csv_locked", CsvAttempts=1 };
        check(csv.DeliveryFailed,"A CSV failure waiting to retry also triggers the warning");
        check((csv with { CsvStatus=CsvDeliveryStatus.NeedsReview }).DeliveryFailed,"CSV entries needing review trigger the warning");
        check(!(csv with { CsvStatus=CsvDeliveryStatus.Saved }).DeliveryFailed,"A successful CSV save clears its warning");
        check((csv with { SheetsRequested=true, Status=DeliveryStatus.Sent }).DeliveryFailed,"Sheets success cannot hide a remaining CSV failure");
        check((retry with { CsvStatus=CsvDeliveryStatus.Saved }).DeliveryFailed,"CSV success cannot hide a remaining Sheets failure");
        var later=csv with { Id=Guid.NewGuid(), SubmittedAt=pending.SubmittedAt.AddMinutes(5) };
        check(OutboxItem.EarliestFailed([later,pending,retry])?.Id==retry.Id,"The warning opens the earliest failed submission regardless of list order");
        check(OutboxItem.EarliestFailed([pending,csv with { CsvStatus=CsvDeliveryStatus.Saved }]) is null,"No warning target exists after every delivery issue is resolved");
    }
}
