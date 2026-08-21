using System;
using System.Collections.Generic;
using System.Globalization;

namespace TaskbarQuota.Usage
{
    public enum ProviderId
    {
        Claude,
        Codex,
        Cursor,
        Antigravity,
        OpenCode,
        OpenCodeGo,
        Copilot,
        Grok,
        Devin,
        Cline,
        ClinePass,
        Zai,
        Kimi,
    }

    /// <summary>A single rate-limit window (for example session or weekly), expressed as percent used.</summary>
    public sealed class RateWindow
    {
        public double UsedPercent { get; init; }
        public int? WindowMinutes { get; init; }
        public DateTimeOffset? ResetAt { get; init; }
        public string? ResetDescription { get; init; }
        /// <summary>Optional bar label override (e.g. "Spend limit" for Claude Enterprise), when the
        /// window isn't the provider's default Session/Weekly meter.</summary>
        public string? Label { get; init; }
        /// <summary>When true this meter's value is a monetary/credit spend (rendered from the snapshot's
        /// <see cref="UsageSnapshot.Cost"/>, e.g. "$9.27/$100.00") rather than a plain used-percent. Kept
        /// separate from <see cref="Label"/> so a mere label override (e.g. Codex "Weekly") never flips a
        /// usage-% bar into a spend value.</summary>
        public bool ShowCostValue { get; init; }

        public RateWindow(double usedPercent, int? windowMinutes = null, DateTimeOffset? resetAt = null, string? resetDescription = null, string? label = null)
        {
            UsedPercent = Math.Clamp(usedPercent, 0, 100);
            WindowMinutes = windowMinutes;
            ResetAt = resetAt;
            ResetDescription = resetDescription;
            Label = label;
        }

        public double RemainingPercent => 100 - UsedPercent;
    }

    public sealed class NamedRateWindow
    {
        public string Id { get; }
        public string Title { get; }
        public RateWindow Window { get; }

        public NamedRateWindow(string id, string title, RateWindow window)
        {
            Id = id;
            Title = title;
            Window = window;
        }
    }

    /// <summary>Monetary balance / spend info for API-billed providers.</summary>
    public sealed class CostSnapshot
    {
        public double Amount { get; }
        public string Currency { get; }
        public string Label { get; }
        public double? Limit { get; set; }
        public DateTimeOffset? ResetsAt { get; set; }

        public CostSnapshot(double amount, string currency, string label)
        {
            Amount = amount;
            Currency = currency;
            Label = label;
        }

        public CostSnapshot WithLimit(double limit) { Limit = limit; return this; }
        public CostSnapshot WithResetsAt(DateTimeOffset at) { ResetsAt = at; return this; }

        private string Money(double v) =>
            string.Equals(Currency, "USD", StringComparison.OrdinalIgnoreCase) ? $"${v:0.00}" : $"{v:0.00} {Currency}";

        public string Display => Limit is double lim ? $"{Money(Amount)} / {Money(lim)}" : Money(Amount);
    }

    /// <summary>Current Z.ai Coding Plan quota coefficient for the active time window.</summary>
    public sealed class UsagePricingSnapshot
    {
        public string Period { get; }
        public double Multiplier { get; }
        public string MultiplierText => Multiplier.ToString("0.##", CultureInfo.InvariantCulture) + "×";
        public string Display => $"{Period} · {MultiplierText}";

        public UsagePricingSnapshot(string period, double multiplier)
        {
            Period = period;
            Multiplier = multiplier;
        }
    }

    /// <summary>
    /// Metered spend beyond included usage. Copilot reports this in USD (overage budget); Grok reports
    /// it in credits (the on-demand / pay-as-you-go cap), so <see cref="IsCredits"/> selects the units.
    /// </summary>
    public sealed class AdditionalUsageSnapshot
    {
        public bool Enabled { get; init; }
        public double SpentUsd { get; init; }
        public double? BudgetUsd { get; init; }
        /// <summary>When true, the spent/budget values are credit counts rather than US dollars.</summary>
        public bool IsCredits { get; init; }

        public string StatusText => Enabled ? "Enabled" : "Not enabled";

        public string SpendText
        {
            get
            {
                string spent = Amount(SpentUsd);
                string suffix = IsCredits ? "credits" : "budget";
                if (!Enabled)
                    return $"{spent} / {(IsCredits ? "0" : "$0")} {suffix}";
                return BudgetUsd is double budget
                    ? $"{spent} / {Amount(budget)} {suffix}"
                    : $"{spent} / — {suffix}";
            }
        }

        private string Amount(double value)
            => IsCredits ? $"{value:0}" : $"${value:0.00}";
    }

    /// <summary>Codex rate-limit reset credits granted by the Codex backend.</summary>
    public sealed class ResetCreditsSnapshot
    {
        public int AvailableCount { get; }
        public IReadOnlyList<ResetCreditGrant> Credits { get; }

        public ResetCreditsSnapshot(int availableCount, IReadOnlyList<ResetCreditGrant> credits)
        {
            AvailableCount = Math.Max(0, availableCount);
            Credits = credits;
        }

        public DateTimeOffset? EarliestExpiresAt
        {
            get
            {
                DateTimeOffset? earliest = null;
                foreach (var credit in Credits)
                {
                    if (credit.ExpiresAt is not { } expiresAt)
                        continue;

                    if (earliest is null || expiresAt < earliest)
                        earliest = expiresAt;
                }

                return earliest;
            }
        }
    }

    public sealed class ResetCreditGrant
    {
        public string Status { get; }
        public DateTimeOffset? GrantedAt { get; }
        public DateTimeOffset? ExpiresAt { get; }

        public ResetCreditGrant(string status, DateTimeOffset? grantedAt, DateTimeOffset? expiresAt)
        {
            Status = status;
            GrantedAt = grantedAt;
            ExpiresAt = expiresAt;
        }
    }

    /// <summary>Normalized usage data for a provider (session / weekly / model-specific windows).</summary>
    public sealed class UsageSnapshot
    {
        public RateWindow Primary { get; }            // session
        /// <summary>False when the provider reported no session window at all (e.g. Codex org/Business
        /// plans that only expose credits): the card skips the primary bar instead of showing "0%".</summary>
        public bool HasPrimaryWindow { get; set; } = true;
        public RateWindow? Secondary { get; set; }    // weekly
        public RateWindow? ModelSpecific { get; set; }// e.g. Opus / code review
        public RateWindow? Monthly { get; set; }      // monthly window when available
        public List<NamedRateWindow> ExtraRateWindows { get; } = new();
        public string? LoginMethod { get; set; }
        public string? Email { get; set; }
        public CostSnapshot? Cost { get; set; }
        public UsagePricingSnapshot? Pricing { get; set; }
        public AdditionalUsageSnapshot? AdditionalUsage { get; set; }
        public ResetCreditsSnapshot? ResetCredits { get; set; }
        /// <summary>Provider-specific usage dashboard link when known (e.g. OpenCode workspace /go or /usage).</summary>
        public string? UsageDashboardUrl { get; set; }
        /// <summary>Local transcript-derived token history and API-equivalent cost estimates.</summary>
        public UsageHistory? UsageHistory { get; set; }

        public UsageSnapshot(RateWindow primary) => Primary = primary;

        public UsageSnapshot WithSecondary(RateWindow w) { Secondary = w; return this; }
        public UsageSnapshot WithModelSpecific(RateWindow w) { ModelSpecific = w; return this; }
        public UsageSnapshot WithLoginMethod(string m) { LoginMethod = m; return this; }
        public UsageSnapshot WithEmail(string e) { Email = e; return this; }
        public UsageSnapshot WithCost(CostSnapshot c) { Cost = c; return this; }
        public UsageSnapshot WithUsageHistory(UsageHistory h) { UsageHistory = h; return this; }
    }

    public sealed class TokenBreakdown
    {
        /// <summary>Input tokens that were neither read from nor written to a prompt cache.</summary>
        public ulong Input { get; set; }
        public ulong CacheWrite5m { get; set; }
        public ulong CacheWrite1h { get; set; }
        public ulong CacheRead { get; set; }
        public ulong Output { get; set; }
        /// <summary>Reasoning is a subset of Output and is never added to TotalTokens.</summary>
        public ulong Reasoning { get; set; }
        public bool IsFast { get; set; }

        public ulong CacheWrite => CacheWrite5m + CacheWrite1h;
        public ulong PromptTokens => Input + CacheWrite5m + CacheWrite1h + CacheRead;
        public ulong TotalTokens => PromptTokens + Output;

        public TokenBreakdown Add(TokenBreakdown other) => new()
        {
            Input = Input + other.Input,
            CacheWrite5m = CacheWrite5m + other.CacheWrite5m,
            CacheWrite1h = CacheWrite1h + other.CacheWrite1h,
            CacheRead = CacheRead + other.CacheRead,
            Output = Output + other.Output,
            Reasoning = Reasoning + other.Reasoning,
        };
    }

    public sealed class ModelUsageEntry
    {
        public string Model { get; }
        public ulong TotalTokens { get; }
        public TokenBreakdown Tokens { get; }
        public double? CostUsd { get; }
        public double CacheSavingsUsd { get; }
        public int Records { get; }
        public int Sessions { get; }

        public ModelUsageEntry(string model, ulong totalTokens, double? costUsd = null)
            : this(model, new TokenBreakdown { Input = totalTokens }, costUsd)
        {
        }

        public ModelUsageEntry(
            string model,
            TokenBreakdown tokens,
            double? costUsd = null,
            double cacheSavingsUsd = 0,
            int records = 0,
            int sessions = 0)
        {
            Model = model;
            Tokens = tokens;
            TotalTokens = tokens.TotalTokens;
            CostUsd = costUsd;
            CacheSavingsUsd = cacheSavingsUsd;
            Records = records;
            Sessions = sessions;
        }
    }

    public sealed class ModelUsageBreakdown
    {
        public IReadOnlyList<ModelUsageEntry> Models { get; }
        public string SourceNote { get; }

        public ModelUsageBreakdown(IReadOnlyList<ModelUsageEntry> models, string sourceNote = "")
        {
            Models = models;
            SourceNote = sourceNote;
        }
    }

    public sealed class UsagePeriod
    {
        public ulong Tokens { get; }
        public double? EstimatedCostUsd { get; }
        public bool CostEstimated { get; }
        public bool EstimateComplete { get; }
        public ModelUsageBreakdown? ModelBreakdown { get; }
        public TokenBreakdown TokenBreakdown { get; }
        public ulong CachedInputTokens => TokenBreakdown.CacheRead;
        public ulong UncachedInputTokens => TokenBreakdown.Input;
        public ulong CacheCreationTokens => TokenBreakdown.CacheWrite;
        public ulong OutputTokens => TokenBreakdown.Output;
        public ulong ReasoningTokens => TokenBreakdown.Reasoning;
        public double CacheSavingsUsd { get; }
        public int Records { get; }
        public int Sessions { get; }

        public UsagePeriod(
            ulong tokens,
            double? estimatedCostUsd = null,
            bool costEstimated = true,
            bool estimateComplete = true,
            ModelUsageBreakdown? modelBreakdown = null,
            TokenBreakdown? tokenBreakdown = null,
            double cacheSavingsUsd = 0,
            int records = 0,
            int sessions = 0)
        {
            TokenBreakdown = tokenBreakdown ?? new TokenBreakdown { Input = tokens };
            Tokens = TokenBreakdown.TotalTokens;
            EstimatedCostUsd = estimatedCostUsd;
            CostEstimated = costEstimated;
            EstimateComplete = estimateComplete;
            ModelBreakdown = modelBreakdown;
            CacheSavingsUsd = cacheSavingsUsd;
            Records = records;
            Sessions = sessions;
        }
    }

    public sealed class DailyUsage
    {
        public string Date { get; }
        public ulong Tokens { get; }
        public double? EstimatedCostUsd { get; }
        public bool EstimateComplete { get; }
        public TokenBreakdown TokenBreakdown { get; }
        public double CacheSavingsUsd { get; }
        public int Records { get; }
        public int Sessions { get; }
        public ModelUsageBreakdown? ModelBreakdown { get; }

        public DailyUsage(
            string date,
            ulong tokens,
            double? estimatedCostUsd = null,
            bool estimateComplete = true,
            TokenBreakdown? tokenBreakdown = null,
            double cacheSavingsUsd = 0,
            int records = 0,
            int sessions = 0,
            ModelUsageBreakdown? modelBreakdown = null)
        {
            Date = date;
            TokenBreakdown = tokenBreakdown ?? new TokenBreakdown { Input = tokens };
            Tokens = TokenBreakdown.TotalTokens;
            EstimatedCostUsd = estimatedCostUsd;
            EstimateComplete = estimateComplete;
            CacheSavingsUsd = cacheSavingsUsd;
            Records = records;
            Sessions = sessions;
            ModelBreakdown = modelBreakdown;
        }
    }

    public sealed class UsageHistory
    {
        public UsagePeriod? Today { get; set; }
        public UsagePeriod? Yesterday { get; set; }
        public UsagePeriod? Last7Days { get; set; }
        public UsagePeriod? Last30Days { get; set; }
        public UsagePeriod? Last90Days { get; set; }
        public IReadOnlyList<DailyUsage> Daily { get; set; } = Array.Empty<DailyUsage>();
    }

    public sealed class ProviderFetchResult
    {
        public UsageSnapshot Usage { get; }
        public string SourceLabel { get; }
        public DateTimeOffset FetchedAt { get; }

        /// <param name="fetchedAt">Original fetch time; only passed when restoring a persisted snapshot.</param>
        public ProviderFetchResult(UsageSnapshot usage, string sourceLabel, DateTimeOffset? fetchedAt = null)
        {
            Usage = usage;
            SourceLabel = sourceLabel;
            FetchedAt = fetchedAt ?? DateTimeOffset.Now;
        }
    }

    public enum ProviderErrorKind
    {
        NotInstalled,
        NotRunning,
        AuthRequired,
        Timeout,
        RateLimited,
        Parse,
        Other,
    }

    public sealed class ProviderException : Exception
    {
        public ProviderErrorKind Kind { get; }
        public ProviderException(ProviderErrorKind kind, string message) : base(message) => Kind = kind;
        public ProviderException(ProviderErrorKind kind, string message, Exception innerException)
            : base(message, innerException) => Kind = kind;
    }
}
