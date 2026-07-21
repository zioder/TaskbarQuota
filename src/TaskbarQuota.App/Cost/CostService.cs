using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TaskbarQuota.Diagnostics;
using TaskbarQuota.Usage;

namespace TaskbarQuota.Cost
{
    /// <summary>
    /// Aggregates raw token events from every <see cref="ICostScanner"/> into per-window,
    /// per-provider <b>API-equivalent</b> cost (what the tokens would cost at public list rates —
    /// not what the subsidized subscription actually charged). Windows are bucketed by the user's
    /// local calendar day so "Today"/"Yesterday" line up with what the user expects.
    /// </summary>
    public sealed class CostService
    {
        private readonly IReadOnlyList<ICostScanner> _scanners;
        private readonly PricingCatalog _pricing;

        private static CostService? _instance;
        public static CostService Instance => _instance ??= new CostService();

        public CostService(IEnumerable<ICostScanner>? scanners = null, PricingCatalog? pricing = null)
        {
            _scanners = scanners?.ToList() ?? DefaultScanners();
            _pricing = pricing ?? PricingCatalog.Instance;
        }

        private static List<ICostScanner> DefaultScanners() => new()
        {
            new Scanners.ClaudeCostScanner(),
            // Codex, Grok, Z.ai, Cline, OpenCode scanners plug in here as they land.
        };

        public Task<IReadOnlyDictionary<CostRange, CostWindow>> ComputeAsync(CancellationToken ct = default) =>
            Task.Run(() => Compute(DateTimeOffset.Now), ct);

        /// <summary>Synchronous core (unit-testable): scans the trailing 30 local days and buckets.</summary>
        public IReadOnlyDictionary<CostRange, CostWindow> Compute(DateTimeOffset now)
        {
            DateTime today = now.LocalDateTime.Date;
            DateTime windowStartLocal = today.AddDays(-29);          // 30 local days inclusive
            var sinceUtc = new DateTimeOffset(windowStartLocal, now.Offset);

            var windows = new Dictionary<CostRange, CostWindow>
            {
                [CostRange.Today] = new CostWindow { Range = CostRange.Today },
                [CostRange.Yesterday] = new CostWindow { Range = CostRange.Yesterday },
                [CostRange.Last7Days] = new CostWindow { Range = CostRange.Last7Days },
                [CostRange.Last30Days] = new CostWindow { Range = CostRange.Last30Days },
            };
            // (range, provider) -> accumulator
            var acc = new Dictionary<(CostRange, ProviderId), ProviderCost>();

            foreach (var scanner in _scanners)
            {
                IEnumerator<TokenUsageRecord> e;
                try { e = scanner.Scan(sinceUtc).GetEnumerator(); }
                catch (Exception ex) { Log.Warning(ex, $"CostService scan {scanner.Provider}"); continue; }

                using (e)
                {
                    while (true)
                    {
                        TokenUsageRecord rec;
                        try { if (!e.MoveNext()) break; rec = e.Current; }
                        catch (Exception ex) { Log.Warning(ex, $"CostService iterate {scanner.Provider}"); break; }

                        DateTime day = rec.Timestamp.ToLocalTime().Date;
                        double? cost = _pricing.CostOf(rec);

                        foreach (var range in RangesFor(day, today))
                            Apply(acc, windows[range], range, rec, cost);
                    }
                }
            }

            foreach (var w in windows.Values)
                w.Providers.Sort((a, b) => b.CostUsd.CompareTo(a.CostUsd));

            return windows;
        }

        private static IEnumerable<CostRange> RangesFor(DateTime day, DateTime today)
        {
            int age = (today - day).Days;
            if (age < 0) yield break;                 // future clock skew
            if (age == 0) yield return CostRange.Today;
            if (age == 1) yield return CostRange.Yesterday;
            if (age <= 6) yield return CostRange.Last7Days;
            if (age <= 29) yield return CostRange.Last30Days;
        }

        private static void Apply(
            Dictionary<(CostRange, ProviderId), ProviderCost> acc,
            CostWindow window, CostRange range, TokenUsageRecord rec, double? cost)
        {
            var key = (range, rec.Provider);
            if (!acc.TryGetValue(key, out var pc))
            {
                pc = new ProviderCost { Provider = rec.Provider };
                acc[key] = pc;
                window.Providers.Add(pc);
            }

            if (cost is double c)
            {
                pc.CostUsd += c;
                pc.PricedTokens += rec.TotalTokens;
                window.TotalCostUsd += c;
            }
            else
            {
                pc.UnpricedTokens += rec.TotalTokens;
                window.UnpricedTokens += rec.TotalTokens;
            }
        }
    }
}
