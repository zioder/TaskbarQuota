using System;
using System.Collections.Generic;
using System.Text.Json;
using TaskbarQuota.Usage;
using TaskbarQuota.Usage.Providers;

namespace TaskbarQuota.Tests;

public class GrokProviderTests
{
    [Fact]
    public void ReadCredentials_PrefersOidcEntryOverLegacySession()
    {
        const string json = """
        {
          "https://accounts.x.ai/sign-in::old": { "key": "legacy-token", "email": "legacy@example.com" },
          "https://auth.x.ai::client-id": { "key": "oidc-token", "email": "user@example.com", "team_id": "team-1", "auth_mode": "oidc" }
        }
        """;
        using var doc = JsonDocument.Parse(json);

        var creds = GrokProvider.ReadCredentials(doc.RootElement);

        Assert.Equal("oidc-token", creds.AccessToken);
        Assert.Equal("user@example.com", creds.Email);
        Assert.Equal("team-1", creds.TeamId);
        Assert.Equal("oidc", creds.AuthMode);
    }

    [Fact]
    public void ReadCredentials_SkipsEntriesWithoutAKey()
    {
        const string json = """
        {
          "https://auth.x.ai::stale": { "email": "no-token@example.com" },
          "https://accounts.x.ai/sign-in": { "key": "session-token", "email": "session@example.com", "auth_mode": "session" }
        }
        """;
        using var doc = JsonDocument.Parse(json);

        var creds = GrokProvider.ReadCredentials(doc.RootElement);

        Assert.Equal("session-token", creds.AccessToken);
        Assert.Equal("session@example.com", creds.Email);
    }

    [Fact]
    public void ParseCreditsResponse_ExtractsPercentAndPreferredReset()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_780_000_000);
        long periodStart = 1_779_000_000; // in the past relative to now -> ignored
        long resetAt = 1_790_000_000;     // future -> selected

        // Message: field1 { field1=fixed32(0.1), field4{field1=periodStart}, field5{field1=resetAt} }
        var inner = new List<byte>();
        inner.AddRange(Fixed32(1, 0.1f));
        inner.AddRange(LenDelim(4, Varint(1, (ulong)periodStart)));
        inner.AddRange(LenDelim(5, Varint(1, (ulong)resetAt)));
        var message = LenDelim(1, inner);
        var framed = DataFrame(message);

        var snapshot = GrokProvider.ParseCreditsResponse(framed.ToArray(), now);

        Assert.Equal(0.1, snapshot.UsedPercent, 3);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(resetAt), snapshot.ResetAt);
    }

    [Theory]
    [InlineData("SUBSCRIPTION_TIER_GROK_PRO", "SuperGrok")]
    [InlineData("SUBSCRIPTION_TIER_GROK_HEAVY", "SuperGrok Heavy")]
    [InlineData("SUBSCRIPTION_TIER_GROK_BASIC", "Basic")]
    public void PlanFromSubscriptions_MapsTier(string tier, string expected)
    {
        string json = $$"""
        { "subscriptions": [ { "tier": "{{tier}}", "status": "SUBSCRIPTION_STATUS_ACTIVE" } ] }
        """;
        using var doc = JsonDocument.Parse(json);

        Assert.Equal(expected, GrokProvider.PlanFromSubscriptions(doc.RootElement));
    }

    [Fact]
    public void PlanFromSubscriptions_AppendsTrialSuffix()
    {
        const string json = """
        { "subscriptions": [ {
            "tier": "SUBSCRIPTION_TIER_GROK_PRO",
            "status": "SUBSCRIPTION_STATUS_ACTIVE",
            "activeOffer": { "type": "ACTIVE_OFFER_FREE_TRIAL" }
        } ] }
        """;
        using var doc = JsonDocument.Parse(json);

        Assert.Equal("SuperGrok (Trial)", GrokProvider.PlanFromSubscriptions(doc.RootElement));
    }

    // --- protobuf / gRPC-web encoding helpers (mirror of the decoder under test) ---

    private static List<byte> Varint(int field, ulong value)
    {
        var bytes = new List<byte> { (byte)((field << 3) | 0) };
        bytes.AddRange(EncodeVarint(value));
        return bytes;
    }

    private static List<byte> Fixed32(int field, float value)
    {
        var bytes = new List<byte> { (byte)((field << 3) | 5) };
        bytes.AddRange(BitConverter.GetBytes(value));
        return bytes;
    }

    private static List<byte> LenDelim(int field, List<byte> payload)
    {
        var bytes = new List<byte> { (byte)((field << 3) | 2) };
        bytes.AddRange(EncodeVarint((ulong)payload.Count));
        bytes.AddRange(payload);
        return bytes;
    }

    private static IEnumerable<byte> EncodeVarint(ulong value)
    {
        do
        {
            byte b = (byte)(value & 0x7F);
            value >>= 7;
            if (value != 0) b |= 0x80;
            yield return b;
        } while (value != 0);
    }

    private static List<byte> DataFrame(List<byte> message)
    {
        int len = message.Count;
        return new List<byte>
        {
            0x00,
            (byte)((len >> 24) & 0xFF), (byte)((len >> 16) & 0xFF), (byte)((len >> 8) & 0xFF), (byte)(len & 0xFF),
        }.Concat(message);
    }
}

file static class GrokTestExtensions
{
    public static List<byte> Concat(this List<byte> head, List<byte> tail)
    {
        head.AddRange(tail);
        return head;
    }
}
