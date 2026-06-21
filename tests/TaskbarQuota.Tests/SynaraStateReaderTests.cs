using TaskbarQuota.ActiveApp;
using TaskbarQuota.Usage;
using Xunit;

namespace TaskbarQuota.Tests;

public class SynaraStateReaderTests
{
    [Theory]
    [InlineData("codex", null, ProviderId.Codex)]
    [InlineData("claudeAgent", null, ProviderId.Claude)]
    [InlineData("cursor", null, ProviderId.Cursor)]
    [InlineData("grok", null, ProviderId.Grok)]
    [InlineData("opencode", "openai/gpt-5", ProviderId.OpenCode)]
    [InlineData("opencode", "opencode-go/kimi-k2.6", ProviderId.OpenCodeGo)]
    public void MapProvider_maps_supported_providers(string literal, string? model, ProviderId expected)
    {
        Assert.Equal(expected, SynaraStateReader.MapProvider(literal, model));
    }

    [Theory]
    [InlineData("gemini")]
    [InlineData("kilo")]
    [InlineData("pi")]
    [InlineData("unknown")]
    [InlineData("")]
    [InlineData(null)]
    public void MapProvider_returns_null_for_unsupported(string? literal)
    {
        Assert.Null(SynaraStateReader.MapProvider(literal, null));
    }

    [Theory]
    [InlineData("Cursor · GPT-5.5", ProviderId.Cursor, "GPT-5.5")]
    [InlineData("OpenCode Go · Deepseek V4 Flash", ProviderId.OpenCodeGo, "Deepseek V4 Flash")]
    [InlineData("Codex · GPT-5.5 · Medium", ProviderId.Codex, "GPT-5.5")]
    public void SynaraModelClassifier_reads_labelled_provider_names(
        string name,
        ProviderId expectedProvider,
        string expectedModel)
    {
        var classified = SynaraModelClassifier.Classify(name);

        Assert.NotNull(classified);
        Assert.Equal(expectedProvider, classified!.Provider);
        Assert.Equal(expectedModel, classified.ModelDisplayName);
    }

    [Theory]
    [InlineData("GPT-5.5")]
    [InlineData("Claude Sonnet 4.6")]
    [InlineData("Grok 4")]
    public void SynaraModelClassifier_does_not_guess_from_bare_model_names(string name)
    {
        Assert.Null(SynaraModelClassifier.Classify(name));
    }

    [Fact]
    public void ParseSelection_reads_provider_model_and_title()
    {
        var json = "{\"provider\":\"opencode\",\"model\":\"opencode-go/kimi-k2.6\"}";
        var selection = SynaraStateReader.ParseSelection(json, "Scroll freeze after response");

        Assert.NotNull(selection);
        Assert.Equal(ProviderId.OpenCodeGo, selection!.Provider);
        Assert.Equal("opencode", selection.ProviderLiteral);
        Assert.Equal("opencode-go/kimi-k2.6", selection.Model);
        Assert.Equal("Scroll freeze after response", selection.ThreadTitle);
    }

    [Fact]
    public void ParseSelection_reads_t3code_instanceId_shape()
    {
        // Upstream T3 Code stores the provider under "instanceId" (+ an options array), not "provider".
        var json = "{\"instanceId\":\"opencode\",\"model\":\"opencode-go/deepseek-v4-pro\",\"options\":[{\"id\":\"agent\",\"value\":\"build\"}]}";
        var selection = SynaraStateReader.ParseSelection(json, "Run project from current path");

        Assert.NotNull(selection);
        Assert.Equal(ProviderId.OpenCodeGo, selection!.Provider);
        Assert.Equal("opencode", selection.ProviderLiteral);
        Assert.Equal("opencode-go/deepseek-v4-pro", selection.Model);
    }

    [Fact]
    public void ParseSelection_reads_t3code_codex_instanceId()
    {
        var json = "{\"instanceId\":\"codex\",\"model\":\"gpt-5.5\",\"options\":[{\"id\":\"reasoningEffort\",\"value\":\"medium\"}]}";
        var selection = SynaraStateReader.ParseSelection(json, null);

        Assert.NotNull(selection);
        Assert.Equal(ProviderId.Codex, selection!.Provider);
        Assert.Equal("gpt-5.5", selection.Model);
    }

    [Fact]
    public void ParseSelection_returns_null_for_unsupported_provider()
    {
        Assert.Null(SynaraStateReader.ParseSelection("{\"provider\":\"gemini\",\"model\":\"gemini-2.5-pro\"}", "t"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("[1,2,3]")]
    [InlineData("{\"model\":\"x\"}")]
    public void ParseSelection_returns_null_for_invalid_or_incomplete(string? json)
    {
        Assert.Null(SynaraStateReader.ParseSelection(json, null));
    }

    [Theory]
    [InlineData("synara", true)]
    [InlineData("Synara", true)]
    [InlineData("Synara (Dev)", true)]
    [InlineData("synara-desktop", true)]
    [InlineData("dpcode", true)]
    [InlineData("dpcode-dev", true)]
    [InlineData("t3code", true)]
    [InlineData("t3code-dev", true)]
    [InlineData("cursor", false)]
    [InlineData(null, false)]
    public void IsSynaraProcessName_matches_synara_only(string? name, bool expected)
    {
        Assert.Equal(expected, SynaraStateReader.IsSynaraProcessName(name));
    }

    [Theory]
    [InlineData("synara", HostApp.Synara)]
    [InlineData("Synara (Dev)", HostApp.Synara)]
    [InlineData("dpcode", HostApp.Synara)]
    [InlineData("t3code", HostApp.T3Code)]
    [InlineData("t3code-dev", HostApp.T3Code)]
    [InlineData("T3 Code (Alpha)", HostApp.T3Code)]
    [InlineData("T3 Code (Nightly)", HostApp.T3Code)]
    [InlineData("T3 Code (Dev)", HostApp.T3Code)]
    public void ResolveHost_maps_process_names_to_host(string name, HostApp expected)
    {
        Assert.Equal(expected, SynaraStateReader.ResolveHost(name));
    }

    [Theory]
    [InlineData("cursor")]
    [InlineData("code")]
    [InlineData("")]
    [InlineData(null)]
    public void ResolveHost_returns_null_for_other_processes(string? name)
    {
        Assert.Null(SynaraStateReader.ResolveHost(name));
    }

    private const string DraftBlob =
        "{\"state\":{\"draftsByThreadId\":{" +
        "\"t-codex\":{\"prompt\":\"\",\"activeProvider\":\"codex\",\"modelSelectionByProvider\":{\"codex\":{\"provider\":\"codex\",\"model\":\"gpt-5.5\"}}}," +
        "\"t-ocgo\":{\"prompt\":\"\",\"activeProvider\":\"opencode\",\"modelSelectionByProvider\":{\"opencode\":{\"provider\":\"opencode\",\"model\":\"opencode-go/kimi-k2.6\"}}}," +
        "\"t-gemini\":{\"activeProvider\":\"gemini\",\"modelSelectionByProvider\":{\"gemini\":{\"provider\":\"gemini\",\"model\":\"gemini-2.5-pro\"}}}" +
        "}},\"version\":0}";

    [Fact]
    public void ParseDrafts_reads_active_provider_and_model_per_thread()
    {
        var drafts = SynaraComposerDraftReader.ParseDrafts(DraftBlob);

        Assert.NotNull(drafts);
        Assert.Equal("codex", drafts!["t-codex"].ProviderLiteral);
        Assert.Equal("gpt-5.5", drafts["t-codex"].Model);
        Assert.Equal("opencode", drafts["t-ocgo"].ProviderLiteral);
        Assert.Equal("opencode-go/kimi-k2.6", drafts["t-ocgo"].Model);
    }

    [Fact]
    public void ParseDrafts_handles_legacy_thread_key_shape()
    {
        var legacy = "{\"state\":{\"draftsByThreadKey\":{\"proj-1:t-abc\":" +
            "{\"activeProvider\":\"grok\",\"modelSelectionByProvider\":{\"grok\":{\"provider\":\"grok\",\"model\":\"grok-4\"}}}}},\"version\":0}";
        var drafts = SynaraComposerDraftReader.ParseDrafts(legacy);

        Assert.NotNull(drafts);
        Assert.True(drafts!.ContainsKey("t-abc"));
        Assert.Equal("grok", drafts["t-abc"].ProviderLiteral);
    }

    [Fact]
    public void ParseStickySelection_reads_visible_composer_provider_and_model()
    {
        var json = "{\"state\":{" +
            "\"stickyActiveProvider\":\"claudeAgent\"," +
            "\"stickyModelSelectionByProvider\":{\"claudeAgent\":{\"provider\":\"claudeAgent\",\"model\":\"claude-fable-5\"}}" +
            "},\"version\":4}";

        var sticky = SynaraComposerDraftReader.ParseStickySelection(json);

        Assert.NotNull(sticky);
        Assert.Equal("claudeAgent", sticky!.ProviderLiteral);
        Assert.Equal("claude-fable-5", sticky.Model);
    }

    [Fact]
    public void ParseStickySelection_uses_model_for_active_provider()
    {
        var json = "{\"state\":{" +
            "\"stickyActiveProvider\":\"cursor\"," +
            "\"stickyModelSelectionByProvider\":{" +
            "\"codex\":{\"provider\":\"codex\",\"model\":\"gpt-5.5\"}," +
            "\"cursor\":{\"provider\":\"cursor\",\"model\":\"cursor-auto\"}" +
            "}" +
            "},\"version\":4}";

        var sticky = SynaraComposerDraftReader.ParseStickySelection(json);

        Assert.NotNull(sticky);
        Assert.Equal("cursor", sticky!.ProviderLiteral);
        Assert.Equal("cursor-auto", sticky.Model);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("{\"state\":{}}")]
    [InlineData("{\"state\":{\"stickyModelSelectionByProvider\":{}}}")]
    public void ParseStickySelection_returns_null_when_provider_is_missing(string json)
    {
        Assert.Null(SynaraComposerDraftReader.ParseStickySelection(json));
    }

    [Fact]
    public void ParseFocusedThreadId_returns_first_thread_in_mru()
    {
        var recentViews = "{\"state\":{\"recentViews\":[" +
            "{\"kind\":\"settings\"}," +
            "{\"kind\":\"thread\",\"threadId\":\"focused-thread\"}," +
            "{\"kind\":\"thread\",\"threadId\":\"older-thread\"}" +
            "]},\"version\":0}";

        Assert.Equal("focused-thread", SynaraComposerDraftReader.ParseFocusedThreadId(recentViews));
    }

    [Fact]
    public void ParseFocusedThreadId_returns_null_when_no_thread_view()
    {
        Assert.Null(SynaraComposerDraftReader.ParseFocusedThreadId("{\"state\":{\"recentViews\":[{\"kind\":\"settings\"}]}}"));
        Assert.Null(SynaraComposerDraftReader.ParseFocusedThreadId("not json"));
    }

    [Fact]
    public void ResolveSelection_prefers_live_draft_over_persisted()
    {
        // No leveldb on the test box → draft lookup returns null → falls back to persisted selection.
        SynaraComposerDraftReader.ResetCacheForTesting();
        var persisted = "{\"provider\":\"opencode\",\"model\":\"openai/gpt-5\"}";
        var sel = SynaraStateReader.ResolveSelection("nonexistent-thread", persisted, "title");

        Assert.NotNull(sel);
        Assert.Equal(ProviderId.OpenCode, sel!.Provider);
    }
}
