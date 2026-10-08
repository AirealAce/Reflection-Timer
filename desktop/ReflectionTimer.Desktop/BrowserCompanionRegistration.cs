using Microsoft.Win32;
using System.Text;
using System.Text.Json;

namespace ReflectionTimer.Desktop;

/// <summary>Explicit, per-user setup for the optional browser companion.</summary>
public static class BrowserCompanionRegistration
{
    public const string ManifestFileName = "native-host.json";
    private static readonly string[] RegistryPaths = [
        @"Software\Google\Chrome\NativeMessagingHosts\" + BrowserCompanionHost.HostName,
        @"Software\Microsoft\Edge\NativeMessagingHosts\" + BrowserCompanionHost.HostName
    ];

    /// <summary>
    /// Register only after the user explicitly enables/configures the companion.
    /// directory is the installed browser-companion folder, not a user profile.
    /// This does not install or enable a browser extension.
    /// </summary>
    public static string Register(string exePath, string directory) => Register(exePath, directory,
        File.Exists, Directory.Exists,
        (path, content) => File.WriteAllText(path, content, new UTF8Encoding(false)),
        (keyPath, manifestPath) => {
            using var key = Registry.CurrentUser.CreateSubKey(keyPath);
            key.SetValue("", manifestPath, RegistryValueKind.String);
        });

    internal static string Register(string exePath, string directory, Func<string, bool> fileExists,
        Func<string, bool> directoryExists, Action<string, string> writeFile, Action<string, string> setRegistry)
    {
        var paths = ValidatePaths(exePath, directory, fileExists, directoryExists);
        var manifest = BuildManifest(paths.Executable);
        // Finish all preflight checks before writing either the manifest or keys.
        // Reusing the fixed filename makes subsequent upgrades idempotent.
        writeFile(paths.Manifest, manifest);
        foreach (var key in RegistryPaths) setRegistry(key, paths.Manifest);
        return paths.Manifest;
    }

    public static string BuildManifest(string exePath)
    {
        if (!Path.IsPathFullyQualified(exePath) || !string.Equals(Path.GetFileName(exePath), "ReflectionTimer.exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Choose the installed ReflectionTimer.exe.", nameof(exePath));
        return JsonSerializer.Serialize(new {
            name = BrowserCompanionHost.HostName,
            description = "Reflection Timer optional browser focus metadata companion",
            path = Path.GetFullPath(exePath), type = "stdio",
            allowed_origins = new[] { BrowserCompanionHost.ExtensionOrigin }
        }, new JsonSerializerOptions { WriteIndented = true });
    }

    internal static (string Executable, string Manifest) ValidatePaths(string exePath, string directory,
        Func<string, bool> fileExists, Func<string, bool> directoryExists)
    {
        if (string.IsNullOrWhiteSpace(exePath) || string.IsNullOrWhiteSpace(directory) ||
            !Path.IsPathFullyQualified(exePath) || !Path.IsPathFullyQualified(directory))
            throw new ArgumentException("Companion setup requires absolute installed app paths.");
        var executable = Path.GetFullPath(exePath);
        var folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        if (!string.Equals(Path.GetFileName(executable), "ReflectionTimer.exe", StringComparison.OrdinalIgnoreCase) || !fileExists(executable))
            throw new ArgumentException("The installed ReflectionTimer.exe was not found.");
        var appFolder = Path.GetDirectoryName(executable)!;
        var expectedFolder = Path.Combine(appFolder, "browser-companion");
        if (!string.Equals(folder, expectedFolder, StringComparison.OrdinalIgnoreCase) || !directoryExists(folder))
            throw new ArgumentException("Choose the browser-companion folder alongside the installed app.");
        if (!fileExists(Path.Combine(folder, "manifest.json")) || !fileExists(Path.Combine(folder, "background.js")))
            throw new ArgumentException("The installed browser companion files are incomplete.");
        return (executable, Path.Combine(folder, ManifestFileName));
    }
}
