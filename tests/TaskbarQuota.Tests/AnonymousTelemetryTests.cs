using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using TaskbarQuota.Services;

namespace TaskbarQuota.Tests;

public sealed class AnonymousTelemetryIdsTests
{
    private static readonly byte[] GoldenSalt = Enumerable.Repeat((byte)1, 32).ToArray();

    [Fact]
    public void PeriodsAt_UsesUtcDateAndIsoWeek()
    {
        var periods = AnonymousTelemetryIds.PeriodsAt(
            new DateTimeOffset(2026, 9, 12, 23, 30, 0, TimeSpan.FromHours(-7)));

        Assert.Equal("2026-09-13", periods.Day);
        Assert.Equal("2026-W37", periods.Week);
        Assert.Equal("2026-09", periods.Month);
    }

    [Theory]
    [InlineData("2025-12-29T00:00:00Z", "2025-12-29", "2026-W01", "2025-12")]
    [InlineData("2027-01-01T00:00:00Z", "2027-01-01", "2026-W53", "2027-01")]
    [InlineData("2026-09-12T00:00:00Z", "2026-09-12", "2026-W37", "2026-09")]
    public void PeriodsAt_IsoWeekYearBoundaries(string utc, string day, string week, string month)
    {
        var periods = AnonymousTelemetryIds.PeriodsAt(DateTimeOffset.Parse(utc, CultureInfo.InvariantCulture));

        Assert.Equal(day, periods.Day);
        Assert.Equal(week, periods.Week);
        Assert.Equal(month, periods.Month);
    }

    [Fact]
    public void Hash_MatchesGoldenHmacValues()
    {
        Assert.Equal(
            "72bda1cc2ff0d1a8daaae71df6af3adb3379d5f647fe3cd3eb202df09bf9b04c",
            AnonymousTelemetryIds.Hash(GoldenSalt, "d", "2026-09-12"));
        Assert.Equal(
            "57d0af8ad4277ca4b47180cd4c5e1a7c16b56e92243ad94682372b4565fae0e2",
            AnonymousTelemetryIds.Hash(GoldenSalt, "w", "2026-W37"));
        Assert.Equal(
            "2b6fe67ed758beb74d0f72a95b7369ee4ab2a5fabee967f91986bd9ff04da941",
            AnonymousTelemetryIds.Hash(GoldenSalt, "m", "2026-09"));
    }

    [Fact]
    public void CreatePayload_DoesNotEmbedTheSaltAndRotatesAcrossDays()
    {
        var monday = DateTimeOffset.Parse("2026-09-07T12:00:00Z", CultureInfo.InvariantCulture);
        var tuesday = DateTimeOffset.Parse("2026-09-08T12:00:00Z", CultureInfo.InvariantCulture);

        var first = AnonymousTelemetryIds.CreatePayload(GoldenSalt, monday, "1.3.2", "github");
        var second = AnonymousTelemetryIds.CreatePayload(GoldenSalt, tuesday, "1.3.2", "github");

        Assert.Equal(1, first.Schema);
        Assert.Equal("2026-09-07", first.Day.Period);
        Assert.Equal("2026-W37", first.Week.Period);
        Assert.Equal("2026-09", first.Month.Period);
        Assert.Equal(64, first.Day.Id.Length);
        Assert.DoesNotContain("010101", first.Day.Id, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(first.Day.Id, second.Day.Id);
        Assert.Equal(first.Week.Id, second.Week.Id);
        Assert.Equal(first.Month.Id, second.Month.Id);
    }

    [Fact]
    public void ChannelLabel_MapsStoreAndGitHubInstalls()
    {
        Assert.Equal("store", AnonymousTelemetryIds.ChannelLabel(AppDistributionChannel.MicrosoftStore));
        Assert.Equal("github", AnonymousTelemetryIds.ChannelLabel(AppDistributionChannel.UnsignedGitHub));
    }
}

public sealed class AnonymousTelemetrySettingsStoreTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"taskbarquota-telemetry-settings-{Guid.NewGuid():N}");

    private string SettingsPath => Path.Combine(_directory, "anonymous-telemetry.json");

    [Fact]
    public void MissingFile_DefaultsToEnabled()
    {
        var store = new AnonymousTelemetrySettingsStore(SettingsPath);

        Assert.True(store.Current.Enabled);
        Assert.Equal(AnonymousTelemetrySettings.Default, store.Current);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("""{"Enabled":"yes"}""")]
    public void InvalidFile_UsesEnabledDefault(string contents)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(SettingsPath, contents);

        var store = new AnonymousTelemetrySettingsStore(SettingsPath);

        Assert.True(store.Current.Enabled);
    }

    [Fact]
    public void Apply_PersistsOptOutAndDoesNotRaiseForUnchangedValue()
    {
        var store = new AnonymousTelemetrySettingsStore(SettingsPath);
        var changed = 0;
        store.Changed += (_, _) => changed++;

        store.Apply(store.Current with { Enabled = false });
        store.Apply(store.Current with { Enabled = false });

        Assert.False(store.Current.Enabled);
        Assert.Equal(1, changed);
        Assert.False(JsonSerializer.Deserialize<AnonymousTelemetrySettings>(File.ReadAllText(SettingsPath))!.Enabled);
        Assert.False(new AnonymousTelemetrySettingsStore(SettingsPath).Current.Enabled);
        Assert.Empty(Directory.EnumerateFiles(_directory, "*.tmp"));
    }

    public void Dispose()
    {
        if (!Directory.Exists(_directory))
            return;

        try { Directory.Delete(_directory, recursive: true); }
        catch { }
    }
}

public sealed class AnonymousTelemetryClientTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"taskbarquota-telemetry-client-{Guid.NewGuid():N}");

    [Fact]
    public void LoadOrCreateSalt_IsStableAcrossReadsAndNeverAllZeros()
    {
        var client = CreateClient(enabled: true);
        var first = client.LoadOrCreateSalt();
        var second = client.LoadOrCreateSalt();

        Assert.Equal(32, first.Length);
        Assert.Equal(first, second);
        Assert.Contains(first, value => value != 0);
        Assert.True(File.Exists(Path.Combine(_directory, AnonymousTelemetryClient.SaltFileName)));
    }

    [Fact]
    public async Task SendIfDueAsync_PostsRotatingIdsOncePerUtcDay()
    {
        var handler = new RecordingHandler(HttpStatusCode.NoContent);
        var now = DateTimeOffset.Parse("2026-09-12T15:00:00Z", CultureInfo.InvariantCulture);
        var client = CreateClient(enabled: true, handler: handler, utcNow: () => now);

        Assert.Equal(AnonymousTelemetrySendResult.Sent, await client.SendIfDueAsync());
        Assert.Equal(AnonymousTelemetrySendResult.AlreadySent, await client.SendIfDueAsync());

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal(new Uri("https://telemetry.test/v1/ping"), request.RequestUri);

        var json = await request.Content!.ReadAsStringAsync();
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal(1, root.GetProperty("schema").GetInt32());
        Assert.Equal("2026-09-12", root.GetProperty("day").GetProperty("period").GetString());
        Assert.Equal(64, root.GetProperty("day").GetProperty("id").GetString()!.Length);
        Assert.Equal("2026-W37", root.GetProperty("week").GetProperty("period").GetString());
        Assert.Equal("1.3.2", root.GetProperty("version").GetString());
        Assert.Equal("github", root.GetProperty("channel").GetString());
        Assert.Equal("2026-09-12", File.ReadAllText(Path.Combine(_directory, AnonymousTelemetryClient.LastPingFileName)).Trim());
    }

    [Fact]
    public async Task SendIfDueAsync_SkipsWhenIngestUrlIsNotConfigured()
    {
        var handler = new RecordingHandler(HttpStatusCode.NoContent);
        var client = new AnonymousTelemetryClient(
            _directory,
            ingestUrl: AnonymousTelemetryClient.TryParseIngestUrl(AnonymousTelemetryService.DefaultIngestUrl),
            utcNow: () => DateTimeOffset.Parse("2026-09-12T15:00:00Z", CultureInfo.InvariantCulture),
            version: () => "1.3.2",
            channel: () => AppDistributionChannel.UnsignedGitHub,
            isEnabled: () => true,
            handler: handler,
            allowFromThisBuild: true);

        Assert.Null(AnonymousTelemetryClient.TryParseIngestUrl(AnonymousTelemetryService.DefaultIngestUrl));
        Assert.Equal(AnonymousTelemetrySendResult.SkippedBuild, await client.SendIfDueAsync());
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task SendIfDueAsync_SkipsWhenDisabledAndDoesNotCreateSalt()
    {
        var handler = new RecordingHandler(HttpStatusCode.NoContent);
        var client = CreateClient(enabled: false, handler: handler);

        Assert.Equal(AnonymousTelemetrySendResult.Disabled, await client.SendIfDueAsync());
        Assert.Empty(handler.Requests);
        Assert.False(File.Exists(Path.Combine(_directory, AnonymousTelemetryClient.SaltFileName)));
    }

    [Fact]
    public async Task SendIfDueAsync_RetriesAfterHttpFailureWithoutRecordingTheDay()
    {
        var handler = new RecordingHandler(HttpStatusCode.BadGateway);
        var client = CreateClient(enabled: true, handler: handler);

        Assert.Equal(AnonymousTelemetrySendResult.Failed, await client.SendIfDueAsync());
        Assert.Null(client.ReadLastPingDay());

        handler.StatusCode = HttpStatusCode.NoContent;
        Assert.Equal(AnonymousTelemetrySendResult.Sent, await client.SendIfDueAsync());
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task SendIfDueAsync_NewUtcDaySendsAgainWithADifferentDayId()
    {
        var handler = new RecordingHandler(HttpStatusCode.NoContent);
        var now = DateTimeOffset.Parse("2026-09-12T15:00:00Z", CultureInfo.InvariantCulture);
        var client = CreateClient(enabled: true, handler: handler, utcNow: () => now);

        Assert.Equal(AnonymousTelemetrySendResult.Sent, await client.SendIfDueAsync());
        now = DateTimeOffset.Parse("2026-09-13T00:00:00Z", CultureInfo.InvariantCulture);
        Assert.Equal(AnonymousTelemetrySendResult.Sent, await client.SendIfDueAsync());

        Assert.Equal(2, handler.Requests.Count);
        var first = await ReadDayId(handler.Requests[0]);
        var second = await ReadDayId(handler.Requests[1]);
        Assert.NotEqual(first, second);
    }

    public void Dispose()
    {
        if (!Directory.Exists(_directory))
            return;

        try { Directory.Delete(_directory, recursive: true); }
        catch { }
    }

    private AnonymousTelemetryClient CreateClient(
        bool enabled,
        HttpMessageHandler? handler = null,
        Func<DateTimeOffset>? utcNow = null)
        => new(
            _directory,
            ingestUrl: new Uri("https://telemetry.test/v1/ping"),
            utcNow: utcNow ?? (() => DateTimeOffset.Parse("2026-09-12T15:00:00Z", CultureInfo.InvariantCulture)),
            version: () => "1.3.2",
            channel: () => AppDistributionChannel.UnsignedGitHub,
            isEnabled: () => enabled,
            handler: handler,
            allowFromThisBuild: true);

    private static async Task<string> ReadDayId(HttpRequestMessage request)
    {
        var json = await request.Content!.ReadAsStringAsync();
        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty("day").GetProperty("id").GetString()!;
    }

    private sealed class RecordingHandler(HttpStatusCode statusCode) : HttpMessageHandler
    {
        public HttpStatusCode StatusCode { get; set; } = statusCode;
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var copy = new HttpRequestMessage(request.Method, request.RequestUri);
            if (request.Content is not null)
                copy.Content = new StringContent(await request.Content.ReadAsStringAsync(cancellationToken), Encoding.UTF8, "application/json");
            Requests.Add(copy);
            return new HttpResponseMessage(StatusCode);
        }
    }
}
