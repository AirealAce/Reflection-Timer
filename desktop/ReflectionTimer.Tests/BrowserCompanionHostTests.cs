using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using ReflectionTimer.Desktop;

static class BrowserCompanionHostTests
{
    internal static async Task Run(Action<bool, string> check)
    {
        const string epoch = "00000000-0000-4000-8000-000000000001";
        byte[] Message(object value) => JsonSerializer.SerializeToUtf8Bytes(value);
        var hello = Message(new { version = 1, type = "hello", browser = "chrome", epoch, seq = 1 });
        bool Rejects(byte[] bytes, bool acknowledgements = false) {
            try { BrowserCompanionHost.ParsePayload(bytes, acknowledgements); return false; }
            catch (InvalidDataException) { return true; }
        }
        async Task<bool> RejectsFrame(byte[] bytes) {
            try { await BrowserCompanionHost.ReadFrameAsync(new MemoryStream(bytes)); return false; }
            catch (InvalidDataException) { return true; }
        }
        byte[] Header(int length) { var result = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(result, length); return result; }

        check(BrowserCompanionHost.IsAllowedOrigin(BrowserCompanionHost.ExtensionOrigin)
            && !BrowserCompanionHost.IsAllowedOrigin(BrowserCompanionHost.ExtensionOrigin + "other")
            && !BrowserCompanionHost.IsAllowedOrigin("chrome-extension://another/")
            && !BrowserCompanionHost.IsAllowedOrigin(null), "Native host admits only the exact fixed companion origin");
        check(await BrowserCompanionHost.RunNativeHostAsync("chrome-extension://another/") == 2,
            "An unknown extension is rejected before any pipe connection or app startup");
        var parsed = BrowserCompanionHost.ParsePayload(hello);
        check(parsed.GetProperty("epoch").GetString() == epoch && parsed.GetProperty("seq").GetInt64() == 1,
            "Validated JSON is cloned and survives parser document disposal");
        check(Rejects(Message(new { version = 2, type = "hello", browser = "chrome", epoch, seq = 1 }))
            && Rejects(Message(new { version = 1, type = "hello", browser = "other", epoch, seq = 1 }))
            && Rejects(Message(new { version = 1, type = "unknown", browser = "chrome", epoch, seq = 1 })),
            "Unknown versions, browsers and message kinds fail closed");
        check(Rejects(Message(new { version = "1", type = "hello", browser = "chrome", epoch, seq = 1 }))
            && Rejects(Message(new { version = 1, type = "hello", browser = "chrome", epoch = "not-an-epoch", seq = 1 }))
            && Rejects(Message(new { version = 1, type = "hello", browser = "chrome", epoch, seq = 0 })),
            "Malformed envelope types, epoch identities and sequence numbers are rejected without unchecked parser exceptions");
        check(Rejects(Encoding.UTF8.GetBytes("not json")) && Rejects([0xff, 0xfe]), "Invalid JSON and malformed UTF-8 are rejected");
        check(Rejects(hello, acknowledgements: true), "The browser-facing stream accepts acknowledgements only");

        var snapshot = Message(new {
            version = 1, type = "snapshot", browser = "msedge", epoch, seq = 2, currentWindowId = 5, overflow = false,
            windows = new[] { new { id = 5, focused = false, state = "normal", left = -1000, top = 0, width = 1000, height = 800 } },
            tabs = new[] { new { id = 9, windowId = 5, index = 0, active = true, title = "Synthetic tab", siteHost = "example.org:8443", groupId = 3, openerTabId = -1 } },
            groups = new[] { new { id = 3, windowId = 5, title = "Synthetic group" } }
        });
        check(BrowserCompanionHost.ParsePayload(snapshot).GetProperty("tabs")[0].GetProperty("siteHost").GetString() == "example.org:8443",
            "Bounded snapshots retain current window, groups and canonical website metadata");
        check(Rejects(Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(snapshot).Replace("example.org:8443", "https://example.org/private?token=secret"))),
            "A full URL in a site host is rejected before desktop matching");
        check(Rejects(Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(snapshot).Replace("Synthetic tab", new string('t', 257)))),
            "Oversized tab metadata is rejected");
        var navigation = Message(new { version = 1, type = "navigation", browser = "chrome", epoch, seq = 3,
            kind = "created", tabId = 10, sourceTabId = 9, transitionType = "", qualifiers = Array.Empty<string>(), previousSiteHost = "example.org", siteHost = "external.example" });
        check(BrowserCompanionHost.ParsePayload(navigation).GetProperty("sourceTabId").GetInt32() == 9,
            "Actual cross-site navigation source IDs pass through without URL paths");
        check(Rejects(Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(navigation).Replace("\"sourceTabId\":9,", "")))
            && Rejects(Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(navigation).Replace("\"qualifiers\":[]", "\"qualifiers\":[\"unknown\"]"))),
            "Missing opener provenance and unknown transition qualifiers are rejected");

        using var framed = new MemoryStream();
        await BrowserCompanionHost.WriteFrameAsync(framed, snapshot);
        var frame = framed.ToArray();
        check(BinaryPrimitives.ReadInt32LittleEndian(frame.AsSpan(0, 4)) == snapshot.Length && frame.AsSpan(4).SequenceEqual(snapshot),
            "Native framing encodes the UTF-8 byte length in four-byte little-endian form");
        using var fragmented = new FragmentedStream(frame);
        check((await BrowserCompanionHost.ReadFrameAsync(fragmented))!.SequenceEqual(snapshot),
            "Fragmented header/body reads reconstruct a complete native message");
        check(await BrowserCompanionHost.ReadFrameAsync(fragmented) is null, "EOF at a frame boundary closes a connection cleanly");
        check(await RejectsFrame(Header(0)) && await RejectsFrame(Header(-1)) && await RejectsFrame(Header(BrowserCompanionHost.MaximumMessageBytes + 1)),
            "Zero, negative and oversized native frame lengths are rejected before allocation");
        check(await RejectsFrame([1, 0]) && await RejectsFrame([.. Header(10), 1, 2, 3]),
            "Truncated frame headers and bodies cannot become partial browser metadata");

        var appFolder = Path.Combine(Path.GetTempPath(), "ReflectionTimerSynthetic");
        var exe = Path.Combine(appFolder, "ReflectionTimer.exe");
        var folder = Path.Combine(appFolder, "browser-companion");
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { exe, Path.Combine(folder, "manifest.json"), Path.Combine(folder, "background.js") };
        var writes = new List<(string Path, string Value)>(); var registry = new List<(string Path, string Value)>();
        var manifestPath = BrowserCompanionRegistration.Register(exe, folder, files.Contains,
            path => string.Equals(path, folder, StringComparison.OrdinalIgnoreCase), (path, value) => writes.Add((path, value)), (path, value) => registry.Add((path, value)));
        using var hostManifest = JsonDocument.Parse(writes.Single().Value);
        check(manifestPath == Path.Combine(folder, BrowserCompanionRegistration.ManifestFileName)
            && hostManifest.RootElement.GetProperty("path").GetString() == exe && hostManifest.RootElement.GetProperty("type").GetString() == "stdio",
            "Explicit companion setup writes an absolute installed executable path without shell arguments");
        check(hostManifest.RootElement.GetProperty("allowed_origins").GetArrayLength() == 1
            && hostManifest.RootElement.GetProperty("allowed_origins")[0].GetString() == BrowserCompanionHost.ExtensionOrigin,
            "Native-host registration restricts allowed origins to the fixed companion ID");
        check(registry.Count == 2 && registry.All(entry => entry.Value == manifestPath)
            && registry.Any(entry => entry.Path.StartsWith(@"Software\Google\Chrome\")) && registry.Any(entry => entry.Path.StartsWith(@"Software\Microsoft\Edge\")),
            "Registration targets only Chrome and Edge per-user native messaging keys");
        bool InvalidRegistration(string executable, string directory) {
            writes.Clear(); registry.Clear();
            try { BrowserCompanionRegistration.Register(executable, directory, files.Contains, _ => true,
                (path, value) => writes.Add((path, value)), (path, value) => registry.Add((path, value))); return false; }
            catch (ArgumentException) { return writes.Count == 0 && registry.Count == 0; }
        }
        check(InvalidRegistration("ReflectionTimer.exe", folder) && InvalidRegistration(exe, Path.GetTempPath())
            && InvalidRegistration(Path.Combine(appFolder, "another.exe"), folder),
            "Invalid, missing or unrelated paths are rejected before any filesystem or registry mutations");
        files.Remove(Path.Combine(folder, "background.js"));
        check(InvalidRegistration(exe, folder), "Incomplete browser packages cannot be registered");

        // A unique synthetic pipe never touches the installed app's transport.
        var testPipe = "ReflectionTimer-Companion-Test-" + Guid.NewGuid().ToString("N");
        using var server = BrowserCompanionHost.StartServerForTests(testPipe);
        var received = new TaskCompletionSource<(string Id, JsonElement Data)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var disconnected = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        server.Received += (id, data) => received.TrySetResult((id, data));
        server.Disconnected += id => disconnected.TrySetResult(id);
        using var client = new NamedPipeClientStream(".", testPipe, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await client.ConnectAsync(3000);
        await BrowserCompanionHost.WriteFrameAsync(client, hello);
        var actual = await received.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var acknowledgement = BrowserCompanionHost.ParsePayload((await BrowserCompanionHost.ReadFrameAsync(client))!, acknowledgements: true);
        check(actual.Data.GetProperty("epoch").GetString() == epoch && acknowledgement.GetProperty("seq").GetInt64() == 1,
            "Current-user IPC delivers validated metadata and acknowledges the same epoch/sequence");
        await BrowserCompanionHost.WriteFrameAsync(client, Encoding.UTF8.GetBytes("invalid JSON"));
        check(await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(3)) == actual.Id,
            "Invalid metadata terminates its connection and emits a disconnect notification");
    }

    private sealed class FragmentedStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(2, buffer.Length)], cancellationToken);
    }
}
