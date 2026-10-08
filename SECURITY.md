# Security and privacy

Download the accessible desktop app from [GitHub Releases](https://github.com/AirealAce/Reflection-Timer/releases/latest). Source updates do not automatically publish an application download. Application packages include .NET and need no SDK. They are unsigned and include a SHA-256 checksum and file manifest; checksums detect corruption and do not establish publisher identity or guarantee safety. The app does not require administrator access.

CSV logging is selected by default. CSV files are ordinary readable files in the chosen folder, initially Desktop/Reflection Timer Logs. Google Sheets is optional: each person supplies their own spreadsheet, Apps Script deployment and randomly generated token. Fresh installations have no connection credentials. When enabled, Sheets delivery uses the configured Google Apps Script service, with HTTPS and restricted redirects. The app has no analytics or automatic diagnostic uploads. The developer is not added to the user's spreadsheet.

Desktop settings, credentials, drafts and delivery queue are encrypted using Windows DPAPI in `%USERPROFILE%\.reflection-timer`. On first launch, an existing legacy `%LOCALAPPDATA%\ReflectionTimerDesktop` profile is copied and verified without deleting its original. Encryption protects files at rest; software running as the same Windows user may still access them. CSV exports are not encrypted by the app.

Private setup codes are encoded, **not encrypted**. Treat a setup code, personalized setup script and API token as passwords. Never post them, state files, private spreadsheet links or unreviewed diagnostics in a public issue. A token allows submissions to its configured receiver. Restrict access to the spreadsheet and Apps Script project to trusted people.

If a token is exposed, disable the deployment or replace `REFLECTION_API_TOKEN` in the receiver's Script Properties, then update trusted clients. Removing a file from Git does not remove earlier commits or copies already downloaded. Old version-1 service-account keys should be revoked if they are still active.

For security reports, use GitHub's private vulnerability reporting option on the repository Security page when available. If unavailable, open an issue asking for a private reporting channel without including exploit details, credentials or personal data. Ordinary issues can include reproduction steps, app version and a reviewed diagnostic export.

Public desktop packages include only the 31 MP3s listed and hashed in `desktop/Sounds/sources.json`. Packaging rejects additional or changed audio files, scans for private data, and includes `AUDIO-NOTICES.txt`. Custom audio selections remain local and are preserved on upgrade; the app never uploads audio paths or recordings. Built-in tones remain available as a fallback when a default file is missing.

The optional browser companion sends current tab/window/group metadata and live navigation events to the running timer through a connection restricted to the current Windows user. It omits URL paths, queries and fragments from website address fields, although browser titles may themselves contain personal text. It does not read page bodies, cookies or browsing history, inject page scripts or send metadata over the network. See [companion data and permissions](browser-companion/README.md#data-and-permissions).

The primary interface uses semantic HTML in WebView2 with labels, headings, tables, keyboard focus and restrained announcements. Automated checks and initial screen-reader feedback support the approach; complete compatibility with every screen reader has not been verified. Earlier implementations remain available in Git history and are not part of the current app package.
