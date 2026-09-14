using System.Security.Cryptography;
using ReflectionTimer.Core;
using ReflectionTimer.Desktop;

namespace ReflectionTimer.Accessible;

internal static class ProfileStorage
{
    // AppData may be transparently redirected by a packaged launcher. A path
    // directly under the Windows user profile is shared by all launch contexts
    // and is not the Documents/Desktop folder that OneDrive can synchronize.
    internal static string DirectoryPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".reflection-timer");
    internal static string LegacyDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ReflectionTimerDesktop");

    // Call only while holding the original desktop single-instance mutex.
    // Copy, never move, legacy data; publish the new directory only after every
    // encrypted file is verified. A completed migration always wins over any
    // stale copy that another launcher can still see in AppData.
    internal static void MigrateLegacy(string destination, string legacy)
    {
        destination = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar);
        legacy = Path.GetFullPath(legacy).TrimEnd(Path.DirectorySeparatorChar);
        if (destination.Equals(legacy, StringComparison.OrdinalIgnoreCase)
            || destination.StartsWith(legacy + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || legacy.StartsWith(destination + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The profile locations must be separate directories.");

        var current = Path.Combine(destination, "state.dat");
        if (File.Exists(current)) return;
        // An existing canonical backup is not a new profile. Do not replace it
        // with defaults or an older launcher's settings.
        if (Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any())
            throw new IOException("The shared profile is incomplete. Keep its files for recovery.");
        var previous = Path.Combine(legacy, "state.dat");
        if (!File.Exists(previous)) {
            if (File.Exists(previous + ".bak")) throw new IOException("The previous profile needs recovery before migration.");
            return;
        }
        RejectLink(legacy);
        RejectLink(previous);
        try { _ = EncryptedStore.Read<AppState>(previous); }
        catch when (File.Exists(previous + ".bak")) {
            RejectLink(previous + ".bak");
            _ = EncryptedStore.Read<AppState>(previous + ".bak");
            // EncryptedStore will perform its guarded recovery in the new copy.
        }

        var parent = Path.GetDirectoryName(destination) ?? throw new ArgumentException("A profile parent is required.");
        Directory.CreateDirectory(parent);
        var stage = Path.Combine(parent, Path.GetFileName(destination) + ".migration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        foreach (var file in Directory.EnumerateFiles(legacy, "*", SearchOption.TopDirectoryOnly)) {
            var name = Path.GetFileName(file);
            if (!(name == "state.dat" || name.StartsWith("state.dat.", StringComparison.Ordinal)
                || name == "diagnostics.dat" || name.StartsWith("diagnostics.dat.", StringComparison.Ordinal))) continue;
            RejectLink(file);
            var bytes = File.ReadAllBytes(file); // Already encrypted; never persist plaintext.
            var copy = Path.Combine(stage, name);
            using (var stream = new FileStream(copy, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough)) {
                stream.Write(bytes); stream.Flush(true);
            }
            if (!SHA256.HashData(bytes).SequenceEqual(SHA256.HashData(File.ReadAllBytes(copy))))
                throw new IOException("The profile copy could not be verified. The previous profile is unchanged.");
        }
        if (Directory.Exists(destination)) Directory.Delete(destination); // Verified empty above; non-recursive.
        Directory.Move(stage, destination);
    }

    private static void RejectLink(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("A profile file is redirected by a link. Keep the original files for recovery.");
    }
}
