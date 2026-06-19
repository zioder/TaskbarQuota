using System;
using System.Collections.Generic;

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
    }

    /// <summary>A single rate-limit window (for example session or weekly), expressed as percent used.</summary>
    public sealed class RateWindow
    {
        public double UsedPercent { get; init; }
        public int? WindowMinutes { get; init; }
        public DateTimeOffset? ResetAt { get; init; }
        public string? ResetDescription { get; init; }

        public RateWindow(double usedPercent, int? windowMinutes = null, DateTimeOffset? resetAt = null, string? resetDescription = null)
        {
            UsedPercent = Math.Clamp(usedPercent, 0, 100);
            WindowMinutes = windowMinutes;
            ResetAt = resetAt;
            ResetDescription = resetDescription;
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
        public RateWindow? Secondary { get; set; }    // weekly
        public RateWindow? ModelSpecific { get; set; }// e.g. Opus / code review
        public RateWindow? Monthly { get; set; }      // monthly window when available
        public List<NamedRateWindow> ExtraRateWindows { get; } = new();
        public string? LoginMethod { get; set; }
        public string? Email { get; set; }
        public CostSnapshot? Cost { get; set; }
        public AdditionalUsageSnapshot? AdditionalUsage { get; set; }
        public ResetCreditsSnapshot? ResetCredits { get; set; }
        /// <summary>Provider-specific usage dashboard link when known (e.g. OpenCode workspace /go or /usage).</summary>
        public string? UsageDashboardUrl { get; set; }

        public UsageSnapshot(RateWindow primary) => Primary = primary;

        public UsageSnapshot WithSecondary(RateWindow w) { Secondary = w; return this; }
        public UsageSnapshot WithModelSpecific(RateWindow w) { ModelSpecific = w; return this; }
        public UsageSnapshot WithLoginMethod(string m) { LoginMethod = m; return this; }
        public UsageSnapshot WithEmail(string e) { Email = e; return this; }
        public UsageSnapshot WithCost(CostSnapshot c) { Cost = c; return this; }
    }

    public sealed class ProviderFetchResult
    {
        public UsageSnapshot Usage { get; }
        public string SourceLabel { get; }
        public DateTimeOffset FetchedAt { get; } = DateTimeOffset.Now;

        public ProviderFetchResult(UsageSnapshot usage, string sourceLabel)
        {
            Usage = usage;
            SourceLabel = sourceLabel;
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
    }
}
