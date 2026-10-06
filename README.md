# Reflection Timer

The primary app is now the accessible Windows desktop version, **4.2.51**. Its HTML interface runs inside a C# / WebView2 desktop host and uses the existing timer engine, encrypted storage, MP3 library, and Google Sheets receiver.

**Search settings** at the top of Settings filters the existing sections by their names, labels and explanations. Multiple words narrow the results; **Clear search** restores everything. Edits remain in their original controls, and Ctrl+S / Ctrl+Enter still save Settings. Help icons sit beside their corresponding heading text in every theme.

The music library includes **25 songs** and **6 notification sounds**. All 17 additional battle recordings added in 4.2.50 are available in Settings, Focus audio, Timer and Scheduler audio selectors. **Random** gives eligible tracks equal chances unless you change their weights: song events initially include the songs, while Success, Failure and Session end initially include notification sounds. The original restored **Battle (Champion)** recording and the separate **Pokemon Diamond Pearl Platinum - 168. Battle! (Champion)** recording keep distinct names and selections. Existing selected sounds, volume levels, fades and saved weights are preserved.

**Settings → Animations → Focus glow style** selects **Crimson halo** (default) or **Classic glow**. Crimson halo has a dark, translucent blood-red perimeter, a wider fade to transparent toward the center and gently rounded inner corners; Classic glow retains the original appearance. The same controls are available under the collapsed **Animations** section in **Choose Window / Tab…** and stay synchronized. The existing glow enable switch, Focus targets and audio preferences are preserved. Both styles respect Windows' reduced-motion preference and allow keyboard and mouse input through the glow.

**Focus** beside Reset on the Timer page toggles focus mode for Timer and Stopwatch. It has the same shape as Reset and uses Start's accent color when enabled; its text stays **Focus**. **Choose window / tab…** sits immediately to its right. The picker offers **Window**, **Browser Tab**, and **Browser Tab Groups**, with **Refresh List** aligned beside the target-type selector. A group permits any of its current member tabs in the foreground browser window; adding/removing tabs and renaming the group update automatically. Group identities distinguish duplicate or unnamed groups, including collapsed groups. Group detection currently supports English Chrome/Edge accessibility labels; unreadable targets and selections spanning groups fail quietly. Moving or reopening a group in another browser window requires choosing it again. The saved target is retained when toggling or reopening the app and is preselected when available in the picker. Settings → Audio → **Focus mode · away from selected window or tab** has the same button and its own MP3, volume, playback behavior, preview and fade controls. Focus mode starts disabled, with a **5-second** delay and the bundled **Battle (Trainer)** MP3 selected. Ctrl+Alt+; toggles Focus mode globally for Timer or Stopwatch, keeping the saved targets and idle options. It reports Focus mode on/off through native screen-reader notifications even with App view hidden, without moving keyboard focus. With no saved target or idle trigger, it opens the chooser first. Each continuous switch away starts a fresh delay. Returning to the selected target, pausing, resetting or finishing stops only the focus alert. An unfaded track repeats while away; enabling Fade out after plays once per switch away. Other disruptive alerts can interrupt it, then focus audio resumes if still needed.

The chooser has **Start audio after** for the away delay, then **Idle for** above **Multiple Targets**, followed by Target type. **Audio** beside these timing options is collapsed each time the chooser opens and uses the same disclosure style as **Animations**. It includes the same track, playback behavior, Preview audio, Choose MP3, volume, fade timing and Stop audio controls; edits stay synchronized with Settings → Audio. Ctrl+S or Ctrl+Enter saves pending timing and audio edits before saving the targets. Audio controls keep their existing autosave behavior. Showing all explanations does not expand this audio section. Both options default unchecked; idle time defaults to **20 seconds**. Multiple Targets adds native checkboxes to all three listings and keeps choices checked across categories and refreshes. Any selected target counts as focused. The optional idle trigger plays the same focus audio after keyboard and mouse inactivity, even on a selected target; fresh input stops that trigger unless the away alert is also due. Both triggers require Focus enabled and a running Timer or Stopwatch. Idle-only mode is also supported. Windows' last-input timestamp is read without recording input content. Saving retains these options and selections in the encrypted profile; older single-target settings are preserved.

Each category starts with **Use focused window**, **Use focused tab**, or **Use focused tab group**. Selecting one keeps that dynamic choice saved and captures the foreground target when the Timer or Stopwatch starts or resumes, including global playback shortcuts. Switching modes or changing settings alone never replaces it. Multiple Targets can check all three together, alongside specific targets. Capture reads only the actual foreground window; an ungrouped tab or a non-browser window cannot borrow a background browser target. Captured identities stay in memory for that session, while the dynamic choices survive app restarts. After an app restart, start or pause/resume to capture again. Unavailable categories do not prevent other resolved targets or the independent idle trigger from working.

The choices underneath include background windows at start/resume: **Use open windows (including background)** captures all eligible open app windows; **Use focused tabs (including background)** captures the selected tab in each readable browser window. **Use focused tab groups (including background)** captures those selected tabs' groups; **Use open tab groups (including background)** captures every readable group. Inactive tabs outside those groups are not included by the focused-tab choice. Multiple Targets can combine these choices with the original choices and specific targets. Opening another window or group later does not change the capture until the next start/resume. An unreadable browser fails quietly rather than producing a false away alert.

**Ctrl+Alt+]** toggles the focused window's saved target checkbox and updates an open Choose Window / Tab list immediately. In a browser, a native accessible dialog offers **1. Window**, **2. Tab**, and **3. Tab Group** when the current tab belongs to a group. Each choice reports whether it is checked. Up/Down selects a choice; Enter or its number toggles it; Escape cancels. The dialog takes keyboard focus and appears in Alt+Tab. Checked/unchecked feedback uses screen-reader notifications and the optional app voice. The shortcut preserves unrelated selections, the session, timing and audio; checking another target enables Multiple Targets. Unchecking the last target turns Focus off unless Idle for is enabled. Obsolete bookmarks that uniquely identify the toggled window are removed with it; separate live windows remain separate targets.

The lists are accessible tables: **App | Window Name**, **Tab # | Tab Name | App**, and **Grp # | Group Name | App**. Columns keep consistent widths, with number headers and values aligned to the same left inset. **Desktop** is included under Window. Separate windows with identical app/title names receive **Window 1**, **Window 2**, and so on; minimized windows are marked. These display labels preserve the saved native window identities. Long names use ellipsis until their row is focused or clicked once, when that row wraps. Hovering does not change wrapping. The current tab in the most recent readable browser window is the first ordinary tab and marked **(current tab)**. Duplicate native identities are removed while distinct same-title tabs remain separate. Single saved choices show the bold type above **app name** for Window, **tab # - tab name** for Tab, or **group name** for Tab Group. Multiple saved choices show only their categories, such as **Window, Tab, Tab Group**. Buttons stay highlighted while Focus is off, and tooltips retain full window context.

In single-target mode, **Enter**, **Space**, or **double-click** confirms the chosen row. In multiple mode these gestures toggle that row; **Save selected targets**, **Ctrl+Enter**, or **Ctrl+S** commits the whole selection and idle options. The save shortcuts work from every chooser control and give the normal success audio. Arrow keys move between rows. Cancel discards the draft and restores opener focus; failures remain readable in the dialog for retry. A saved closed target remains listed so it can be unchecked.

Reflection Timer's App view can also be selected as a Window target. Window targets permit any tab inside that window. Browser-tab targets require the exact tab to be selected in its foreground browser window, including when several tabs share a title. Tab tracking uses the browser's Windows accessibility tab strip; browsers that do not expose it can still be selected as windows. Navigation and title changes keep the same target. Saved windows reconnect after restarting an app when its app/title identifies one current window; if the title changed, its sole eligible window can reconnect. Separate windows with identical titles remain separate, and ambiguous matches stay unavailable. Shared hosts and Explorer folders require their saved title. Closing or moving a tab/group to another browser window requires choosing it again. Closed or unreadable targets stop the alert and show a status instead of producing false alarms. The keyboard-accessible selector restores focus when closed. Target names, executable paths and native class hints are stored only in the encrypted local profile and are excluded from Sheets uploads and diagnostic exports.

Ctrl+Alt+' (apostrophe) switches Timer ↔ Stopwatch from any app. In the focused App or Compact view, that same press focuses the Stopwatch play button or selects the Timer duration field. It pauses and preserves the current session; switching back does not resume automatically. Hidden windows stay hidden and Time-only stays small. The backtick start/end shortcut is unchanged.

Sending a stopwatch reflection returns the App, Compact, and Time-only clocks to 0:00, while retaining the actual work duration in the saved response. Saving a draft continues the same stopwatch; a failed send retains its elapsed display.

## Stopwatch mode (4.2.0)

Stopwatch stop-time correction (4.2.2): global pause, mode switch, and reflection shortcuts freeze elapsed time at the queued key's timestamp, before logging or popup work. Slow saving and reflection loading do not inflate actual time sent to Sheets. Shortcut mappings are unchanged.

Click **S**, immediately left of **−** in Compact or Time-only, to switch to Stopwatch; **T** returns to Timer. App view has the same mode switch. Switching pauses and preserves the unfinished session. Switching back does not start it automatically, and only one mode can run at a time. Start/pause/resume and reset controls operate on the selected mode; countdown input values remain intact.

In Stopwatch, **Ctrl+Alt+/** pauses active time, plays your session-end sound, and opens the session's reflection. **Save** (or Ctrl+S) keeps the response and resumes that same selected stopwatch. **Save & send** finishes it without a second completion alert. Saving a parked or superseded reflection never starts another session. Pausing excludes break time; reset starts over from zero. A running stopwatch continues until paused, including across reopening the app.

**Reset**, **Ctrl+R**, and **Ctrl+Alt+R** ask for confirmation when the selected timer or stopwatch is running, or an unsent reflection has text in either box, including saved or closed drafts. The accessible confirmation takes focus on **OK** and appears in Alt+Tab, even with the timer's other windows hidden. The focused button has a thick accent-colored outline. Tab moves between OK, Cancel, and the read-only warning text. Enter activates the focused button; Escape, Cancel, or closing the dialog keeps the session and page open. Confirming retains reflection text; Ctrl+R then refreshes the focused view. Turn this off under **Settings → Timer and stopwatch → Confirm before resetting**. The setting starts enabled for new and existing installations.

**Ctrl+Alt+R** resets the selected timer or stopwatch from any app, including with every timer window hidden. Timer returns to the shared duration inputs; Stopwatch returns to zero. This global shortcut preserves the current pages and viewer visibility. The ChatGPT App Hotkeys reader no longer assigns Ctrl+Alt+R.

Settings → Audio → **Time reached · stopwatch** follows Low on time audio. Its editable threshold defaults to **300 seconds (5 minutes)** and plays once per stopwatch session. New installations select Battle (Champion); existing profiles without a separate stopwatch sound keep inheriting their low-time sound until edited. None, custom MP3s, previews, fades, and the three playback behaviors work as for other sounds. The same threshold control appears on the Timer page. Scheduled sessions remain countdowns; existing overlap rules apply. A scheduled countdown replacing an unfinished paused countdown retains that earlier work as a reflection.

Receiver **2.9.2+** writes new entries as **C = active time, D = allotted time (blank for a stopwatch), E = status, F = reason for ending early, G = `timer` or `stop watch`**. Completed sessions leave E blank unless marked auto-sent; check-ins and early finishes retain their status. Existing rows are not migrated. Stopwatch delivery requires receiver 2.9.0+; update the existing deployment to 2.9.2+ for the new column layout. Outbox and diagnostic exports identify the mode. Receivers without stopwatch support leave those entries safely in Outbox with `receiver_update_required` rather than writing incompatible data.

Each pause records its date/time and duration for that session. In the reflection popup, scroll below the original controls to enter an optional reason for each pause. The popup and original fields retain their sizes and positions. Reasons autosave with the draft and work with Ctrl+S, Escape, Ctrl+Enter and Alt+S. Switching away from a running mode and opening a running stopwatch's review also count as pauses; simply reopening a paused review does not add another.

Receiver **2.10.0+** adds **H = Pause time, I = Pause Duration, J = Pause Reason**, using the existing column stripes, borders and wrapping. Multiple pauses are numbered consistently across these cells; durations use the existing hours/minutes/seconds wording, and blank reasons show `N/A`. Existing rows remain unchanged. Pause-only responses use `N/A` in B. Update the existing Apps Script web-app deployment with `google-sheets-script.gs` (retain its Script Properties and deployment URL); older deployments keep entries containing pauses in Outbox for retry after the receiver update. Pause details remain available in Outbox's local entry details.

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

A fresh installation starts at 15 minutes with the compact timer enabled at bottom left, session-end prompts at bottom right, and App centered. Low on time audio's **Use threshold** starts unchecked with **13 seconds** retained; checking it enables the warning. Existing choices take precedence. Dark, Light, High Contrast and Glamour themes and the original Ctrl+Alt hotkeys are available. Ctrl+Space or Ctrl+Alt+Space starts, resumes, or pauses from any app while Reflection Timer is running, including with all its windows hidden; it uses the same duration and pause/resume behavior as Compact.

New profiles use these bundled audio selections, with App sound at **50%** and each sound at **100%**:

| Audio event | Selected track | Behavior | Fade out after | Fade after message sent |
| --- | --- | --- | --- | --- |
| Session end | Original extension sound | Assertive | Off (10 seconds retained) | Off (3 seconds retained) |
| Success | Level Up | Disruptive | Off (10 seconds retained) | Off (3 seconds retained) |
| Failure | Out of Health | Disruptive | Off (10 seconds retained) | Off (3 seconds retained) |
| Low on time | Battle (Trainer) | Disruptive | On, after 25 seconds | On, over 5 seconds |
| Time reached · stopwatch | Battle (Champion) | Disruptive | On, after 6 seconds | On, over 5 seconds |
| Focus mode | Battle (Trainer) | Polite | Off (10 seconds retained) | — |

The stopwatch alert starts enabled at 300 seconds. Timer and Settings share the countdown threshold preference; Scheduler retains its own saved choices and its inherited 15-second threshold. Existing profiles retain their saved audio settings.

Audio previews honor **Fade out after**: they play through the selected delay and the same one-second fade used during a session, unless the track finishes sooner. With timed fading disabled, previews remain limited to five seconds. This applies across Settings, Timer and Scheduler, including automatic previews when selecting a sound. **Stop all app audio** or a new preview can stop a long preview. Previewing does not simulate sending a reflection or trigger message-sent fading.

## Views and screen readers

In App Settings, **Ctrl+Enter** and **Ctrl+S** both perform **Save settings** from any focused input, dropdown, button, or page text. They save edits without requiring you to leave the current field, keep focus in place, and play the success sound only after all saves finish. An open dialog keeps its own keys; repeated presses while saving cannot duplicate the save.

Section guidance is collapsed by default. Circled question-mark buttons open and close explanations with a click, Enter, or Space. The Help tab collects instructions and shortcut descriptions. Settings → Help and guidance → Show all explanations saves your preferred visibility; labels, status messages, and errors remain visible.

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
