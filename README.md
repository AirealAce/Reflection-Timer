# Reflection Timer

The primary app is now the accessible Windows desktop version, **4.1.26**. Its HTML interface runs inside a C# / WebView2 desktop host and uses the existing timer engine, encrypted storage, MP3 library, and Google Sheets receiver.

## Project folders

- `desktop/` — current accessible app, shared engine, tests, audio, installation and packaging.
- `extension-version-useless/` — archived Chrome extension.
- `inaccessible-version-useless/` — archived original native Windows app.
- `google-sheets-script.gs` — current shared Google Sheets receiver.
- `scripts/` and `test/` — current audio, source-package and receiver checks.

The archives are preserved for reference and rollback; they are not dependencies of the current app. Do not run the archived extension alongside the desktop timer.

## Install or upgrade

Windows x64 and Microsoft Edge WebView2 Evergreen Runtime are required. A self-contained package includes .NET. Open `desktop/START-HERE.html` for setup and keyboard help.

From source with the .NET 10 SDK:
```powershell
.\desktop\install.ps1
```

The installer backs up the old executable folder and encrypted data, installs all web assets, and retains the usual desktop shortcut. It does not publish anything. Starting with 4.1.24, normal launches share `%USERPROFILE%\.reflection-timer`, outside AppData's package-specific redirection. On first launch, the app copies and verifies the existing `%LOCALAPPDATA%\ReflectionTimerDesktop` encrypted profile without deleting the original. The same Sheet, receiver URL, token, routing, drafts, schedules, Outbox, audio choices, and preferences are retained. Once the shared profile exists, a stale AppData copy cannot replace it. Installer backups are in `%USERPROFILE%\.reflection-timer-backups`. Do not copy private configuration into this repository.

If an older app appeared to lose its settings depending on how it was launched, there may be separate encrypted profiles in Windows AppData and a packaged launcher's private AppData. Keep both copies. Recover the intended profile into the shared folder while the timer app is fully quit; do not blindly merge Outbox entries or overwrite an existing shared profile. This preserves request IDs and avoids duplicate submissions. Keep this private, Windows-account-encrypted data outside Git and cloud sync; other PCs should use Connection setup.

A fresh installation starts at 15 minutes with the compact timer enabled at bottom left, session-end prompts at bottom right, App centered, and low-time warnings enabled at 15 seconds. Existing choices take precedence. Dark, Light, High Contrast and Glamour themes and the original Ctrl+Alt hotkeys are available. Ctrl+Space starts, resumes, or pauses from any app while Reflection Timer is running, including with all its windows hidden; it uses the same duration and pause/resume behavior as Compact.

## Views and screen readers

App, the floating timer, and session-end prompts are separate windows. Compact and Time-only share one floating window. The interface uses headings, labels, real buttons, tables, keyboard focus, and restrained announcements; the countdown does not speak every tick. Use your screen reader's web reading and table commands. Compatibility still benefits from testing with your particular reader and version.

Dropdown selections provide a short screen-reader status, such as “Light selected.” This applies automatically to theme, placement, audio, routing, and dynamically added dropdowns. Rapid changes announce the latest choice; moving away cancels pending feedback. Numeric time fields retain their native announcements. Display autosaves preserve newer choices when earlier save replies arrive.

Viewer switching reuses browser controls, including when closing and reopening the floating timer or a reflection. If WebView2 crashes, the app restores the interface from saved state while the timer continues. Saved drafts are reloaded without being submitted; text not yet saved before the crash cannot be recovered. Repeated failures offer a Retry interface button.

After a session ends, the clock and read-time feedback use the duration in the input boxes. Finished clock updates containing zero cannot overwrite that preview. Paused sessions keep their remaining time, and Auto-start continues the next countdown. Entering zero in every duration field keeps the preview at 0:00.

Compact, Time-only, and reflection prompts each have an independent **always on top** setting, enabled by default. Reflection prompts stay out of Alt+Tab. Only one session-end prompt is shown at a time: a new prompt saves and queues the previous open response, including a blank response, marked **auto-sent**. An early-ended response retains both flags and its reason. An unsent check-in becomes the same session's completion prompt, keeping its saved text and editor; older sessions' check-ins remain separate. Failed local saves keep the old prompt open and the new prompt pending; offline delivery stays in Outbox.

Existing Sheets receivers show `[auto-sent]` in the reflection text. The bundled receiver 2.8.0 puts `auto-sent` beside `ended early` in column E and keeps the original response in B, including an empty response. A full 5,000-character automatic response requires that receiver update; older deployments keep it safely in Outbox until updated. Receiver code changes are not deployed automatically.

## Development checks

```powershell
dotnet run --project desktop/ReflectionTimer.Tests -c Release
node desktop/ReflectionTimer.Tests/ui.cjs
node --test test/apps-script.test.js test/receiver-setup.test.js test/release-assets.test.js
node scripts/check-public-source.cjs
```

Browser checks require Playwright and Microsoft Edge. `desktop/package-release.ps1` runs these checks and creates a verified self-contained ZIP locally. It never uploads a release.

Explicit `--profile NAME` launches remain isolated in the old accessibility-preview data location for development. Normal launches use the installed desktop profile. `ReflectionTimer.exe --check-connection` performs an authenticated read-only Sheets check and emits only its result and capabilities.

See [keyboard shortcuts](desktop/HOTKEYS.md), [audio sources](desktop/Sounds/README.md), and [security notes](SECURITY.md).
