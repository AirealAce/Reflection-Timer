using System.Collections.Immutable;

namespace ReflectionTimer.Core;

public static class RandomAudio
{
    public const int MaxWeight = 1000;
    public static bool IsTrack(LibrarySound track) => Enum.IsDefined(track) && track is not (LibrarySound.Default or LibrarySound.None or LibrarySound.Random);
    public static bool IsSong(LibrarySound track) => track is LibrarySound.TrainerBattle or LibrarySound.ChampionBattle
        or LibrarySound.RgbyTrainerBattle or LibrarySound.RgbyWildBattle or LibrarySound.JohtoWildDay
        or LibrarySound.JohtoWildNight or LibrarySound.RgbyGymLeader or LibrarySound.RegiBattle;
    public static RandomTrackWeight For(SoundEvent kind, LibrarySound track, ImmutableList<RandomTrackWeight>? saved)
        => saved?.FirstOrDefault(x => x.Track == track)
            ?? new(track, IsSong(track) == (kind is SoundEvent.LowTime or SoundEvent.TimeReached or SoundEvent.FocusLost));

    public static void Validate(ImmutableList<RandomTrackWeight>? tracks)
    {
        if (tracks is null) return;
        if (tracks.Count > Enum.GetValues<LibrarySound>().Count(IsTrack) || tracks.Any(x => x is null || !IsTrack(x.Track) || x.Weight is < 0 or > MaxWeight)
            || tracks.Select(x => x.Track).Distinct().Count() != tracks.Count)
            throw new ArgumentException($"Choose each library track once, with a whole-number chance weight from 0 to {MaxWeight}.");
    }

    // One draw per playback. Unavailable and excluded tracks receive no chance;
    // an empty pool is silent and never substitutes an excluded default track.
    public static LibrarySound Select(SoundEvent kind, ImmutableList<RandomTrackWeight>? saved, IEnumerable<LibrarySound> available, double draw)
    {
        Validate(saved);
        if (!double.IsFinite(draw) || draw is < 0 or >= 1) throw new ArgumentOutOfRangeException(nameof(draw));
        var choices = available.Distinct().Where(IsTrack).Select(t => For(kind, t, saved)).Where(x => x.Enabled && x.Weight > 0).ToArray();
        var remaining = draw * choices.Sum(x => x.Weight);
        foreach (var choice in choices) {
            if (remaining < choice.Weight) return choice.Track;
            remaining -= choice.Weight;
        }
        return LibrarySound.None;
    }
}
