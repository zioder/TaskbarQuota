using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TaskbarQuota.Helpers;
using TaskbarQuota.Services;
using Windows.System;

namespace TaskbarQuota.Controls;

public sealed partial class UpdateActionBar : UserControl
{
    private enum UpdateUiState
    {
        Idle,
        Checking,
        UpToDate,
        UpdateAvailable,
        Downloading,
        ReadyToInstall,
        Error,
    }

    private readonly UpdateCheckerService _updateChecker = new();
    private UpdateUiState _state = UpdateUiState.Idle;
    private UpdateCheckResult? _pendingResult;
    private DownloadedUpdate? _downloadedUpdate;
    private CancellationTokenSource? _operationCts;

    public UpdateActionBar()
    {
        InitializeComponent();
        Loaded += (_, _) => SetState(UpdateUiState.Idle);
    }

    public async Task RunCheckAsync()
    {
        if (_state is UpdateUiState.Checking or UpdateUiState.Downloading)
            return;

        await CheckForUpdatesAsync();
    }

    private async void ActionButton_Click(object sender, RoutedEventArgs e)
    {
        switch (_state)
        {
            case UpdateUiState.Idle:
            case UpdateUiState.UpToDate:
            case UpdateUiState.Error:
                if (_pendingResult?.ReleaseUrl is { } releasePage
                    && ActionButton.Content?.ToString() == "View release")
                {
                    _ = Launcher.LaunchUriAsync(releasePage);
                }
                else
                {
                    await CheckForUpdatesAsync();
                }
                break;
            case UpdateUiState.UpdateAvailable:
                await DownloadUpdateAsync();
                break;
            case UpdateUiState.ReadyToInstall:
                LaunchInstallerAndQuit();
                break;
        }
    }

    private async Task CheckForUpdatesAsync()
    {
        CancelOperation();
        _operationCts = new CancellationTokenSource();
        var ct = _operationCts.Token;
        _pendingResult = null;
        _downloadedUpdate = null;

        SetState(UpdateUiState.Checking);
        try
        {
            var current = AppVersion.GetDisplayLabel();
            var result = await _updateChecker.CheckAsync(current, ct);
            _pendingResult = result;

            if (result.Kind == UpdateCheckResultKind.UpToDate)
            {
                SetState(UpdateUiState.UpToDate, $"You are on v{current}.");
                return;
            }

            if (result.DownloadUrl is null)
            {
                SetState(UpdateUiState.Error, "Update found but no installer asset is attached to the release.");
                ActionButton.Content = "View release";
                return;
            }

            SetState(UpdateUiState.UpdateAvailable, $"v{result.Version} is available (you have v{current}).");
        }
        catch (OperationCanceledException)
        {
            SetState(UpdateUiState.Idle);
        }
        catch (Exception ex)
        {
            SetState(UpdateUiState.Error, ex.Message);
        }
    }

    private async Task DownloadUpdateAsync()
    {
        if (_pendingResult is not { Kind: UpdateCheckResultKind.UpdateAvailable } result)
            return;

        CancelOperation();
        _operationCts = new CancellationTokenSource();
        var ct = _operationCts.Token;

        SetState(UpdateUiState.Downloading, $"Downloading v{result.Version}…");
        try
        {
            var progress = new Progress<UpdateDownloadProgress>(report =>
            {
                _ = DispatcherQueue.TryEnqueue(() => ReportDownload(report));
            });

            _downloadedUpdate = await _updateChecker.DownloadAsync(result, progress, ct);
            SetState(UpdateUiState.ReadyToInstall, $"Installer ready — v{_downloadedUpdate.Version}.");
        }
        catch (OperationCanceledException)
        {
            SetState(UpdateUiState.UpdateAvailable, $"v{result.Version} is available.");
        }
        catch (Exception ex)
        {
            SetState(UpdateUiState.UpdateAvailable, $"Download failed: {ex.Message}");
            ActionButton.Content = "Retry download";
        }
    }

    private void LaunchInstallerAndQuit()
    {
        if (_downloadedUpdate is null && _pendingResult?.DownloadUrl is { } releaseUrl)
        {
            _ = Launcher.LaunchUriAsync(releaseUrl);
            return;
        }

        if (_downloadedUpdate is not { } update)
            return;

        Process.Start(new ProcessStartInfo(update.FilePath)
        {
            UseShellExecute = true,
        });

        App.Quit();
    }

    private void ReportDownload(UpdateDownloadProgress progress)
    {
        if (progress.TotalBytes is > 0)
        {
            DownloadProgress.IsIndeterminate = false;
            DownloadProgress.Value = progress.Percent;
            DetailText.Text =
                $"{FormatByteSize(progress.BytesReceived)} / {FormatByteSize(progress.TotalBytes.Value)} ({progress.Percent:0}%)";
            DetailText.Visibility = Visibility.Visible;
        }
        else
        {
            DownloadProgress.IsIndeterminate = true;
            DetailText.Text = $"{FormatByteSize(progress.BytesReceived)} downloaded";
            DetailText.Visibility = Visibility.Visible;
        }
    }

    private void SetState(UpdateUiState state, string? message = null)
    {
        _state = state;
        BusyRing.Visibility = Visibility.Collapsed;
        BusyRing.IsActive = false;
        DownloadProgress.Visibility = Visibility.Collapsed;
        DownloadProgress.IsIndeterminate = false;
        DownloadProgress.Value = 0;
        DetailText.Visibility = Visibility.Collapsed;
        ActionButton.IsEnabled = true;

        switch (state)
        {
            case UpdateUiState.Idle:
                StatusText.Text = message ?? "Check for the latest release on GitHub.";
                ActionButton.Content = "Check for updates";
                ActionButton.Visibility = Visibility.Visible;
                break;
            case UpdateUiState.Checking:
                StatusText.Text = message ?? "Checking for updates…";
                BusyRing.Visibility = Visibility.Visible;
                BusyRing.IsActive = true;
                ActionButton.Visibility = Visibility.Collapsed;
                ActionButton.IsEnabled = false;
                break;
            case UpdateUiState.UpToDate:
                StatusText.Text = message ?? "You are up to date.";
                ActionButton.Content = "Check again";
                ActionButton.Visibility = Visibility.Visible;
                break;
            case UpdateUiState.UpdateAvailable:
                StatusText.Text = message ?? "A new version is available.";
                ActionButton.Content = "Download update";
                ActionButton.Visibility = Visibility.Visible;
                break;
            case UpdateUiState.Downloading:
                StatusText.Text = message ?? "Downloading update…";
                DownloadProgress.Visibility = Visibility.Visible;
                DownloadProgress.IsIndeterminate = true;
                ActionButton.Visibility = Visibility.Collapsed;
                ActionButton.IsEnabled = false;
                break;
            case UpdateUiState.ReadyToInstall:
                StatusText.Text = message ?? "Update downloaded.";
                ActionButton.Content = "Install update";
                ActionButton.Visibility = Visibility.Visible;
                break;
            case UpdateUiState.Error:
                StatusText.Text = message ?? "Could not check for updates.";
                ActionButton.Content = "Check for updates";
                ActionButton.Visibility = Visibility.Visible;
                break;
        }
    }

    private void CancelOperation()
    {
        _operationCts?.Cancel();
        _operationCts?.Dispose();
        _operationCts = null;
    }

    private static string FormatByteSize(long bytes)
    {
        if (bytes < 1_024)
            return $"{bytes} B";

        var size = bytes / 1024.0;
        if (size < 1_024)
            return $"{size:0.#} KB";

        return $"{size / 1024.0:0.#} MB";
    }
}
