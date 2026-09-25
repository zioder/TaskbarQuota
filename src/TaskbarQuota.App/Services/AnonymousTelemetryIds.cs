using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace TaskbarQuota.Services;

/// <summary>
/// Period-scoped anonymous IDs. A local salt never leaves the PC; the collector only sees
/// HMAC hashes that rotate every UTC day, ISO week, and month, so users cannot be followed
/// across those windows.
/// </summary>
internal static class AnonymousTelemetryIds
{
    public const int SchemaVersion = 1;
    public const int SaltLength = 32;
    public const string ChannelStore = "store";
    public const string ChannelGitHub = "github";

    public static AnonymousTelemetryPeriods PeriodsAt(DateTimeOffset utcNow)
    {
        var utc = utcNow.UtcDateTime;
        var isoYear = ISOWeek.GetYear(utc);
        var isoWeek = ISOWeek.GetWeekOfYear(utc);
        return new AnonymousTelemetryPeriods(
            utc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            string.Create(CultureInfo.InvariantCulture, $"{isoYear}-W{isoWeek:D2}"),
            utc.ToString("yyyy-MM", CultureInfo.InvariantCulture));
    }

    public static AnonymousTelemetryPayload CreatePayload(
        ReadOnlySpan<byte> salt,
        DateTimeOffset utcNow,
        string version,
        string channel)
    {
        var periods = PeriodsAt(utcNow);
        return new AnonymousTelemetryPayload(
            SchemaVersion,
            new AnonymousTelemetryPeriodId(periods.Day, Hash(salt, "d", periods.Day)),
            new AnonymousTelemetryPeriodId(periods.Week, Hash(salt, "w", periods.Week)),
            new AnonymousTelemetryPeriodId(periods.Month, Hash(salt, "m", periods.Month)),
            version,
            channel);
    }

    public static string Hash(ReadOnlySpan<byte> salt, string scope, string period)
    {
        var message = Encoding.UTF8.GetBytes($"{scope}:{period}");
        var digest = HMACSHA256.HashData(salt, message);
        return Convert.ToHexString(digest).ToLowerInvariant();
    }

    public static string ChannelLabel(AppDistributionChannel channel) =>
        channel == AppDistributionChannel.MicrosoftStore ? ChannelStore : ChannelGitHub;
}

internal readonly record struct AnonymousTelemetryPeriods(string Day, string Week, string Month);

internal sealed record AnonymousTelemetryPeriodId(
    [property: JsonPropertyName("period")] string Period,
    [property: JsonPropertyName("id")] string Id);

internal sealed record AnonymousTelemetryPayload(
    [property: JsonPropertyName("schema")] int Schema,
    [property: JsonPropertyName("day")] AnonymousTelemetryPeriodId Day,
    [property: JsonPropertyName("week")] AnonymousTelemetryPeriodId Week,
    [property: JsonPropertyName("month")] AnonymousTelemetryPeriodId Month,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("channel")] string Channel);
