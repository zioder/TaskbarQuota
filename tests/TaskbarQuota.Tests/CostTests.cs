using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TaskbarQuota.Cost;
using TaskbarQuota.Cost.Scanners;
using TaskbarQuota.Usage;
using Xunit;

namespace TaskbarQuota.Tests;

public class CostTests
{
    // Rates match those derived by least-squares vs ccusage (relerr ~0):
    // claude-opus-4-8 = $5 / $25 / $0.50 / $10 per Mtok (in/out/cacheRead/cacheWrite).
    [Fact]
    public void PricingCatalog_PricesOpus48_AtApiListRates()
    {
        var rec = new TokenUsageRecord
        {
            Provider = ProviderId.Claude,
            RawModel = "claude-opus-4-8",
            Timestamp = DateTimeOffset.UtcNow,
            InputTokens = 1_000_000,
            OutputTokens = 1_000_000,
            CacheReadTokens = 1_000_000,
            CacheWriteTokens = 1_000_000,
        };

        double? cost = PricingCatalog.Instance.CostOf(rec);

        Assert.NotNull(cost);
        // 5 + 25 + 0.5 + 10 = $40.50 for 1M of each lane.
        Assert.Equal(40.50, cost!.Value, 3);
    }

    [Fact]
    public void PricingCatalog_UnknownModel_IsUnpriced()
    {
        var rec = new TokenUsageRecord
        {
            Provider = ProviderId.Claude,
            RawModel = "totally-made-up-model-x",
            Timestamp = DateTimeOffset.UtcNow,
            InputTokens = 1000,
        };

        Assert.Null(PricingCatalog.Instance.CostOf(rec));
    }

    [Fact]
    public void ModelResolver_MapsFamilies()
    {
        Assert.Equal("claude-opus-4-8", ModelResolver.Resolve("claude-opus-4-8"));
        Assert.Equal("claude-fable-5", ModelResolver.Resolve("Claude-Fable-5"));
        Assert.Equal("gpt-5.3-codex", ModelResolver.Resolve("gpt-5.3-codex"));
        Assert.Equal("glm-5.2", ModelResolver.Resolve("GLM-5.2"));
        Assert.Null(ModelResolver.Resolve("<synthetic>"));
        Assert.Null(ModelResolver.Resolve(""));
    }

    [Fact]
    public void ClaudeScanner_DedupsAndBuckets_MatchesHandComputedCost()
    {
        string dir = Path.Combine(Path.GetTempPath(), "tbq-cost-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var now = DateTimeOffset.Now;
            string todayTs = now.AddHours(-1).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

            // Two lines with the SAME message.id + requestId → must be counted once.
            string dup = Line("msg1", "req1", todayTs, "claude-opus-4-8", 1_000_000, 1_000_000, 0, 0);
            // A distinct second request, priced independently.
            string other = Line("msg2", "req2", todayTs, "claude-opus-4-8", 0, 0, 1_000_000, 1_000_000);
            File.WriteAllText(Path.Combine(dir, "session.jsonl"), dup + "\n" + dup + "\n" + other + "\n");

            var scanner = new ClaudeCostScanner(dir);
            var service = new CostService(new[] { scanner });
            var windows = service.Compute(now);

            var today = windows[CostRange.Today];
            // msg1 once: 1M in ($5) + 1M out ($25) = $30. msg2: 1M cacheRead ($0.50) + 1M cacheWrite ($10) = $10.50.
            Assert.Equal(40.50, today.TotalCostUsd, 3);

            var claude = today.Providers.Single(p => p.Provider == ProviderId.Claude);
            Assert.Equal(40.50, claude.CostUsd, 3);
            Assert.Equal(0, today.UnpricedTokens);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ClaudeScanner_ExcludesRowsOlderThanWindow()
    {
        string dir = Path.Combine(Path.GetTempPath(), "tbq-cost-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var now = DateTimeOffset.Now;
            string oldTs = now.AddDays(-45).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
            File.WriteAllText(Path.Combine(dir, "old.jsonl"),
                Line("m", "r", oldTs, "claude-opus-4-8", 1_000_000, 0, 0, 0) + "\n");

            var windows = new CostService(new[] { new ClaudeCostScanner(dir) }).Compute(now);

            Assert.Equal(0, windows[CostRange.Last30Days].TotalCostUsd);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static string Line(string msgId, string reqId, string ts, string model,
        long input, long output, long cacheRead, long cacheWrite) =>
        "{\"requestId\":\"" + reqId + "\",\"timestamp\":\"" + ts + "\",\"message\":{\"id\":\"" + msgId
        + "\",\"model\":\"" + model + "\",\"usage\":{\"input_tokens\":" + input
        + ",\"output_tokens\":" + output
        + ",\"cache_read_input_tokens\":" + cacheRead
        + ",\"cache_creation_input_tokens\":" + cacheWrite + "}}}";
}
