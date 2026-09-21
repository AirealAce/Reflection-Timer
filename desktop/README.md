# Reflection Timer desktop 4.2.4

This is the primary accessible desktop app. Build `ReflectionTimer.Desktop/ReflectionTimer.Desktop.csproj`; run `ReflectionTimer.Tests` and its `ui.cjs` browser checks.

The HTML/WebView2 interface retains the four original views and uses the shared C# timer and Sheets engine. Existing installations keep their encrypted desktop profile and connection automatically. Explicit named test profiles remain isolated.

Use `install.ps1` for a per-user install with backups, or `package-release.ps1` to create a self-contained verified ZIP. Packaging does not publish. The old native desktop source is preserved in `../inaccessible-version-useless/desktop`.

Open START-HERE.html for setup. See HOTKEYS.md for shortcuts and Sounds/README.md for the bundled MP3 defaults.
