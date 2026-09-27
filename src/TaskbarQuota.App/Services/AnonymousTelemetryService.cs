using System;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TaskbarQuota.Diagnostics;
using TaskbarQuota.Helpers;

namespace TaskbarQuota.Services;

/// <summary>
/// Sends at most one anonymous daily ping while TaskbarQuota is running. Failures are
/// swallowed; the next launch or 6-hour timer retries.
/// </summary>
internal sealed class AnonymousTelemetryService
{
    /// <summary>
    /// Deployed collector POST URL, including <c>/v1/ping</c>. Leave empty until
    /// <c>telemetry/README.md</c> has been followed; an empty value disables sending.
    /// </summary>
    internal const string DefaultIngestUrl = "";
    private static readonly TimeSpan RepeatInterval = TimeSpan.FromHours(6);

    public static AnonymousTelemetryService Instance { get; } = new();

    private readonly AnonymousTelemetryClient _client = new(AppStorage.AppDataDirectory);
    private readonly object _timerLock = new();
    private Timer? _timer;
    private int _started;

    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
            return;

        App.Quitting += Stop;
        lock (_timerLock)
        {
            _timer = new Timer(
                _ => _ = SendIfDueAsync(),
                null,
                TimeSpan.FromSeconds(12),
                RepeatInterval);
        }
    }

    public void Stop()
    {
        lock (_timerLock)
        {
            _timer?.Dispose();
            _timer = null;
        }
    }

    public Task SendIfDueAsync(CancellationToken cancellationToken = default)
        => _client.SendIfDueAsync(cancellationToken);

    internal void SendSoonAfterOptIn()
    {
        if (!AnonymousTelemetrySettingsService.IsEnabled)
            return;

        _ = _client.SendIfDueAsync();
    }
}

/// <summary>Testable ping client. The local salt never leaves the machine.</summary>
internal sealed class AnonymousTelemetryClient
{
    internal const string SaltFileName = "anonymous-telemetry-salt.bin";
    internal const string LastPingFileName = "anonymous-telemetry-last-day.txt";
    private const string HttpUserAgent = "TaskbarQuota";

    private static readonly HttpClient SharedHttp = CreateHttpClient();
    private static readonly JsonSerializerOptions JsonOptions = new();

    private readonly string _directory;
    private readonly Uri? _ingestUrl;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Func<string> _version;
    private readonly Func<AppDistributionChannel> _channel;
    private readonly Func<bool> _isEnabled;
    private readonly HttpMessageHandler? _handler;
    private readonly bool _allowFromThisBuild;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public AnonymousTelemetryClient(
        string directory,
        Uri? ingestUrl = null,
        Func<DateTimeOffset>? utcNow = null,
        Func<string>? version = null,
        Func<AppDistributionChannel>? channel = null,
        Func<bool>? isEnabled = null,
        HttpMessageHandler? handler = null,
        bool? allowFromThisBuild = null)
    {
        _directory = directory;
        _ingestUrl = ingestUrl ?? TryParseIngestUrl(AnonymousTelemetryService.DefaultIngestUrl);
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _version = version ?? AppVersion.GetDisplayLabel;
        _channel = channel ?? (() => AppDistribution.CurrentChannel);
        _isEnabled = isEnabled ?? (() => AnonymousTelemetrySettingsService.IsEnabled);
        _handler = handler;
        _allowFromThisBuild = allowFromThisBuild ?? ShouldSendFromThisBuild();
    }

    public async Task<AnonymousTelemetrySendResult> SendIfDueAsync(CancellationToken cancellationToken = default)
    {
        if (!_allowFromThisBuild)
            return AnonymousTelemetrySendResult.SkippedBuild;

        if (!_isEnabled())
            return AnonymousTelemetrySendResult.Disabled;

        if (_ingestUrl is null)
            return AnonymousTelemetrySendResult.SkippedBuild;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = _utcNow();
            var today = AnonymousTelemetryIds.PeriodsAt(now).Day;
            if (ReadLastPingDay() == today)
                return AnonymousTelemetrySendResult.AlreadySent;

            var salt = LoadOrCreateSalt();
            var payload = AnonymousTelemetryIds.CreatePayload(
                salt,
                now,
                _version(),
                AnonymousTelemetryIds.ChannelLabel(_channel()));

            using var request = new HttpRequestMessage(HttpMethod.Post, _ingestUrl)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(payload, JsonOptions),
                    Encoding.UTF8,
                    "application/json"),
            };

            using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                Log.Warning($"Anonymous telemetry ping failed: HTTP {(int)response.StatusCode}");
                return AnonymousTelemetrySendResult.Failed;
            }

            WriteLastPingDay(today);
            Log.Information("Anonymous telemetry ping sent");
            return AnonymousTelemetrySendResult.Sent;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Anonymous telemetry ping failed");
            return AnonymousTelemetrySendResult.Failed;
        }
        finally
        {
            _gate.Release();
        }
    }

    internal byte[] LoadOrCreateSalt()
    {
        var path = Path.Combine(_directory, SaltFileName);
        try
        {
            if (File.Exists(path))
            {
                var existing = File.ReadAllBytes(path);
                if (existing.Length == AnonymousTelemetryIds.SaltLength)
                    return existing;
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not read anonymous telemetry salt");
        }

        var salt = new byte[AnonymousTelemetryIds.SaltLength];
        RandomNumberGenerator.Fill(salt);
        try
        {
            Directory.CreateDirectory(_directory);
            var tempPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllBytes(tempPath, salt);
            File.Move(tempPath, path, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not persist anonymous telemetry salt");
        }

        return salt;
    }

    internal string? ReadLastPingDay()
    {
        try
        {
            var path = Path.Combine(_directory, LastPingFileName);
            if (!File.Exists(path))
                return null;

            var value = File.ReadAllText(path).Trim();
            return value.Length == 0 ? null : value;
        }
        catch
        {
            return null;
        }
    }

    internal static bool ShouldSendFromThisBuild()
    {
#if DEBUG
        return false;
#else
        return true;
#endif
    }

    internal static Uri? TryParseIngestUrl(string value)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
            ? uri
            : null;

    private void WriteLastPingDay(string day)
    {
        try
        {
            Directory.CreateDirectory(_directory);
            var path = Path.Combine(_directory, LastPingFileName);
            var tempPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(tempPath, day);
            File.Move(tempPath, path, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not persist anonymous telemetry last-ping day");
        }
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (_handler is not null)
        {
            using var client = new HttpClient(_handler, disposeHandler: false)
            {
                Timeout = TimeSpan.FromSeconds(10),
            };
            client.DefaultRequestHeaders.UserAgent.ParseAdd(HttpUserAgent);
            return await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }

        return await SharedHttp.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(HttpUserAgent);
        return client;
    }

}

internal enum AnonymousTelemetrySendResult
{
    Sent,
    AlreadySent,
    Disabled,
    Failed,
    SkippedBuild,
}
