# Reflection Timer desktop 4.3.14

## Download the app

To use Reflection Timer, follow the [download and setup instructions](../README.md#download-and-open). Download the application ZIP from [GitHub Releases](https://github.com/AirealAce/Reflection-Timer/releases/latest), extract the whole folder and open **ReflectionTimer.exe**. The prebuilt application includes .NET and needs no SDK, Visual Studio, build commands or terminal. WebView2 Runtime is required if it is not already installed.

**The instructions below are for developers working with the source code.** GitHub's Code → Download ZIP and Source code downloads are source packages, not prebuilt applications.

## Source requirements

The current app uses semantic HTML inside a C# / WebView2 Windows desktop host, backed by the shared timer and logging engine. Development requires Windows x64, the **.NET 10 SDK**, and **Node.js** for the JavaScript checks. Browser tests also use Playwright; install its dependencies with `npm ci --ignore-scripts` from the repository root. The local browser tests use Microsoft Edge by default; CI installs Chromium and sets `REFLECTION_TEST_BROWSER=chromium`.

Commands on this page run from the repository root unless stated otherwise. These development dependencies are not required by users of the application ZIP.

## Build and check

```powershell
dotnet build desktop/ReflectionTimer.Desktop/ReflectionTimer.Desktop.csproj -c Release
.\desktop\validate.ps1
```

`validate.ps1` runs version and public-source checks, desktop tests, browser accessibility/settings checks, and receiver/package tests. For focused checks:

```powershell
dotnet run --project desktop/ReflectionTimer.Tests -c Release
node desktop/ReflectionTimer.Tests/ui.cjs
node desktop/ReflectionTimer.Tests/focus-mode.cjs
node desktop/ReflectionTimer.Tests/site-focus.cjs
node browser-companion/tests.cjs
node --test test/apps-script.test.js test/receiver-setup.test.js test/release-assets.test.js test/version.test.cjs
node scripts/check-public-source.cjs
```

Add `-- --focus-mode` to the `dotnet run` command to run the focused native checks. `-- --focus-target-scan` reads current window/tab/group accessibility providers and reports counts without target names. The [feature reference](../docs/FEATURES.md) documents target identity, focus behavior, audio defaults, screen-reader support and receiver compatibility.

## Install a source build

```powershell
.\desktop\install.ps1
```

This command **requires the .NET 10 SDK**: it runs desktop tests, publishes a self-contained application, then installs it per user with the usual desktop shortcut. Quit Reflection Timer from its tray menu first. The installer retains the previous application folder, verifies backups of encrypted data, preserves locally added MP3s, and carries forward a valid existing browser-companion configuration without registering or enabling one automatically. It never pushes or publishes changes.

`-PackageDirectory` can install an already published application folder without running the build. Ordinary users can simply extract and launch the release ZIP and do not need this source installer.

## Create a release ZIP

```powershell
.\desktop\package-release.ps1
```

This developer command runs validation and publishes **win-x64 with `--self-contained true`**, including .NET and the required web/audio assets. It verifies the public payload, creates **ReflectionTimer-&lt;version&gt;-win-x64.zip**, and writes a SHA-256 checksum beside it. Packaging happens locally and never uploads a release. Distribute this application ZIP rather than a source archive or a normal framework-dependent build folder.

## Data and test profiles

Normal launches use the encrypted `%USERPROFILE%\.reflection-timer` profile. First launch copies and verifies an existing `%LOCALAPPDATA%\ReflectionTimerDesktop` profile without deleting it; an existing shared profile is never replaced by a stale legacy copy. Settings, drafts, schedules, Outbox, audio choices and the optional Sheets connection are retained. Installer backups are under `%USERPROFILE%\.reflection-timer-backups`.

Explicit `--profile NAME` launches use an isolated development profile in the older accessibility-preview data location. `ReflectionTimer.exe --check-connection` performs an authenticated read-only Sheets check and prints only the result and receiver capabilities. Keep personal settings, generated receiver scripts, credentials and data backups outside Git and application packages.

If older launches created multiple encrypted profiles, keep all copies and recover deliberately while the app is fully quit. Do not merge Outbox entries blindly: request IDs prevent duplicate submissions. See [migration details](../docs/FEATURES.md#preferences-upgrades-and-defaults) and [security notes](../SECURITY.md).

## Repository layout

- `desktop/ReflectionTimer.Desktop/` — current accessible app and WebView2 host.
- `desktop/ReflectionTimer.Core/` — shared timer, storage and logging engine.
- `desktop/ReflectionTimer.Tests/` — desktop and browser checks.
- `desktop/Sounds/` — approved bundled audio and source notices.
- `browser-companion/` — optional Chrome/Edge metadata companion.
- `google-sheets-script.gs` — shared optional Google Sheets receiver.
- `scripts/` and `test/` — version, source, audio and receiver/package checks.

Earlier implementations remain available in Git history. The optional browser companion is part of the current app.

See [START-HERE](START-HERE.html), [HOTKEYS](HOTKEYS.md), and [audio sources](Sounds/README.md) for user-facing help.
