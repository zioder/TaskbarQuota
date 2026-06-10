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

    /// <summary>Metered Copilot spend beyond included credits (from quota snapshot overage fields).</summary>
    public sealed class AdditionalUsageSnapshot
    {
        public bool Enabled { get; init; }
        public double SpentUsd { get; init; }
        public double? BudgetUsd { get; init; }

        public string StatusText => Enabled ? "Enabled" : "Not enabled";

        public string SpendText
        {
            get
            {
                string spent = $"${SpentUsd:0.00}";
                if (!Enabled)
                    return $"{spent} / $0 budget";
                return BudgetUsd is double budget
                    ? $"{spent} / ${budget:0.00} budget"
                    : $"{spent} / — budget";
            }
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
