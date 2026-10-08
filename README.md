# Reflection Timer 4.3.9

An accessible Windows timer and stopwatch with reflection prompts, focus reminders, and local CSV or optional Google Sheets logging.

## Download and open

1. Open the [latest GitHub release](https://github.com/AirealAce/Reflection-Timer/releases/latest).
2. Under **Assets**, download the application ZIP named **ReflectionTimer-&lt;version&gt;-win-x64.zip**. **Code → Download ZIP** and **Source code** downloads contain developer files, not the ready-to-use app.
3. Right-click the downloaded ZIP and choose **Extract All**. Open **ReflectionTimer.exe** inside the extracted folder. Keep the entire folder together, including **Web**, DLLs and audio files.

You need **Windows x64**. The application ZIP includes .NET: you do **not** need the .NET SDK, Visual Studio, PowerShell or a terminal. If the app asks for it, install the [Microsoft Edge WebView2 Evergreen Runtime](https://developer.microsoft.com/microsoft-edge/webview2/), then reopen the app.

The timer starts at **15 minutes**. Use Timer or Stopwatch, then write your reflection when the session ends. The Help tab and circled question-mark buttons explain controls and keyboard shortcuts.

## Save your reflections

**CSV files** are selected by default. Your first sent reflection creates a daily CSV in **Desktop → Reflection Timer Logs**. Change the folder under **Settings → Save reflection data**.

**Google Sheets is optional.** Use Guided setup to connect your own Sheet, then select Google Sheets under Logging destinations. You can keep CSV and Sheets enabled together. Existing users keep their saved connection and preferences when upgrading.

## Focus mode

Focus reminders can target a **Window**, **Browser Tab** or **Tab Group** without installing a browser extension. You can also enable an idle reminder and choose its audio.

The [optional Chrome/Edge companion](browser-companion/README.md) is needed for **Site** targets, listing all open websites, and following links to other websites from a selected target. Site selection stays disabled until the companion is configured, enabled and connected. The companion is unnecessary for ordinary timer, stopwatch, CSV and Sheets use.

## Updating

Choose **Quit desktop app** from the app's tray menu before replacing its files; closing App view only hides it. Extract the complete new application ZIP and open its **ReflectionTimer.exe**. Your saved settings, drafts and Outbox are kept separately from the application folder. Keep your private settings and backups out of GitHub; a friend should configure their own optional Sheets connection.

If you use the optional browser companion and move the application folder, repeat its [setup steps](browser-companion/README.md) for the new location.

## More help

Open **START-HERE.html** inside the application folder for setup and everyday keyboard help. Further documentation:

- [All keyboard shortcuts](desktop/HOTKEYS.md)
- [Detailed features, defaults and compatibility](docs/FEATURES.md)
- [Browser companion setup](browser-companion/README.md)
- [Security and privacy](SECURITY.md)
- [Developer instructions: building from source](desktop/README.md)

Earlier implementations remain available in Git history. The supported app is the accessible desktop version linked above.
