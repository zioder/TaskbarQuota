using System;
using System.Collections.Generic;

namespace TaskbarQuota.Usage
{
    public static class UsageHistoryHelper
    {
        public static UsageHistory CreateSampleHistoryForProvider(ProviderId providerId)
        {
            switch (providerId)
            {
                case ProviderId.Claude:
                    return new UsageHistory
                    {
                        Today = new UsagePeriod(
                            tokens: 420_000,
                            estimatedCostUsd: 1.85,
                            costEstimated: true,
                            estimateComplete: true,
                            modelBreakdown: new ModelUsageBreakdown(new[]
                            {
                                new ModelUsageEntry("claude-3-7-sonnet", 350_000, 1.57),
                                new ModelUsageEntry("claude-3-5-haiku", 70_000, 0.28),
                            }, "Local log estimate")
                        ),
                        Yesterday = new UsagePeriod(
                            tokens: 650_000,
                            estimatedCostUsd: 2.92,
                            costEstimated: true,
                            estimateComplete: true,
                            modelBreakdown: new ModelUsageBreakdown(new[]
                            {
                                new ModelUsageEntry("claude-3-7-sonnet", 580_000, 2.61),
                                new ModelUsageEntry("claude-3-5-haiku", 70_000, 0.31),
                            }, "Local log estimate")
                        ),
                        Last30Days = new UsagePeriod(
                            tokens: 14_200_000,
                            estimatedCostUsd: 63.90,
                            costEstimated: true,
                            estimateComplete: true
                        ),
                    };

                case ProviderId.Codex:
                    return new UsageHistory
                    {
                        Today = new UsagePeriod(
                            tokens: 280_000,
                            estimatedCostUsd: 0.98,
                            costEstimated: true,
                            estimateComplete: true,
                            modelBreakdown: new ModelUsageBreakdown(new[]
                            {
                                new ModelUsageEntry("gpt-4o", 200_000, 0.85),
                                new ModelUsageEntry("o3-mini", 80_000, 0.13),
                            }, "ChatGPT API metrics")
                        ),
                        Yesterday = new UsagePeriod(
                            tokens: 410_000,
                            estimatedCostUsd: 1.44,
                            costEstimated: true,
                            estimateComplete: true
                        ),
                        Last30Days = new UsagePeriod(
                            tokens: 9_800_000,
                            estimatedCostUsd: 34.30,
                            costEstimated: true,
                            estimateComplete: true
                        ),
                    };

                case ProviderId.Cursor:
                    return new UsageHistory
                    {
                        Today = new UsagePeriod(
                            tokens: 310_000,
                            estimatedCostUsd: 1.15,
                            costEstimated: true,
                            estimateComplete: true,
                            modelBreakdown: new ModelUsageBreakdown(new[]
                            {
                                new ModelUsageEntry("claude-3-7-sonnet", 180_000, 0.81),
                                new ModelUsageEntry("gpt-4o", 130_000, 0.34),
                            }, "Cursor usage telemetry")
                        ),
                        Yesterday = new UsagePeriod(
                            tokens: 520_000,
                            estimatedCostUsd: 1.95,
                            costEstimated: true,
                            estimateComplete: true
                        ),
                        Last30Days = new UsagePeriod(
                            tokens: 11_500_000,
                            estimatedCostUsd: 43.10,
                            costEstimated: true,
                            estimateComplete: true
                        ),
                    };

                case ProviderId.Antigravity:
                    return new UsageHistory
                    {
                        Today = new UsagePeriod(
                            tokens: 550_000,
                            estimatedCostUsd: 0.45,
                            costEstimated: true,
                            estimateComplete: true,
                            modelBreakdown: new ModelUsageBreakdown(new[]
                            {
                                new ModelUsageEntry("gemini-2.0-flash", 450_000, 0.22),
                                new ModelUsageEntry("claude-3-7-sonnet", 100_000, 0.23),
                            }, "AGY usage quota pool")
                        ),
                        Yesterday = new UsagePeriod(
                            tokens: 890_000,
                            estimatedCostUsd: 0.72,
                            costEstimated: true,
                            estimateComplete: true
                        ),
                        Last30Days = new UsagePeriod(
                            tokens: 18_400_000,
                            estimatedCostUsd: 14.80,
                            costEstimated: true,
                            estimateComplete: true
                        ),
                    };

                case ProviderId.Grok:
                    return new UsageHistory
                    {
                        Today = new UsagePeriod(
                            tokens: 190_000,
                            estimatedCostUsd: 0.65,
                            costEstimated: true,
                            estimateComplete: true,
                            modelBreakdown: new ModelUsageBreakdown(new[]
                            {
                                new ModelUsageEntry("grok-3", 190_000, 0.65),
                            }, "xAI API session")
                        ),
                        Yesterday = new UsagePeriod(
                            tokens: 220_000,
                            estimatedCostUsd: 0.75,
                            costEstimated: true,
                            estimateComplete: true
                        ),
                        Last30Days = new UsagePeriod(
                            tokens: 4_200_000,
                            estimatedCostUsd: 14.30,
                            costEstimated: true,
                            estimateComplete: true
                        ),
                    };

                default:
                    return new UsageHistory
                    {
                        Today = new UsagePeriod(
                            tokens: 120_000,
                            estimatedCostUsd: 0.35,
                            costEstimated: true,
                            estimateComplete: true
                        ),
                        Yesterday = new UsagePeriod(
                            tokens: 180_000,
                            estimatedCostUsd: 0.52,
                            costEstimated: true,
                            estimateComplete: true
                        ),
                        Last30Days = new UsagePeriod(
                            tokens: 3_100_000,
                            estimatedCostUsd: 9.10,
                            costEstimated: true,
                            estimateComplete: true
                        ),
                    };
            }
        }
    }
}
