using Microsoft.Win32;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace ReflectionTimer.Accessible;

internal static class PreviewStartup
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    internal static bool ValidProfile(string profile) => Regex.IsMatch(profile, @"\A[a-zA-Z0-9-]{1,40}\z");
    internal static (string? Profile, bool Tray, bool CheckConnection) Parse(string[] args)
    {
        string? profile = null; var tray = false; var check = false;
        for (var i = 0; i < args.Length; i++) {
            if (args[i] == "--tray" && !tray) tray = true;
            else if (args[i] == "--check-connection" && !check) check = true;
            else if (args[i] == "--profile" && profile is null && i + 1 < args.Length) profile = args[++i];
            else throw new ArgumentException("Use optional --tray, --check-connection, or --profile followed by a short test profile name.");
        }
        if (profile is not null && !ValidProfile(profile)) throw new ArgumentException("Profile names accept only letters, digits, and hyphens.");
        return (profile, tray, check);
    }
    internal static string DirectoryFor(string? profile) => profile is null
        ? ProfileStorage.DirectoryPath
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ReflectionTimerAccessibilityPreview", ValidProfile(profile) ? profile : throw new ArgumentException("Invalid profile."));
    // Share the installed app's identity to prevent simultaneous edits by old and new versions.
    internal static string InstanceSuffix(string directory) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(directory)))[..16];
    internal static string MutexName(string? profile) => profile is null
        ? @"Local\ReflectionTimerDesktop-" + InstanceSuffix(ProfileStorage.LegacyDirectory)
        : @"Local\ReflectionTimerAccessibilityPreview-" + (ValidProfile(profile) ? profile.ToLowerInvariant() : throw new ArgumentException("Invalid profile."));
    internal static string ShowEventName(string? profile) => profile is null
        ? @"Local\ReflectionTimerDesktopShow-" + InstanceSuffix(ProfileStorage.LegacyDirectory)
        : @"Local\ReflectionTimerAccessibilityPreviewShow-" + profile.ToLowerInvariant();
    internal static string ValueName(string? profile)
    {
        if (profile is null) return "Reflection Timer Desktop";
        if (!ValidProfile(profile)) throw new ArgumentException("Invalid profile.");
        return "Reflection Timer accessibility preview (" + profile.ToLowerInvariant() + ")";
    }
    internal static string Command(string executable, string? profile)
    {
        _ = ValueName(profile);
        if (executable.Contains('"') || !Path.IsPathFullyQualified(executable)) throw new ArgumentException("Invalid executable path.");
        return $"\"{executable}\"" + (profile is null ? "" : $" --profile {profile}") + " --tray";
    }
    internal static void Set(string? profile, bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(ValueName(profile), Command(Environment.ProcessPath!, profile));
        else key.DeleteValue(ValueName(profile), false);
    }
}
