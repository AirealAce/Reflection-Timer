using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using ReflectionTimer.Accessible;

internal static class DesktopUpdateClientTests
{
    private static readonly Version ReleaseVersion = new(9, 2, 3);
    private const string AssetName = "ReflectionTimer-9.2.3-win-x64.zip";
    private const string AssetUrl = "https://github.com/AirealAce/Reflection-Timer/releases/download/v9.2.3/" + AssetName;
    private const string RootName = "ReflectionTimer-9.2.3-win-x64";

    internal static async Task Run(Action<bool, string> check)
    {
        using (var fixture = new Fixture()) {
            var release = await fixture.Client.CheckAsync(new(9, 2, 2));
            check(release is not null && release.Version == ReleaseVersion && release.AssetName == AssetName && release.Sha256 == Hex(fixture.Zip),
                "Updates discover only the exact newer stable Windows package with its SHA-256 digest");
            check(await fixture.Client.CheckAsync(ReleaseVersion) is null && await fixture.Client.CheckAsync(new(10, 0, 0)) is null,
                "Updates never reinstall the current version or downgrade to an older release");
            var progress = new List<int>();
            var payload = await fixture.Client.DownloadAsync(release!, fixture.Staging, new ImmediateProgress(progress.Add));
            check(Directory.Exists(payload) && Path.GetFileName(payload) == RootName && File.Exists(Path.Combine(payload, "package-manifest.json")),
                "A verified ZIP extracts to a fresh package folder");
            check(progress.Count > 2 && progress.First() == 0 && progress.Last() == 100 && progress.Zip(progress.Skip(1)).All(pair => pair.First <= pair.Second),
                "Update download progress is bounded and completes only after package verification");
            check(fixture.Requests.All(request => !request.Authorization && request.UserAgent.Contains("ReflectionTimer-Desktop-Updater")),
                "Public app updates use no access token or saved connection credentials");
        }

        foreach (var test in new (string Name, Action<JsonObject> Change)[] {
            ("draft release", data => data["draft"] = true),
            ("preview release", data => data["prerelease"] = true),
            ("preview tag", data => data["tag_name"] = "v9.2.3-beta"),
            ("four-part tag", data => data["tag_name"] = "v9.2.3.4"),
            ("tag without v", data => data["tag_name"] = "9.2.3"),
            ("zero-prefixed tag", data => data["tag_name"] = "v09.2.3"),
            ("missing stable flag", data => data.Remove("draft")),
            ("duplicate Windows asset", data => ((JsonArray)data["assets"]!).Add(data["assets"]![0]!.DeepClone())),
            ("wrong platform asset", data => data["assets"]![0]!["name"] = "ReflectionTimer-9.2.3-linux-x64.zip"),
            ("empty archive", data => data["assets"]![0]!["size"] = 0),
            ("oversized archive", data => data["assets"]![0]!["size"] = DesktopUpdateClient.MaxArchiveBytes + 1),
            ("unfinished asset", data => data["assets"]![0]!["state"] = "new"),
            ("unsupported checksum", data => data["assets"]![0]!["digest"] = "md5:" + new string('a', 32)),
            ("invalid checksum", data => data["assets"]![0]!["digest"] = "sha256:not-a-hash"),
            ("missing checksum", data => ((JsonObject)data["assets"]![0]!).Remove("digest")),
            ("insecure download", data => data["assets"]![0]!["browser_download_url"] = AssetUrl.Replace("https:", "http:")),
            ("other repository download", data => data["assets"]![0]!["browser_download_url"] = AssetUrl.Replace("AirealAce", "OtherAccount")),
            ("credential-bearing download", data => data["assets"]![0]!["browser_download_url"] = AssetUrl.Replace("https://", "https://user@")),
            ("download query", data => data["assets"]![0]!["browser_download_url"] = AssetUrl + "?other=1"),
            ("untrusted download host", data => data["assets"]![0]!["browser_download_url"] = "https://github.com.example.invalid/update.zip")
        }) {
            using var fixture = new Fixture(); test.Change(fixture.Metadata);
            check(await Reject(() => fixture.Client.CheckAsync(new(9, 2, 2))), "Update metadata rejects " + test.Name);
        }

        using (var fixture = new Fixture()) {
            fixture.UseChecksumSidecar();
            var release = await fixture.Client.CheckAsync(new(9, 2, 2));
            check(release?.Sha256 == Hex(fixture.Zip) && fixture.Requests.Count == 2, "An exact published SHA-256 sidecar supports releases without an API digest");
            fixture.SidecarText = Hex(fixture.Zip) + "  another-package.zip\n";
            check(await Reject(() => fixture.Client.CheckAsync(new(9, 2, 2))), "Checksum sidecars must name this exact ZIP");
            fixture.SidecarText = Hex(fixture.Zip) + "  " + AssetName + "\n" + Hex(fixture.Zip) + "  extra.zip";
            check(await Reject(() => fixture.Client.CheckAsync(new(9, 2, 2))), "Checksum sidecars cannot include ambiguous extra entries");
        }
        using (var fixture = new Fixture()) {
            fixture.MetadataResponse = _ => new(HttpStatusCode.NotFound);
            check(await fixture.Client.CheckAsync(new(9, 2, 2)) is null, "A repository without a published release has no update");
            fixture.MetadataResponse = _ => new(HttpStatusCode.Forbidden);
            check(await Reject(() => fixture.Client.CheckAsync(new(9, 2, 2)), typeof(HttpRequestException)), "GitHub rate limits produce a recoverable update error");
            fixture.MetadataResponse = _ => new(HttpStatusCode.OK) { Content = new StringContent("not JSON") };
            check(await Reject(() => fixture.Client.CheckAsync(new(9, 2, 2))), "Malformed GitHub metadata cannot enable an update");
            fixture.MetadataResponse = _ => new(HttpStatusCode.OK) { Content = new StringContent(new string(' ', 1024 * 1024 + 1)) };
            check(await Reject(() => fixture.Client.CheckAsync(new(9, 2, 2))), "Oversized GitHub metadata is rejected before parsing");
        }
        using (var fixture = new Fixture()) {
            var release = (await fixture.Client.CheckAsync(new(9, 2, 2)))!;
            fixture.Zip = fixture.Zip.Concat(new byte[] { 1 }).ToArray();
            check(await Reject(() => fixture.Client.DownloadAsync(release, fixture.Staging)) && !Directory.EnumerateFileSystemEntries(fixture.Staging).Any(),
                "A changed archive size leaves no extracted or partial update");
        }
        using (var fixture = new Fixture()) {
            var release = (await fixture.Client.CheckAsync(new(9, 2, 2)))!;
            fixture.Zip[fixture.Zip.Length / 2] ^= 1;
            check(await Reject(() => fixture.Client.DownloadAsync(release, fixture.Staging)) && !Directory.EnumerateFileSystemEntries(fixture.Staging).Any(),
                "An archive checksum failure cleans staging and cannot be installed");
        }
        foreach (var name in new[] { "../escape.txt", "/absolute.txt", RootName + "/../escape.txt", RootName + "/C:/escape.txt",
            RootName + "/nested\\escape.txt", RootName + "/CON.txt", RootName + "/trailing. /file.txt", RootName + "/Web/../escape.txt",
            RootName + "/unsafe%2fpath.txt", "different-root/file.txt" }) {
            using var fixture = new Fixture(extraEntries: [(name, new byte[] { 1 }, 0)]);
            var release = (await fixture.Client.CheckAsync(new(9, 2, 2)))!;
            check(await Reject(() => fixture.Client.DownloadAsync(release, fixture.Staging)), "Update ZIP rejects unsafe path " + name);
        }
        foreach (var entry in new[] {
            (RootName + "/Web/APP.js", new byte[] { 1 }, 0),
            (RootName + "/linked.txt", new byte[] { 1 }, unchecked((int)0xA1FF0000)),
            (RootName + "/Web", new byte[] { 1 }, 0)
        }) {
            using var fixture = new Fixture(extraEntries: [entry]);
            var release = (await fixture.Client.CheckAsync(new(9, 2, 2)))!;
            check(await Reject(() => fixture.Client.DownloadAsync(release, fixture.Staging)), "Update ZIP rejects duplicates, links and file-directory conflicts: " + entry.Item1);
        }
        foreach (var name in new[] { "state.dat", "diagnostics.json", "browser-companion/native-host.json", "settings.json", ".env.json", "personal.mp3", "Sounds/popup.mp3", "run.ps1" }) {
            using var fixture = new Fixture(files => files[name] = [1]);
            var release = (await fixture.Client.CheckAsync(new(9, 2, 2)))!;
            check(await Reject(() => fixture.Client.DownloadAsync(release, fixture.Staging)), "Update packages exclude private configuration and unexpected files: " + name);
        }
        foreach (var test in new (string Name, Action<Dictionary<string, byte[]>>? Files, Action<JsonObject>? Manifest)[] {
            ("wrong release version", null, manifest => manifest["Version"] = "9.2.2"),
            ("wrong platform", null, manifest => manifest["Runtime"] = "win-arm64"),
            ("unsupported manifest format", null, manifest => manifest["FormatVersion"] = 2),
            ("missing required app file", files => files.Remove("Web/app.js"), null),
            ("missing self-contained runtime", files => files.Remove("coreclr.dll"), null),
            ("unlisted extra file", null, manifest => ((JsonArray)manifest["Files"]!).RemoveAt(0)),
            ("duplicate manifest entry", null, manifest => ((JsonArray)manifest["Files"]!).Add(manifest["Files"]![0]!.DeepClone())),
            ("unsafe manifest path", null, manifest => manifest["Files"]![0]!["Path"] = "../escape.txt"),
            ("incorrect file checksum", null, manifest => manifest["Files"]![0]!["Sha256"] = new string('0', 64)),
            ("incorrect runtime version", null, manifest => manifest["RuntimeVersion"] = "10.0.99"),
            ("framework-dependent runtime", files => files["ReflectionTimer.runtimeconfig.json"] = Encoding.UTF8.GetBytes("{\"runtimeOptions\":{\"tfm\":\"net10.0\",\"framework\":{\"name\":\"Microsoft.NETCore.App\",\"version\":\"10.0.12\"}}}"), null),
            ("non-Windows executable", files => files["ReflectionTimer.exe"] = [1, 2, 3], null),
            ("non-x64 executable", files => { var exe = files["ReflectionTimer.exe"]; exe[68] = 0x4C; exe[69] = 0x01; }, null)
        }) {
            using var fixture = new Fixture(test.Files, test.Manifest);
            var release = (await fixture.Client.CheckAsync(new(9, 2, 2)))!;
            check(await Reject(() => fixture.Client.DownloadAsync(release, fixture.Staging)), "Package verification rejects " + test.Name);
        }
        using (var fixture = new Fixture(executableVersion: new(9, 2, 2, 0))) {
            var release = (await fixture.Client.CheckAsync(new(9, 2, 2)))!;
            check(await Reject(() => fixture.Client.DownloadAsync(release, fixture.Staging)), "The executable file version must match the advertised release");
        }
        using (var fixture = new Fixture()) {
            var release = (await fixture.Client.CheckAsync(new(9, 2, 2)))!;
            fixture.Redirect = "https://release-assets.githubusercontent.com/github-production-release-asset/known/update.zip?signature=test";
            var payload = await fixture.Client.DownloadAsync(release, fixture.Staging);
            check(File.Exists(Path.Combine(payload, "ReflectionTimer.exe")), "Verified release downloads may follow GitHub's secure asset CDN redirect");
        }
        foreach (var redirect in new[] { "http://release-assets.githubusercontent.com/update.zip", "https://untrusted.example/update.zip", "https://github.com/Other/repo/releases/download/v1/file.zip" }) {
            using var fixture = new Fixture();
            var release = (await fixture.Client.CheckAsync(new(9, 2, 2)))!; fixture.Redirect = redirect;
            check(await Reject(() => fixture.Client.DownloadAsync(release, fixture.Staging)) && fixture.Requests.All(request => request.Url != redirect),
                "An untrusted update redirect is rejected before requesting it: " + redirect);
        }
        using (var fixture = new Fixture()) {
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            check(await Reject(() => fixture.Client.CheckAsync(new(9, 2, 2), cancellation.Token), typeof(OperationCanceledException)),
                "A cancelled update check stops immediately");
            var release = (await fixture.Client.CheckAsync(new(9, 2, 2)))!;
            check(await Reject(() => fixture.Client.DownloadAsync(release, fixture.Staging, cancellationToken: cancellation.Token), typeof(OperationCanceledException)),
                "A cancelled update download leaves no partial installation");
            check(!Directory.EnumerateFileSystemEntries(fixture.Staging).Any(), "Cancelled update staging is removed");
        }
        using (var fixture = new Fixture()) {
            var release = (await fixture.Client.CheckAsync(new(9, 2, 2)))!;
            using var cancellation = new CancellationTokenSource();
            var progress = new ImmediateProgress(value => { if (value > 0) cancellation.Cancel(); });
            check(await Reject(() => fixture.Client.DownloadAsync(release, fixture.Staging, progress, cancellation.Token), typeof(OperationCanceledException))
                && !Directory.EnumerateFileSystemEntries(fixture.Staging).Any(),
                "Cancellation during a streamed download removes its temporary ZIP");
        }
    }

    private static async Task<bool> Reject(Func<Task> action, Type? exceptionType = null)
    {
        try { await action(); return false; }
        catch (Exception error) when ((exceptionType ?? typeof(InvalidDataException)).IsInstanceOfType(error)) { return true; }
    }
    private static string Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private sealed class ImmediateProgress(Action<int> report) : IProgress<int> { public void Report(int value) => report(value); }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => reply(request, cancellationToken);
    }
    private sealed class Fixture : IDisposable
    {
        internal readonly string Staging = Path.Combine(Path.GetTempPath(), "ReflectionTimer-update-test-" + Guid.NewGuid().ToString("N"));
        internal readonly DesktopUpdateClient Client;
        private readonly HttpClient http;
        internal byte[] Zip;
        internal JsonObject Metadata;
        internal string? SidecarText;
        internal string? Redirect;
        internal Func<HttpRequestMessage, HttpResponseMessage>? MetadataResponse;
        internal readonly List<(string Url, bool Authorization, string UserAgent)> Requests = [];
        internal Fixture(Action<Dictionary<string, byte[]>>? changeFiles = null, Action<JsonObject>? changeManifest = null,
            (string Path, byte[] Bytes, int Attributes)[]? extraEntries = null, Version? executableVersion = null)
        {
            Zip = Package(changeFiles, changeManifest, extraEntries);
            Metadata = new JsonObject {
                ["tag_name"] = "v9.2.3", ["draft"] = false, ["prerelease"] = false,
                ["assets"] = new JsonArray(new JsonObject { ["name"] = AssetName, ["state"] = "uploaded", ["size"] = Zip.Length,
                    ["digest"] = "sha256:" + Hex(Zip), ["browser_download_url"] = AssetUrl })
            };
            http = new HttpClient(new Handler((request, token) => {
                token.ThrowIfCancellationRequested();
                var url = request.RequestUri!.AbsoluteUri;
                Requests.Add((url, request.Headers.Authorization is not null, request.Headers.UserAgent.ToString()));
                HttpResponseMessage response;
                if (request.RequestUri == DesktopUpdateClient.LatestRelease) response = MetadataResponse?.Invoke(request)
                    ?? new(HttpStatusCode.OK) { Content = new StringContent(Metadata.ToJsonString()) };
                else if (url == AssetUrl + ".sha256.txt") response = new(HttpStatusCode.OK) { Content = new StringContent(SidecarText ?? "") };
                else if (url == AssetUrl && Redirect is not null) response = new(HttpStatusCode.Found) { Headers = { Location = new Uri(Redirect) } };
                else if (url == AssetUrl || url == Redirect) response = new(HttpStatusCode.OK) { Content = new ByteArrayContent(Zip) };
                else throw new Exception("An unexpected URL was requested: " + url);
                response.RequestMessage = request;
                return Task.FromResult(response);
            }));
            Client = new DesktopUpdateClient(http, _ => executableVersion ?? new Version(9, 2, 3, 0));
        }
        internal void UseChecksumSidecar()
        {
            ((JsonObject)Metadata["assets"]![0]!).Remove("digest");
            SidecarText = Hex(Zip) + "  " + AssetName + "\n";
            ((JsonArray)Metadata["assets"]!).Add(new JsonObject { ["name"] = AssetName + ".sha256.txt", ["state"] = "uploaded",
                ["size"] = SidecarText.Length, ["browser_download_url"] = AssetUrl + ".sha256.txt" });
        }
        public void Dispose() { Client.Dispose(); http.Dispose(); if (Directory.Exists(Staging)) Directory.Delete(Staging, true); }
    }

    private static byte[] Package(Action<Dictionary<string, byte[]>>? changeFiles, Action<JsonObject>? changeManifest,
        (string Path, byte[] Bytes, int Attributes)[]? extraEntries)
    {
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal) {
            ["ReflectionTimer.exe"] = FakeX64Executable(),
            ["ReflectionTimer.runtimeconfig.json"] = Encoding.UTF8.GetBytes("{\"runtimeOptions\":{\"tfm\":\"net10.0\",\"includedFrameworks\":[{\"name\":\"Microsoft.NETCore.App\",\"version\":\"10.0.12\"},{\"name\":\"Microsoft.WindowsDesktop.App\",\"version\":\"10.0.12\"}]}}")
        };
        foreach (var name in new[] { "ReflectionTimer.dll", "ReflectionTimer.deps.json", "coreclr.dll", "hostfxr.dll", "hostpolicy.dll",
            "System.Private.CoreLib.dll", "System.Windows.Forms.dll", "Microsoft.Web.WebView2.WinForms.dll", "WebView2Loader.dll",
            "START-HERE.html", "START-HERE.txt", "google-sheets-script.gs", "THIRD-PARTY-NOTICES.txt", "AUDIO-NOTICES.txt", "Web/index.html",
            "Web/app.js", "Web/compact.html", "Web/themes.css", "Web/themes.js", "popup.mp3" }) files[name] = Encoding.UTF8.GetBytes("synthetic update fixture " + name);
        changeFiles?.Invoke(files);
        var entries = new JsonArray();
        foreach (var file in files) entries.Add(new JsonObject { ["Path"] = file.Key, ["Sha256"] = Hex(file.Value) });
        var manifest = new JsonObject { ["FormatVersion"] = 1, ["Version"] = "9.2.3", ["Runtime"] = "win-x64", ["RuntimeVersion"] = "10.0.12", ["Files"] = entries };
        changeManifest?.Invoke(manifest);
        files["package-manifest.json"] = Encoding.UTF8.GetBytes(manifest.ToJsonString());
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true)) {
            foreach (var file in files) { using var output = zip.CreateEntry(RootName + "/" + file.Key).Open(); output.Write(file.Value); }
            foreach (var entry in extraEntries ?? []) {
                var item = zip.CreateEntry(entry.Path); item.ExternalAttributes = entry.Attributes;
                using var output = item.Open(); output.Write(entry.Bytes);
            }
        }
        return stream.ToArray();
    }
    private static byte[] FakeX64Executable()
    {
        var bytes = new byte[256];
        using var writer = new BinaryWriter(new MemoryStream(bytes));
        writer.Write((ushort)0x5A4D); writer.BaseStream.Position = 60; writer.Write(64);
        writer.BaseStream.Position = 64; writer.Write(0x00004550); writer.Write((ushort)0x8664);
        writer.BaseStream.Position = 88; writer.Write((ushort)0x20B);
        return bytes;
    }
}
