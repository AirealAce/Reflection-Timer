using ReflectionTimer.Accessible;
using ReflectionTimer.Core;
using ReflectionTimer.Desktop;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

static class PromotionTests
{
    internal static async Task Run(Action<bool,string> check)
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ReflectionTimerDesktop");
        var suffix = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root)))[..16];
        check(PreviewStartup.DirectoryFor(null) == Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".reflection-timer")
            && PreviewStartup.MutexName(null) == @"Local\ReflectionTimerDesktop-" + suffix
            && PreviewStartup.ShowEventName(null) == @"Local\ReflectionTimerDesktopShow-" + suffix, "Primary app uses launcher-independent storage and retains original single-instance signals");
        check(PreviewStartup.DirectoryFor("review-03") != root && PreviewStartup.MutexName("review-03") == @"Local\ReflectionTimerAccessibilityPreview-review-03",
            "Named test profiles remain isolated and compatible with earlier previews");
        check(PreviewStartup.ValueName(null) == "Reflection Timer Desktop"
            && PreviewStartup.Command(@"C:\Timer App\ReflectionTimer.exe", null) == "\"C:\\Timer App\\ReflectionTimer.exe\" --tray"
            && PreviewStartup.Parse(["--check-connection"]).CheckConnection, "Primary startup registration and read-only connection command retain the desktop identity");
        var connection = new ConnectionSettings { SheetUrl = "https://docs.google.com/spreadsheets/d/syntheticPromotionSpreadsheet/edit",
            WebAppUrl = "https://script.google.com/macros/s/syntheticPromotionReceiver/exec", ApiToken = new string('p', 64), SheetMode = "fixed", SheetName = "Existing tab" };
        var now = DateTimeOffset.Now;
        var existing = new AppState { Connection = connection, ExtensionDisabledConfirmed = true,
            Theme = AppColorTheme.HighContrast, ShowFloatingTimer = false, StartAtLogin = true, LoggingEnabled = false,
            SetupDraft = connection with { SheetName = "Unfinished setup" }, SetupDraftUsesExistingReceiver = true,
            Timer = new() { DurationSeconds = 1200, RemainingSeconds = 500, PausedRemainingMilliseconds = 499500 },
            Prompts = [new(Guid.NewGuid(), now.ToUnixTimeMilliseconds(), 900, 50, false, "An existing private draft")],
            Schedules = [new(Guid.NewGuid(), now.AddDays(2).ToUnixTimeMilliseconds(), 300, true, 35)],
            Outbox = [new() { Message = "Existing sent reflection", Status = DeliveryStatus.Sent, Attempts = 1,
                SheetUrl = connection.SheetUrl, ReceiverUrl = connection.WebAppUrl, SheetMode = "fixed", SheetName = connection.SheetName, RetryProtected = true },
                new() { Message = "Saved before setup", Status = DeliveryStatus.Pending },
                new() { Message = "Old sample", LocalOnly = true, Status = DeliveryStatus.Pending }] };
        var directory = Path.Combine(Path.GetTempPath(), "ReflectionTimer-Promotion-" + Guid.NewGuid().ToString("N"));
        try {
            var store = new EncryptedStore(directory); store.Save(existing);
            var bytes = File.ReadAllBytes(Path.Combine(directory, "state.dat"));
            var session = new PreviewSession(store, () => now);
            check(JsonSerializer.Serialize(session.Engine.Snapshot, DataJson.Options) == JsonSerializer.Serialize(existing, DataJson.Options)
                && bytes.SequenceEqual(File.ReadAllBytes(Path.Combine(directory, "state.dat"))),
                "Opening an original encrypted profile preserves connection, preferences, paused time, drafts, schedules and Outbox without rewriting it");
            var offline = new PreviewSession(new MemoryStore());
            var id = offline.Engine.TestPrompt();
            offline.Execute("queue", JsonSerializer.SerializeToElement(new { id, text = "Queued offline", reason = "" }));
            check(!offline.Engine.Snapshot.Outbox.Single().LocalOnly, "A new primary reflection saved offline can later be delivered");
            var receiver = new Receiver();
            using var services = new PreviewServices(offline.Engine, directory, new SheetsClient(receiver));
            offline.Engine.SetAppVolume(0);
            services.SaveConnection(connection, false);
            var bound = offline.Engine.Snapshot.Outbox.Single();
            check(bound.Id == id && bound.SheetUrl == connection.SheetUrl && bound.ReceiverUrl == connection.WebAppUrl
                && bound.SheetName == connection.SheetName, "First connection binds offline entries without replacing their request IDs");
            await services.Sync(true);
            check(receiver.Writes == 0, "Paused Sheets delivery remains paused after promotion");
            services.SaveConnection(connection, true);
            await services.Sync(true);
            check(receiver.Writes == 1 && receiver.Id == id && offline.Engine.Snapshot.Outbox.Single().Status == DeliveryStatus.Sent,
                "An offline primary reflection uploads exactly once to its configured Sheet");
            session.Engine.SaveSettings(connection, false, true, true);
            var restored = new PreviewSession(store, () => now).Engine.Snapshot;
            check(restored.Outbox[1].SheetUrl == connection.SheetUrl && !restored.Outbox[1].LocalOnly && restored.Outbox[2].LocalOnly
                && restored.Outbox[2].SheetUrl == "", "Existing offline entries are deliverable while earlier preview samples stay local");
        }
        finally {
            foreach (var name in new[] { "state.dat", "state.dat.bak", "diagnostics.dat", "diagnostics.dat.bak" })
                File.Delete(Path.Combine(directory, name));
            if (Directory.Exists(directory)) Directory.Delete(directory);
        }
    }
    private sealed class Receiver : HttpMessageHandler
    {
        internal int Writes; internal Guid Id;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellation));
            if (body.RootElement.GetProperty("action").GetString() == "appendReflection") {
                Writes++; Id = body.RootElement.GetProperty("requestId").GetGuid();
            }
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new {
                success = true, target = "syntheticPromotionSpreadsheet", sheet = "Existing tab", deliveryProtocol = SheetsClient.DeliveryProtocol, supportsCheckIns = true })) };
        }
    }
}
