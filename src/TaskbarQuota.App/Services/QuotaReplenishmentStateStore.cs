using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TaskbarQuota.Diagnostics;
using TaskbarQuota.Usage;

namespace TaskbarQuota.Services;

internal sealed record PersistedQuotaWindow(
    string WindowId,
    string Title,
    double UsedPercent,
    int? WindowMinutes,
    DateTimeOffset? ResetAt);

internal sealed record PersistedQuotaProvider(
    ProviderId Provider,
    string? IdentityHash,
    DateTimeOffset ObservedAt,
    IReadOnlyList<PersistedQuotaWindow> Windows);

internal sealed class QuotaReplenishmentStateStore
{
    internal const string FileName = "quota-replenishment-state.json";
    internal static readonly TimeSpan ConfirmationRefreshInterval = TimeSpan.FromHours(1);

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _path;
    private readonly Dictionary<ProviderId, PersistedQuotaProvider> _providers;

    public QuotaReplenishmentStateStore(string path)
    {
        _path = path;
        _providers = Load();
    }

    public static string DefaultPath =>
        Path.Combine(AppStorage.AppDataDirectory, FileName);

    public bool TryGet(ProviderId provider, out PersistedQuotaProvider observation)
        => _providers.TryGetValue(provider, out observation!);

    public bool Upsert(PersistedQuotaProvider observation)
    {
        if (_providers.TryGetValue(observation.Provider, out var existing)
            && SameValues(existing, observation)
            && observation.ObservedAt >= existing.ObservedAt
            && observation.ObservedAt - existing.ObservedAt < ConfirmationRefreshInterval)
        {
            return true;
        }

        _providers[observation.Provider] = observation;
        return Save();
    }

    public bool Clear()
    {
        _providers.Clear();
        try
        {
            if (File.Exists(_path))
                File.Delete(_path);

            var tempPath = _path + ".tmp";
            if (File.Exists(tempPath))
                File.Delete(tempPath);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private Dictionary<ProviderId, PersistedQuotaProvider> Load()
    {
        var providers = new Dictionary<ProviderId, PersistedQuotaProvider>();
        try
        {
            if (!File.Exists(_path))
                return providers;

            var file = JsonSerializer.Deserialize<StoredFile>(
                File.ReadAllText(_path),
                SerializerOptions);
            if (file?.Version != 1 || file.Providers is null)
                return providers;

            foreach (var stored in file.Providers)
            {
                if (!Enum.TryParse<ProviderId>(stored.Provider, out var provider)
                    || stored.Windows is null
                    || providers.ContainsKey(provider))
                {
                    continue;
                }

                providers[provider] = new PersistedQuotaProvider(
                    provider,
                    stored.IdentityHash,
                    stored.ObservedAt,
                    stored.Windows
                        .Where(window => !string.IsNullOrWhiteSpace(window.WindowId))
                        .Select(window => new PersistedQuotaWindow(
                            window.WindowId!,
                            window.Title ?? string.Empty,
                            window.UsedPercent,
                            window.WindowMinutes,
                            window.ResetAt))
                        .ToArray());
            }
        }
        catch
        {
            return new Dictionary<ProviderId, PersistedQuotaProvider>();
        }

        return providers;
    }

    private bool Save()
    {
        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            var file = new StoredFile
            {
                Version = 1,
                Providers = _providers.Values
                    .OrderBy(provider => provider.Provider)
                    .Select(provider => new StoredProvider
                    {
                        Provider = provider.Provider.ToString(),
                        IdentityHash = provider.IdentityHash,
                        ObservedAt = provider.ObservedAt,
                        Windows = provider.Windows
                            .OrderBy(window => window.WindowId, StringComparer.Ordinal)
                            .Select(window => new StoredWindow
                            {
                                WindowId = window.WindowId,
                                Title = window.Title,
                                UsedPercent = window.UsedPercent,
                                WindowMinutes = window.WindowMinutes,
                                ResetAt = window.ResetAt,
                            })
                            .ToList(),
                    })
                    .ToList(),
            };

            var json = JsonSerializer.Serialize(file, SerializerOptions);
            var tempPath = _path + ".tmp";
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, _path, overwrite: true);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool SameValues(
        PersistedQuotaProvider left,
        PersistedQuotaProvider right)
    {
        if (!string.Equals(left.IdentityHash, right.IdentityHash, StringComparison.Ordinal)
            || left.Windows.Count != right.Windows.Count)
        {
            return false;
        }

        var leftWindows = left.Windows.OrderBy(window => window.WindowId, StringComparer.Ordinal);
        var rightWindows = right.Windows.OrderBy(window => window.WindowId, StringComparer.Ordinal);
        return leftWindows.SequenceEqual(rightWindows);
    }

    internal static string? HashIdentity(string? identity)
    {
        if (string.IsNullOrWhiteSpace(identity))
            return null;

        var normalized = identity.Trim().ToUpperInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
    }

    private sealed class StoredFile
    {
        public int Version { get; set; }
        public List<StoredProvider>? Providers { get; set; }
    }

    private sealed class StoredProvider
    {
        public string? Provider { get; set; }
        public string? IdentityHash { get; set; }
        public DateTimeOffset ObservedAt { get; set; }
        public List<StoredWindow>? Windows { get; set; }
    }

    private sealed class StoredWindow
    {
        public string? WindowId { get; set; }
        public string? Title { get; set; }
        public double UsedPercent { get; set; }
        public int? WindowMinutes { get; set; }
        public DateTimeOffset? ResetAt { get; set; }
    }
}

internal sealed class QuotaReplenishmentCrossSessionTracker
{
    internal static readonly TimeSpan MaximumObservationAge = TimeSpan.FromDays(35);

    private readonly QuotaReplenishmentStateStore _store;
    private readonly HashSet<ProviderId> _evaluatedProviders = new();
    private readonly Dictionary<ProviderId, long> _lastSequences = new();

    public QuotaReplenishmentCrossSessionTracker(QuotaReplenishmentStateStore store)
    {
        _store = store;
    }

    public IReadOnlyList<QuotaReplenishmentEvent> Observe(
        UsageResult result,
        DateTimeOffset now)
    {
        if (result.ObservationOrigin != UsageObservationOrigin.Live
            || result.Fetch?.Usage is not { } usage
            || result.ObservedAt is not { } observedAt)
        {
            return Array.Empty<QuotaReplenishmentEvent>();
        }

        if (_lastSequences.TryGetValue(result.Id, out var lastSequence)
            && result.ObservationSequence <= lastSequence)
        {
            return Array.Empty<QuotaReplenishmentEvent>();
        }

        _lastSequences[result.Id] = result.ObservationSequence;
        var current = CreatePersistedObservation(result, usage, observedAt);
        var firstLiveThisSession = _evaluatedProviders.Add(result.Id);
        var events = firstLiveThisSession
            ? CompareWithPrevious(current, now)
            : Array.Empty<QuotaReplenishmentEvent>();

        // Consume the startup candidate before delivery. A restart after a successful comparison then
        // sees the new values and cannot emit the same catch-up notification twice.
        _store.Upsert(current);
        return events;
    }

    public void Reset(bool clearPersisted)
    {
        _evaluatedProviders.Clear();
        _lastSequences.Clear();
        if (clearPersisted)
            _store.Clear();
    }

    private IReadOnlyList<QuotaReplenishmentEvent> CompareWithPrevious(
        PersistedQuotaProvider current,
        DateTimeOffset now)
    {
        if (!_store.TryGet(current.Provider, out var previous)
            || previous.IdentityHash is null
            || current.IdentityHash is null
            || !string.Equals(previous.IdentityHash, current.IdentityHash, StringComparison.Ordinal))
        {
            return Array.Empty<QuotaReplenishmentEvent>();
        }

        var age = now - previous.ObservedAt;
        if (age < TimeSpan.Zero || age > MaximumObservationAge)
            return Array.Empty<QuotaReplenishmentEvent>();

        var previousWindows = previous.Windows
            .GroupBy(window => window.WindowId, StringComparer.Ordinal)
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single(), StringComparer.Ordinal);

        var events = new List<QuotaReplenishmentEvent>();
        foreach (var currentWindow in current.Windows)
        {
            if (!previousWindows.TryGetValue(currentWindow.WindowId, out var previousWindow))
                continue;

            var key = new QuotaWindowKey(current.Provider, currentWindow.WindowId);
            var previousObservation = new QuotaWindowObservation(
                key,
                previousWindow.Title,
                previousWindow.UsedPercent,
                previousWindow.WindowMinutes,
                previousWindow.ResetAt,
                0,
                previous.ObservedAt);
            var currentObservation = new QuotaWindowObservation(
                key,
                currentWindow.Title,
                currentWindow.UsedPercent,
                currentWindow.WindowMinutes,
                currentWindow.ResetAt,
                1,
                current.ObservedAt);

            var replenishment = QuotaReplenishmentEvaluator.Evaluate(
                previousObservation,
                currentObservation,
                now);
            if (replenishment is null)
                continue;

            replenishment = replenishment with { IsCrossSession = true };
            events.Add(replenishment);
            Log.Information(
                $"[quota-replenishment] detected provider={current.Provider} " +
                $"window={currentWindow.WindowId} " +
                $"from={FormatPercent(replenishment.Previous.AvailablePercent)} " +
                $"to={FormatPercent(replenishment.Current.AvailablePercent)} " +
                $"reason={QuotaReplenishmentEvaluator.Reason(replenishment.Kind)}-since-last-session");
        }

        return events;
    }

    private static PersistedQuotaProvider CreatePersistedObservation(
        UsageResult result,
        UsageSnapshot usage,
        DateTimeOffset observedAt)
    {
        var windows = QuotaAlertWindowCatalog.Enumerate(result)
            .Where(window => QuotaReplenishmentEvaluator.IsValid(window.Window.UsedPercent))
            .Select(window => new PersistedQuotaWindow(
                window.Id,
                window.Title,
                window.Window.UsedPercent,
                window.Window.WindowMinutes,
                window.Window.ResetAt))
            .OrderBy(window => window.WindowId, StringComparer.Ordinal)
            .ToArray();

        return new PersistedQuotaProvider(
            result.Id,
            QuotaReplenishmentStateStore.HashIdentity(usage.Email),
            observedAt,
            windows);
    }

    private static string FormatPercent(double value)
        => value.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
}
