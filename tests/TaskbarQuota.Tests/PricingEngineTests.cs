using System;
using System.Text.Json;
using TaskbarQuota.Usage;
using Xunit;

namespace TaskbarQuota.Tests
{
    public class PricingEngineTests
    {
        [Theory]
        [InlineData("claude-3-7-sonnet", 3.00, 15.00)]
        [InlineData("claude-3-5-sonnet", 3.00, 15.00)]
        [InlineData("claude-3-5-haiku", 0.80, 4.00)]
        [InlineData("gpt-4o", 2.50, 10.00)]
        [InlineData("o3-mini", 1.10, 4.40)]
        [InlineData("deepseek-r1", 0.55, 2.19)]
        [InlineData("gemini-2.0-flash", 0.10, 0.40)]
        [InlineData("grok-3", 3.00, 15.00)]
        public void KnownModels_ResolveToCorrectInputOutputRates(string modelName, double expectedInputRate, double expectedOutputRate)
        {
            var rates = PricingEngine.Resolve(modelName);
            Assert.NotNull(rates);
            Assert.Equal(expectedInputRate, rates.InputPerMillion);
            Assert.Equal(expectedOutputRate, rates.OutputPerMillion);
        }

        [Fact]
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test name convention")]
        public void TokenBreakdown_CalculatesCostCorrectly()
        {
            var tokens = new TokenBreakdown
            {
                Input = 1_000_000,
                Output = 1_000_000,
                CacheWrite5m = 500_000,
                CacheRead = 200_000,
            };

            // claude-3-7-sonnet rates: Input $3.00, Output $15.00, CacheWrite5m $3.75, CacheRead $0.30
            // Total cost = $3.00 + $15.00 + $1.875 + $0.06 = $19.935
            double? cost = PricingEngine.EstimateCostUsd("claude-3-7-sonnet", tokens);
            Assert.NotNull(cost);
            Assert.InRange(cost.Value, 19.93, 19.94);
        }

        [Fact]
        public void OpenAI_Models_ResolveWith272kThreshold()
        {
            var rates = PricingEngine.Resolve("gpt-5");
            Assert.NotNull(rates);
            Assert.Equal(272_000ul, rates.LongContextThreshold);
        }

        [Fact]
        public void PerModelThreshold_SwitchesWholeRequestAtOpenAiBoundary()
        {
            var rates = new ModelRates(10, 30, 10, 1, 20, 60, 20, 2, longContextThreshold: 272_000);

            // 201k input: below OpenAI's 272k boundary -> short/standard tier.
            var shortContext = new TokenBreakdown { Input = 201_000, Output = 1000 };
            Assert.Equal(
                (201_000 * 10 + 1000 * 30) / 1_000_000d,
                rates.CalculateCostDollars(shortContext));

            // 300k input -> entire request switches to the long-context tier.
            var longContext = new TokenBreakdown { Input = 300_000, Output = 1000 };
            Assert.Equal(
                (300_000 * 20 + 1000 * 60) / 1_000_000d,
                rates.CalculateCostDollars(longContext));
        }

        [Fact]
        public void CacheWrite1h_UsesTieredInputRateNotFlatRate()
        {
            // At 300k input above the 272k threshold, cache-create-1h must be
            // billed at 2x the long-context input rate (20), not the flat rate (10).
            var rates = new ModelRates(10, 30, cacheWritePerMillion: 10, cacheWriteAbove200kPerMillion: 20,
                inputAbove200kPerMillion: 20, outputAbove200kPerMillion: 60, longContextThreshold: 272_000);
            var tokens = new TokenBreakdown
            {
                Input = 300_000,
                Output = 1000,
                CacheWrite1h = 100_000,
            };

            double cost = rates.CalculateCostDollars(tokens);
            double expected =
                (300_000 * 20          // long input
                 + 1000 * 60           // long output
                 + 100_000 * 20 * 2)   // cache-create-1h @ long input * 2
                / 1_000_000d;
            Assert.Equal(expected, cost, 6);

            // Below the threshold the same bucket uses the flat rate.
            var shortTokens = new TokenBreakdown { Input = 100_000, Output = 1000, CacheWrite1h = 100_000 };
            double shortCost = rates.CalculateCostDollars(shortTokens);
            double shortExpected = (100_000 * 10 + 1000 * 30 + 100_000 * 10 * 2) / 1_000_000d;
            Assert.Equal(shortExpected, shortCost, 6);
        }

        [Fact]
        public void ModelsDevCostObject_IsAlreadyPricedPerMillionTokens()
        {
            using var document = JsonDocument.Parse("""
                {"cost":{"input":1.4,"output":4.4,"cache_read":0.26,"cache_write":0}}
                """);

            var rates = PricingCatalogStore.TryReadRateOverride(document.RootElement);

            Assert.NotNull(rates);
            Assert.Equal(1.4, rates.InputPerMillion);
            Assert.Equal(4.4, rates.OutputPerMillion);
            Assert.Equal(0.26, rates.CacheReadPerMillion);
            Assert.Equal(0, rates.CacheWritePerMillion);
            Assert.Equal(6.06, rates.CalculateCostDollars(new TokenBreakdown
            {
                Input = 1_000_000,
                Output = 1_000_000,
                CacheRead = 1_000_000,
            }), 6);
        }

        [Theory]
        [InlineData("gemini-3.6-flash", 0.75, 3.75)]
        [InlineData("gemini-3.7-flash", 0.75, 3.75)]
        [InlineData("gemini-3.8-flash", 0.75, 3.75)]
        public void Gemini36And37Flash_UseIntroRates(string modelName, double expectedInputRate, double expectedOutputRate)
        {
            var rates = PricingEngine.Resolve(modelName);
            Assert.NotNull(rates);
            Assert.Equal(expectedInputRate, rates.InputPerMillion);
            Assert.Equal(expectedOutputRate, rates.OutputPerMillion);
            Assert.Equal(0.075, rates.CacheReadPerMillion);
        }

        [Theory]
        [InlineData("gemini-3.6-flash")]
        [InlineData("gemini-3.7-flash")]
        [InlineData("gemini-3.8-flash")]
        public void GeminiFlash_PromotionExpiresAtPublishedBoundary(string modelName)
        {
            var intro = PricingEngine.Resolve(modelName, new DateTimeOffset(2026, 12, 31, 23, 59, 59, TimeSpan.Zero));
            var standard = PricingEngine.Resolve(modelName, new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero));

            Assert.NotNull(intro);
            Assert.Equal(0.75, intro.InputPerMillion);
            Assert.Equal(3.75, intro.OutputPerMillion);
            Assert.NotNull(standard);
            Assert.Equal(1.5, standard.InputPerMillion);
            Assert.Equal(7.5, standard.OutputPerMillion);
            Assert.Equal(0.15, standard.CacheReadPerMillion);
        }

        [Fact]
        public void LiteLlmEntry_ReadsPriorityAnd272kTiers()
        {
            using var document = JsonDocument.Parse("""
                {"input_cost_per_token":4e-06,"output_cost_per_token":2e-05,"cache_read_input_token_cost":4e-07,
                 "cache_creation_input_token_cost":5e-06,
                 "input_cost_per_token_above_272k_tokens":8e-06,"output_cost_per_token_above_272k_tokens":3e-05,
                 "input_cost_per_token_priority":8e-06,"output_cost_per_token_priority":4e-05,
                 "cache_read_input_token_cost_priority":8e-07}
                """);

            var rates = PricingCatalogStore.TryReadRateOverride(document.RootElement);

            Assert.NotNull(rates);
            Assert.Equal(4, rates.InputPerMillion, 6);
            Assert.Equal(272_000ul, rates.LongContextThreshold);
            Assert.Equal(8, rates.InputAbove200kPerMillion!.Value, 6);
            Assert.NotNull(rates.Fast);
            Assert.Equal(8, rates.Fast.InputPerMillion, 6);
            Assert.Equal(40, rates.Fast.OutputPerMillion, 6);
            Assert.Equal(0.8, rates.Fast.CacheReadPerMillion, 6);
            // Priority omits a cache-write rate: it keeps the standard cache-write-to-input ratio.
            Assert.Equal(10, rates.Fast.CacheWritePerMillion, 6);
        }

        [Fact]
        public void LiteLlmProviderSpecificFast_ScalesStandardRates()
        {
            using var document = JsonDocument.Parse("""
                {"input_cost_per_token":5e-06,"output_cost_per_token":2.5e-05,"provider_specific_entry":{"us":1.1,"fast":2.0}}
                """);

            var rates = PricingCatalogStore.TryReadRateOverride(document.RootElement);

            Assert.NotNull(rates?.Fast);
            Assert.Equal(10, rates.Fast.InputPerMillion, 6);
            Assert.Equal(50, rates.Fast.OutputPerMillion, 6);
        }

        [Fact]
        public void FastTokens_BillAtPriorityRates()
        {
            var rates = new ModelRates(4, 20, 5, 0.4) { Fast = new ModelRates(8, 40, 10, 0.8) };
            var tokens = new TokenBreakdown { Input = 1_000_000, Output = 1_000_000 };

            Assert.Equal(24, rates.ForSpeed(tokens.IsFast).CalculateCostDollars(tokens), 6);
            tokens.IsFast = true;
            Assert.Equal(48, rates.ForSpeed(tokens.IsFast).CalculateCostDollars(tokens), 6);
        }

        [Theory]
        [InlineData("gpt-5.6-sol", 4.0, 20.0, 0.4)]
        [InlineData("gpt-5.6-terra", 2.0, 12.0, 0.2)]
        [InlineData("gpt-5.6-luna", 0.2, 1.2, 0.02)]
        public void Gpt56Models_UseLiteLlmRates(string model, double input, double output, double cacheRead)
        {
            var rates = PricingEngine.Resolve(model);

            Assert.NotNull(rates);
            Assert.Equal(input, rates.InputPerMillion, 6);
            Assert.Equal(output, rates.OutputPerMillion, 6);
            Assert.Equal(cacheRead, rates.CacheReadPerMillion, 6);
        }

        [Fact]
        public void Gpt56FastAlias_UsesPriorityTier()
        {
            var rates = PricingEngine.Resolve("gpt-5.6-sol-high-fast");

            Assert.NotNull(rates);
            Assert.Equal(8, rates.InputPerMillion, 6);
            Assert.Equal(40, rates.OutputPerMillion, 6);
        }

        [Fact]
        public void Glm52Rates_AreDollarsPerMillionRatherThanDollarsPerToken()
        {
            var rates = PricingEngine.Resolve("GLM-5.2");

            Assert.NotNull(rates);
            Assert.InRange(rates.InputPerMillion, 0.01, 10);
            Assert.InRange(rates.OutputPerMillion, 0.01, 20);
        }
    }
}
