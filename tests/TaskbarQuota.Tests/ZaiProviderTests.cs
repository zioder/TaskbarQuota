using System;
using System.Text.Json;
using TaskbarQuota.Usage;
using TaskbarQuota.Usage.Providers;

namespace TaskbarQuota.Tests;

public class ZaiProviderTests
{
    [Fact]
    public void BuildResult_ParsesTokenLimitWithPlanName()
    {
        const string json = """
        {
          "code": 200,
          "success": true,
          "msg": "ok",
          "data": {
            "planName": "GLM Coding Pro",
            "limits": [
              {
                "type": "TOKENS_LIMIT",
                "unit": 3,
                "number": 30,
                "usage": 500000,
                "currentValue": 120000,
                "remaining": 380000,
                "percentage": 24
              }
            ]
          }
        }
        """;
        using var doc = JsonDocument.Parse(json);
        var result = ZaiProvider.BuildResult(doc.RootElement);
        Assert.Equal("api", result.SourceLabel);
        Assert.Equal("GLM Coding Pro", result.Usage.LoginMethod);
        Assert.InRange(result.Usage.Primary.UsedPercent, 23, 25);
        Assert.Equal(30 * 60, result.Usage.Primary.WindowMinutes);
    }

    [Fact]
    public void BuildResult_ParsesDualTokenLimits_ShortAndLong()
    {
        const string json = """
        {
          "code": 200,
          "success": true,
          "data": {
            "planName": "GLM Coding Max",
            "limits": [
              {
                "type": "TOKENS_LIMIT",
                "unit": 3,
                "number": 5,
                "usage": 100000,
                "currentValue": 40000,
                "remaining": 60000,
                "percentage": 40
              },
              {
                "type": "TOKENS_LIMIT",
                "unit": 1,
                "number": 30,
                "usage": 1000000,
                "currentValue": 200000,
                "remaining": 800000,
                "percentage": 20
              }
            ]
          }
        }
        """;
        using var doc = JsonDocument.Parse(json);
        var result = ZaiProvider.BuildResult(doc.RootElement);
        Assert.Equal(5 * 60, result.Usage.Primary.WindowMinutes);
        Assert.InRange(result.Usage.Primary.UsedPercent, 39, 41);
        Assert.NotNull(result.Usage.Secondary);
        Assert.Equal(30 * 24 * 60, result.Usage.Secondary!.WindowMinutes);
        Assert.InRange(result.Usage.Secondary.UsedPercent, 19, 21);
    }


    [Fact]
    public void BuildResult_FallsBackToTimeLimitWhenNoTokens()
    {
        const string json = """
        {
          "code": 200,
          "success": true,
          "data": {
            "planName": "Custom Plan",
            "limits": [
              {
                "type": "TIME_LIMIT",
                "unit": 3,
                "number": 5,
                "usage": 600,
                "currentValue": 100,
                "remaining": 500,
                "percentage": 17
              }
            ]
          }
        }
        """;
        using var doc = JsonDocument.Parse(json);
        var result = ZaiProvider.BuildResult(doc.RootElement);
        Assert.Equal(5 * 60, result.Usage.Primary.WindowMinutes);
        Assert.InRange(result.Usage.Primary.UsedPercent, 16, 18);
        Assert.Null(result.Usage.Secondary);
    }

    [Fact]
    public void BuildResult_UsesPercentageWhenNoUsageField()
    {
        const string json = """
        {
          "code": 200,
          "success": true,
          "data": {
            "planName": "GLM Coding Pro",
            "limits": [
              {
                "type": "TOKENS_LIMIT",
                "unit": 3,
                "number": 5,
                "percentage": 55
              }
            ]
          }
        }
        """;
        using var doc = JsonDocument.Parse(json);
        var result = ZaiProvider.BuildResult(doc.RootElement);
        Assert.Equal(55, result.Usage.Primary.UsedPercent, 0);
    }

    [Fact]
    public void BuildResult_ThrowsOnApiError()
    {
        const string json = """{ "code": 401, "success": false, "msg": "Invalid API key" }""";
        using var doc = JsonDocument.Parse(json);
        var ex = Assert.Throws<ProviderException>(() => ZaiProvider.BuildResult(doc.RootElement));
        Assert.Equal(ProviderErrorKind.Other, ex.Kind);
        Assert.Contains("Invalid API key", ex.Message);
    }

    [Fact]
    public void BuildResult_HandlesNoLimits()
    {
        const string json = """
        { "code": 200, "success": true, "data": { "planName": "Free" } }
        """;
        using var doc = JsonDocument.Parse(json);
        var result = ZaiProvider.BuildResult(doc.RootElement);
        Assert.Equal(0, result.Usage.Primary.UsedPercent);
        Assert.Null(result.Usage.Secondary);
        Assert.Equal("Free", result.Usage.LoginMethod);
    }

    [Fact]
    public void BuildResult_SetsDashboardUrl()
    {
        const string json = """
        { "code": 200, "success": true, "data": { "planName": "GLM Coding Lite", "limits": [] } }
        """;
        using var doc = JsonDocument.Parse(json);
        var result = ZaiProvider.BuildResult(doc.RootElement);
        Assert.Equal("https://z.ai/manage-apikey/coding-plan/personal/my-plan", result.Usage.UsageDashboardUrl);
    }

    [Fact]
    public void BuildResult_ParsesPlanFromAlternateFieldNames()
    {
        const string json = """
        { "code": 200, "success": true, "data": { "plan": "GLM Coding Max", "limits": [] } }
        """;
        using var doc = JsonDocument.Parse(json);
        var result = ZaiProvider.BuildResult(doc.RootElement);
        Assert.Equal("GLM Coding Max", result.Usage.LoginMethod);
    }

    [Fact]
    public void BuildResult_ComputesUsedPercentFromRemaining()
    {
        const string json = """
        {
          "code": 200,
          "success": true,
          "data": {
            "limits": [
              {
                "type": "TOKENS_LIMIT",
                "unit": 3,
                "number": 5,
                "usage": 1000,
                "remaining": 750,
                "percentage": 0
              }
            ]
          }
        }
        """;
        using var doc = JsonDocument.Parse(json);
        var result = ZaiProvider.BuildResult(doc.RootElement);
        Assert.Equal(25, result.Usage.Primary.UsedPercent, 0);
    }

    [Fact]
    public void BuildResult_ThrowsOnNoCodingPlan()
    {
        // Real Z.ai API response when user has no active coding plan
        const string json = """
        { "code": 500, "msg": "no active coding plan", "success": false }
        """;
        using var doc = JsonDocument.Parse(json);
        var ex = Assert.Throws<ProviderException>(() => ZaiProvider.BuildResult(doc.RootElement));
        Assert.Equal(ProviderErrorKind.Other, ex.Kind);
        Assert.Contains("no active coding plan", ex.Message);
    }

    [Fact]
    public void ReadApiKeyFromZCodeConfig_FindsKeyInConfig()
    {
        // This test verifies the method can parse the ZCode config format
        var tempDir = Path.Combine(Path.GetTempPath(), $"zcode-test-{Guid.NewGuid():N}");
        var configDir = Path.Combine(tempDir, ".zcode", "v2");
        Directory.CreateDirectory(configDir);

        try
        {
            var configJson = """
            {
              "provider": {
                "builtin:zai-coding-plan": {
                  "options": {
                    "apiKey": "test-key-12345",
                    "baseURL": "https://api.z.ai/api/anthropic"
                  }
                }
              }
            }
            """;
            File.WriteAllText(Path.Combine(configDir, "config.json"), configJson);

            var configPath = Path.Combine(configDir, "config.json");
            Assert.True(File.Exists(configPath));
            Assert.Equal("test-key-12345", ZaiProvider.TryLoadApiKeyFromZCodeConfig(tempDir));
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }
}
