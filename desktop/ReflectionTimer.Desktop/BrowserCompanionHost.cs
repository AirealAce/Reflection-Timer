using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace ReflectionTimer.Desktop;

/// <summary>Current-user IPC for the optional browser metadata companion. No TCP listener or browser-profile reads.</summary>
public sealed class BrowserCompanionHost : IDisposable
{
    public const string HostName = "com.reflectiontimer.focus";
    public const string ExtensionId = "mgalafjodgnoeponalohbmdopkkbnfok";
    public const string ExtensionOrigin = "chrome-extension://" + ExtensionId + "/";
    public const int MaximumMessageBytes = 1024 * 1024;
    private readonly CancellationTokenSource cancellation = new();
    private readonly ConcurrentDictionary<string, NamedPipeServerStream> connections = new();
    private readonly ConcurrentDictionary<int, NamedPipeServerStream> waiting = new();
    private int nextListener;
    private int disposed;
    private readonly string serverPipeName;

    // Callbacks run on worker threads. JsonElement is cloned and remains valid
    // after the callback. The consumer owns synchronization with its state/UI.
    public event Action<string, JsonElement>? Received;
    public event Action<string>? Disconnected;

    private BrowserCompanionHost(string pipeName) => serverPipeName = pipeName;

    public static BrowserCompanionHost StartServer()
    {
        var server = new BrowserCompanionHost(PipeName);
        _ = server.AcceptAsync();
        return server;
    }

    internal static BrowserCompanionHost StartServerForTests(string pipeName)
    {
        var server = new BrowserCompanionHost(pipeName);
        _ = server.AcceptAsync();
        return server;
    }

    public static string PipeName
    {
        get {
            using var identity = WindowsIdentity.GetCurrent();
            var user = identity.User?.Value ?? throw new InvalidOperationException("A Windows user identity is required.");
            var suffix = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(user)))[..24];
            return "ReflectionTimer-BrowserCompanion-" + suffix;
        }
    }

    public static bool IsAllowedOrigin(string? origin) => string.Equals(origin, ExtensionOrigin, StringComparison.Ordinal);

    /// <summary>
    /// Intercept a chrome-extension:// first argument before normal app startup,
    /// and return this result as the process exit code. Chrome supplies the
    /// origin itself; the manifest path is the ordinary ReflectionTimer.exe.
    /// </summary>
    public static async Task<int> RunNativeHostAsync(string origin, CancellationToken token = default)
    {
        if (!IsAllowedOrigin(origin)) return 2;
        try {
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            // A browser must not launch or alter the timer's saved profile. With
            // no app running, this host exits and the companion retries later.
            await pipe.ConnectAsync(1500, token).ConfigureAwait(false);
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
            using var input = Console.OpenStandardInput();
            using var output = Console.OpenStandardOutput();
            var towardApp = ForwardAsync(input, pipe, lifetime.Token, acknowledgements: false);
            var towardBrowser = ForwardAsync(pipe, output, lifetime.Token, acknowledgements: true);
            await Task.WhenAny(towardApp, towardBrowser).ConfigureAwait(false);
            await lifetime.CancelAsync().ConfigureAwait(false);
            return 0;
        }
        catch (Exception error) when (error is IOException or TimeoutException or OperationCanceledException or UnauthorizedAccessException or InvalidDataException) {
            // stdout belongs entirely to Chrome's framed protocol. Do not log
            // received messages, browser titles, or hostnames on either stream.
            return 1;
        }
    }

    private static async Task ForwardAsync(Stream from, Stream to, CancellationToken token, bool acknowledgements)
    {
        try {
            while (!token.IsCancellationRequested) {
                var bytes = await ReadFrameAsync(from, token).ConfigureAwait(false);
                if (bytes is null) return;
                _ = ParsePayload(bytes, acknowledgements);
                await WriteFrameAsync(to, bytes, token).ConfigureAwait(false);
            }
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException or InvalidDataException) {
            // A bad or disconnected peer terminates only this transport.
        }
    }

    private async Task AcceptAsync()
    {
        while (!cancellation.IsCancellationRequested) {
            NamedPipeServerStream? pipe = null;
            var listener = Interlocked.Increment(ref nextListener);
            try {
                pipe = new NamedPipeServerStream(serverPipeName, PipeDirection.InOut, 16, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 4096, 4096);
                waiting[listener] = pipe;
                await pipe.WaitForConnectionAsync(cancellation.Token).ConfigureAwait(false);
                waiting.TryRemove(listener, out _);
                var id = Guid.NewGuid().ToString("N");
                connections[id] = pipe;
                _ = ReceiveAsync(id, pipe);
                pipe = null; // The individual connection now owns this stream.
            }
            catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException or UnauthorizedAccessException) {
                if (!cancellation.IsCancellationRequested) {
                    try { await Task.Delay(1000, cancellation.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { }
                }
            }
            finally { waiting.TryRemove(listener, out _); pipe?.Dispose(); }
        }
    }

    private async Task ReceiveAsync(string id, NamedPipeServerStream pipe)
    {
        try {
            while (!cancellation.IsCancellationRequested) {
                var bytes = await ReadFrameAsync(pipe, cancellation.Token).ConfigureAwait(false);
                if (bytes is null) break;
                var payload = ParsePayload(bytes);
                try { Received?.Invoke(id, payload); }
                catch { /* A consumer error must not crash another app window. */ }
                var ack = JsonSerializer.SerializeToUtf8Bytes(new {
                    version = 1, type = "ack", epoch = payload.GetProperty("epoch").GetString(), seq = payload.GetProperty("seq").GetInt64()
                });
                await WriteFrameAsync(pipe, ack, cancellation.Token).ConfigureAwait(false);
            }
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException or InvalidDataException) { }
        finally {
            connections.TryRemove(id, out _);
            pipe.Dispose();
            try { Disconnected?.Invoke(id); }
            catch { }
        }
    }

    internal static async ValueTask<byte[]?> ReadFrameAsync(Stream stream, CancellationToken token = default)
    {
        var header = new byte[4];
        var first = await stream.ReadAsync(header.AsMemory(0, 1), token).ConfigureAwait(false);
        if (first == 0) return null;
        try { await stream.ReadExactlyAsync(header.AsMemory(1, 3), token).ConfigureAwait(false); }
        catch (EndOfStreamException error) { throw new InvalidDataException("Incomplete native message header.", error); }
        var count = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (count <= 0 || count > MaximumMessageBytes) throw new InvalidDataException("Invalid native message length.");
        var body = new byte[count];
        try { await stream.ReadExactlyAsync(body, token).ConfigureAwait(false); }
        catch (EndOfStreamException error) { throw new InvalidDataException("Incomplete native message body.", error); }
        return body;
    }

    internal static async Task WriteFrameAsync(Stream stream, ReadOnlyMemory<byte> bytes, CancellationToken token = default)
    {
        if (bytes.Length <= 0 || bytes.Length > MaximumMessageBytes) throw new InvalidDataException("Invalid native message length.");
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
        await stream.WriteAsync(header, token).ConfigureAwait(false);
        await stream.WriteAsync(bytes, token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
    }

    // Check the transport envelope and bounded metadata. Application matching
    // policy remains in the desktop index; untrusted strings never become HTML.
    internal static JsonElement ParsePayload(ReadOnlyMemory<byte> bytes, bool acknowledgements = false)
    {
        if (bytes.Length <= 0 || bytes.Length > MaximumMessageBytes) throw new InvalidDataException("Invalid native message length.");
        try {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 12 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !Int(root, "version", out var version) || version != 1 ||
                !Text(root, "type", 16, out var type) || !Text(root, "epoch", 36, out var epoch) || !Guid.TryParseExact(epoch, "D", out _) ||
                !Int(root, "seq", out var seq) || seq < 1 || seq > 9007199254740991) throw new InvalidDataException("Invalid companion envelope.");
            if (acknowledgements) {
                if (type != "ack") throw new InvalidDataException("Invalid companion acknowledgement.");
                return root.Clone();
            }
            if (!Text(root, "browser", 8, out var browser) || browser is not ("chrome" or "msedge")) throw new InvalidDataException("Invalid browser identity.");
            switch (type) {
                case "hello": break;
                case "snapshot":
                    ValidateArray(root, "windows", 128, window => {
                        RequireId(window, "id"); RequireBoolean(window, "focused");
                        RequireText(window, "state", 32);
                        foreach (var name in new[] { "left", "top", "width", "height" })
                            if (!Int(window, name, out var value) || value < -1000000 || value > 1000000) throw new InvalidDataException("Invalid window bounds.");
                    });
                    ValidateArray(root, "tabs", 2048, tab => {
                        foreach (var name in new[] { "id", "windowId", "index" }) RequireId(tab, name);
                        RequireBoolean(tab, "active"); RequireText(tab, "title", 256); RequireHost(tab, "siteHost");
                        foreach (var name in new[] { "groupId", "openerTabId" }) RequireId(tab, name, allowNone: true);
                    });
                    ValidateArray(root, "groups", 512, group => {
                        RequireId(group, "id"); RequireId(group, "windowId"); RequireText(group, "title", 256);
                    });
                    if (root.TryGetProperty("overflow", out var overflow) && overflow.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                        throw new InvalidDataException("Invalid snapshot overflow flag.");
                    if (root.TryGetProperty("currentWindowId", out _)) RequireId(root, "currentWindowId", allowNone: true);
                    break;
                case "navigation":
                    RequireId(root, "tabId");
                    if (!Text(root, "kind", 16, out var kind) || kind is not ("created" or "committed" or "replaced" or "removed"))
                        throw new InvalidDataException("Invalid navigation kind.");
                    if (kind == "created") RequireId(root, "sourceTabId");
                    if (kind == "replaced") RequireId(root, "replacedTabId");
                    RequireText(root, "transitionType", 32); RequireHost(root, "previousSiteHost"); RequireHost(root, "siteHost");
                    ValidateArray(root, "qualifiers", 4, qualifier => {
                        if (qualifier.ValueKind != JsonValueKind.String || qualifier.GetString() is not
                            ("client_redirect" or "server_redirect" or "forward_back" or "from_address_bar")) throw new InvalidDataException("Invalid transition qualifier.");
                    });
                    break;
                default: throw new InvalidDataException("Unknown companion message.");
            }
            return root.Clone();
        }
        catch (JsonException error) { throw new InvalidDataException("Invalid companion JSON.", error); }
    }

    private static bool Int(JsonElement value, string name, out long result)
    {
        result = 0;
        return value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var child) &&
            child.ValueKind == JsonValueKind.Number && child.TryGetInt64(out result);
    }

    private static bool Text(JsonElement value, string name, int maximum, out string result)
    {
        result = "";
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(name, out var child) || child.ValueKind != JsonValueKind.String) return false;
        result = child.GetString()!;
        return result.Length <= maximum;
    }

    private static void RequireText(JsonElement value, string name, int maximum)
    {
        if (!Text(value, name, maximum, out _)) throw new InvalidDataException("Invalid metadata text.");
    }

    private static void RequireId(JsonElement value, string name, bool allowNone = false)
    {
        if (!Int(value, name, out var id) || id < (allowNone ? -1 : 0) || id > 9007199254740991) throw new InvalidDataException("Invalid metadata identifier.");
    }

    private static void RequireBoolean(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(name, out var child) || child.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new InvalidDataException("Invalid metadata flag.");
    }

    private static void RequireHost(JsonElement value, string name)
    {
        if (!Text(value, name, 320, out var host) || host.Any(char.IsWhiteSpace) || host.IndexOfAny(['/', '\\', '?', '#', '@']) >= 0)
            throw new InvalidDataException("Invalid site host.");
    }

    private static void ValidateArray(JsonElement root, string name, int maximum, Action<JsonElement> validate)
    {
        if (!root.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array || array.GetArrayLength() > maximum)
            throw new InvalidDataException("Invalid metadata array.");
        foreach (var item in array.EnumerateArray()) validate(item);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        cancellation.Cancel();
        foreach (var pipe in waiting.Values) pipe.Dispose();
        foreach (var pipe in connections.Values) pipe.Dispose();
        // Do not dispose the CancellationTokenSource while worker loops still
        // observe its token. All operations are canceled and their pipes closed.
    }
}
