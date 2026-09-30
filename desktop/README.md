# Reflection Timer desktop 4.2.29

This is the primary accessible desktop app. Build `ReflectionTimer.Desktop/ReflectionTimer.Desktop.csproj`; run `ReflectionTimer.Tests` and its `ui.cjs` browser checks.

The HTML/WebView2 interface retains the four original views and uses the shared C# timer and Sheets engine. Existing installations keep their encrypted desktop profile and connection automatically. Explicit named test profiles remain isolated.

Focus mode works for Timer and Stopwatch, with a keyboard-accessible window/browser-tab/tab-group selector and independent Settings → Audio controls. Tab groups use English Chrome/Edge accessibility labels and their current members; duplicate and unnamed groups retain separate identities. It defaults off with a five-second away delay and the existing Battle (Trainer) MP3. Run `ReflectionTimer.Tests --focus-mode` and `ReflectionTimer.Tests/focus-mode.cjs` for focused checks; `--focus-target-scan` reads the current window/tab/group accessibility providers and reports counts without target names.

The target button shows **Window**, **Tab**, or **Tab Group** in bold above the saved name and keeps the active theme color even when Focus is off. Its name follows the same app/window-first order as the picker listing, including existing saved selections. Long names stay on one line, with their full text available to screen readers and in the hover tooltip. In the picker list, Enter, Space, double-click, and **Use selected target** perform the same save; arrow keys navigate without saving. Confirmation returns focus to the opener, blocks duplicate requests while saving, and keeps a failed selection open for retry.

Use `install.ps1` for a per-user install with backups, or `package-release.ps1` to create a self-contained verified ZIP. Packaging does not publish. The old native desktop source is preserved in `../inaccessible-version-useless/desktop`.

Open START-HERE.html for setup. See HOTKEYS.md for shortcuts and Sounds/README.md for the bundled MP3 defaults.
