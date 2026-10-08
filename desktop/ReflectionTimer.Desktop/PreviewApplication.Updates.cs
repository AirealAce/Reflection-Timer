namespace ReflectionTimer.Accessible;

internal sealed record DesktopUpdateState(string Status, string CurrentVersion, string Message, string? AvailableVersion = null, int? Progress = null);

internal sealed partial class PreviewApplication
{
    private static readonly Version InstalledVersion = new(typeof(PreviewApplication).Assembly.GetName().Version!.ToString(3));
    private readonly DesktopUpdateClient desktopUpdates = new();
    private readonly CancellationTokenSource updateLifetime = new();
    private DesktopUpdateRelease? availableUpdate;
    private string? preparedUpdatePayload;
    private bool updateBusy;
    private bool updatesDisposed;
    internal DesktopUpdateState UpdateState { get; private set; } = new("idle", InstalledVersion.ToString(3), "Check for a newer version.");

    private void PublishUpdate(string status, string message, int? progress = null)
    {
        if(closing || updateLifetime.IsCancellationRequested)return;
        UpdateState = new(status, InstalledVersion.ToString(3), message, availableUpdate?.Version.ToString(3), progress);
        Broadcast(new { type = "updates", update = UpdateState });
    }

    internal async Task CheckForUpdatesAsync()
    {
        if(updateBusy || closing)return;
        updateBusy = true;
        PublishUpdate("checking", "Checking GitHub for updates…");
        try {
            availableUpdate = await desktopUpdates.CheckAsync(InstalledVersion, updateLifetime.Token);
            preparedUpdatePayload = null;
            PublishUpdate(availableUpdate is null ? "current" : "available", availableUpdate is null
                ? $"Version {InstalledVersion.ToString(3)} is up to date."
                : $"Version {availableUpdate.Version.ToString(3)} is available. Update now downloads it and restarts the app.");
        }
        catch(OperationCanceledException) when(updateLifetime.IsCancellationRequested) { }
        catch(Exception error) { PublishUpdate("error", UpdateFailure(error)); }
        finally { updateBusy = false; }
    }

    internal async Task InstallUpdateAsync()
    {
        if(updateBusy || closing)return;
        if(availableUpdate is null) { await CheckForUpdatesAsync();return; }
        if(Session.Engine.SettingsSnapshot.Timer.IsRunning) {
            PublishUpdate("available", "Pause the timer or stopwatch before installing. Your session and drafts will be kept.");return;
        }
        updateBusy = true;
        try {
            if(!DesktopUpdateInstaller.IsUpdateAllowed(AppContext.BaseDirectory, out var reason))throw new InvalidOperationException(reason);
            if(preparedUpdatePayload is null) {
                PublishUpdate("downloading", $"Downloading version {availableUpdate.Version.ToString(3)}…", 0);
                var stage = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ReflectionTimerDesktop", "Updates");
                var progress = new Progress<int>(percent => {
                    if(UpdateState.Status == "downloading")PublishUpdate("downloading", $"Downloading update: {percent}%.", percent);
                });
                preparedUpdatePayload = await desktopUpdates.DownloadAsync(availableUpdate, stage, progress, updateLifetime.Token);
            }
            if(Session.Engine.SettingsSnapshot.Timer.IsRunning) {
                PublishUpdate("ready", "The update is verified and ready. Pause the timer or stopwatch, then choose Update now.");return;
            }
            PublishUpdate("installing", "Update verified. Saving open drafts and restarting…");
            var restartArguments = new List<string>();
            if(ProfileName is not null) { restartArguments.Add("--profile");restartArguments.Add(ProfileName); }
            if(StartInTray)restartArguments.Add("--tray");
            var plan = await Task.Run(() => DesktopUpdateInstaller.Prepare(preparedUpdatePayload, AppContext.BaseDirectory,
                Environment.ProcessPath ?? throw new InvalidOperationException("The running application location could not be found."), restartArguments), updateLifetime.Token);
            var exited = await CloseMainAsync(() => {
                if(Session.Engine.SettingsSnapshot.Timer.IsRunning)throw new InvalidOperationException("Pause the timer or stopwatch before installing.");
                DesktopUpdateInstaller.Launch(plan, Environment.ProcessId);
            });
            if(!exited)PublishUpdate("ready", "The update could not restart the app. Your data is retained. Close any open dialogs and choose Update now again.");
        }
        catch(OperationCanceledException) when(updateLifetime.IsCancellationRequested) { }
        catch(Exception error) { PublishUpdate("error", UpdateFailure(error)); }
        finally { updateBusy = false; }
    }

    private static string UpdateFailure(Exception error) => error switch {
        HttpRequestException => "Could not reach GitHub. Check your connection and try again. The installed app is unchanged.",
        OperationCanceledException => "The update request timed out. Check your connection and try again. The installed app is unchanged.",
        UnauthorizedAccessException => "The app folder cannot be updated. Move the complete app folder to a writable location and try again.",
        InvalidOperationException => error.Message,
        _ => "The update could not be verified or prepared. The installed app and your saved data are unchanged. Check for updates and try again."
    };

    private void DisposeUpdates()
    {
        // ApplicationContext can be disposed by the message loop and again by
        // its using scope after the last form closes.
        if(updatesDisposed)return;
        updatesDisposed = true;
        updateLifetime.Cancel();
        desktopUpdates.Dispose();
        updateLifetime.Dispose();
    }
}
