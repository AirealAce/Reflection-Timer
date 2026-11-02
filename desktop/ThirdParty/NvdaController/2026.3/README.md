# NVDA Controller Client 2026.3

Reflection Timer dynamically loads the unmodified official Windows x64 NVDA
Controller Client. It communicates with an already-running NVDA; it does not
install or start NVDA and requires no SDK or separate user configuration.

Copyright NV Access Limited and the NVDA contributors. The library and its
corresponding source are licensed under the GNU Lesser General Public License,
version 2.1; see `LICENSE.txt`. The upstream notices and warranty disclaimer are
retained in `nvdaController.h`, `UPSTREAM-README.md`, and the source archive.

The library is kept as a separate, replaceable `nvdaControllerClient.dll` in the
installed `nvda` folder. An interface-compatible modified library can replace
it. Reflection Timer uses the version 1 `testIfRunning`, `speakText`, and
`brailleMessage` interfaces. Action feedback prefers version 2 `speakSsml` with
important priority, which temporarily interrupts and then resumes lower-priority
reading. When that interface is unavailable, actions fall back to version 1
text output. Ordinary confirmations and view messages use normal text output.
No speech-mode preference is changed and existing speech is never forcibly
cancelled with `cancelSpeech`.

`nvda-2026.3-source.zip` contains the complete upstream repository snapshot for
the corresponding release commit, including the controller implementation,
RPC interface definitions, shared helper source, and build scripts. Extract it
and follow its upstream build instructions. The controller-specific upstream
build command is `scons source client`; see `UPSTREAM-README.md`. Building the
library is optional and is separate from running Reflection Timer.

Installed application packages give the source archive and header an additional
`.txt` suffix for compatibility with earlier versions of Reflection Timer's
updater. The contents are unchanged. To inspect or build the optional library
source, make a copy of `nvda-2026.3-source.zip.txt` and rename that copy to
`nvda-2026.3-source.zip`, then extract it with a ZIP tool. Similarly, a copy of
`nvdaController.h.txt` can be renamed to `nvdaController.h`. The original names
are retained in this repository and in the hash keys in `PROVENANCE.json`;
the hashes also apply to the identically sized installed copies.

Upstream release: https://github.com/nvaccess/nvda/releases/tag/release-2026.3

SDK download: https://download.nvaccess.org/releases/2026.3/nvda_2026.3_controllerClient.zip

Corresponding source commit: `7eb1738dd1556775145453fdd8b6b7ca6b397693`

Source download: https://codeload.github.com/nvaccess/nvda/zip/7eb1738dd1556775145453fdd8b6b7ca6b397693

The pinned file hashes and original download information are recorded in
`PROVENANCE.json`. The upstream DLL's Authenticode signature was verified as
valid for NV Access Limited when imported. No upstream library code was changed.
