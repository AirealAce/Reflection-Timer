using ReflectionTimer.Core;

namespace ReflectionTimer.Desktop;

public static class SoundLibrary
{
    public static IEnumerable<LibrarySound> Tracks => Available(AppContext.BaseDirectory);
    private static IEnumerable<LibrarySound> Available(string directory) => Enum.GetValues<LibrarySound>().Where(x => RandomAudio.IsTrack(x)
        && File.Exists(Path.Combine(directory, FileName(x))));
    public static string DefaultName(SoundEvent kind) => File.Exists(Path.Combine(AppContext.BaseDirectory, FileName(DefaultFor(kind))))
        ? Name(DefaultFor(kind)) : "Built-in " + (kind == SoundEvent.SessionEnd ? "session-end tone" : kind == SoundEvent.LowTime ? "low-time tone" : kind.ToString().ToLowerInvariant() + " tone");
    public static LibrarySound DefaultFor(SoundEvent kind) => kind switch {
        SoundEvent.Success => LibrarySound.LevelUp, SoundEvent.Failure => LibrarySound.OutOfHealth,
        SoundEvent.LowTime or SoundEvent.TimeReached or SoundEvent.FocusLost => LibrarySound.TrainerBattle, _ => LibrarySound.SessionEnd
    };
    public static string Name(LibrarySound track) => track switch {
        LibrarySound.SessionEnd => "Original extension sound", LibrarySound.ObtainedItem => "Obtained an Item",
        LibrarySound.LevelUp => "Level Up", LibrarySound.PokemonHealed => "Pokémon Healed",
        LibrarySound.KeyItem => "Obtained a Key Item", LibrarySound.TrainerBattle => "Battle (Trainer)",
        LibrarySound.ChampionBattle => "Battle (Champion)", LibrarySound.OutOfHealth => "Out of Health",
        LibrarySound.RgbyTrainerBattle => "Pokemon Red Green Blue Yellow - 10. Battle! (Trainer Battle)",
        LibrarySound.RgbyWildBattle => "Pokemon Red Green Blue Yellow - 14 Battle! (Wild Pokémon)",
        LibrarySound.JohtoWildDay => "Pokemon Gold Silver Crystal - 17. Battle! (Wild Pokémon - Johto - Day)",
        LibrarySound.JohtoWildNight => "Pokemon Gold Silver Crystal - 18. Battle! (Wild Pokémon - Johto - Night)",
        LibrarySound.RgbyGymLeader => "Pokemon Red Green Blue Yellow - 28. Battle! (Gym Leader)",
        LibrarySound.RegiBattle => "Pokemon Diamond Pearl Platinum - 200. Battle! (Regirock - Regice - Registeel)",
        LibrarySound.RgbyFinalRival => "Pokemon Red Green Blue Yellow - 51 Final Battle! (Rival)",
        LibrarySound.HgssHoOh => "Pokemon HeartGold SoulSilver - 111. Battle! (Ho-Oh)",
        LibrarySound.KantoWild => "Pokemon Gold Silver Crystal - 108. Battle! (Wild Pokémon - Kanto)",
        LibrarySound.KantoGymLeader => "Pokemon Gold Silver Crystal - 106. Battle! (Gym Leader - Kanto)",
        LibrarySound.KantoTrainer => "Pokemon Gold Silver Crystal - 105. Battle! (Trainer Battle - Kanto)",
        LibrarySound.DppDialgaPalkia => "Pokemon Diamond Pearl Platinum - 85. Battle! (Dialga - Palkia)",
        LibrarySound.DppGiratina => "Pokemon Diamond Pearl Platinum - 89. Battle! (Giratina)",
        LibrarySound.DppLakeTrio => "Pokemon Diamond Pearl Platinum - 134. Battle! (Azelf - Mesprit - Uxie)",
        LibrarySound.DppEliteFour => "Pokemon Diamond Pearl Platinum - 165. Battle! (Elite Four)",
        LibrarySound.DppChampion => "Pokemon Diamond Pearl Platinum - 168. Battle! (Champion)",
        LibrarySound.FrlgTrainer => "Pokemon FireRed LeafGreen - 1-11. Battle! (Trainer)",
        LibrarySound.FrlgWild => "Pokemon FireRed LeafGreen - 1-18. Battle! (Wild Pokémon)",
        LibrarySound.FrlgGymLeader => "Pokemon FireRed LeafGreen - 1-27. Battle! (Gym Leader)",
        LibrarySound.FrlgLegendary => "Pokemon FireRed LeafGreen - 1-53. Battle! (Legendary Pokémon)",
        LibrarySound.FrlgMewtwo => "Pokemon FireRed LeafGreen - 1-68. Battle! (Mewtwo)",
        LibrarySound.FrlgFinalRival => "Pokemon FireRed LeafGreen - 1-70. Final Battle! (Rival)",
        LibrarySound.FrlgDeoxys => "Pokemon FireRed LeafGreen - 2-02. Battle! (Deoxys)",
        LibrarySound.Random => "Random", LibrarySound.None => "None", _ => "Default"
    };
    public static string FileName(LibrarySound track) => track switch {
        LibrarySound.ObtainedItem => "pokemon-obtained-item.mp3", LibrarySound.LevelUp => "pokemon-level-up.mp3",
        LibrarySound.PokemonHealed => "pokemon-healed.mp3", LibrarySound.KeyItem => "pokemon-key-item.mp3",
        LibrarySound.TrainerBattle => "pokemon-battle-trainer.mp3", LibrarySound.ChampionBattle => "pokemon-battle-champion.mp3",
        LibrarySound.OutOfHealth => "kirby-out-of-health.mp3",
        LibrarySound.RgbyTrainerBattle => "pokemon-rgby-trainer-battle.mp3", LibrarySound.RgbyWildBattle => "pokemon-rgby-wild-battle.mp3",
        LibrarySound.JohtoWildDay => "pokemon-gsc-wild-johto-day.mp3", LibrarySound.JohtoWildNight => "pokemon-gsc-wild-johto-night.mp3",
        LibrarySound.RgbyGymLeader => "pokemon-rgby-gym-leader.mp3", LibrarySound.RegiBattle => "pokemon-dpp-regi-battle.mp3",
        LibrarySound.RgbyFinalRival => "pokemon-rgby-final-rival.mp3",
        LibrarySound.HgssHoOh => "pokemon-hgss-ho-oh.mp3",
        LibrarySound.KantoWild => "pokemon-gsc-wild-kanto.mp3",
        LibrarySound.KantoGymLeader => "pokemon-gsc-gym-leader-kanto.mp3",
        LibrarySound.KantoTrainer => "pokemon-gsc-trainer-kanto.mp3",
        LibrarySound.DppDialgaPalkia => "pokemon-dpp-dialga-palkia.mp3",
        LibrarySound.DppGiratina => "pokemon-dpp-giratina.mp3",
        LibrarySound.DppLakeTrio => "pokemon-dpp-lake-trio.mp3",
        LibrarySound.DppEliteFour => "pokemon-dpp-elite-four.mp3",
        LibrarySound.DppChampion => "pokemon-dpp-champion.mp3",
        LibrarySound.FrlgTrainer => "pokemon-frlg-trainer.mp3",
        LibrarySound.FrlgWild => "pokemon-frlg-wild.mp3",
        LibrarySound.FrlgGymLeader => "pokemon-frlg-gym-leader.mp3",
        LibrarySound.FrlgLegendary => "pokemon-frlg-legendary.mp3",
        LibrarySound.FrlgMewtwo => "pokemon-frlg-mewtwo.mp3",
        LibrarySound.FrlgFinalRival => "pokemon-frlg-final-rival.mp3",
        LibrarySound.FrlgDeoxys => "pokemon-frlg-deoxys.mp3",
        LibrarySound.None or LibrarySound.Random => throw new ArgumentException("This selection has no single audio file."), _ => "popup.mp3"
    };
    public static string? Resolve(SoundEvent kind, SoundSetting setting, string? directory = null)
    {
        if (setting.Track == LibrarySound.None) return null;
        if (setting.Mp3Path.Length > 0) return setting.Mp3Path;
        if (setting.Track == LibrarySound.Random) {
            var chosen = RandomAudio.Select(kind, setting.RandomTracks, Available(directory ?? AppContext.BaseDirectory), Random.Shared.NextDouble());
            return chosen == LibrarySound.None ? null : Path.Combine(directory ?? AppContext.BaseDirectory, FileName(chosen));
        }
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
