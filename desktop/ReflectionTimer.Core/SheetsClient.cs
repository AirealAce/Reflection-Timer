using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ReflectionTimer.Core;

public record SheetReply(bool Success, string ErrorKind, string DisplayMessage, string Tab = "", string Target = "",
    bool SupportsSafeRetry = false, bool Retryable = false, bool SupportsCheckIns = false, bool SupportsAutoSent = false, bool SupportsStopwatch = false);

/// <summary>A single send batch, bound to the connection verified when it began.
/// Create a fresh batch for each sync; never retain it as a capability cache.</summary>
public sealed class SheetUploadBatch
{
    public SheetReply Capability { get; }
    private readonly Func<OutboxItem, CancellationToken, Task<SheetReply>> upload;
    internal SheetUploadBatch(SheetReply capability, Func<OutboxItem, CancellationToken, Task<SheetReply>> upload)
    {
        Capability = capability;
        this.upload = upload;
    }
    public Task<SheetReply> Upload(OutboxItem item, CancellationToken cancellation = default)
        => Capability.Success ? upload(item, cancellation) : Task.FromResult(Capability);
}

public sealed class SheetsClient : IDisposable
{
    public const string DeliveryProtocol = "request-id-v1";
    public const int MaxResponseBytes = 64 * 1024;
    private readonly HttpClient client;
    private readonly TimeSpan requestTimeout;
    public SheetsClient(HttpMessageHandler? handler = null, TimeSpan? requestTimeout = null)
    {
        this.requestTimeout = requestTimeout ?? TimeSpan.FromSeconds(25);
        if (this.requestTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(requestTimeout));
        client = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
    }
    public static string? Validate(ConnectionSettings? settings)
    {
        if (settings is null) return "Finish your connection in Guided setup.";
        if (!ConnectionSetup.IsReceiverUrl(settings.WebAppUrl))
            return "Enter the deployed Apps Script URL ending in /exec, not the editor URL.";
        try { _ = ConnectionSetup.SpreadsheetId(settings.SheetUrl); }
        catch (ArgumentException) { return "Enter a valid Google Sheets URL."; }
        if (string.IsNullOrWhiteSpace(settings.ApiToken) || settings.ApiToken.Trim().Length is < 16 or > 512)
            return "Enter your private Reflection API token, or generate one with Guided setup.";
        if (settings.SheetMode != "date" && settings.SheetMode != "fixed") return "Choose automatic dates or a fixed tab.";
        if (settings.SheetMode == "fixed" && (string.IsNullOrWhiteSpace(settings.SheetName) || settings.SheetName.Length > 100)) return "Enter the fixed tab name.";
        return null;
    }
    public Task<SheetReply> Ping(ConnectionSettings settings, CancellationToken cancellation = default, bool isTest = false) => Send(settings, null, cancellation, isTest);
    public Task<SheetReply> Upload(ConnectionSettings settings, OutboxItem item, CancellationToken cancellation = default) => Send(settings, item, cancellation);
    public async Task<SheetUploadBatch> BeginBatch(ConnectionSettings settings, CancellationToken cancellation = default)
    {
        var capability = await Ping(settings, cancellation);
        return new(capability, (item, token) => Send(settings, item, token, verified: capability));
    }
    private async Task<SheetReply> Send(ConnectionSettings settings, OutboxItem? item, CancellationToken cancellation, bool pingTest = false, SheetReply? verified = null)
    {
        if (item?.LocalOnly == true) return new(false, "settings_required", "This entry is local only and cannot be uploaded.");
        var invalid = Validate(settings);
        if (invalid is not null) return new(false, "settings_required", invalid);
        if (item is not null && item.ReceiverUrl.Length > 0 && !ConnectionSetup.SameReceiver(item.ReceiverUrl, settings.WebAppUrl))
            return new(false, "receiver_changed", "This entry belongs to a different receiver. Restore its original connection before retrying.");
        // Independent uploads still verify all needed capabilities. In a batch,
        // one authenticated check covers them all for this exact connection.
        var needsCapability = item is not null && (item.IsCheckIn || item.Mode == SessionMode.Stopwatch || (item.AutoSent && item.Message.Length > 4988));
        var receiver = verified;
        if (needsCapability) {
            receiver ??= await Ping(settings, cancellation);
            if (!receiver.Success) return receiver;
        }
        if (item?.IsCheckIn == true) {
            // Older receivers ignore unknown fields and would silently leave E
            // blank. Check capability before sending any check-in data.
            if (!receiver!.SupportsCheckIns) return new(false, "receiver_update_required", "Update the Apps Script deployment to support check-ins, then retry this saved entry from the Outbox.");
        }
        if (item?.Mode == SessionMode.Stopwatch) {
            // Never let an old receiver invent allotted time or reject elapsed
            // stopwatch time after already modifying a user's sheet.
            if (!receiver!.SupportsStopwatch) return new(false,"receiver_update_required",
                "Update the Apps Script deployment to 2.9.0 or newer for stopwatch entries, then retry from Outbox. Your reflection is saved locally.");
        }
        if(item?.AutoSent==true && item.Message.Length>4988) {
            if(!receiver!.SupportsAutoSent)return new(false,"receiver_update_required","Update the Apps Script deployment to send this full-length reflection with its auto-sent marker. The complete entry is retained in Outbox.");
        }
        var submitted = item?.SubmittedAt ?? DateTimeOffset.Now;
        var body = JsonSerializer.Serialize(new {
            action = item is null ? "ping" : "appendReflection", token = settings.ApiToken.Trim(),
            sheetUrl = item?.SheetUrl ?? settings.SheetUrl, sheetMode = item?.SheetMode ?? settings.SheetMode,
            sheetName = item?.SheetName ?? settings.SheetName, submittedAt = submitted.UtcDateTime.ToString("O"),
            timezoneOffsetMinutes = -(int)submitted.Offset.TotalMinutes, durationSeconds = item?.Mode==SessionMode.Stopwatch ? (int?)null : item?.DurationSeconds ?? 0,
            sessionMode = item?.Mode==SessionMode.Stopwatch ? "stopwatch" : "timer",
            actualDurationSeconds = item?.ActualDurationSeconds, endedEarly = item?.EndedEarly ?? false, isCheckIn = item?.IsCheckIn ?? false,
            earlyEndReason = item?.EarlyEndReason ?? "",
            autoSent = item?.AutoSent ?? false,
            deliveryProtocol = item?.RetryProtected == true ? DeliveryProtocol : null,
            // Keep the marker in the wire text so existing deployments also
            // record auto-send status and accept otherwise blank reflections.
            isTest = item?.IsTest ?? pingTest, message = item?.AutoSent == true ? "[auto-sent]" + (item.Message.Length>0 ? "\n"+item.Message : "") : item?.Message ?? "", requestId = item?.Id.ToString()
        });
        try
        {
            // One deadline covers redirects AND streamed content. HttpClient's
            // header-only timeout alone would not protect a stalled response body.
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            deadline.CancelAfter(requestTimeout);
            var requestCancellation = deadline.Token;
            var uri = new Uri(settings.WebAppUrl);
            var method = HttpMethod.Post;
            for (var redirects = 0; redirects <= 5; redirects++)
            {
                using var request = new HttpRequestMessage(method, uri);
                if (method == HttpMethod.Post) request.Content = new StringContent(body, Encoding.UTF8, "text/plain");
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, requestCancellation);
                if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
                {
                    var next = response.Headers.Location is { } location ? new Uri(uri, location) : null;
                    if (next is null || next.Scheme != "https" || next.Port != 443 || next.UserInfo.Length != 0
                        || (next.Host != "script.googleusercontent.com" && next.Host != "script.google.com"))
                        return new(false, "invalid_response", "The receiver returned an unexpected redirect. Check your deployment URL.");
                    if ((int)response.StatusCode is 301 or 302 or 303) method = HttpMethod.Get;
                    uri = next;
                    continue;
                }
                if ((int)response.StatusCode == 429 || (int)response.StatusCode >= 500)
                    return new(false, "network", "The receiver is temporarily unavailable.", Retryable: true);
                if (response.Content.Headers.ContentLength > MaxResponseBytes)
                    return new(false, "invalid_response", "The receiver returned an oversized response. Check the deployment; your entry has not been marked sent.");
                using var stream = await response.Content.ReadAsStreamAsync(requestCancellation);
                var bytes = new byte[MaxResponseBytes + 1];
                var length = 0;
                while (length < bytes.Length) {
                    var read = await stream.ReadAsync(bytes.AsMemory(length), requestCancellation);
                    if (read == 0) break;
                    length += read;
                }
                if (length > MaxResponseBytes)
                    return new(false, "invalid_response", "The receiver returned an oversized response. Check the deployment; your entry has not been marked sent.");
                var offset = length >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf ? 3 : 0;
                using var data = JsonDocument.Parse(bytes.AsMemory(offset, length - offset));
                var root = data.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                    return new(false, "invalid_response", "The receiver did not return the expected JSON object.");
                if (response.IsSuccessStatusCode && root.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.True) {
                    if (item is null && string.IsNullOrWhiteSpace(Read(root, "target")))
                        return new(false, "invalid_response", "The receiver did not confirm an authenticated spreadsheet destination. Its public health page alone does not verify setup. Check the /exec deployment and receiver code.");
                    // A public health page can say success without acknowledging
                    // a write. Keep that entry for review rather than losing it.
                    if (item is not null && (string.IsNullOrWhiteSpace(Read(root, "sheet"))
                        || (item.RetryProtected && Read(root, "deliveryProtocol") != DeliveryProtocol)))
                        return new(false, "invalid_response", "The receiver did not confirm this entry's destination and delivery protocol. Check the Sheet and deployment before retrying; the entry is kept in the Outbox.");
                    return new(true, "", "Connected.", Read(root, "sheet"), Read(root, "target"), Read(root, "deliveryProtocol") == DeliveryProtocol,
                        SupportsCheckIns: root.TryGetProperty("supportsCheckIns", out var checkIns) && checkIns.ValueKind == JsonValueKind.True,
                        SupportsAutoSent: root.TryGetProperty("supportsAutoSent", out var autoSent) && autoSent.ValueKind == JsonValueKind.True,
                        SupportsStopwatch: root.TryGetProperty("supportsStopwatch",out var stopwatch)&&stopwatch.ValueKind==JsonValueKind.True);
                }
                // Raw server responses can contain arbitrary reflection text or
                // credentials. Never send them to diagnostics or persisted errors.
                var code = Read(root, "code");
                return code is "write_uncertain" or "id_conflict"
                    ? new(false, code, "The receiver held this entry for review. Check the Sheet before retrying or marking it already sent.")
                    : new(false, "rejected", "Google Sheets rejected the request. Check the connection settings, token, and destination tab.");
            }
            return new(false, "invalid_response", "Too many receiver redirects.");
        }
        catch (OperationCanceledException) { return new(false, "timeout", cancellation.IsCancellationRequested ? "The request was cancelled." : "The request timed out.", Retryable: !cancellation.IsCancellationRequested); }
        catch (HttpRequestException) { return new(false, "network", "The network request failed.", Retryable: true); }
        catch (IOException) { return new(false, "network", "The response was interrupted.", Retryable: true); }
        catch (UriFormatException) { return new(false, "invalid_response", "The receiver returned an invalid redirect. Check the deployment URL."); }
        catch (JsonException) { return new(false, "invalid_response", "The receiver did not return JSON. Check the /exec URL and web-app access: Execute as Me, access Anyone. A Google sign-in page cannot be used by the desktop app. See Guided setup for account restrictions."); }
    }
    private static string Read(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    public void Dispose() => client.Dispose();
}
