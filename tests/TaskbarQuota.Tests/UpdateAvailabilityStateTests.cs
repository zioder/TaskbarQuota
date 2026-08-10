using TaskbarQuota.Services;

namespace TaskbarQuota.Tests;

public sealed class UpdateAvailabilityStateTests
{
    [Fact]
    public void Restore_returns_null_when_nothing_was_recorded()
    {
        var cache = new UpdateStateCache { LastCheckUtc = DateTime.UtcNow };

        Assert.Null(UpdateAvailabilityService.TryRestoreAvailableUpdate(cache, "1.0.0"));
    }

    [Theory]
    [InlineData("1.2.0", "1.2.0")] // same build: the user already has it
    [InlineData("1.2.0", "1.3.0")] // older than the running build: user updated some other way
    [InlineData("v1.2.0", "1.2.0")] // tag-style prefixes normalize away
    public void Restore_returns_null_when_recorded_version_is_not_newer(string recorded, string current)
    {
        var cache = new UpdateStateCache { AvailableVersion = recorded };

        Assert.Null(UpdateAvailabilityService.TryRestoreAvailableUpdate(cache, current));
    }

    [Fact]
    public void Restore_rebuilds_the_update_record_when_a_newer_version_was_recorded()
    {
        var cache = new UpdateStateCache
        {
            AvailableVersion = "1.3.0",
            ReleaseUrl = "https://github.com/zioder/TaskbarQuota/releases/tag/v1.3.0",
            DownloadUrl = "https://github.com/zioder/TaskbarQuota/releases/download/v1.3.0/TaskbarQuotaSetup-1.3.0-x64-unsigned.exe",
            DeliveryChannel = UpdateDeliveryChannel.GitHubUnsigned,
        };

        var restored = UpdateAvailabilityService.TryRestoreAvailableUpdate(cache, "1.2.0");

        Assert.NotNull(restored);
        Assert.Equal(UpdateCheckResultKind.UpdateAvailable, restored.Kind);
        Assert.Equal("1.3.0", restored.Version);
        Assert.Equal(cache.ReleaseUrl, restored.ReleaseUrl?.AbsoluteUri);
        Assert.Equal(cache.DownloadUrl, restored.DownloadUrl?.AbsoluteUri);
        Assert.Equal(UpdateDeliveryChannel.GitHubUnsigned, restored.DeliveryChannel);
    }

    [Fact]
    public void Restore_tolerates_unparseable_urls()
    {
        var cache = new UpdateStateCache
        {
            AvailableVersion = "1.3.0",
            ReleaseUrl = "not a uri",
            DownloadUrl = null,
        };

        var restored = UpdateAvailabilityService.TryRestoreAvailableUpdate(cache, "1.2.0");

        Assert.NotNull(restored);
        Assert.Null(restored.ReleaseUrl);
        Assert.Null(restored.DownloadUrl);
    }

    [Theory]
    [InlineData("1.3.0", "1.3.0", true)]
    [InlineData("v1.3.0", "1.3.0", true)] // normalization covers tag-style prefixes
    [InlineData("1.3.0", "1.4.0", false)] // a newer release clears the snooze on its own
    [InlineData(null, "1.3.0", false)]
    [InlineData("1.3.0", null, false)]
    public void Dismissal_matches_only_the_snoozed_version(string? dismissed, string? available, bool expected)
    {
        Assert.Equal(expected, UpdateAvailabilityService.IsVersionDismissed(dismissed, available));
    }

    [Fact]
    public void Find_downloaded_installer_returns_the_cached_setup_exe()
    {
        var root = Path.Combine(Path.GetTempPath(), "TaskbarQuotaTests", Guid.NewGuid().ToString("N"));
        try
        {
            var versionDir = Path.Combine(root, "1.3.0");
            Directory.CreateDirectory(versionDir);
            var installer = Path.Combine(versionDir, "TaskbarQuotaSetup-1.3.0-x64-unsigned.exe");
            File.WriteAllText(installer, "placeholder");

            Assert.Equal(installer, UpdateAvailabilityService.FindDownloadedInstaller(root, "1.3.0"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Find_downloaded_installer_returns_null_when_nothing_is_cached()
    {
        var root = Path.Combine(Path.GetTempPath(), "TaskbarQuotaTests", Guid.NewGuid().ToString("N"));

        Assert.Null(UpdateAvailabilityService.FindDownloadedInstaller(root, "9.9.9"));
    }

    [Fact]
    public void Persisted_cache_round_trips_through_json()
    {
        var cache = new UpdateStateCache
        {
            LastCheckUtc = new DateTime(2026, 8, 10, 12, 0, 0, DateTimeKind.Utc),
            AvailableVersion = "1.3.0",
            ReleaseUrl = "https://example.test/release",
            DownloadUrl = "https://example.test/setup.exe",
            DeliveryChannel = UpdateDeliveryChannel.MicrosoftStore,
            DismissedVersion = "1.3.0",
        };

        var json = System.Text.Json.JsonSerializer.Serialize(cache);
        var read = System.Text.Json.JsonSerializer.Deserialize<UpdateStateCache>(json);

        Assert.NotNull(read);
        Assert.Equal(cache.LastCheckUtc, read.LastCheckUtc);
        Assert.Equal(cache.AvailableVersion, read.AvailableVersion);
        Assert.Equal(cache.ReleaseUrl, read.ReleaseUrl);
        Assert.Equal(cache.DownloadUrl, read.DownloadUrl);
        Assert.Equal(cache.DeliveryChannel, read.DeliveryChannel);
        Assert.Equal(cache.DismissedVersion, read.DismissedVersion);
    }
}
