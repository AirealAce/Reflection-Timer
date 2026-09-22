using System.Text.Json;
using ReflectionTimer.Accessible;
using ReflectionTimer.Core;
using ReflectionTimer.Desktop;

static class ProfileStorageTests
{
    internal static void Run(Action<bool, string> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "ReflectionTimer-ProfileStorage-" + Guid.NewGuid().ToString("N"));
        var legacy = Path.Combine(root, "old"); var shared = Path.Combine(root, "shared");
        Directory.CreateDirectory(root);
        try {
            ProfileStorage.MigrateLegacy(shared, legacy);
            check(!Directory.Exists(shared), "A fresh profile does not manufacture old settings");
            var state = new AppState { Theme = AppColorTheme.Glamour, StartAtLogin = true,
                Connection = new() { ApiToken = "synthetic-private-token" },
                Timer = new() { DurationSeconds = 2222, RemainingSeconds = 123, Volume = 36 },
                Prompts = [new(Guid.NewGuid(), 1, 90, 36, false, "Synthetic private draft")],
                Audio = new() { LowTime = new() { Behavior = SoundBehavior.Polite, Volume = 24, FadeOutAfterMessageSent = true } } };
            var store = new EncryptedStore(legacy); store.Save(state); store.Save(state);
            using(var log=new DiagnosticLog(legacy)){log.Record("theme.changed", value: 3);}
            var original = File.ReadAllBytes(Path.Combine(legacy, "state.dat"));
            ProfileStorage.MigrateLegacy(shared, legacy);
            check(JsonSerializer.Serialize(new EncryptedStore(shared).Load(), DataJson.Options) == JsonSerializer.Serialize(state, DataJson.Options),
                "Migration retains every setting, credential, timer value and private draft");
            check(original.SequenceEqual(File.ReadAllBytes(Path.Combine(shared, "state.dat")))
                && original.SequenceEqual(File.ReadAllBytes(Path.Combine(legacy, "state.dat"))), "Migration keeps both encrypted copies byte-for-byte intact");
            check(File.Exists(Path.Combine(shared, "state.dat.bak")) && File.Exists(Path.Combine(shared, "diagnostics.dat")),
                "Migration preserves encrypted recovery and diagnostic files");
            check(!File.ReadAllText(Path.Combine(shared, "state.dat")).Contains("Synthetic private draft"), "Migration never writes plaintext profile data");
            var changed = state with { Theme = AppColorTheme.Light, Timer = state.Timer with { Volume = 71 } };
            new EncryptedStore(shared).Save(changed);
            var anotherLauncher = Path.Combine(root, "other-launcher"); new EncryptedStore(anotherLauncher).Save(new());
            ProfileStorage.MigrateLegacy(shared, anotherLauncher);
            check(new EncryptedStore(shared).Load().Theme == AppColorTheme.Light && new EncryptedStore(shared).Load().Timer.Volume == 71,
                "A second launcher with stale/default AppData cannot replace shared preferences");
            File.WriteAllText(Path.Combine(shared, "state.dat"), "corrupt");
            ProfileStorage.MigrateLegacy(shared, legacy);
            check(File.ReadAllText(Path.Combine(shared, "state.dat")) == "corrupt", "Migration never overwrites a damaged canonical profile with legacy data");
            var unreadable = Path.Combine(root, "unreadable"); Directory.CreateDirectory(unreadable);
            File.WriteAllText(Path.Combine(unreadable, "state.dat"), "corrupt");
            var rejected = Path.Combine(root, "rejected");
            try { ProfileStorage.MigrateLegacy(rejected, unreadable); throw new Exception("Unreadable migration was accepted"); }
            catch (System.Security.Cryptography.CryptographicException) { }
            check(!Directory.Exists(rejected), "Unreadable legacy data fails closed without publishing defaults");
            var incomplete = Path.Combine(root, "incomplete"); Directory.CreateDirectory(incomplete);
            File.Copy(Path.Combine(legacy, "state.dat"), Path.Combine(incomplete, "state.dat.bak"));
            try { ProfileStorage.MigrateLegacy(incomplete, legacy); throw new Exception("Incomplete canonical data was replaced"); }
            catch (IOException) { }
            check(!File.Exists(Path.Combine(incomplete, "state.dat")), "An incomplete canonical profile is retained for recovery");
            var backupOnly = Path.Combine(root, "backup-only"); Directory.CreateDirectory(backupOnly);
            File.Copy(Path.Combine(legacy, "state.dat"), Path.Combine(backupOnly, "state.dat.bak"));
            try { ProfileStorage.MigrateLegacy(rejected, backupOnly); throw new Exception("Orphaned backup was ignored"); }
            catch (IOException) { }
            check(!Directory.Exists(rejected), "A legacy backup without its primary file never silently becomes a new profile");
            File.Copy(Path.Combine(legacy, "state.dat"), Path.Combine(unreadable, "state.dat.bak"));
            ProfileStorage.MigrateLegacy(rejected, unreadable);
            var recovered = new EncryptedStore(rejected); var recoveredState = recovered.Load();
            check(recoveredState.Theme == AppColorTheme.Glamour && recovered.RecoveryNotice is not null
                && File.ReadAllText(Path.Combine(unreadable, "state.dat")) == "corrupt", "A recoverable migration retains normal guarded backup recovery and leaves the source alone");
            try { ProfileStorage.MigrateLegacy(Path.Combine(legacy, "child"), legacy); throw new Exception("Nested migration was accepted"); }
            catch (ArgumentException) { }
            check(!Directory.Exists(Path.Combine(legacy, "child")), "Migration rejects overlapping profile directories");
        }
        finally {
            // This unique test-owned tree contains only the synthetic fixtures above.
            if (!Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)) throw new IOException("Unexpected test path.");
            Directory.Delete(root, true);
        }
    }
}
