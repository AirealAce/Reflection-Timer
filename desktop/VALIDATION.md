# Release validation

Run `npm ci --ignore-scripts` once, with Node 24 and the .NET 10 SDK installed.
On Windows with Edge installed, run `./desktop/validate.ps1` from PowerShell.
For bundled Chromium instead, run `npx playwright install chromium` and set
`REFLECTION_TEST_BROWSER=chromium`. No account credentials or real profile are used.

The same gate runs for master pushes and pull requests in GitHub Actions and before
`desktop/package-release.ps1` creates a package. It checks version consistency,
source privacy patterns, engine/service behavior, browser/accessibility behavior,
the Sheets receiver and bundled release assets. CI does not publish or install.

`VERSION` is the authoritative desktop release number. After changing it, run
`npm run version:sync` to update generated documentation, HTML and npm mirrors.
MSBuild reads VERSION directly; native window labels use the assembly version.
Archived extension/desktop versions and historical release notes are not changed.

On an unlocked Windows development desktop, also run the test executable with
`--native-clock` and `--native-stopwatch`. These opt-in smoke checks create isolated,
muted WebView2 windows with synthetic data and fake shortcut registration. They do
not operate the installed app. Native focus/tray checks still require manual QA;
headless CI alone does not prove operating-system integration.
