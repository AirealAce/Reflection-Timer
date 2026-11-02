using System.Text.RegularExpressions;

namespace ReflectionTimer.Core;

// An App bookmark identifies an installation, never a particular process or
// window. Keep these native hints inside the encrypted profile.
public static class FocusAppIdentity
{
    public static bool IsValid(string? processPath, string? applicationId)
        => Key(processPath, applicationId).Length > 0;

    public static string Key(string? processPath, string? applicationId)
    {
        if (!string.IsNullOrEmpty(applicationId)) {
            // Windows package/explicit AppUserModelIDs contain no whitespace.
            // Reject malformed IDs rather than falling back to a shared host.
            if (applicationId.Length > 512 || applicationId.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c is '\\' or '/')) return "";
            return "aumid:" + applicationId;
        }
        if (string.IsNullOrWhiteSpace(processPath) || processPath.Length > 32767 || processPath.Any(char.IsControl)) return "";
        try {
            if (!Path.IsPathFullyQualified(processPath)) return "";
            var path = Path.GetFullPath(processPath);
            var executable = Path.GetFileName(path);
            if (!executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                || IsSharedHost(executable)) return "";
            // Only the version component of an actual WindowsApps package may
            // change. Publisher, installation root and relative exe stay exact.
            var package = Regex.Match(path,
                @"^([a-z]:\\(?:[^\\]+\\)*WindowsApps\\[a-z0-9.-]+_)\d+\.\d+\.\d+\.\d+(_(?:x86|x64|arm|arm64|neutral)_[a-z0-9.-]*_[a-z0-9]{13}\\.+)$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            return (package.Success ? "windows-package:" + package.Groups[1].Value + package.Groups[2].Value : "file:" + path).ToUpperInvariant();
        } catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { return ""; }
    }

    public static bool IsSharedHost(string executable)
        => executable.ToLowerInvariant() is "applicationframehost.exe" or "wwahost.exe" or "rundll32.exe" or "dllhost.exe" or "svchost.exe" or "runtimebroker.exe";
}
