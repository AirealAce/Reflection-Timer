# Keyboard shortcuts and upgrading another PC

The current repository is [AirealAce/Reflection-Timer](https://github.com/AirealAce/Reflection-Timer). The current accessible desktop source is 4.1.20. Local branch builds are not automatically published to Releases; the old 3.6.4 build only had Ctrl+Alt+T. Downloading source or renaming a repository does not update an already installed app.

## App tab navigation

In App view, Ctrl+Tab selects the next tab and Ctrl+Shift+Tab selects the previous tab. The order is Timer, Scheduler, Outbox, Settings, Diagnostics, then back to Timer. These shortcuts work from inputs, preserve unfinished edits, and focus the selected tab for screen readers. An open dialog retains focus. Ordinary Tab and Shift+Tab continue to move between controls.

Escape hides the focused App view, like Ctrl+Alt+T or its X button. It works on every tab, including from inputs, and retains the selected tab and unfinished edits. The timer and other viewers continue unchanged. If an App dialog is open, Escape dismisses that dialog first.

## Current mappings

All shortcuts require Reflection Timer to be running, including in the system tray. They do not depend on Chrome or Google Sheets connectivity.

| Shortcut | Action |
| --- | --- |
| Ctrl+Alt+T | If the main window is focused, hide it exactly like its X button. Otherwise bring it forward, preserving the selected tab; on Timer, select the first positive duration field. The timer and compact view continue unchanged. |
| Ctrl+Alt+backtick (`) | Start the specified timer, resume a paused timer, or end a running session early and show its reflection. Auto-start and its cutoff still apply. |
| Ctrl+Alt+/ | Cycle compact controls → time-only → hidden → controls. The countdown continues. |
| Ctrl+Alt+. | Select the compact duration. Press twice within 0.8 seconds to select it in the full Timer tab. |
| Ctrl+Alt+, | Bring an existing reflection forward and focus its first text box. In the focused reflection, focus the first box if neither is focused; if either text box is focused, Save the draft and close. With no open window, reopen the latest pending reflection or create a check-in for a running/paused session. Never opens App. |

Duration focus chooses the first value above zero from the left, or Hours if all are zero. Running-session duration fields are read-only; pause to edit. Period double-press pairing resets after a different app shortcut or more than 0.8 seconds.

In the Hours, Minutes, or Seconds field of App or Compact view, Enter pauses a running timer, resumes a paused timer, or starts an idle timer. Changing the duration while paused starts a new timer with that duration. Holding Enter does not repeatedly toggle the timer.

Ctrl+Enter performs that same timer action from anywhere in the focused Compact or Time-only viewer, including its clock, page background, and buttons. It does not activate the focused button's other action. Held-key repeats and repeated requests while saving are ignored; an open dialog keeps its own keys. App Settings and reflection prompts retain their existing Ctrl+Enter save actions.

In Compact and Time-only, Escape performs the same action as the top-right minus button. From Compact, either control switches to Time-only and focuses the clock. From Time-only, either hides the floating viewer. This works while ready, running, paused, or finished and does not change the timer or other windows. Holding Escape performs only one step.

Reflection Prev and Next buttons browse pending drafts without sending them and keep only one popup visible. Ctrl+Alt+comma still saves/closes from either focused reflection field. A genuine early-ended reflection retains its smaller reason box, including after reopening or navigation. Natural completion hides that box. To retain older drafts when another session ends, uncheck Settings → Auto-send incomplete reflections when a session ends (on by default).

Ctrl+Enter runs Save & send from anywhere in a session reflection or check-in window, including its buttons and page text. If both text boxes are completely empty, it runs Skip instead. Otherwise, a reflection response is required; both fields are saved before the entry is queued for delivery. Escape also skips when both boxes are empty; if either contains any text, it saves the draft locally and closes, like Save or Ctrl+Alt+comma from a text box. Spaces and line breaks count as text, so Escape preserves them. Holding either key or pressing it again while saving cannot duplicate an action. In App view, Ctrl+Enter continues to save settings only on the Settings tab.

## Upgrade without replacing the Sheet connection

1. Finish or pause active work, save reflection drafts, and **Quit** the old app from its tray menu. Closing the main window normally leaves it running.
2. Extract the verified 4.1.20 Windows x64 release to a new folder. Do not overwrite files in a running app's folder.
3. Run the extracted ReflectionTimer.exe. To replace the regular per-user install from this source checkout, use **desktop/install.ps1**. The installer retains local settings/data and existing MP3 files. Do not import someone else's connection code.
4. Launch the installed app and check its executable's **Properties → Details → Product version**. It should say 4.1.20. Existing desktop shortcuts should point to the installed copy, not an old extracted download.
5. Check **Settings → Keyboard shortcuts** for individual registration failures. Another running copy or another app can own a chord. Quit the conflicting copy/app; Reflection Timer retries unavailable shortcuts automatically; there is no need to change the Sheets URL or token.

The update does not require changing an already working receiver deployment or credentials. Receiver 2.6.0 or newer is needed for sending check-in rows; if an older receiver is detected, the entry remains saved locally. A receiver source file in a download is not deployed automatically.

For a source checkout, update origin to `https://github.com/AirealAce/Reflection-Timer.git`, fetch, and inspect local changes before updating the branch. Do not reset or overwrite uncommitted work. Build the current source instead of reusing an old `bin`, `artifacts`, or installed executable.

## Verify behavior safely

- Hide the main app in the tray; Ctrl+Alt+T should restore it. Press again while the main window is focused: it should hide like X, without quitting or stopping the timer. Minimize it and repeat. From a reflection or compact window, T should bring the main app forward, not hide it. An owned modal stays in front.
- Use slash to cycle compact modes; period once selects compact duration and twice selects the full Timer duration.
- With a short disposable session, backtick starts the timer. Another press ends it early and opens a reflection; skip that test reflection instead of sending it to a live sheet.
- During a disposable running session with no pending reflection, comma opens a check-in without stopping the countdown. From a reflection button or the page background, comma focuses the first text box. From either text box, it saves the draft locally and closes. A later press reopens the saved draft. Save & send remains the separate submission action.
- If comma is unavailable, use the App check-in control. Other unavailable shortcuts also have normal app controls as alternatives.

Punctuation bindings currently use Windows US-keyboard virtual keys (OEM grave, slash, period, comma). Different keyboard layouts can label those keys differently; custom remapping is not implemented. No general keyboard hook records typed content. Local diagnostics record each shortcut's registration/usage result, not keystrokes, reflection text, or credentials.

## Developer regression gate

Run `dotnet run --project desktop/ReflectionTimer.Tests -c Release` on Windows. These isolated tests use synthetic state and an injected registration backend, leaving the running user's timer and real global chords alone. They cover all five exact virtual-key/modifier mappings, independent conflicts/disposal, hidden/minimized window behavior, focus selection, compact cycling, early endings, and check-ins. They do not prove that another PC's real chords are free; check that PC's status panel as well.

The public packaging script runs this gate before producing a ZIP, alongside onboarding/install, delivery-safety, receiver, and bundled-audio checks. It includes only the eight hash-verified approved MP3s and excludes local data, credentials, and additional personal audio.
