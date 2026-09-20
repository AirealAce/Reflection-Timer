using ReflectionTimer.Core;

namespace ReflectionTimer.Desktop;

public static class SoundLibrary
{
    public static IEnumerable<LibrarySound> Tracks => Enum.GetValues<LibrarySound>().Where(x => x is not (LibrarySound.Default or LibrarySound.None)
        && File.Exists(Path.Combine(AppContext.BaseDirectory, FileName(x))));
    public static string DefaultName(SoundEvent kind) => File.Exists(Path.Combine(AppContext.BaseDirectory, FileName(DefaultFor(kind))))
        ? Name(DefaultFor(kind)) : "Built-in " + (kind == SoundEvent.SessionEnd ? "session-end tone" : kind == SoundEvent.LowTime ? "low-time tone" : kind.ToString().ToLowerInvariant() + " tone");
    public static LibrarySound DefaultFor(SoundEvent kind) => kind switch {
        SoundEvent.Success => LibrarySound.LevelUp, SoundEvent.Failure => LibrarySound.OutOfHealth,
        SoundEvent.LowTime or SoundEvent.TimeReached => LibrarySound.TrainerBattle, _ => LibrarySound.SessionEnd
    };
    public static string Name(LibrarySound track) => track switch {
        LibrarySound.SessionEnd => "Original extension sound", LibrarySound.ObtainedItem => "Obtained an Item",
        LibrarySound.LevelUp => "Level Up", LibrarySound.PokemonHealed => "Pokémon Healed",
        LibrarySound.KeyItem => "Obtained a Key Item", LibrarySound.TrainerBattle => "Battle (Trainer)",
        LibrarySound.ChampionBattle => "Battle (Champion)", LibrarySound.OutOfHealth => "Out of Health",
        LibrarySound.None => "None", _ => "Default"
    };
    public static string FileName(LibrarySound track) => track switch {
        LibrarySound.ObtainedItem => "pokemon-obtained-item.mp3", LibrarySound.LevelUp => "pokemon-level-up.mp3",
        LibrarySound.PokemonHealed => "pokemon-healed.mp3", LibrarySound.KeyItem => "pokemon-key-item.mp3",
        LibrarySound.TrainerBattle => "pokemon-battle-trainer.mp3", LibrarySound.ChampionBattle => "pokemon-battle-champion.mp3",
        LibrarySound.OutOfHealth => "kirby-out-of-health.mp3", LibrarySound.None => throw new ArgumentException("None has no audio file."), _ => "popup.mp3"
    };
    public static string? Resolve(SoundEvent kind, SoundSetting setting, string? directory = null)
    {
        if (setting.Track == LibrarySound.None) return null;
        if (setting.Mp3Path.Length > 0) return setting.Mp3Path;
        var path = Path.Combine(directory ?? AppContext.BaseDirectory, FileName(setting.Track == LibrarySound.Default ? DefaultFor(kind) : setting.Track));
        return setting.Track == LibrarySound.Default && !File.Exists(path) ? BuiltInTone.PathFor(kind) : path;
    }
    public static string Fallback(SoundEvent kind)
    {
        var path = Path.Combine(AppContext.BaseDirectory, FileName(DefaultFor(kind)));
        return File.Exists(path) ? path : File.Exists(Path.Combine(AppContext.BaseDirectory, "popup.mp3")) ? AlertSoundPlayer.BundledPath : BuiltInTone.PathFor(kind);
    }
    public static string Describe(SoundEvent kind, SoundSetting setting) => setting.Mp3Path.Length > 0 ? "Custom MP3 · " + Path.GetFileName(setting.Mp3Path)
        : setting.Track == LibrarySound.Default ? "Default · " + DefaultName(kind) : Name(setting.Track);
}
