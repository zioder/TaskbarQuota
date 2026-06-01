using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TaskbarQuota.Diagnostics;
using TaskbarQuota.Helpers;

namespace TaskbarQuota.Services;

public enum UpdateAvailabilityUiState
{
    Hidden,
    UpdateAvailable,
    Downloading,
    ReadyToInstall,
}

/// <summary>Silent background update checks; UI only appears when a newer release exists.</summary>
public sealed class UpdateAvailabilityService
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);

    public static UpdateAvailabilityService Instance { get; } = new();

    private readonly UpdateCheckerService _checker = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CancellationTokenSource? _operationCts;

    public UpdateAvailabilityUiState UiState { get; private set; } = UpdateAvailabilityUiState.Hidden;
    public UpdateCheckResult? AvailableUpdate { get; private set; }
    public DownloadedUpdate? DownloadedUpdate { get; private set; }
    public string? StatusMessage { get; private set; }
    public string? UpToDateSummary { get; private set; }
    public bool IsChecking { get; private set; }

    public bool IsBannerVisible => UiState != UpdateAvailabilityUiState.Hidden;

    public event Action? Changed;

    public Task CheckManuallyAsync() => CheckSilentlyAsync(force: true);

    public async Task CheckSilentlyAsync(bool force = false)
    {
        if (UiState is UpdateAvailabilityUiState.Downloading or UpdateAvailabilityUiState.ReadyToInstall)
            return;

        if (!force && !ShouldCheckNow())
            return;

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (UiState is UpdateAvailabilityUiState.Downloading or UpdateAvailabilityUiState.ReadyToInstall)
                return;

            CancelOperation();
            _operationCts = new CancellationTokenSource();
            var ct = _operationCts.Token;

            IsChecking = true;
            NotifyChanged();

            var current = AppVersion.GetDisplayLabel();
            var result = await _checker.CheckAsync(current, ct).ConfigureAwait(false);
            RecordCheckAttempt();

            if (result.Kind == UpdateCheckResultKind.UpToDate)
            {
                UpToDateSummary = $"You are on v{current} (latest).";
                SetHidden();
                return;
            }

            AvailableUpdate = result;
            DownloadedUpdate = null;

            if (result.DownloadUrl is null)
            {
                StatusMessage = $"New update available (v{result.Version}) — see GitHub release.";
                UiState = UpdateAvailabilityUiState.UpdateAvailable;
            }
            else
            {
                StatusMessage = $"New update available! v{result.Version} is ready.";
                UiState = UpdateAvailabilityUiState.UpdateAvailable;
            }

            NotifyChanged();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Silent update check failed");
            UpToDateSummary = "Could not check for updates. Try again.";
            SetHidden();
        }
        finally
        {
            IsChecking = false;
            _gate.Release();
            NotifyChanged();
        }
    }

    public async Task DownloadAsync(IProgress<UpdateDownloadProgress>? progress = null)
    {
        if (AvailableUpdate is not { Kind: UpdateCheckResultKind.UpdateAvailable } result
            || result.DownloadUrl is null)
        {
            return;
        }

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            CancelOperation();
            _operationCts = new CancellationTokenSource();
            var ct = _operationCts.Token;

            UiState = UpdateAvailabilityUiState.Downloading;
            StatusMessage = $"Downloading v{result.Version}…";
            NotifyChanged();

            DownloadedUpdate = await _checker.DownloadAsync(result, progress, ct).ConfigureAwait(false);
            UiState = UpdateAvailabilityUiState.ReadyToInstall;
            StatusMessage = $"New update available! Install v{DownloadedUpdate.Version}.";
            NotifyChanged();
        }
        catch (OperationCanceledException)
        {
            if (AvailableUpdate is not null)
            {
                UiState = UpdateAvailabilityUiState.UpdateAvailable;
                StatusMessage = $"New update available! v{AvailableUpdate.Version} is ready.";
                NotifyChanged();
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Update download failed");
            if (AvailableUpdate is not null)
            {
                UiState = UpdateAvailabilityUiState.UpdateAvailable;
                StatusMessage = $"Download failed — tap to retry (v{AvailableUpdate.Version}).";
                NotifyChanged();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public void InstallAndQuit()
    {
        if (DownloadedUpdate is { } downloaded)
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(downloaded.FilePath)
            {
                UseShellExecute = true,
            });
            App.Quit();
            return;
        }

        if (AvailableUpdate?.ReleaseUrl is { } releaseUrl)
            _ = Windows.System.Launcher.LaunchUriAsync(releaseUrl);
    }

    private void SetHidden()
    {
        UiState = UpdateAvailabilityUiState.Hidden;
        AvailableUpdate = null;
        DownloadedUpdate = null;
        StatusMessage = null;
        NotifyChanged();
    }

    private void NotifyChanged() => Changed?.Invoke();

    private void CancelOperation()
    {
        _operationCts?.Cancel();
        _operationCts?.Dispose();
        _operationCts = null;
    }

    private static bool ShouldCheckNow()
    {
        try
        {
            var path = GetCachePath();
            if (!File.Exists(path))
                return true;

            var json = File.ReadAllText(path);
            var cache = JsonSerializer.Deserialize<UpdateCheckCache>(json);
            if (cache?.LastCheckUtc is not { } last)
                return true;

            return DateTime.UtcNow - last >= CheckInterval;
        }
        catch
        {
            return true;
        }
    }

    private static void RecordCheckAttempt()
    {
        try
        {
            var path = GetCachePath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var cache = new UpdateCheckCache { LastCheckUtc = DateTime.UtcNow };
            File.WriteAllText(path, JsonSerializer.Serialize(cache));
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to write update check cache");
        }
    }

    private static string GetCachePath() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TaskbarQuota",
            "update-check.json");

    private sealed class UpdateCheckCache
    {
        public DateTime? LastCheckUtc { get; set; }
    }
}
