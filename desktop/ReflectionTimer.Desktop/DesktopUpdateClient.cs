using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ReflectionTimer.Core;
using ReflectionTimer.Desktop;

namespace ReflectionTimer.Accessible;

internal sealed record DesktopUpdateRelease(Version Version, string AssetName, Uri AssetUrl,
    long Size, string Sha256, Uri ReleaseUrl);

// Updates are published ZIPs, never repository source archives or arbitrary URLs.
// No account, browser cookies, saved connection settings or access token is used.
internal sealed class DesktopUpdateClient : IDisposable
{
    internal static readonly Uri LatestRelease = new("https://api.github.com/repos/AirealAce/Reflection-Timer/releases/latest");
    internal const long MaxArchiveBytes = 512L * 1024 * 1024;
    internal const long MaxExpandedBytes = 1536L * 1024 * 1024;
    internal const long MaxFileBytes = 256L * 1024 * 1024;
    internal const int MaxFiles = 4000;
    private const int MaxMetadataBytes = 1024 * 1024;
    private const int MaxManifestBytes = 2 * 1024 * 1024;
    private readonly HttpClient client;
    private readonly bool ownsClient;
    private readonly Func<string, Version?> executableVersion;
    private static readonly Regex StableTag = new(@"^v(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$", RegexOptions.CultureInvariant);
    private static readonly Regex Hash = new(@"^[a-fA-F0-9]{64}$", RegexOptions.CultureInvariant);
    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase) {
        ".exe", ".dll", ".json", ".txt", ".html", ".gs", ".mp3", ".js", ".css", ".md", ".xml"
    };
    private static readonly HashSet<string> AudioNames = Enum.GetValues<LibrarySound>()
        .Where(x => x is not (LibrarySound.None or LibrarySound.Random)).Select(SoundLibrary.FileName)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);
    private static readonly string[] RequiredFiles = [
        "ReflectionTimer.exe", "ReflectionTimer.dll", "ReflectionTimer.runtimeconfig.json", "ReflectionTimer.deps.json",
        "coreclr.dll", "hostfxr.dll", "hostpolicy.dll", "System.Private.CoreLib.dll", "System.Windows.Forms.dll",
        "Microsoft.Web.WebView2.WinForms.dll", "WebView2Loader.dll", "START-HERE.html", "START-HERE.txt",
        "google-sheets-script.gs", "THIRD-PARTY-NOTICES.txt", "AUDIO-NOTICES.txt", "Web/index.html",
        "Web/app.js", "Web/compact.html", "Web/themes.css", "Web/themes.js"
    ];

    internal DesktopUpdateClient(HttpClient? client = null, Func<string, Version?>? executableVersion = null)
    {
        ownsClient = client is null;
        this.client = client ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
        this.executableVersion = executableVersion ?? ReadExecutableVersion;
    }

    internal async Task<DesktopUpdateRelease?> CheckAsync(Version currentVersion, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(currentVersion);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var response = await SendAsync(LatestRelease, metadata: true, timeout.Token).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        EnsureSuccess(response);
        var bytes = await ReadBoundedAsync(response.Content, MaxMetadataBytes, timeout.Token).ConfigureAwait(false);
        try {
            using var document = JsonDocument.Parse(WithoutBom(bytes));
            var data = document.RootElement;
            if (data.ValueKind != JsonValueKind.Object || Boolean(data, "draft") || Boolean(data, "prerelease"))
                throw Invalid("Only published stable releases can be installed.");
            var tag = String(data, "tag_name");
            if (!StableTag.IsMatch(tag) || !Version.TryParse(tag[1..], out var version))
                throw Invalid("The release version is not a stable app version.");
            var installed = new Version(currentVersion.Major, currentVersion.Minor, Math.Max(0, currentVersion.Build));
            if (version <= installed) return null;
            var assetName = $"ReflectionTimer-{version}-win-x64.zip";
            if (!data.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array || assets.GetArrayLength() > 100)
                throw Invalid("The release does not contain a valid Windows download.");
            var matches = assets.EnumerateArray().Where(x => String(x, "name") == assetName).ToArray();
            if (matches.Length != 1) throw Invalid("The release must contain exactly one matching Windows download.");
            var asset = matches[0];
            var assetUrl = AssetUrl(String(asset, "browser_download_url"), version, assetName);
            if (String(asset, "state") != "uploaded" || !asset.TryGetProperty("size", out var sizeValue) || !sizeValue.TryGetInt64(out var size) || size is <= 0 or > MaxArchiveBytes)
                throw Invalid("The update download has an invalid size or is not ready.");
            var sha = "";
            if (asset.TryGetProperty("digest", out var digest) && digest.ValueKind is not JsonValueKind.Null) {
                var value = digest.GetString() ?? "";
                if (!value.StartsWith("sha256:", StringComparison.Ordinal) || !Hash.IsMatch(value[7..]))
                    throw Invalid("The update has an unsupported download checksum.");
                sha = value[7..];
            }
            if (sha.Length == 0) {
                var sidecarName = assetName + ".sha256.txt";
                var sidecars = assets.EnumerateArray().Where(x => String(x, "name") == sidecarName).ToArray();
                if (sidecars.Length != 1 || String(sidecars[0], "state") != "uploaded")
                    throw Invalid("The release has no verified download checksum. It cannot be installed.");
                var sidecar = sidecars[0];
                if (!sidecar.TryGetProperty("size", out var checksumSize) || !checksumSize.TryGetInt64(out var checksumLength) || checksumLength is <= 0 or > 4096)
                    throw Invalid("The release checksum file has an invalid size.");
                using var checksumResponse = await SendAsync(AssetUrl(String(sidecar, "browser_download_url"), version, sidecarName), false, timeout.Token).ConfigureAwait(false);
                EnsureSuccess(checksumResponse);
                var checksum = Encoding.UTF8.GetString(await ReadBoundedAsync(checksumResponse.Content, 4096, timeout.Token).ConfigureAwait(false)).Trim();
                var checksumPattern = @"^([a-fA-F0-9]{64}) [ *]" + Regex.Escape(assetName) + "$";
                var match = Regex.Match(checksum, checksumPattern, RegexOptions.CultureInvariant);
                if (!match.Success) throw Invalid("The checksum does not identify this exact update download.");
                sha = match.Groups[1].Value;
            }
            return new(version, assetName, assetUrl, size, sha.ToLowerInvariant(),
                new Uri($"https://github.com/AirealAce/Reflection-Timer/releases/tag/{tag}"));
        }
        catch (JsonException) { throw Invalid("GitHub returned unreadable release information. Please try again later."); }
        catch (InvalidOperationException) { throw Invalid("GitHub returned invalid release information. Please try again later."); }
    }

    internal async Task<string> DownloadAsync(DesktopUpdateRelease release, string stagingRoot,
        IProgress<int>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(release);
        if (release.Version.Build < 0 || release.Version.Revision >= 0 || release.AssetName != $"ReflectionTimer-{release.Version}-win-x64.zip"
            || release.Size is <= 0 or > MaxArchiveBytes || !Hash.IsMatch(release.Sha256)) throw Invalid("Invalid update download.");
        _ = AssetUrl(release.AssetUrl.AbsoluteUri, release.Version, release.AssetName);
        var root = Path.GetFullPath(stagingRoot);
        RejectRedirectedDirectory(root);
        Directory.CreateDirectory(root);
        var job = Path.Combine(root, "update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(job);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(10));
        try {
            var archivePath = Path.Combine(job, "download.zip");
            using (var response = await SendAsync(release.AssetUrl, false, timeout.Token).ConfigureAwait(false)) {
                EnsureSuccess(response);
                if (response.Content.Headers.ContentLength is { } advertised && advertised != release.Size)
                    throw Invalid("The update download size changed. Check for updates again.");
                await using var input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
                await using var output = new FileStream(archivePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
                using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[81920];
                long total = 0;
                progress?.Report(0);
                var lastProgress = 0;
                int count;
                while ((count = await input.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) > 0) {
                    total += count;
                    if (total > release.Size || total > MaxArchiveBytes) throw Invalid("The update download exceeded its expected size.");
                    hasher.AppendData(buffer, 0, count);
                    await output.WriteAsync(buffer.AsMemory(0, count), timeout.Token).ConfigureAwait(false);
                    var percent = (int)(total * 90 / release.Size);
                    if (percent != lastProgress) { progress?.Report(percent); lastProgress = percent; }
                }
                if (total != release.Size || !Convert.ToHexString(hasher.GetHashAndReset()).Equals(release.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw Invalid("The update download failed its checksum. Nothing was installed.");
            }
            var payload = Path.Combine(job, $"ReflectionTimer-{release.Version}-win-x64");
            await ExtractAsync(archivePath, job, Path.GetFileName(payload), timeout.Token).ConfigureAwait(false);
            progress?.Report(92);
            await VerifyPackageAsync(payload, release.Version, timeout.Token, executableVersion).ConfigureAwait(false);
            timeout.Token.ThrowIfCancellationRequested();
            File.Delete(archivePath);
            progress?.Report(100);
            return payload;
        }
        catch {
            // This exact, freshly-created directory is the only deletion target.
            if (Directory.Exists(job) && Path.GetDirectoryName(job) == root) Directory.Delete(job, true);
            throw;
        }
    }

    internal static async Task VerifyPackageAsync(string payload, Version expectedVersion,
        CancellationToken cancellationToken = default, Func<string, Version?>? executableVersion = null)
    {
        var root = Path.GetFullPath(payload);
        RejectRedirectedDirectory(root);
        var manifestPath = Path.Combine(root, "package-manifest.json");
        if (!File.Exists(manifestPath) || new FileInfo(manifestPath).Length > MaxManifestBytes) throw Invalid("The update package manifest is missing or too large.");
        var files = EnumeratePackageFiles(root);
        try {
            using var document = JsonDocument.Parse(WithoutBom(await File.ReadAllBytesAsync(manifestPath, cancellationToken).ConfigureAwait(false)));
            var manifest = document.RootElement;
            if (!manifest.TryGetProperty("FormatVersion", out var format) || !format.TryGetInt32(out var formatVersion) || formatVersion != 1
                || String(manifest, "Version") != expectedVersion.ToString() || String(manifest, "Runtime") != "win-x64")
                throw Invalid("The update package does not match this Windows release.");
            var runtimeVersion = String(manifest, "RuntimeVersion");
            if (!Regex.IsMatch(runtimeVersion, @"^10\.0\.(0|[1-9]\d*)$", RegexOptions.CultureInvariant))
                throw Invalid("The update does not contain a supported self-contained runtime.");
            if (!manifest.TryGetProperty("Files", out var entries) || entries.ValueKind != JsonValueKind.Array || entries.GetArrayLength() is <= 0 or > MaxFiles)
                throw Invalid("The update file manifest is invalid.");
            var expectedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in entries.EnumerateArray()) {
                cancellationToken.ThrowIfCancellationRequested();
                var path = SafeRelativePath(String(entry, "Path"));
                var hash = String(entry, "Sha256");
                if (path.Equals("package-manifest.json", StringComparison.OrdinalIgnoreCase) || !expectedFiles.Add(path) || !Hash.IsMatch(hash))
                    throw Invalid("The update file manifest contains a duplicate or invalid entry.");
                ValidatePublicFile(path);
                if (!files.TryGetValue(path, out var file)) throw Invalid("A file named in the update manifest is missing.");
                await using var stream = File.OpenRead(file);
                var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
                if (!actual.Equals(hash, StringComparison.OrdinalIgnoreCase)) throw Invalid("An update file failed its checksum. Nothing was installed.");
            }
            if (files.Count != expectedFiles.Count + 1 || files.Keys.Any(path => !path.Equals("package-manifest.json", StringComparison.OrdinalIgnoreCase) && !expectedFiles.Contains(path)))
                throw Invalid("The update contains files that were not listed in its manifest.");
            if (RequiredFiles.Any(path => !expectedFiles.Contains(path))) throw Invalid("The update is missing required app or runtime files.");
            VerifyX64Executable(Path.Combine(root, "ReflectionTimer.exe"));
            var version = (executableVersion ?? ReadExecutableVersion)(Path.Combine(root, "ReflectionTimer.exe"));
            if (version is null || version.Major != expectedVersion.Major || version.Minor != expectedVersion.Minor || version.Build != expectedVersion.Build || version.Revision is > 0)
                throw Invalid("The update executable does not match the release version.");
            VerifyRuntime(Path.Combine(root, "ReflectionTimer.runtimeconfig.json"), runtimeVersion);
        }
        catch (JsonException) { throw Invalid("The update package contains an unreadable manifest or runtime configuration."); }
        catch (InvalidOperationException) { throw Invalid("The update package contains an invalid manifest or runtime configuration."); }
    }

    private async Task<HttpResponseMessage> SendAsync(Uri uri, bool metadata, CancellationToken cancellationToken)
    {
        for (var redirects = 0; ; redirects++) {
            if (!(metadata ? uri == LatestRelease : IsTrustedDownload(uri))) throw Invalid("The update URL is not a trusted GitHub download.");
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd("ReflectionTimer-Desktop-Updater/1.0");
            request.Headers.Accept.ParseAdd(metadata ? "application/vnd.github+json" : "application/octet-stream");
            if (metadata) request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (response.RequestMessage?.RequestUri is { } final && !(metadata ? final == LatestRelease : IsTrustedDownload(final))) {
                response.Dispose(); throw Invalid("GitHub redirected the update to an untrusted download.");
            }
            if ((int)response.StatusCode is not (301 or 302 or 303 or 307 or 308)) return response;
            var location = response.Headers.Location;
            response.Dispose();
            if (metadata || redirects >= 4 || location is null) throw Invalid("The update download returned an invalid redirect.");
            uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, int limit, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > limit) throw Invalid("GitHub returned an oversized update response.");
        await using var input = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0) {
            if (output.Length + count > limit) throw Invalid("GitHub returned an oversized update response.");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }

    private static async Task ExtractAsync(string archivePath, string job, string packageName, CancellationToken cancellationToken)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        if (archive.Entries.Count is <= 0 or > MaxFiles * 2) throw Invalid("The update ZIP has too many files.");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var filePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long expanded = 0;
        foreach (var entry in archive.Entries) {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = entry.FullName.EndsWith('/');
            var path = SafeRelativePath(directory ? entry.FullName[..^1] : entry.FullName);
            if (path != packageName && !path.StartsWith(packageName + "/", StringComparison.Ordinal)) throw Invalid("The update ZIP has an unexpected root folder.");
            if (!paths.Add(path) || ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000 || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
                throw Invalid("The update ZIP contains duplicate or redirected files.");
            if (directory) {
                if (entry.Length != 0) throw Invalid("The update ZIP has an invalid directory.");
                continue;
            }
            if (path == packageName || entry.Length is < 0 or > MaxFileBytes || (expanded += entry.Length) > MaxExpandedBytes || !filePaths.Add(path))
                throw Invalid("The update ZIP exceeds safe extraction limits.");
            ValidatePublicFile(path[(packageName.Length + 1)..]);
        }
        if (filePaths.Count > MaxFiles || filePaths.Any(path => path.Split('/').Select((_, i) => string.Join('/', path.Split('/').Take(i + 1))).SkipLast(1).Any(filePaths.Contains)))
            throw Invalid("The update ZIP contains conflicting file paths.");
        foreach (var entry in archive.Entries) {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.FullName.EndsWith('/')) continue;
            var destination = Path.GetFullPath(Path.Combine(job, entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
            if (!destination.StartsWith(job + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw Invalid("The update ZIP contains an unsafe path.");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await using var input = entry.Open();
            await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
            var buffer = new byte[81920];
            long written = 0;
            int count;
            while ((count = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0) {
                written += count;
                if (written > entry.Length || written > MaxFileBytes) throw Invalid("An update ZIP file exceeded its expected size.");
                await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
            }
            if (written != entry.Length) throw Invalid("The update ZIP is incomplete.");
        }
    }

    private static Dictionary<string, string> EnumeratePackageFiles(string root)
    {
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>(); pending.Push(root);
        long total = 0;
        while (pending.TryPop(out var directory)) {
            foreach (var path in Directory.EnumerateFileSystemEntries(directory)) {
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw Invalid("The update package contains redirected files.");
                if ((attributes & FileAttributes.Directory) != 0) { pending.Push(path); continue; }
                var relative = SafeRelativePath(Path.GetRelativePath(root, path).Replace('\\', '/'));
                ValidatePublicFile(relative);
                var length = new FileInfo(path).Length;
                if (length > MaxFileBytes || (total += length) > MaxExpandedBytes || files.Count >= MaxFiles || !files.TryAdd(relative, path))
                    throw Invalid("The update package exceeds safe file limits.");
            }
        }
        return files;
    }

    private static void VerifyRuntime(string path, string expectedRuntime)
    {
        if (new FileInfo(path).Length > MaxMetadataBytes) throw Invalid("The update runtime configuration is too large.");
        using var document = JsonDocument.Parse(WithoutBom(File.ReadAllBytes(path)));
        if (!document.RootElement.TryGetProperty("runtimeOptions", out var runtime)
            || runtime.TryGetProperty("framework", out _) || runtime.TryGetProperty("frameworks", out _)
            || String(runtime, "tfm") != "net10.0" || !runtime.TryGetProperty("includedFrameworks", out var frameworks) || frameworks.ValueKind != JsonValueKind.Array)
            throw Invalid("The update is not a self-contained app. A .NET installation must not be required.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var framework in frameworks.EnumerateArray()) {
            if (!names.Add(String(framework, "name")) || String(framework, "version") != expectedRuntime) throw Invalid("The update runtime version does not match its manifest.");
        }
        if (!names.SetEquals(["Microsoft.NETCore.App", "Microsoft.WindowsDesktop.App"])) throw Invalid("The update is missing required self-contained runtime components.");
    }

    private static string SafeRelativePath(string path)
    {
        if (path.Length is <= 0 or > 220 || path.Contains('\\') || path.Contains(':') || path.Contains('%') || path.StartsWith('/') || path.Any(char.IsControl))
            throw Invalid("The update contains an unsafe file path.");
        foreach (var part in path.Split('/')) {
            if (part.Length == 0 || part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ') || part.IndexOfAny(['<', '>', '"', '|', '?', '*']) >= 0
                || Regex.IsMatch(part, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                throw Invalid("The update contains an unsafe file path.");
        }
        return path;
    }

    private static void ValidatePublicFile(string path)
    {
        var name = path.Split('/')[^1];
        if (!AllowedExtensions.Contains(Path.GetExtension(name)) || path.Split('/').Any(x => x.StartsWith('.') || x.Equals("profiles", StringComparison.OrdinalIgnoreCase))
            || name.StartsWith("state.", StringComparison.OrdinalIgnoreCase) || name.StartsWith("diagnostics.", StringComparison.OrdinalIgnoreCase)
            || new[] { "native-host.json", "settings.json", "config.json", "appsettings.json", "outbox.json", "drafts.json", "connections.json", "preferences.json" }.Contains(name, StringComparer.OrdinalIgnoreCase)
            || (Path.GetExtension(name).Equals(".mp3", StringComparison.OrdinalIgnoreCase) && (path.Contains('/') || !AudioNames.Contains(name))))
            throw Invalid("The update contains personal configuration or an unexpected package file.");
    }

    private static Uri AssetUrl(string url, Version version, string assetName)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !IsSecure(uri) || !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            || uri.Query.Length != 0 || uri.AbsolutePath != $"/AirealAce/Reflection-Timer/releases/download/v{version}/{assetName}")
            throw Invalid("The release download does not belong to this app's GitHub repository.");
        return uri;
    }

    private static bool IsTrustedDownload(Uri uri) => IsSecure(uri) && (
        (uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) && uri.Query.Length == 0
            && uri.AbsolutePath.StartsWith("/AirealAce/Reflection-Timer/releases/download/", StringComparison.Ordinal))
        || uri.Host.Equals("release-assets.githubusercontent.com", StringComparison.OrdinalIgnoreCase)
        || uri.Host.Equals("objects.githubusercontent.com", StringComparison.OrdinalIgnoreCase));
    private static bool IsSecure(Uri uri) => uri.IsAbsoluteUri && uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0;
    private static string String(JsonElement value, string name) => value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
        ? property.GetString()! : throw Invalid("GitHub or the update package returned an invalid " + name + ".");
    private static bool Boolean(JsonElement value, string name) => value.TryGetProperty(name, out var property) && property.ValueKind is JsonValueKind.True or JsonValueKind.False
        ? property.GetBoolean() : throw Invalid("GitHub returned invalid release information.");
    private static Version? ReadExecutableVersion(string path) => Version.TryParse(FileVersionInfo.GetVersionInfo(path).FileVersion, out var version) ? version : null;
    private static ReadOnlyMemory<byte> WithoutBom(byte[] bytes) => bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? bytes.AsMemory(3) : bytes;
    private static void VerifyX64Executable(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);
        if (stream.Length < 64 || reader.ReadUInt16() != 0x5A4D) throw Invalid("The update executable is not a Windows app.");
        stream.Position = 60;
        var peOffset = reader.ReadInt32();
        if (peOffset < 64 || peOffset > stream.Length - 26) throw Invalid("The update executable has an invalid Windows header.");
        stream.Position = peOffset;
        if (reader.ReadUInt32() != 0x00004550 || reader.ReadUInt16() != 0x8664) throw Invalid("The update executable is not a Windows x64 app.");
        stream.Position = peOffset + 24;
        if (reader.ReadUInt16() != 0x20B) throw Invalid("The update executable is not a Windows x64 app.");
    }
    private static void RejectRedirectedDirectory(string root)
    {
        for (var directory = new DirectoryInfo(root); directory is not null; directory = directory.Parent)
            if (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0) throw Invalid("Choose an update staging folder without directory links.");
    }
    private static InvalidDataException Invalid(string message) => new(message);
    private static void EnsureSuccess(HttpResponseMessage response)
    {
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            throw new HttpRequestException("GitHub is temporarily limiting update checks. Please try again later.", null, response.StatusCode);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException("The update could not be downloaded from GitHub. Please try again later.", null, response.StatusCode);
    }
    public void Dispose() { if (ownsClient) client.Dispose(); }
}
