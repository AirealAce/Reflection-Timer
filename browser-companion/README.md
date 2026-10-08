# Reflection Timer Focus Companion (optional)

Reflection Timer uses native Windows accessibility for Window, Tab and Tab Group targets by default. This separate companion is optional and required for Site targets. It supplies a complete open-website inventory and browser metadata for linked sites opened from selected targets, including links to other websites. It is not the old browser timer extension.

The app's **Browser companion** setting is off by default. Ordinary Window, Tab and Tab Group targets do not require this companion. Site selection and linked-site tracking require both the companion installed in the browser and the app setting enabled. Site selection additionally waits for a fresh, complete browser snapshot; a handshake alone is insufficient. Enabling the app setting cannot install a browser extension.

## One-time setup

1. In Reflection Timer's focus target chooser, choose **Configure companion…**. The setup action registers the native messaging host for your Windows account and opens this installed `browser-companion` folder. Check **Use browser companion** and save the focus settings. Keep the folder in its installed location.
2. In Chrome open `chrome://extensions`; in Edge open `edge://extensions`.
3. Turn on **Developer mode**, choose **Load unpacked**, and select this `browser-companion` folder. Load it separately in each browser/profile you want to use.
4. Keep Reflection Timer running. If Chrome/Edge still reports a disconnected native host, reload the companion once on the extensions page. Automatic reconnect also runs, with a delay of up to 30 seconds after repeated failures.

The fixed unpacked extension ID is `mgalafjodgnoeponalohbmdopkkbnfok`. The `key` in `manifest.json` is a public key used to keep that ID stable, not a credential. There is no Chrome Web Store installation in this package. The first-time Developer mode / Load unpacked step is required for this distribution.

To stop the optional connection, turn off **Browser companion** in the app. You can also disable/remove the companion on the browser's extensions page to stop its reconnect attempts. Site selection becomes unavailable. Saved Site targets are retained and can be removed; they remain quiet until the connection is ready again. No app installer automatically registers or enables this connection. An app upgrade preserves a valid existing native-host configuration in the installed folder.

## Data and permissions

The companion reads current window/tab/group metadata and live navigation events. It sends tab/group titles, IDs, window focus/bounds, and normalized website hosts (for example `example.org` or `example.org:8443`) to the app through a Windows named pipe. Website address fields contain only the host and optional non-default port; they omit URL paths, queries, and fragments. Browser titles can contain personal text or even a full URL, and those titles are included as display metadata. Live metadata and link relationships stay in memory. Names of targets you explicitly save are encrypted with the app's ordinary target settings. The companion does not collect page bodies, cookies, or browsing history, inject scripts into pages, make network requests, or open a network listener.

The `tabs`, `tabGroups`, `webNavigation`, and `nativeMessaging` permissions support that connection. Chrome/Edge may describe tab/navigation permissions using wording about browsing history; this code does not call the history API or read a browser profile/history database. Private browsing is not enabled by default.

Tracking uses browser session IDs and actual navigation events. It does not guess links from matching titles, newly appearing tabs, or old opener metadata. Connections and inherited links reset after a service-worker restart or connection gap; links followed while disconnected cannot be reconstructed reliably. Unrelated address-bar navigation can end inherited targeting according to the app's focus policy.

Desktop-to-browser window binding accounts for normal Windows display scaling and accessibility text scaling. It supports the primary monitor and ordinary mixed-scale monitor layouts whose touching displays form an unambiguous tree without overlapping scaled bounds. More complex monitor arrangements, unreadable display scaling, and custom browser device-scale launch overrides are not supported for companion window binding. When the app cannot establish coordinates safely, it treats the affected Site target as unavailable instead of guessing a browser window from its title.

Only the companion's fixed extension origin is allowed in the native-host manifest. The native host connects only to the current Windows user's running Reflection Timer. It does not launch the main app, read app settings, or change drafts/audio. Its host-registration file contains the installed executable path; it does not contain browsing metadata.

## Protocol and development checks

Messages use a four-byte little-endian UTF-8 JSON length followed by JSON, bounded to 1 MiB. Browser messages have `version: 1`, `type`, `browser`, a random worker `epoch`, and increasing `seq`. Snapshots contain bounded `windows`, `tabs`, `groups`, and `currentWindowId`; `siteHost` is a host with a non-default port, never a full URL. Immediate `navigation` events describe `created`, top-frame `committed`, `replaced`, or `removed` tabs. Desktop acknowledgements echo `epoch` and `seq`; a stale connection is closed.

Run `node browser-companion/tests.cjs` from the repository root. Tests use mocked browser APIs and synthetic metadata; they do not open a browser, read profiles, register a host, or change your settings.
