# App view comparison — accessibility preview 0.4.2

Compared against the original 3.12.9 `MainWindow.cs`, `Controls.cs`, `AudioControls.cs`, `SetupWindow.cs`, and `ReflectionWindow.cs`. This is an inventory of corresponding features and the corrections made, not a claim of pixel-identical rendering or completed screen-reader acceptance.

| Original page | Corresponding features in 0.4 | Correction / verification |
| --- | --- | --- |
| Timer | Countdown/status, Hours/Minutes/Seconds, Start/Pause/Resume, Reset | Shared duration draft and timer behavior tests pass. Visual countdown remains quiet for readers. |
| Timer | Start timer at and Schedule session | Label, field, and button restored to one horizontal row. Geometry checked at the original App width. |
| Timer | Auto-start next session and optional cutoff | Centered checkbox/date row; enabling a cutoff enables repeat. Disabling repeat clears the cutoff. |
| Timer | Low on time audio, inherited/individual threshold, sound selection, preview, custom MP3 | Present. Threshold drafts survive background updates; individual sound and threshold are sent together. |
| Timer | App sound slider | Restored and synchronized with the Settings slider. Keyboard changes reach the master-volume command. |
| Timer | Test reflection prompt, Pending reflections, Mark issue, pending/unsent counts, floating visibility, Quit | Present. Pending reflections opens the latest draft; Prev/Next traverse saved unsent drafts with only one visible reflection. Closing App still hides to tray; Quit flushes drafts. |
| Scheduling | Overlap policy and explanation | Restored to this tab, with the original three choices. |
| Scheduling | Start time, Duration, Auto-start, Auto-start cutoff, Sound, Low on time, Status | Original seven columns restored. Selection retains native table cells and stable row identities. |
| Scheduling | Edit selected, Remove selected | One action row below the table. No repeated action buttons or added action column. |
| Scheduling | Start date/time, three duration fields, repeat/cutoff, low-time settings, App sound, Add/Save, Cancel edit / new session | Present. Edit retains the entry ID. Reset restores the initial editor values. Hours/minutes/seconds are preserved in the saved duration. |
| Scheduling | Start / Wait / Skip for a due appointment needing a decision | Shared conditional controls operate on the selected appointment. |
| Outbox | Saved locally, Destination, Status, Attempts | Original four columns restored; newest entries first. |
| Outbox | Selected reflection and metadata below the table | Readable region on the page, including actual/allotted time, check-in/early-end details, reason, retry time, and review information. |
| Outbox | Send pending now, Retry selected…, Already in Sheet, Open Google Sheet | Original shared action row restored. Retry uses a labeled review dialog and returns focus on cancel. Already in Sheet acts on the selected review entry. |
| Settings | Theme choice, preview and explanation | Present for all four palettes. Windows contrast colors remain supported by CSS and the host. |
| Settings | Guided setup / another PC and Setup guide | Guided setup opens a separate three-step dialog; closing it restores the same draft fields to Settings. The packaged original guide opens locally. |
| Settings | Compact visibility/position, reflection position, placement explanations | Present. App opens centered; default floating and popup corners are retained. Popup monitor selection follows the mouse pointer, as in the original. |
| Settings | Keyboard shortcut information and availability | Present for all five shortcuts. Start/end shortcut now starts or resumes when stopped and ends early when running. Reopening App preserves a selected Settings tab and does not move focus behind a setup dialog. |
| Settings / Audio | Playback explanation; default low-time warning | Original text and position restored. Enter leaves the threshold field; Save settings / Ctrl+Enter commits it. |
| Settings / Audio | App sound slider and explanation | Restored separately in Settings, sharing the same master volume as Timer. |
| Settings / Audio | Success messages; Failure messages; Low on time audio; Session end · time limit reached | Original four-group order restored. |
| Settings / Audio | Track, Disruptive / Assertive / Polite, Preview audio, Choose MP3… | Present in every group. Defaults/None/bundled/custom selections are supported; unavailable saved tracks remain represented. Native MP3 dialogs require interactive acceptance. |
| Settings / Audio | Per-event Volume slider; Fade out after; seconds; fade explanation | Restored in every group. Fade seconds are disabled when unchecked. Keyboard slider, playback, and fade changes are verified to autosave to the correct event. |
| Settings / Audio | Stop all app audio; autosave and mixing explanation | One shared stop button restored and connected to the backend. |
| Settings | Duplicate-timer confirmation, connection URLs/token/destination, fixed tab name, Save & test connection | Present. Fixed tab name is enabled only for fixed mode. Incomplete settings can be saved; explicit connection testing uses an authenticated ping. |
| Settings | Sign-in startup and local diagnostic recording | Both controls restored. Startup targets only the preview executable/profile with a separate registry value, off by default. Argument/name construction and preference preservation are tested; sign-in itself was not exercised. |
| Settings | Save settings button and Ctrl+Enter | Available only on Settings, matching the original. Visibility checked on all five tabs; shortcut checked outside Settings as well. |
| Diagnostics | Explanation, recording/event/storage summary, recent history, Mark issue, Refresh, Export, Clear log | Present. Summary/history are readable text. Export uses the existing redaction rules; clearing requires the existing confirmation. |

## Optional guidance in 4.2.34

The five working tabs retain their controls and gain a sixth Help tab. Long explanations are collapsed by default, with a circled question-mark button beside each section heading. Each button supports Enter/Space, has a context-specific accessible name, and exposes its expanded state. Closed guidance is removed from optional input descriptions; essential connection and table status descriptions remain. Help collects section guidance, receiver instructions, reflection actions, and keyboard shortcuts without copying private setup fields or reflection data. Settings → Help and guidance → Show all explanations persists independently of timer/audio settings. Individual sections can still be opened or closed. Compact, Time-only, and reflection-window layouts are unchanged.

## Window restoration and Focus captures in 4.2.45

Saved Window targets reconnect to a unique app/title match or, where appropriate, that app's sole eligible window. Executable and native class hints are confined to the encrypted profile. Exact live identities remain authoritative; two real same-title windows and ambiguous documents are not merged. Repeated obsolete bookmarks resolving to the same window are combined. Restoration runs while stopped or Focus is off, retains unrelated settings, and cannot overwrite a changed selection during an asynchronous read. Open chooser drafts follow restored identities without rechecking a choice the user unchecked.

The original dynamic choices remain first. Background choices capture open windows, each browser window's selected tab, each selected tab's group, or every readable group on start/resume. Saved scope identities are distinct and compatible with existing profiles. Captures stay session-local, mode changes do not recapture, and unknown providers suppress false away alerts. Multiple Targets saves all seven scopes together.

Ctrl+Alt+] adds/removes a specific target globally. Browser selection uses a named native dialog, a focused ListBox, Up/Down and Enter, fixed type numbers 1 Tab / 2 Tab Group / 3 Window, Escape cancellation, Alt+Tab visibility, theme palettes and focused-button outlines. Native list selection supplies screen-reader feedback, while the optional vocalizer speaks the same choice without a second reader notification. Committed target changes use both independent feedback channels. Synthetic tests cover choices, number keys, speech routing, profile persistence and session preservation; actual screen-reader speech and keyboard usability still require manual acceptance.

## Global target checkbox consistency in 4.2.46

Ctrl+Alt+] toggles the same saved target checkbox as Choose Window / Tab. An open chooser immediately reflects the change without losing unrelated draft targets, idle options or the keyboard focus of an unchanged row. A toggle during a pending chooser save is retained instead of being overwritten by that older save. Newly captured targets are registered for immediate saving, and a live target replaces its unavailable placeholder.

The browser popup now lists 1 Window / 2 Tab / 3 Tab Group, omitting Tab Group when the current tab is ungrouped. Native accessible item names include checked/unchecked; committed changes use screen-reader feedback and the optional vocalizer. A uniquely matching obsolete window bookmark is unchecked together with its current identity. Reservations cannot turn an ambiguous same-title match into a unique match, and separate live windows remain separate. Synthetic core/native-dialog and browser tests cover these cases; actual screen-reader speech remains a manual acceptance check.

## Browser target selector layout in 4.2.47

The Ctrl+Alt+] browser dialog shows a bold numbered target type, the full name's visual preview on a second line, and a consistently aligned checked/unchecked state. Long names use visual ellipsis while native list item names retain the complete target and state for screen readers. Rows have room for both lines at normal and larger text sizes. The horizontal scrollbar is removed, every available option is fully visible, the heading and keyboard hint share the list's left edge, and the action buttons align to its right edge. Native list selection, arrow/Enter/number shortcuts, cancellation, vocalizer routing, themes and saved target behavior are retained. Tests verify row visibility, alignment and full native accessibility names for two/three targets and larger text across all palettes.

## Independent CSV and Sheets destinations in 4.3.0

In 4.3.1 the default folder is Desktop\Reflection Timer Logs. CSV uses quotes only for cells containing commas, quotes, line breaks, or surrounding whitespace. Empty cells and plain headers are unquoted. Older fully quoted files remain compatible, including safe retries; appends preserve all existing cell values. Multiline responses and pause lists retain their original data and display inside cells when opened in a spreadsheet with Wrap text enabled.

Settings has a searchable Save reflection data section with independently labeled CSV and Google Sheets checkboxes, an editable CSV folder and native Choose folder dialog. Ctrl+S / Ctrl+Enter save these choices from anywhere on Settings. The abandoned extension confirmation and schedule import are absent. Guided Sheets setup retains the same connection fields and tests, with the Google Sheets checkbox replacing extension confirmation. Existing Sheets delivery remains selected on upgrade; new profiles prefer CSV. Each queued entry snapshots its selected destinations. CSV-only entries never become Google uploads when Sheets is enabled later; legacy pending Sheets entries keep their existing routing.

CSV creates missing folders and daily UTF-8 files on entry, with quoted fields, headers, local dates, multiline responses, pause columns and stable entry IDs. The filename replaces Sheets date slashes with hyphens. Atomic replacement and per-file locking retain existing data; retries deduplicate by entry ID even after a failed profile commit. Invalid or unrelated existing files are preserved and reported in Outbox. CSV and Sheets delivery statuses, backoff and retries are independent. Test profiles never export preview-only records. Core checks cover formatting, migration, all destination combinations, concurrency, offline delivery and crash recovery; browser checks cover labels, keyboard saves, folder selection, Settings search, Guided setup, themes and CSV-only retries.

## Focus chooser checkbox clearance in 4.2.52

Window, Browser Tab and Browser Tab Group checkbox cells use an 8-pixel inset on both sides, with a 32-pixel column and no inherited input padding. Keyboard focus outlines remain inside the table border. Number columns remain compact at 64 pixels, with matching left padding for headings and values and enough trailing space to make the heading's left alignment visible. Browser checks cover all three target types and all four themes, including focused checkbox bounds.

## Focus chooser consistency and Desktop in 4.2.51

The chooser's Audio and Animations disclosures share spacing and styling, remain collapsed on opening, and retain their existing controls. Tab # and Grp # use matching left alignment and eight-pixel padding for headers and values, including checkbox mode in every theme. Windows includes one canonical Desktop target; the icon host and shell resolve to the same native identity for capture and live checking, while wallpaper helpers and ordinary app windows stay separate. Same-title real windows receive display-only window numbers and minimized labels without renaming persisted bookmarks or merging distinct windows. These distinctions are also exposed to screen readers. Native inspection confirmed two genuine ChatGPT windows, one minimized, and the desktop shell's tool-window flag explained its previous omission.

## Expanded music library in 4.2.50

Seventeen exact recordings from the maintainer's local music folder extend the library to 25 songs and six notification sounds. The original restored Champion track keeps its saved ID and name; the requested Diamond/Pearl/Platinum Champion recording has its own entry. New IDs append after existing tracks. Settings, Focus audio, Timer and Scheduler share the expanded list and accept the full enum range. Random retains song/notification presets, equal default weights and existing explicit choices; previews retain their normal volume and fading behavior. All 31 MP3s decode, match recorded SHA-256 checksums and appear in the explicit package allowlist. Browser checks cover every selector, probabilities, shared saves and long-name layout in all themes.

## Focus animation styles in 4.2.49

Settings → Animations and Choose Window / Tab → Animations now share a labeled Focus glow style selector. Crimson halo is the default for new profiles and older profiles without a saved style; a previously disabled glow stays disabled. Classic glow preserves 4.2.48's exact pixel gradient, dimensions and pulse. Crimson uses a dark, translucent blood-red perimeter, more than twice the inward reach at normal display sizes, and gently rounded inner corners. It becomes continuously more transparent toward the clear center. Its slow pulse and steady reduced-motion fallback retain the dark red appearance. Style changes update the active native edges without recapturing targets, changing audio or moving focus. Both dropdowns use the existing shared screen-reader selection feedback. Persistence, rapid-change save ordering, shared controls, scaled geometry, corner coverage and native click-through/focus behavior are checked independently of the installed profile.

## Focus tab verification and Animations in 4.2.48

Background focused tabs capture exactly one selected tab per browser window on start/resume, including windows behind another app. These exact tabs remain pinned until the next start/resume. Live checking now verifies a fresh, unique selected tab from its own browser strip rather than retaining a cached accessibility element. Ambiguous providers return Unknown; tab groups and browser Window targets retain their broader OR semantics. Each browser strip is read once per probe, and selected ordinary windows are checked before potentially slow browser providers. The chooser briefly explains the capture and conditionally identifies broader selected categories.

Settings → Animations and the chooser's collapsed Animations disclosure share the persisted screen-edge glow switch, enabled by default for new and existing profiles. While a Timer or Stopwatch Focus session is running and known to be away from every target, the desktop perimeter glows red immediately, independently of audio delay. On-target inactivity can trigger its configured audio but does not produce an away glow. Return, pause, reset, completion, disable, unknown targets and quit remove the glow. Four narrow, transparent native surfaces per monitor are nonactivating tool windows with no input or accessibility text. The slow pulse respects Windows reduced motion; monitor bounds and scale are handled independently. Core and browser tests cover exact-tab selection, gating, defaults, shared persistence, Settings search and keyboard saves. An isolated native preview verified focus preservation, click-through, window styles and removal without changing the installed profile.

## Settings search and help placement in 4.2.37

Settings search in 4.2.37 adds a labeled search landmark at the top of Settings and filters the existing sections without copying controls, indexing typed private values, clearing drafts or changing help disclosure state. Clear search restores all settings and focuses the search field. An invalid pending setting is revealed before validation focuses it. Native Ctrl+S and Ctrl+Enter still save filtered-out edits. Help icons are placed directly after their heading text; the existing full-width heading bars, button styling and accessible heading names are retained.

## Global Focus feedback in 4.2.38

Ctrl+Alt+; sends “Focus mode on” / “Focus mode off” through the same stable native UI Automation notification provider as timer actions. This works without showing App view or moving keyboard focus, independently of optional app voice and volume. Successful native delivery updates readable status without a duplicate live-region event; an unavailable provider retains the live-region fallback. Synthetic host tests cover both Timer and Stopwatch with hidden views, visible-App duplicate suppression and fallback. Windows accepts these notifications; actual JAWS/NVDA/Narrator speech remains a manual acceptance check.

## Focus chooser audio in 4.2.39

Choose Window / Tab now places Start audio after directly above Idle for. Its Focus audio disclosure is collapsed each time the chooser opens, independently of Show all explanations. It contains track, playback behavior, preview, MP3 selection, relative volume, fade timing and Stop audio. The chooser and Settings Audio share one editor revision/save queue and immediately synchronize their controls, retaining pending edits across delayed saves. Ctrl+S and Ctrl+Enter flush audio and away timing before saving targets, with validation and accessible inline failures. Existing audio autosave and preview behavior are retained. The expanded chooser fits all four themes and narrow layouts; native WebView tests cover both save keys from the audio number input.

## Tab sizing and space in 0.4.1

The Scheduling tab is named Scheduler. All five headings remain visible; the row wraps when needed in narrow windows, with unchanged text size and keyboard navigation. Empty status space collapses outside Settings, while Save settings retains its footer. Outbox and diagnostic history expand into spare page height without rearranging their actions.

## Themes and hotkeys in 0.4.2

All four palettes cover the four original views, including text/row selection and warning colors. Glamour adds the original italic header and decorative bow. The theme sample is passive and descriptively labeled. Native frames and the tray menu follow the selection and Windows contrast. Browser checks verify theme changes preserve focused inputs and drafts, and Windows contrast suppresses decoration. All five original chords register through the shared Windows implementation; conflicts are shown in Settings and retry every 15 seconds. Existing reflection shortcuts return focus to its text without replacing its draft.

## Verification and remaining boundaries

The 0.4.2 browser suite passes 132 behavior/semantic checks, including all five selected tab captions at 940, 739, 420, and 336 pixels, wrapped-row keyboard navigation, list growth with available height, and the Settings footer. The 90 C# checks now include original defaults, decoding all eight bundled MP3s, theme persistence/native palette colors, all five shortcut registrations, matching dispatch, collision recovery, release on exit, and period double-press timing. Browser tests use a synthetic bridge and render the App tabs and audio groups. C# tests cover timer/delivery behavior, isolated storage, startup command construction, and read-only connection testing. Packaged audio is checked separately against source hashes.

This preview keeps its separate profile and preserves earlier local example records. New profiles start empty using the original defaults. Those records remain local, with a collapsed simulation action separate from the original Outbox toolbar. The production profile and Windows startup entry are never adopted. The guided setup uses file export/display for private setup material; its clipboard commands and production installer are outside this App-page restoration.

Full native file-dialog, Windows sign-in, high-DPI/multiple-monitor, and JAWS/NVDA/Narrator acceptance remain manual checks. The prior legacy suite's native focus/theme failures are documented in PREVIEW-README.md; these are not represented as passing. No GitHub main update or production release is part of this change.
