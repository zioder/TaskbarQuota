using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TaskbarQuota.Usage
{
    /// <summary>
    /// Persists the last successful usage snapshot per provider to %LOCALAPPDATA% so the taskbar widget
    /// can paint real numbers immediately after a reboot instead of a placeholder (issue #21).
    /// Restored snapshots are marked stale — the widget dims them until the first live fetch lands.
    ///
    /// The domain models are constructor-shaped and carry computed members, so they are mapped through
    /// explicit DTOs rather than serialized directly.
    /// </summary>
    internal static class UsageSnapshotStore
    {
        /// <summary>A restored snapshot older than this is discarded: quota windows have likely rolled.</summary>
        internal static readonly TimeSpan MaxRestoreAge = TimeSpan.FromHours(24);

        private static readonly JsonSerializerOptions SerializerOptions = new()
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        /// <summary>Default location; tests pass their own directory instead.</summary>
        public static string DefaultDirectory => AppStorage.AppDataDirectory;

        private static string FilePathIn(string directory) => Path.Combine(directory, "usage-snapshots.json");

        /// <summary>
        /// Reads the persisted snapshots, dropping any that are too old or whose rate windows have already
        /// reset (a reset window would render a percentage that is no longer true).
        /// </summary>
        public static Dictionary<ProviderId, UsageResult> Load(string directory, Func<ProviderId, IUsageProvider?> providerLookup)
        {
            var restored = new Dictionary<ProviderId, UsageResult>();
            try
            {
                var path = FilePathIn(directory);
                if (!File.Exists(path))
                    return restored;

                var file = JsonSerializer.Deserialize<StoredFile>(File.ReadAllText(path), SerializerOptions);
                if (file?.Entries is null)
                    return restored;

                var now = DateTimeOffset.Now;
                foreach (var entry in file.Entries)
                {
                    if (!Enum.TryParse<ProviderId>(entry.Provider, out var id))
                        continue;
                    if (providerLookup(id) is not { } provider)
                        continue;
                    if (now - entry.FetchedAt > MaxRestoreAge)
                        continue;
                    if (entry.Usage is not { } usage)
                        continue;
                    if (HasResetWindow(usage, now))
                        continue;

                    var fetch = new ProviderFetchResult(ToSnapshot(usage), entry.SourceLabel ?? string.Empty, entry.FetchedAt);
                    restored[id] = UsageResult.Success(id, provider, fetch).AsStale();
                }
            }
            catch (Exception ex)
            {
                Diagnostics.Log.Warning(ex, "Failed to restore persisted usage snapshots");
            }

            return restored;
        }

        public static void Save(string directory, IReadOnlyDictionary<ProviderId, UsageResult> results)
        {
            try
            {
                var entries = results
                    .Where(kv => kv.Value.Fetch is not null)
                    .Select(kv => new StoredEntry
                    {
                        Provider = kv.Key.ToString(),
                        FetchedAt = kv.Value.Fetch!.FetchedAt,
                        SourceLabel = kv.Value.Fetch!.SourceLabel,
                        Usage = FromSnapshot(kv.Value.Fetch!.Usage),
                    })
                    .ToList();

                Directory.CreateDirectory(directory);
                var json = JsonSerializer.Serialize(new StoredFile { Entries = entries }, SerializerOptions);

                // Write through a temp file so a crash mid-write can't leave a truncated cache behind.
                var path = FilePathIn(directory);
                var temp = path + ".tmp";
                File.WriteAllText(temp, json);
                File.Move(temp, path, overwrite: true);
            }
            catch (Exception ex)
            {
                Diagnostics.Log.Warning(ex, "Failed to persist usage snapshots");
            }
        }

        private static bool HasResetWindow(StoredUsage usage, DateTimeOffset now)
            => new[] { usage.Primary, usage.Secondary, usage.ModelSpecific, usage.Monthly }
                .Concat(usage.ExtraRateWindows?.Select(w => w.Window) ?? Enumerable.Empty<StoredWindow?>())
                .Any(w => w?.ResetAt is { } resetAt && now >= resetAt);

        private static UsageSnapshot ToSnapshot(StoredUsage stored)
        {
            var snapshot = new UsageSnapshot(ToWindow(stored.Primary) ?? new RateWindow(0))
            {
                HasPrimaryWindow = stored.HasPrimaryWindow,
                Secondary = ToWindow(stored.Secondary),
                ModelSpecific = ToWindow(stored.ModelSpecific),
                Monthly = ToWindow(stored.Monthly),
                LoginMethod = stored.LoginMethod,
                Email = stored.Email,
                UsageDashboardUrl = stored.UsageDashboardUrl,
                Cost = ToCost(stored.Cost),
                AdditionalUsage = stored.AdditionalUsage is { } a
                    ? new AdditionalUsageSnapshot
                    {
                        Enabled = a.Enabled,
                        SpentUsd = a.SpentUsd,
                        BudgetUsd = a.BudgetUsd,
                        IsCredits = a.IsCredits,
                    }
                    : null,
                ResetCredits = stored.ResetCredits is { } rc
                    ? new ResetCreditsSnapshot(
                        rc.AvailableCount,
                        rc.Credits?.Select(c => new ResetCreditGrant(c.Status ?? string.Empty, c.GrantedAt, c.ExpiresAt)).ToList()
                            ?? new List<ResetCreditGrant>())
                    : null,
            };

            foreach (var extra in stored.ExtraRateWindows ?? new List<StoredNamedWindow>())
            {
                if (ToWindow(extra.Window) is { } window)
                    snapshot.ExtraRateWindows.Add(new NamedRateWindow(extra.Id ?? string.Empty, extra.Title ?? string.Empty, window));
            }

            return snapshot;
        }

        private static StoredUsage FromSnapshot(UsageSnapshot usage) => new()
        {
            Primary = FromWindow(usage.Primary),
            HasPrimaryWindow = usage.HasPrimaryWindow,
            Secondary = FromWindow(usage.Secondary),
            ModelSpecific = FromWindow(usage.ModelSpecific),
            Monthly = FromWindow(usage.Monthly),
            ExtraRateWindows = usage.ExtraRateWindows
                .Select(w => new StoredNamedWindow { Id = w.Id, Title = w.Title, Window = FromWindow(w.Window) })
                .ToList(),
            LoginMethod = usage.LoginMethod,
            Email = usage.Email,
            UsageDashboardUrl = usage.UsageDashboardUrl,
            Cost = usage.Cost is { } c
                ? new StoredCost { Amount = c.Amount, Currency = c.Currency, Label = c.Label, Limit = c.Limit, ResetsAt = c.ResetsAt }
                : null,
            AdditionalUsage = usage.AdditionalUsage is { } a
                ? new StoredAdditionalUsage { Enabled = a.Enabled, SpentUsd = a.SpentUsd, BudgetUsd = a.BudgetUsd, IsCredits = a.IsCredits }
                : null,
            ResetCredits = usage.ResetCredits is { } rc
                ? new StoredResetCredits
                {
                    AvailableCount = rc.AvailableCount,
                    Credits = rc.Credits
                        .Select(c => new StoredResetCredit { Status = c.Status, GrantedAt = c.GrantedAt, ExpiresAt = c.ExpiresAt })
                        .ToList(),
                }
                : null,
        };

        private static RateWindow? ToWindow(StoredWindow? stored)
            => stored is null
                ? null
                : new RateWindow(stored.UsedPercent, stored.WindowMinutes, stored.ResetAt, stored.ResetDescription, stored.Label)
                {
                    ShowCostValue = stored.ShowCostValue,
                };

        private static StoredWindow? FromWindow(RateWindow? window)
            => window is null
                ? null
                : new StoredWindow
                {
                    UsedPercent = window.UsedPercent,
                    WindowMinutes = window.WindowMinutes,
                    ResetAt = window.ResetAt,
                    ResetDescription = window.ResetDescription,
                    Label = window.Label,
                    ShowCostValue = window.ShowCostValue,
                };

        private static CostSnapshot? ToCost(StoredCost? stored)
        {
            if (stored is null)
                return null;

            var cost = new CostSnapshot(stored.Amount, stored.Currency ?? "USD", stored.Label ?? string.Empty)
            {
                ResetsAt = stored.ResetsAt,
            };
            if (stored.Limit is { } limit)
                cost.Limit = limit;
            return cost;
        }

        private sealed class StoredFile
        {
            public List<StoredEntry>? Entries { get; set; }
        }

        private sealed class StoredEntry
        {
            public string? Provider { get; set; }
            public DateTimeOffset FetchedAt { get; set; }
            public string? SourceLabel { get; set; }
            public StoredUsage? Usage { get; set; }
        }

        private sealed class StoredUsage
        {
            public StoredWindow? Primary { get; set; }
            public bool HasPrimaryWindow { get; set; } = true;
            public StoredWindow? Secondary { get; set; }
            public StoredWindow? ModelSpecific { get; set; }
            public StoredWindow? Monthly { get; set; }
            public List<StoredNamedWindow>? ExtraRateWindows { get; set; }
            public string? LoginMethod { get; set; }
            public string? Email { get; set; }
            public string? UsageDashboardUrl { get; set; }
            public StoredCost? Cost { get; set; }
            public StoredAdditionalUsage? AdditionalUsage { get; set; }
            public StoredResetCredits? ResetCredits { get; set; }
        }

        private sealed class StoredWindow
        {
            public double UsedPercent { get; set; }
            public int? WindowMinutes { get; set; }
            public DateTimeOffset? ResetAt { get; set; }
            public string? ResetDescription { get; set; }
            public string? Label { get; set; }
            public bool ShowCostValue { get; set; }
        }

        private sealed class StoredNamedWindow
        {
            public string? Id { get; set; }
            public string? Title { get; set; }
            public StoredWindow? Window { get; set; }
        }

        private sealed class StoredCost
        {
            public double Amount { get; set; }
            public string? Currency { get; set; }
            public string? Label { get; set; }
            public double? Limit { get; set; }
            public DateTimeOffset? ResetsAt { get; set; }
        }

        private sealed class StoredAdditionalUsage
        {
            public bool Enabled { get; set; }
            public double SpentUsd { get; set; }
            public double? BudgetUsd { get; set; }
            public bool IsCredits { get; set; }
        }

        private sealed class StoredResetCredits
        {
            public int AvailableCount { get; set; }
            public List<StoredResetCredit>? Credits { get; set; }
        }

        private sealed class StoredResetCredit
        {
            public string? Status { get; set; }
            public DateTimeOffset? GrantedAt { get; set; }
            public DateTimeOffset? ExpiresAt { get; set; }
        }
    }
}
