# Screen reader feedback review

Review date: 2026-10-29. This records implementation and regression coverage, not certification for every screen reader or a substitute for listening with a real reader.

## Why a background shortcut could be silent

Timer actions already produced a detailed, committed status message. The previous native route used JAWS's speech API when available, otherwise a Windows UI Automation notification from the timer's main window. Windows accepting that notification did not establish that NVDA spoke it: NVDA's standard notification handler explicitly ignores notifications from a different application than the focused application. An inactive WebView live region is also insufficient for this use case. See the [NVDA notification handler](https://github.com/nvaccess/nvda/blob/master/source/NVDAObjects/UIA/__init__.py) and the [official NVDA Controller Client documentation](https://github.com/nvaccess/nvda/blob/master/extras/controllerClient/readme.md).

Feedback now has a direct route to an already-running NVDA as well as JAWS, with the native notification and browser live-region paths retained as fallbacks. This does not start a screen reader, change its settings, force forms mode, or move keyboard focus. Reader output remains independent of the app's optional Windows voice and audio volume.

## Coverage by feature

| Feature | Feedback contract | Regression coverage |
| --- | --- | --- |
| Timer and Stopwatch start, pause and resume | One committed action with remaining or elapsed time; works from another app | `ScreenReaderFeedbackTests`, `KeyboardFeedbackDeliveryTests`, `NativeFeedbackRoutingTests` |
| Ctrl+Alt+Space status request | Read current status without changing the session or focus; repeated identical requests remain announceable | `NativeTimerStatusSmoke`, `NativeFeedbackRoutingTests` |
| Reset and Timer/Stopwatch mode changes | Announce the committed result; a cancelled or failed change must not claim success | `ResetConfirmationTests`, `NativeResetDialogSmoke`, `NativeFeedbackRoutingTests` |
| Focus mode on/off and target changes | Report saved state through the shared output; do not open App view just to announce | `ScreenReaderFeedbackTests`, `FocusTargetToggleTests`, `NativeFeedbackRoutingTests` |
| App, Compact, Time-only and hidden viewer preferences | Output does not require a focused or visible timer WebView; view labels follow the main action | `KeyboardFeedbackDeliveryTests`, `NativeFeedbackRoutingTests`, `NativeGlobalPlaybackFocusSmoke` |
| Reflection Skip, save and send results | Confirm committed actions and retain readable status after a popup closes | `NativeFeedbackRoutingTests`, `feedback-announcements.cjs`, reflection command tests |
| Settings and general command confirmations or errors | Retain visible text; schedule one reader attempt without turning every status into optional app speech | `NativeFeedbackRoutingTests`, `feedback-announcements.cjs`, settings save tests |
| Outbox/delivery guidance | Use the same general feedback path; reader failure must not change delivery or resend a message | `NativeFeedbackRoutingTests`, existing delivery/retry tests |
| Low-time, natural timer completion and stopwatch time-reached guidance | One announcement per emitted boundary, with audio settings independent | `NativeFeedbackRoutingTests`, existing clock/audio tests |
| Focus-target and reset dialogs | Named native controls expose actual focus and checked state; avoid an extra reader announcement for native focus events | `FocusTargetToggleTests`, `NativeFocusTargetPopupSmoke`, `NativeResetDialogSmoke` |
| Select controls, help and modal browser dialogs | Keep native HTML semantics and put live feedback inside the active modal scope | `feedback-announcements.cjs`, existing select/help tests |
| Reader unavailable, restarted or failing | Contain failures, retry on later requests, and use the remaining routes without undoing app actions | Reader backend/router tests and `KeyboardFeedbackDeliveryTests` |
| Held shortcuts, rapid actions and shutdown | Wait for key release, preserve primary action priority, and discard obsolete/disposed work | `KeyboardFeedbackDeliveryTests`, reader backend tests, `NativeFeedbackRoutingTests` |

`NativeFeedbackRoutingTests` uses hidden native hosts, saved viewer preferences, fake registration/output, a controlled dispatch queue and an isolated in-memory profile. It verifies message generation, routing and focus preservation. It does not claim that a real NVDA, JAWS or Narrator synthesizer was audible. Browser tests separately verify that native-delivered text remains readable without a duplicate live announcement, and that fallback status/errors remain accessible in modal dialogs.

## Manual acceptance still needed

The 2026-10-29 listening session confirmed a direct background NVDA Controller
message was audible. The in-memory production playback test emitted one pause
and one resume message, kept foreground/native keyboard focus unchanged and
left the real session untouched; the listener heard an announcement but was
occupied, so exact audibility of both playback messages remains unconfirmed.
API return codes and the automated message assertions are not recorded as a
complete listening pass.

Use the currently installed NVDA, JAWS and Narrator in turn. From Chrome, a native editor and the Desktop, test Ctrl+Space, Ctrl+Alt+Space, reset, mode switching, Focus mode and target selection with the timer viewers hidden and visible. Confirm the action and its time are heard once, repeated status requests are heard, and keyboard focus stays in the originating application. Also test native dialog focus/checked state, browser form fields and help, general success/error feedback, and reader restart while the timer stays open.

Test with optional app voice disabled and app audio muted, then with optional voice enabled. Reader speech mode, braille preferences, sleep mode, keyboard interception and secure-desktop policy remain under the reader/Windows user's control. Automated acceptance of a Windows event or reader API request is not proof of audible speech or complete assistive-technology compatibility.
