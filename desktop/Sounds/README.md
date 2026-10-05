# Bundled audio

The library includes fourteen recordings. The original eight were added after the project maintainer's confirmation on 2026-09-09; six additional battle tracks were supplied locally on 2026-10-05. Credits and a notice that these assets are separate from software-library licenses are included in [AUDIO-NOTICES.txt](AUDIO-NOTICES.txt).

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
| pokemon-rgby-trainer-battle.mp3 | Red/Green/Blue/Yellow · Trainer Battle | Optional song |
| pokemon-rgby-wild-battle.mp3 | Red/Green/Blue/Yellow · Wild Pokémon | Optional song |
| pokemon-gsc-wild-johto-day.mp3 | Gold/Silver/Crystal · Wild Pokémon, Johto, Day | Optional song |
| pokemon-gsc-wild-johto-night.mp3 | Gold/Silver/Crystal · Wild Pokémon, Johto, Night | Optional song |
| pokemon-rgby-gym-leader.mp3 | Red/Green/Blue/Yellow · Gym Leader | Optional song |
| pokemon-dpp-regi-battle.mp3 | Diamond/Pearl/Platinum · Regirock, Regice, Registeel | Optional song |

[sources.json](sources.json) records provenance, repository paths, exact file sizes and SHA-256 hashes. Packaging includes only these clips, validates their hashes and decodes every recording. Other MP3s remain ignored by Git and are never included through a wildcard.

Fresh installations select these defaults automatically, without downloading audio at runtime. Existing user selections remain in effect. Users can choose any library track, select their own local MP3, or select **None**. If a default file is missing, the desktop falls back to its original synthesized tone.

**Random** appears first in each selector without changing the saved selection. Its collapsed **Random probabilities** panel enables individual tracks and edits their relative weights, displaying the resulting percentages. Equal enabled weights give equal chances. Session end, Success and Failure start with the six notification sounds checked and songs unchecked; Low on time, Time reached and Focus start with the eight songs checked and notification sounds unchecked. Overrides persist independently per event and per Timer/Scheduler low-time choice. The two Focus editors share the same saved pool. Every playback or preview draws again; unavailable, unchecked and zero-weight tracks cannot be selected. An entirely excluded pool is silent.

The installer retains extra user MP3s and backs up the previous installation. Bundled filenames are updated from the verified package; old copies remain in that backup. Custom files outside the installation are not modified.

The Chrome extension remains disabled for desktop users. Its separate `notification.wav` is an original synthesized chime, reproducible with `node scripts/generate-notification.cjs`.
