# Bundled audio

The repository and Windows release include the eight recordings below. The project maintainer confirmed redistribution permission on 2026-09-09. Credits and a notice that these assets are separate from software-library licenses are included in [AUDIO-NOTICES.txt](AUDIO-NOTICES.txt).

| File | Track | Default use |
| --- | --- | --- |
| pokemon-obtained-item.mp3 | Obtained an Item | Optional library selection |
| pokemon-level-up.mp3 | Level Up | Success |
| pokemon-healed.mp3 | Pokémon Healed | Optional library selection |
| pokemon-key-item.mp3 | Obtained a Key Item | Optional library selection |
| pokemon-battle-trainer.mp3 | Battle (Trainer) | Low on time; Focus mode |
| pokemon-battle-champion.mp3 | Battle (Champion) | Optional library selection |
| kirby-out-of-health.mp3 | Out of Health | Failure |
| ../../popup.mp3 | Original extension sound | Session end |

[sources.json](sources.json) records provenance, repository paths, exact file sizes and SHA-256 hashes. Public packaging includes only these clips, validates the hashes and decodes all eight recordings. Other MP3s remain ignored by Git and are never included through a wildcard.

Fresh installations select these defaults automatically, without downloading audio at runtime. Existing user selections remain in effect. Users can choose any library track, select their own local MP3, or select **None**. If a default file is missing, the desktop falls back to its original synthesized tone.

The installer retains extra user MP3s and backs up the previous installation. Bundled filenames are updated from the verified package; old copies remain in that backup. Custom files outside the installation are not modified.

The Chrome extension remains disabled for desktop users. Its separate `notification.wav` is an original synthesized chime, reproducible with `node scripts/generate-notification.cjs`.
