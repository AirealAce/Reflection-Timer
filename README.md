# Reflection Timer

The primary app is now the accessible Windows desktop version, **4.2.7**. Its HTML interface runs inside a C# / WebView2 desktop host and uses the existing timer engine, encrypted storage, MP3 library, and Google Sheets receiver.

Ctrl+Alt+' (apostrophe) switches Timer ↔ Stopwatch from any app in 4.2.3 and later. It pauses and preserves the current session; switching back does not resume automatically. Hidden windows stay hidden and Time-only stays small. The backtick start/end shortcut is unchanged.

## Stopwatch mode (4.2.0)

Stopwatch stop-time correction (4.2.2): global pause, mode switch, and reflection shortcuts freeze elapsed time at the queued key's timestamp, before logging or popup work. Slow saving and reflection loading do not inflate actual time sent to Sheets. Shortcut mappings are unchanged.

Click **S**, immediately left of **−** in Compact or Time-only, to switch to Stopwatch; **T** returns to Timer. App view has the same mode switch. Switching pauses and preserves the unfinished session. Switching back does not start it automatically, and only one mode can run at a time. Start/pause/resume and reset controls operate on the selected mode; countdown input values remain intact.

In Stopwatch, **Ctrl+Alt+/** pauses active time, plays your session-end sound, and opens the session's reflection. **Save** (or Ctrl+S) keeps the response and resumes that same selected stopwatch. **Save & send** finishes it without a second completion alert. Saving a parked or superseded reflection never starts another session. Pausing excludes break time; reset starts over from zero. A running stopwatch continues until paused, including across reopening the app.

**Reset**, **Ctrl+R**, and **Ctrl+Alt+R** ask for confirmation when the selected timer or stopwatch is running, or an unsent reflection has text in either box, including saved or closed drafts. Cancel is selected by default and keeps the session and page open. Confirming retains reflection text; Ctrl+R then refreshes the focused view. Turn this off under **Settings → Timer and stopwatch → Confirm before resetting**. The setting starts enabled for new and existing installations.

**Ctrl+Alt+R** resets the selected timer or stopwatch from any app, including with every timer window hidden. Timer returns to the shared duration inputs; Stopwatch returns to zero. This global shortcut preserves the current pages and viewer visibility. The ChatGPT App Hotkeys reader no longer assigns Ctrl+Alt+R.

Settings → Audio → **Time reached · stopwatch** follows Low on time audio. Its editable threshold defaults to **300 seconds (5 minutes)** and plays once per stopwatch session. Initially it inherits your low-time MP3, volume, and playback behavior; editing it makes the selection independent. None, custom MP3s, previews, fades, and the three playback behaviors work as for other sounds. The same threshold control appears on the Timer page. Scheduled sessions remain countdowns; existing overlap rules apply. A scheduled countdown replacing an unfinished paused countdown retains that earlier work as a reflection.

Receiver **2.9.2+** writes new entries as **C = active time, D = allotted time (blank for a stopwatch), E = status, F = reason for ending early, G = `timer` or `stop watch`**. Completed sessions leave E blank unless marked auto-sent; check-ins and early finishes retain their status. Existing rows are not migrated. Stopwatch delivery requires receiver 2.9.0+; update the existing deployment to 2.9.2+ for the new column layout. Outbox and diagnostic exports identify the mode. Receivers without stopwatch support leave those entries safely in Outbox with `receiver_update_required` rather than writing incompatible data.

To upgrade an existing Sheet connection, use Connection setup to generate the current private receiver with your existing sheet/token, replace the code in your existing Apps Script project, save, then **Deploy → Manage deployments → Edit → New version → Deploy**. Keep the same `/exec` deployment URL; do not create a different account or token. Verify the connection, then retry held stopwatch entries in Outbox. Receiver source updates are not deployed automatically. Do not commit your generated private receiver or setup code.

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

A fresh installation starts at 15 minutes with the compact timer enabled at bottom left, session-end prompts at bottom right, App centered, and low-time warnings enabled at 15 seconds. Existing choices take precedence. Dark, Light, High Contrast and Glamour themes and the original Ctrl+Alt hotkeys are available. Ctrl+Space or Ctrl+Alt+Space starts, resumes, or pauses from any app while Reflection Timer is running, including with all its windows hidden; it uses the same duration and pause/resume behavior as Compact.

Audio previews honor **Fade out after**: they play through the selected delay and the same one-second fade used during a session, unless the track finishes sooner. With timed fading disabled, previews remain limited to five seconds. This applies across Settings, Timer and Scheduler, including automatic previews when selecting a sound. **Stop all app audio** or a new preview can stop a long preview. Previewing does not simulate sending a reflection or trigger message-sent fading.

## Views and screen readers

In App Settings, **Ctrl+Enter** and **Ctrl+S** both perform **Save settings** from any focused input, dropdown, button, or page text. They save edits without requiring you to leave the current field, keep focus in place, and play the success sound only after all saves finish. An open dialog keeps its own keys; repeated presses while saving cannot duplicate the save.

App, the floating timer, and session-end prompts are separate windows. Compact and Time-only share one floating window. The interface uses headings, labels, real buttons, tables, keyboard focus, and restrained announcements; the countdown does not speak every tick. Use your screen reader's web reading and table commands. Compatibility still benefits from testing with your particular reader and version.

Dropdown selections provide a short screen-reader status, such as “Light selected.” This applies automatically to theme, placement, audio, routing, and dynamically added dropdowns. Rapid changes announce the latest choice; moving away cancels pending feedback. Numeric time fields retain their native announcements. Display autosaves preserve newer choices when earlier save replies arrive.

Viewer switching reuses browser controls, including when closing and reopening the floating timer or a reflection. If WebView2 crashes, the app restores the interface from saved state while the timer continues. Saved drafts are reloaded without being submitted; text not yet saved before the crash cannot be recovered. Repeated failures offer a Retry interface button.

After a session ends, the clock and read-time feedback use the duration in the input boxes. Finished clock updates containing zero cannot overwrite that preview. Paused sessions keep their remaining time, and Auto-start continues the next countdown. Entering zero in every duration field keeps the preview at 0:00.

Compact, Time-only, and reflection prompts each have an independent **always on top** setting, enabled by default. Reflection prompts stay out of Alt+Tab. Only one session-end prompt is shown at a time: a new prompt saves and queues the previous open response, including a blank response, marked **auto-sent**. An early-ended response retains both flags and its reason. An unsent check-in becomes the same session's completion prompt, keeping its saved text and editor; older sessions' check-ins remain separate. Failed local saves keep the old prompt open and the new prompt pending; offline delivery stays in Outbox.

Older Sheets receivers show `[auto-sent]` in the reflection text. The bundled receiver 2.8.1 puts `auto-sent` beside any `ended early` or `Check-in` status in column E, without brackets. Column B keeps the original response, or shows `N/A` when an auto-sent response is blank. A full 5,000-character automatic response requires that receiver update; older deployments keep it safely in Outbox until updated. Receiver code changes are not deployed automatically.

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
