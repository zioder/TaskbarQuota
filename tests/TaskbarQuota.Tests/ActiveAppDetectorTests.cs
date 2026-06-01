using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using TaskbarQuota.ActiveApp;
using TaskbarQuota.Usage;

namespace TaskbarQuota.Tests;

public class ActiveAppDetectorTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string? _originalXdgStateHome;
    private readonly string? _originalXdgDataHome;
    private readonly string? _originalXdgConfigHome;

    public ActiveAppDetectorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"TaskbarQuotaTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _originalXdgStateHome = Environment.GetEnvironmentVariable("XDG_STATE_HOME");
        _originalXdgDataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        _originalXdgConfigHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        Environment.SetEnvironmentVariable("XDG_STATE_HOME", _tempDir);
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", _tempDir);
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _tempDir);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("XDG_STATE_HOME", _originalXdgStateHome);
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", _originalXdgDataHome);
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _originalXdgConfigHome);
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private void WriteModelState(object data)
    {
        var dir = Path.Combine(_tempDir, "opencode");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "model.json");
        File.WriteAllText(path, JsonSerializer.Serialize(data));
    }

    [Fact]
    public void DetectOpenCodeProviderFromModelState_WithOpenCodeGo_ReturnsOpenCodeGo()
    {
        WriteModelState(new { recent = new[] { new { providerID = "opencode-go", modelID = "claude-3-5-sonnet" } } });
        var result = ActiveAppDetector.DetectOpenCodeProviderFromModelState();
        Assert.Equal(ProviderId.OpenCodeGo, result);
    }

    [Fact]
    public void DetectOpenCodeProviderFromModelState_WithOpenCode_ReturnsOpenCode()
    {
        WriteModelState(new { recent = new[] { new { providerID = "opencode", modelID = "gpt-4" } } });
        var result = ActiveAppDetector.DetectOpenCodeProviderFromModelState();
        Assert.Equal(ProviderId.OpenCode, result);
    }

    [Fact]
    public void DetectOpenCodeProviderFromModelState_WithUnknownProvider_ReturnsNull()
    {
        WriteModelState(new { recent = new[] { new { providerID = "anthropic", modelID = "claude-3" } } });
        var result = ActiveAppDetector.DetectOpenCodeProviderFromModelState();
        Assert.Null(result);
    }

    [Fact]
    public void DetectOpenCodeProviderFromModelState_WithNoFile_ReturnsNull()
    {
        var result = ActiveAppDetector.DetectOpenCodeProviderFromModelState();
        Assert.Null(result);
    }

    [Fact]
    public void DetectOpenCodeProviderFromModelState_WithEmptyRecent_ReturnsNull()
    {
        WriteModelState(new { recent = Array.Empty<object>() });
        var result = ActiveAppDetector.DetectOpenCodeProviderFromModelState();
        Assert.Null(result);
    }

    [Fact]
    public void DetectOpenCodeProviderFromModelState_WithNoRecentProperty_ReturnsNull()
    {
        WriteModelState(new { current = new { providerID = "opencode-go" } });
        var result = ActiveAppDetector.DetectOpenCodeProviderFromModelState();
        Assert.Null(result);
    }

    [Fact]
    public void DetectOpenCodeProviderFromModelState_WithInvalidJson_ReturnsNull()
    {
        var dir = Path.Combine(_tempDir, "opencode");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "model.json"), "not valid json {{{");
        var result = ActiveAppDetector.DetectOpenCodeProviderFromModelState();
        Assert.Null(result);
    }

    [Fact]
    public void TryDetectCliProvider_WhenCommandMentionsClaudeAndCodex_PrefersClaude()
    {
        var result = ActiveAppDetector.TryDetectCliProvider(
            "node.exe",
            @"node C:\Users\me\AppData\Roaming\npm\node_modules\@anthropic-ai\claude-code\cli.js --project C:\src\codex-tools");

        Assert.Equal(ProviderId.Claude, result);
    }

    [Fact]
    public void TryDetectCliProvider_DoesNotMatchCodexInsideLongerWord()
    {
        var result = ActiveAppDetector.TryDetectCliProvider(
            "node.exe",
            @"node C:\tools\codexbar\scripts\status.js");

        Assert.Null(result);
    }

    [Theory]
    [InlineData("antigravity")]
    [InlineData("claude")]
    [InlineData("codex")]
    [InlineData("cursor")]
    [InlineData("opencode")]
    [InlineData("opencode beta")]
    [InlineData("opencode-beta")]
    public void IsKnownGuiProcessName_KnownProcesses_ReturnsTrue(string processName)
    {
        Assert.True(ActiveAppDetector.IsKnownGuiProcessName(processName));
    }

    [Theory]
    [InlineData("code", ProviderId.Copilot)]
    [InlineData("code-insiders", ProviderId.Copilot)]
    public void TryResolveGuiProcess_VsCode_ReturnsCopilot(string processName, ProviderId expected)
    {
        Assert.Equal(expected, ActiveAppDetector.TryResolveGuiProcess(processName));
    }

    [Theory]
    [InlineData("code")]
    [InlineData("code-insiders")]
    public void IsKnownGuiProcessName_VsCode_ReturnsTrue(string processName)
    {
        Assert.True(ActiveAppDetector.IsKnownGuiProcessName(processName));
    }

    [Theory]
    [InlineData("antigravity.exe", @"C:\Program Files\Antigravity\bin\antigravity.exe", ProviderId.Antigravity)]
    [InlineData("node.exe", @"node C:\Users\me\AppData\Roaming\npm\node_modules\@anthropic-ai\claude-code\cli.js", ProviderId.Claude)]
    [InlineData("claude.exe", @"C:\Users\me\AppData\Roaming\npm\claude.cmd", ProviderId.Claude)]
    [InlineData("codex.exe", @"C:\Users\me\AppData\Roaming\npm\codex.cmd", ProviderId.Codex)]
    [InlineData("node.exe", @"node C:\Users\me\AppData\Roaming\npm\node_modules\@openai\codex\bin\codex.js", ProviderId.Codex)]
    [InlineData("cursor-agent.exe", @"C:\Users\me\AppData\Local\Programs\Cursor\resources\app\bin\cursor-agent.exe", ProviderId.Cursor)]
    [InlineData("cursor.exe", @"C:\Users\me\AppData\Local\Programs\Cursor\Cursor.exe --cli", ProviderId.Cursor)]
    [InlineData("node.exe", @"node C:\Users\me\AppData\Local\Programs\Cursor\resources\app\out\cli.js", ProviderId.Cursor)]
    [InlineData("opencode.exe", @"C:\Users\me\AppData\Roaming\npm\opencode.cmd", ProviderId.OpenCode)]
    [InlineData("node.exe", @"node C:\Users\me\AppData\Roaming\npm\node_modules\opencode\bin\opencode.js", ProviderId.OpenCode)]
    public void TryDetectCliProvider_KnownCliCommands_ReturnsProvider(string processName, string commandLine, ProviderId expected)
    {
        var result = ActiveAppDetector.TryDetectCliProvider(processName, commandLine);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void DetectOpenCodeProviderFromModelState_ReturnsFirstKnownProvider()
    {
        WriteModelState(new
        {
            recent = new[]
            {
                new { providerID = "opencode", modelID = "gpt-4" },
                new { providerID = "opencode-go", modelID = "claude-3-5-sonnet" }
            }
        });
        var result = ActiveAppDetector.DetectOpenCodeProviderFromModelState();
        Assert.Equal(ProviderId.OpenCode, result);
    }

    [Fact]
    public void DetectOpenCodeProviderFromModelState_ReturnsGoWhenFirst()
    {
        WriteModelState(new
        {
            recent = new[]
            {
                new { providerID = "opencode-go", modelID = "claude-3-5-sonnet" },
                new { providerID = "opencode", modelID = "gpt-4" }
            }
        });
        var result = ActiveAppDetector.DetectOpenCodeProviderFromModelState();
        Assert.Equal(ProviderId.OpenCodeGo, result);
    }

    [Fact]
    public void DetectOpenCodeProviderFromModelState_CaseInsensitiveProviderId()
    {
        WriteModelState(new { recent = new[] { new { providerID = "OpenCode-Go", modelID = "test" } } });
        var result = ActiveAppDetector.DetectOpenCodeProviderFromModelState();
        Assert.Equal(ProviderId.OpenCodeGo, result);
    }

    private void WriteDesktopWorkspace(string providerId, string modelId)
    {
        var desktopDir = Path.Combine(_tempDir, "ai.opencode.desktop");
        Directory.CreateDirectory(desktopDir);
        var datPath = Path.Combine(desktopDir, "opencode.workspace.test.abc123.dat");
        var selection = new
        {
            draft = new { agent = "build", model = new { providerID = providerId, modelID = modelId }, variant = (string?)null }
        };
        var content = new { workspace_model_selection = JsonSerializer.Serialize(selection) };
        var json = JsonSerializer.Serialize(content).Replace("workspace_model_selection", "workspace:model-selection");
        File.WriteAllText(datPath, json);
    }

    private void WriteDesktopGlobal(string firstProviderId, bool beta = false)
    {
        var desktopDir = Path.Combine(_tempDir, beta ? "ai.opencode.desktop.beta" : "ai.opencode.desktop");
        Directory.CreateDirectory(desktopDir);
        var globalPath = Path.Combine(desktopDir, "opencode.global.dat");
        var modelData = new
        {
            recent = new[] { new { providerID = firstProviderId, modelID = "test-model" } }
        };
        var global = new { model = JsonSerializer.Serialize(modelData) };
        File.WriteAllText(globalPath, JsonSerializer.Serialize(global));
    }

    [Fact]
    public void DetectOpenCodeProviderFromModelState_DesktopGlobalGo_ReturnsOpenCodeGo()
    {
        WriteDesktopGlobal("opencode-go");
        var result = ActiveAppDetector.DetectOpenCodeProviderFromModelState();
        Assert.Equal(ProviderId.OpenCodeGo, result);
    }

    [Fact]
    public void DetectOpenCodeProviderFromModelState_DesktopGlobalZen_ReturnsOpenCode()
    {
        WriteDesktopGlobal("opencode");
        var result = ActiveAppDetector.DetectOpenCodeProviderFromModelState();
        Assert.Equal(ProviderId.OpenCode, result);
    }

    [Fact]
    public void DetectOpenCodeProviderFromModelState_DesktopGlobalTakesPrecedenceOverWorkspace()
    {
        WriteDesktopWorkspace("opencode-go", "qwen3.6-plus");
        System.Threading.Thread.Sleep(10);
        WriteDesktopGlobal("opencode");
        var result = ActiveAppDetector.DetectOpenCodeProviderFromModelState();
        Assert.Equal(ProviderId.OpenCode, result);
    }

    [Fact]
    public void DetectOpenCodeProviderFromModelState_PrefersMostRecentSource()
    {
        WriteDesktopGlobal("opencode-go");
        System.Threading.Thread.Sleep(10);
        WriteModelState(new { recent = new[] { new { providerID = "opencode", modelID = "gpt-4" } } });
        var result = ActiveAppDetector.DetectOpenCodeProviderFromModelState();
        Assert.Equal(ProviderId.OpenCode, result);
    }

    [Fact]
    public void DetectOpenCodeProviderFromModelState_GlobalMoreRecentThanTui()
    {
        WriteModelState(new { recent = new[] { new { providerID = "opencode", modelID = "gpt-4" } } });
        System.Threading.Thread.Sleep(10);
        WriteDesktopGlobal("opencode-go");
        var result = ActiveAppDetector.DetectOpenCodeProviderFromModelState();
        Assert.Equal(ProviderId.OpenCodeGo, result);
    }

    [Fact]
    public void DetectOpenCodeProviderFromModelState_BetaGlobalDetected()
    {
        WriteDesktopGlobal("opencode", beta: true);
        var result = ActiveAppDetector.DetectOpenCodeProviderFromModelState();
        Assert.Equal(ProviderId.OpenCode, result);
    }

    [Fact]
    public void DetectOpenCodeProviderFromModelState_BetaGlobalMoreRecentThanStable()
    {
        WriteDesktopGlobal("opencode-go", beta: false);
        System.Threading.Thread.Sleep(10);
        WriteDesktopGlobal("opencode", beta: true);
        var result = ActiveAppDetector.DetectOpenCodeProviderFromModelState();
        Assert.Equal(ProviderId.OpenCode, result);
    }

    [Fact]
    public void DetectOpenCodeProviderFromModelState_DesktopAppGo_ReturnsOpenCodeGo()
    {
        WriteDesktopWorkspace("opencode-go", "qwen3.6-plus");
        var result = ActiveAppDetector.DetectOpenCodeProviderFromModelState();
        Assert.Equal(ProviderId.OpenCodeGo, result);
    }

    [Fact]
    public void DetectOpenCodeProviderFromModelState_DesktopAppZen_ReturnsOpenCode()
    {
        WriteDesktopWorkspace("opencode", "gpt-5.4");
        var result = ActiveAppDetector.DetectOpenCodeProviderFromModelState();
        Assert.Equal(ProviderId.OpenCode, result);
    }

    [Fact]
    public void DetectOpenCodeProviderFromModelState_DesktopAppTakesPrecedenceOverTui()
    {
        WriteModelState(new { recent = new[] { new { providerID = "opencode", modelID = "gpt-4" } } });
        System.Threading.Thread.Sleep(10);
        WriteDesktopWorkspace("opencode-go", "qwen3.6-plus");
        var result = ActiveAppDetector.DetectOpenCodeProviderFromModelState();
        Assert.Equal(ProviderId.OpenCodeGo, result);
    }

    [Fact]
    public void GetOpenCodeModelStateWriteUtc_ReflectsLatestModelFileChange()
    {
        WriteModelState(new { recent = new[] { new { providerID = "opencode", modelID = "gpt-4" } } });
        var first = ActiveAppDetector.GetOpenCodeModelStateWriteUtc();
        Assert.NotEqual(default, first);

        System.Threading.Thread.Sleep(10);
        WriteModelState(new { recent = new[] { new { providerID = "opencode-go", modelID = "qwen3.6-plus" } } });
        var second = ActiveAppDetector.GetOpenCodeModelStateWriteUtc();

        Assert.True(second > first);
        Assert.Equal(ProviderId.OpenCodeGo, ActiveAppDetector.DetectOpenCodeProviderFromModelState());
    }

    [Theory]
    [InlineData(@"C:\Users\me\.local\state\opencode\model.json", true)]
    [InlineData(@"C:\Users\me\AppData\Roaming\ai.opencode.desktop\opencode.global.dat", true)]
    [InlineData(@"C:\Users\me\AppData\Roaming\ai.opencode.desktop\opencode.workspace.abc.dat", true)]
    [InlineData(@"C:\Users\me\AppData\Roaming\ai.opencode.desktop\other.dat", false)]
    [InlineData(@"C:\Users\me\.local\state\other\model.json", false)]
    public void IsOpenCodeModelStatePath_MatchesExpectedFiles(string path, bool expected)
        => Assert.Equal(expected, ActiveAppDetector.IsOpenCodeModelStatePath(path));

    [Fact]
    public void OpenCodeModelStateWatcher_NotifiesWhenModelJsonChanges()
    {
        var signaled = new ManualResetEventSlim(false);
        var detector = new ActiveAppDetector();
        detector.OpenCodeModelStateChanged += () => signaled.Set();
        detector.StartOpenCodeModelStateWatcher();

        WriteModelState(new { recent = new[] { new { providerID = "opencode", modelID = "gpt-4" } } });
        System.Threading.Thread.Sleep(10);
        WriteModelState(new { recent = new[] { new { providerID = "opencode-go", modelID = "qwen3.6-plus" } } });

        Assert.True(signaled.Wait(TimeSpan.FromSeconds(3)));
    }
}
