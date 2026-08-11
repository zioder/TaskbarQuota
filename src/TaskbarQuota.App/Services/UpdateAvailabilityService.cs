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

/// <summary>
/// Silent background update checks; UI only appears when a newer release exists.
///
/// Checks run on a periodic timer (not just at launch or when a surface happens to
/// open), the last known state is persisted to disk so the banner/badge survive an
/// app restart, a discovered GitHub installer is pre-downloaded in the background so
/// the action button goes straight to "Install", and the user can dismiss a specific
/// version — the taskbar badge and flyout banner then stay hidden until an even newer
/// release ships. Settings remains the pull surface where a dismissed update is still
/// visible and actionable.
/// </summary>
public sealed class UpdateAvailabilityService
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);
    private static readonly TimeSpan InitialCheckDelay = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan FailureRetryDelay = TimeSpan.FromMinutes(1);
    private const int MaxAutomaticRetries = 3;

    public static UpdateAvailabilityService Instance { get; } = new();

    private readonly UpdateCheckerService _checker = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _timerSync = new();
    private CancellationTokenSource? _operationCts;
    private Timer? _periodicTimer;
    private Timer? _retryTimer;
    private bool _started;
    private int _automaticRetries;
    private DateTime? _lastCheckUtc;

    public UpdateAvailabilityUiState UiState { get; private set; } = UpdateAvailabilityUiState.Hidden;
    public UpdateCheckResult? AvailableUpdate { get; private set; }
    public DownloadedUpdate? DownloadedUpdate { get; private set; }
    public string? StatusMessage { get; private set; }
    public string? UpToDateSummary { get; private set; }
    public bool IsChecking { get; private set; }

    /// <summary>Version the user dismissed. The taskbar badge and flyout banner stay hidden
    /// for exactly this version and reappear on their own when a newer one is discovered.</summary>
    public string? DismissedVersion { get; private set; }

    /// <summary>An update is known, regardless of whether the user dismissed its chrome.</summary>
    public bool HasUpdate => UiState is UpdateAvailabilityUiState.UpdateAvailable
        or UpdateAvailabilityUiState.Downloading
        or UpdateAvailabilityUiState.ReadyToInstall;

    public bool IsDismissed => IsVersionDismissed(DismissedVersion, AvailableUpdate?.Version);

    /// <summary>True when update chrome (taskbar badge, flyout banner) should be visible.</summary>
    public bool IsBannerVisible => HasUpdate && !IsDismissed;

    public event Action? Changed;

    /// <summary>Restores the persisted update state and starts the periodic silent-check
    /// timer. Called once at app launch; later calls are no-ops.</summary>
    public void Start()
    {
        lock (_timerSync)
        {
            if (_started)
                return;
            _started = true;
        }

        RestorePersistedState();

        // The first tick doubles as the launch check. It is delayed a few seconds so widget
        // and tray initialization (and network bring-up on a login autostart) win the race.
        _periodicTimer = new Timer(
            _ => _ = CheckSilentlyAsync(),
            state: null,
            dueTime: InitialCheckDelay,
            period: CheckInterval);
    }

    public Task CheckManuallyAsync() => CheckSilentlyAsync(force: true);

    public async Task CheckSilentlyAsync(bool force = false)
    {
        // ReadyToInstall deliberately does NOT skip the check: a persisted pending install
        // would otherwise block all future checks and the user would never hear about an
        // even newer release. ApplyAvailableUpdate preserves the pending install when the
        // latest release is still the downloaded one.
        if (UiState is UpdateAvailabilityUiState.Downloading)
            return;

        if (!force && !ShouldCheckNow())
            return;

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (UiState is UpdateAvailabilityUiState.Downloading)
                return;

            CancelOperation();
            _operationCts = new CancellationTokenSource();
            var ct = _operationCts.Token;

            IsChecking = true;
            NotifyChanged();

            var current = AppVersion.GetDisplayLabel();
            var result = await _checker.CheckAsync(current, ct).ConfigureAwait(false);
            _lastCheckUtc = DateTime.UtcNow;
            _automaticRetries = 0;

            if (result.Kind == UpdateCheckResultKind.UpToDate)
            {
                UpToDateSummary = $"You are on v{current} (latest).";
                SetHidden();
                PersistState();
                return;
            }

            ApplyAvailableUpdate(result);
            PersistState();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Silent update check failed");
            UpToDateSummary = "Could not check for updates. Try again.";
            // A failed re-check must not wipe a previously discovered update: the banner
            // and badge keep offering the version we already know about.
            if (!HasUpdate)
            {
                UiState = UpdateAvailabilityUiState.Hidden;
                StatusMessage = null;
            }

            // Login autostart routinely races network bring-up; give a silent check a few
            // short retries before waiting for the next periodic tick. Manual checks are
            // excluded — the user can simply click again.
            if (!force)
                ScheduleFailureRetry();
        }
        finally
        {
            IsChecking = false;
            _gate.Release();
            NotifyChanged();
        }

        // Pre-download outside the gate so a slow installer fetch never blocks checks.
        // Idempotent: an already-downloaded installer is reused, and DownloadAsync
        // re-validates the state after re-acquiring the gate.
        if (UiState == UpdateAvailabilityUiState.UpdateAvailable
            && AvailableUpdate is { DeliveryChannel: UpdateDeliveryChannel.GitHubUnsigned, DownloadUrl: not null }
            && !IsDismissed)
        {
            _ = PreDownloadAsync();
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
            // Re-validate after acquiring the gate: a concurrent pre-download may have
            // completed while this call was waiting.
            if (UiState is UpdateAvailabilityUiState.Downloading)
                return;

            if (UiState is UpdateAvailabilityUiState.ReadyToInstall
                && DownloadedUpdate is { } done
                && done.Version == result.Version
                && File.Exists(done.FilePath))
            {
                return;
            }

            CancelOperation();
            _operationCts = new CancellationTokenSource();
            var ct = _operationCts.Token;

            UiState = UpdateAvailabilityUiState.Downloading;
            StatusMessage = $"Downloading v{result.Version}…";
            NotifyChanged();

            DownloadedUpdate = await _checker.DownloadAsync(result, progress, ct).ConfigureAwait(false);
            UiState = UpdateAvailabilityUiState.ReadyToInstall;
            StatusMessage = $"New update available! Install v{DownloadedUpdate.Version}.";
            PersistState();
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

    /// <summary>Hides the taskbar badge and flyout banner for the currently known version.
    /// A newer version clears the dismissal automatically, and Settings keeps offering the
    /// dismissed update meanwhile.</summary>
    public void DismissUpdate()
    {
        if (AvailableUpdate?.Version is null)
            return;

        DismissedVersion = AvailableUpdate.Version;
        PersistState();
        NotifyChanged();
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

    /// <summary>True when the dismissed version matches the available one. Normalized so a
    /// "v1.2.3" dismissal still covers a "1.2.3" record and vice versa.</summary>
    internal static bool IsVersionDismissed(string? dismissedVersion, string? availableVersion) =>
        dismissedVersion is not null
        && availableVersion is not null
        && string.Equals(
            VersionComparer.Normalize(dismissedVersion),
            VersionComparer.Normalize(availableVersion),
            StringComparison.OrdinalIgnoreCase);

    /// <summary>Rebuilds the update record from the persisted cache, or null when there is
    /// nothing to restore (nothing recorded, or the recorded version is no longer newer than
    /// the running build).</summary>
    internal static UpdateCheckResult? TryRestoreAvailableUpdate(UpdateStateCache cache, string currentVersion)
    {
        if (cache.AvailableVersion is not { } version)
            return null;

        if (VersionComparer.Compare(VersionComparer.Normalize(version), VersionComparer.Normalize(currentVersion)) <= 0)
            return null;

        var releaseUrl = Uri.TryCreate(cache.ReleaseUrl, UriKind.Absolute, out var release) ? release : null;
        var downloadUrl = Uri.TryCreate(cache.DownloadUrl, UriKind.Absolute, out var download) ? download : null;
        return UpdateCheckResult.UpdateAvailable(version, releaseUrl, downloadUrl, cache.DeliveryChannel);
    }

    /// <summary>The installer a previous run downloaded for <paramref name="version"/>, if it
    /// is still on disk. Lets a restart offer "Install" immediately instead of re-fetching.</summary>
    internal static string? FindDownloadedInstaller(string updatesRoot, string version)
    {
        try
        {
            var directory = Path.Combine(updatesRoot, version);
            if (!Directory.Exists(directory))
                return null;

            foreach (var file in Directory.EnumerateFiles(directory, "TaskbarQuotaSetup-*.exe"))
                return file;
        }
        catch
        {
        }

        return null;
    }

    internal static string GetUpdatesRoot() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TaskbarQuota",
        "Updates");

    private void ApplyAvailableUpdate(UpdateCheckResult result)
    {
        AvailableUpdate = result;
        UpToDateSummary = null;

        // The periodic check keeps running while an installer sits downloaded; when the
        // latest release is still that same version, keep the one-click install state.
        if (DownloadedUpdate is { } downloaded
            && result.Version is { } version
            && VersionComparer.Compare(VersionComparer.Normalize(downloaded.Version), VersionComparer.Normalize(version)) == 0
            && File.Exists(downloaded.FilePath))
        {
            UiState = UpdateAvailabilityUiState.ReadyToInstall;
            StatusMessage = $"New update available! Install v{downloaded.Version}.";
            return;
        }

        DownloadedUpdate = null;
        UiState = UpdateAvailabilityUiState.UpdateAvailable;
        StatusMessage = result.DeliveryChannel switch
        {
            UpdateDeliveryChannel.MicrosoftStore => $"New update available in Microsoft Store (v{result.Version}).",
            _ when result.DownloadUrl is null => $"New update available (v{result.Version}) — see GitHub release.",
            _ => $"New update available! v{result.Version} is ready.",
        };
    }

    private async Task PreDownloadAsync()
    {
        var update = AvailableUpdate;
        if (update?.Version is not { } version || update.DownloadUrl is null)
            return;

        // A previous run may already have fetched this installer; skip the network entirely.
        if (FindDownloadedInstaller(GetUpdatesRoot(), version) is { } existing)
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (AvailableUpdate?.Version == update.Version
                    && UiState == UpdateAvailabilityUiState.UpdateAvailable)
                {
                    DownloadedUpdate = new DownloadedUpdate(version, existing);
                    UiState = UpdateAvailabilityUiState.ReadyToInstall;
                    StatusMessage = $"New update available! Install v{version}.";
                    PersistState();
                }
            }
            finally
            {
                _gate.Release();
            }

            NotifyChanged();
            return;
        }

        await DownloadAsync(progress: null).ConfigureAwait(false);
    }

    private void ScheduleFailureRetry()
    {
        lock (_timerSync)
        {
            if (!_started || _retryTimer is not null || _automaticRetries >= MaxAutomaticRetries)
                return;

            _automaticRetries++;
            _retryTimer = new Timer(
                _ =>
                {
                    lock (_timerSync)
                    {
                        _retryTimer?.Dispose();
                        _retryTimer = null;
                    }

                    _ = CheckSilentlyAsync();
                },
                state: null,
                dueTime: FailureRetryDelay,
                period: Timeout.InfiniteTimeSpan);
        }
    }

    private void RestorePersistedState()
    {
        try
        {
            var cache = ReadCache();
            if (cache is null)
                return;

            _lastCheckUtc = cache.LastCheckUtc;
            DismissedVersion = cache.DismissedVersion;

            var restored = TryRestoreAvailableUpdate(cache, AppVersion.GetDisplayLabel());
            if (restored is null)
            {
                // The recorded update is no longer newer (the user updated, or the release
                // was pulled); drop the stale record so nothing ghost-appears.
                if (cache.AvailableVersion is not null)
                {
                    DismissedVersion = null;
                    PersistState();
                }

                return;
            }

            AvailableUpdate = restored;

            if (restored.Version is { } version
                && FindDownloadedInstaller(GetUpdatesRoot(), version) is { } installer)
            {
                DownloadedUpdate = new DownloadedUpdate(version, installer);
                UiState = UpdateAvailabilityUiState.ReadyToInstall;
                StatusMessage = $"New update available! Install v{version}.";
            }
            else
            {
                UiState = UpdateAvailabilityUiState.UpdateAvailable;
                StatusMessage = restored.DeliveryChannel == UpdateDeliveryChannel.MicrosoftStore
                    ? $"New update available in Microsoft Store (v{restored.Version})."
                    : $"New update available! v{restored.Version} is ready.";
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to restore persisted update state");
        }
        finally
        {
            NotifyChanged();
        }

        // A restored, undismissed GitHub update goes straight to pre-download: the launch
        // check may be throttled for hours, and re-fetching the installer hits no release API.
        if (UiState == UpdateAvailabilityUiState.UpdateAvailable
            && AvailableUpdate is { DeliveryChannel: UpdateDeliveryChannel.GitHubUnsigned, DownloadUrl: not null }
            && !IsDismissed)
        {
            _ = PreDownloadAsync();
        }
    }

    private void SetHidden()
    {
        UiState = UpdateAvailabilityUiState.Hidden;
        AvailableUpdate = null;
        DownloadedUpdate = null;
        StatusMessage = null;
        DismissedVersion = null;
    }

    private bool ShouldCheckNow() =>
        _lastCheckUtc is not { } last || DateTime.UtcNow - last >= CheckInterval;

    private void PersistState()
    {
        try
        {
            WriteCache(new UpdateStateCache
            {
                LastCheckUtc = _lastCheckUtc,
                AvailableVersion = AvailableUpdate?.Version,
                ReleaseUrl = AvailableUpdate?.ReleaseUrl?.AbsoluteUri,
                DownloadUrl = AvailableUpdate?.DownloadUrl?.AbsoluteUri,
                DeliveryChannel = AvailableUpdate?.DeliveryChannel ?? UpdateDeliveryChannel.GitHubUnsigned,
                DismissedVersion = DismissedVersion,
            });
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to persist update state");
        }
    }

    private void NotifyChanged() => Changed?.Invoke();

    private void CancelOperation()
    {
        _operationCts?.Cancel();
        _operationCts?.Dispose();
        _operationCts = null;
    }

    private static UpdateStateCache? ReadCache()
    {
        var path = GetCachePath();
        if (!File.Exists(path))
            return null;

        return JsonSerializer.Deserialize<UpdateStateCache>(File.ReadAllText(path));
    }

    private static void WriteCache(UpdateStateCache cache)
    {
        var path = GetCachePath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(cache));
    }

    private static string GetCachePath() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TaskbarQuota",
            "update-check.json");
}

/// <summary>On-disk record of the update checker state (update-check.json). Property names
/// match the original cache shape so a cache written by an older build still reads back.</summary>
internal sealed class UpdateStateCache
{
    public DateTime? LastCheckUtc { get; set; }
    public string? AvailableVersion { get; set; }
    public string? ReleaseUrl { get; set; }
    public string? DownloadUrl { get; set; }
    public UpdateDeliveryChannel DeliveryChannel { get; set; }
    public string? DismissedVersion { get; set; }
}
