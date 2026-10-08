using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using ReflectionTimer.Accessible;

/// <summary>Prepares and verifies an isolated real-helper fixture. The human/root agent controls app startup and saved shutdown.</summary>
static class DesktopUpdateInstallerProcessSmoke
{
    private const string Prefix = "ReflectionTimer-UpdateProcessSmoke-";
    private sealed record Fixture(string Root, string Plan, string Install, string Payload, string Profile,
        string CompanionHash, string CustomAudioHash, string PackageCssHash, string OldCssHash);

    internal static void Prepare(string payload)
    {
        payload = Path.TrimEndingDirectorySeparator(Path.GetFullPath(payload));
        if (!Directory.Exists(payload) || !File.Exists(Path.Combine(payload, "ReflectionTimer.exe"))) throw new IOException("A published self-contained payload is required.");
        var root = Path.Combine(Path.GetTempPath(), Prefix + Guid.NewGuid().ToString("N"));
        var install = Path.Combine(root, "installed app");
        Directory.CreateDirectory(install);
        foreach (var source in Directory.EnumerateFiles(payload, "*", SearchOption.AllDirectories)) {
            if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0) throw new IOException("Smoke payload cannot contain links.");
            var destination = Path.Combine(install, Path.GetRelativePath(payload, source));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(source, destination);
        }
        var css = Path.Combine(install, "Web", "themes.css");
        var packageCssHash = Hash(css);
        File.AppendAllText(css, "\n/* isolated old-installation update smoke fixture */\n");
        File.WriteAllText(Path.Combine(install, "smoke-old-only.txt"), "This old file must remain only in the backup.");
        var customAudio = Path.Combine(install, "custom-smoke.mp3");
        File.WriteAllText(customAudio, "Synthetic custom MP3 preservation fixture; never played.");
        var companion = Path.Combine(install, "browser-companion", "native-host.json");
        Directory.CreateDirectory(Path.GetDirectoryName(companion)!);
        File.WriteAllText(companion, JsonSerializer.Serialize(new {
            name = "com.reflectiontimer.focus", description = "Synthetic update process smoke fixture",
            path = Path.Combine(install, "ReflectionTimer.exe"), type = "stdio",
            allowed_origins = new[] { "chrome-extension://mgalafjodgnoeponalohbmdopkkbnfok/" }
        }));
        var profile = "update-smoke-" + Guid.NewGuid().ToString("N")[..12];
        var plan = DesktopUpdateInstaller.Prepare(payload, install, Path.Combine(install, "ReflectionTimer.exe"), ["--profile", profile]);
        var fixture = new Fixture(root, plan, install, payload, profile, Hash(companion), Hash(customAudio), packageCssHash, Hash(css));
        File.WriteAllText(Path.Combine(root, "smoke-fixture.json"), JsonSerializer.Serialize(fixture));
        Console.WriteLine(JsonSerializer.Serialize(new {
            fixture.Root, fixture.Plan, fixture.Install, fixture.Payload, fixture.Profile,
            Executable = Path.Combine(install, "ReflectionTimer.exe"),
            Helper = Path.Combine(payload, "ReflectionTimer.exe"),
            fixture.CompanionHash, fixture.CustomAudioHash, fixture.PackageCssHash, fixture.OldCssHash
        }));
    }

    internal static void Verify(string planPath)
    {
        var plan = JsonNode.Parse(File.ReadAllText(planPath))!.AsObject();
        var install = plan["Install"]!.GetValue<string>();
        var root = Path.GetDirectoryName(install)!;
        RequireOwnedRoot(root);
        var fixture = JsonSerializer.Deserialize<Fixture>(File.ReadAllText(Path.Combine(root, "smoke-fixture.json"))) ?? throw new IOException("Missing smoke fixture.");
        if (!string.Equals(Path.GetFullPath(fixture.Plan), Path.GetFullPath(planPath), StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(install, fixture.Install, StringComparison.OrdinalIgnoreCase)) throw new IOException("The smoke plan does not match its fixture.");
        var backup = plan["Backup"]!.GetValue<string>();
        if (!Path.GetFullPath(backup).StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("Unexpected smoke backup path.");
        var checks = 0;
        void Check(bool condition, string name) { if (!condition) throw new Exception(name); checks++; Console.WriteLine("PASS " + name); }
        Check(Directory.Exists(backup), "Real helper retained the complete previous app backup");
        Check(Hash(Path.Combine(install, "Web", "themes.css")) == fixture.PackageCssHash && Hash(Path.Combine(backup, "Web", "themes.css")) == fixture.OldCssHash,
            "Real helper replaced the old payload and retained the old contents in backup");
        Check(!File.Exists(Path.Combine(install, "smoke-old-only.txt")) && File.Exists(Path.Combine(backup, "smoke-old-only.txt")),
            "Real helper installed the new package instead of copying new files over obsolete ones");
        Check(Hash(Path.Combine(install, "custom-smoke.mp3")) == fixture.CustomAudioHash,
            "Real helper retained locally added custom audio byte for byte");
        Check(Hash(Path.Combine(install, "browser-companion", "native-host.json")) == fixture.CompanionHash,
            "Real helper retained the browser companion's final-path configuration byte for byte");
        Check(!DesktopUpdateInstaller.IsHostStopping(install), "Real helper released the temporary browser-host update signal");
        Check(!Directory.Exists(plan["Staging"]!.GetValue<string>()), "Real helper completed the atomic staging-folder swap");
        var arguments = plan["Arguments"]!.AsArray().Select(value => value!.GetValue<string>()).ToArray();
        Check(arguments.SequenceEqual(new[] { "--profile", fixture.Profile }), "Real helper's restart plan retains the isolated profile identity");
        foreach (var source in Directory.EnumerateFiles(fixture.Payload, "*", SearchOption.AllDirectories)) {
            var relative = Path.GetRelativePath(fixture.Payload, source);
            if (!File.Exists(Path.Combine(install, relative)) || Hash(source) != Hash(Path.Combine(install, relative)))
                throw new Exception("Real helper payload mismatch: " + relative);
        }
        Check(true, "Every real installed package file matches the verified downloaded payload");
        Console.WriteLine($"{checks} real update-helper process checks passed.");
    }

    private static void RequireOwnedRoot(string root)
    {
        var temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(root).StartsWith(temp + Prefix, StringComparison.OrdinalIgnoreCase)) throw new IOException("The smoke fixture is outside its test-owned temporary folder.");
    }
    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
