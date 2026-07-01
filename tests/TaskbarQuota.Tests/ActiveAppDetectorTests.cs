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
    [InlineData("chrome", true)]
    [InlineData("msedge", true)]
    [InlineData("arc", true)]
    [InlineData("firefox", true)]
    [InlineData("zen", true)]
    [InlineData("brave", true)]
    [InlineData("vivaldi", true)]
    [InlineData("opera", true)]
    [InlineData("chromium", true)]
    [InlineData("slack", false)]
    [InlineData("codex", false)]
    public void BrowserActiveTabDetector_IsBrowserProcessName_KnownBrowsers(string processName, bool expected)
        => Assert.Equal(expected, BrowserActiveTabDetector.IsBrowserProcessName(processName));

    [Theory]
    [InlineData("https://chatgpt.com/", null)]
    [InlineData("chatgpt.com/c/abc", null)]
    [InlineData("https://chat.openai.com/", null)]
    [InlineData("https://claude.ai/new", ProviderId.Claude)]
    [InlineData("https://grok.com/chat/123", null)]
    [InlineData("https://aistudio.google.com/usage", null)]
    [InlineData("https://gemini.google.com/app", null)]
    [InlineData("https://example.com/", null)]
    public void BrowserActiveTabDetector_TryResolveProviderFromUrl_MapsChatSurfaces(string url, ProviderId? expected)
        => Assert.Equal(expected, BrowserActiveTabDetector.TryResolveProviderFromUrl(url));

    [Theory]
    [InlineData("ChatGPT — Zen Browser", null)]
    [InlineData("Claude — Zen Browser", ProviderId.Claude)]
    [InlineData("Grok — Zen Browser", null)]
    [InlineData("Git clone checkout error — Zen Browser", null)]
    public void BrowserActiveTabDetector_TryResolveProviderFromTitle_MapsKnownChatTitles(string title, ProviderId? expected)
        => Assert.Equal(expected, BrowserActiveTabDetector.TryResolveProviderFromTitle(title));

    [Theory]
    [InlineData("chrome", "Chrome")]
    [InlineData("msedge.exe", "Edge")]
    [InlineData("firefox", "Firefox")]
    public void BrowserActiveTabDetector_ResolveBrowserSource_UsesFriendlyName(string processName, string expected)
    {
        var source = BrowserActiveTabDetector.ResolveBrowserSource(processName);

        Assert.Equal(ProviderSourceKind.Browser, source.Kind);
        Assert.Equal(expected, source.DisplayName);
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
    [InlineData("grok.exe", @"C:\Users\me\.grok\bin\grok.exe", ProviderId.Grok)]
    [InlineData("antigravity.exe", @"C:\Program Files\Antigravity\bin\antigravity.exe", ProviderId.Antigravity)]
    [InlineData("agy.exe", @"C:\Users\me\AppData\Local\agy\bin\agy.exe", ProviderId.Antigravity)]
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
    public void ExpandTerminalSessionPids_ConhostForeground_IncludesSiblingGrok()
    {
        // Windows reports conhost as foreground while grok runs as a sibling under powershell.
        var parents = new Dictionary<int, int>
        {
            [101136] = 101192, // conhost -> powershell
            [116616] = 101192, // grok -> powershell
            [101192] = 9084,   // powershell -> explorer
        };
        var names = new Dictionary<int, string>
        {
            [101136] = "conhost",
            [101192] = "powershell",
            [116616] = "grok",
        };

        var session = ActiveAppDetector.ExpandTerminalSessionPids(101136, "conhost", parents, names);

        Assert.Contains(101136, session);
        Assert.Contains(101192, session);
        Assert.Contains(116616, session);
    }

    [Fact]
    public void IsInTerminalSession_GrokSiblingOfConhost_ReturnsTrue()
    {
        var parents = new Dictionary<int, int>
        {
            [101136] = 101192,
            [116616] = 101192,
            [101192] = 9084,
        };
        var names = new Dictionary<int, string>
        {
            [101136] = "conhost",
            [101192] = "powershell",
            [116616] = "grok",
        };
        var session = ActiveAppDetector.ExpandTerminalSessionPids(101136, "conhost", parents, names);

        Assert.True(ActiveAppDetector.IsInTerminalSession(116616, 101192, session, parents));
    }

    [Fact]
    public void IsInTerminalSession_GrokInOtherShell_ReturnsFalse()
    {
        var parents = new Dictionary<int, int>
        {
            [101136] = 101192,
            [116616] = 202202,
            [101192] = 9084,
            [202202] = 9084,
        };
        var names = new Dictionary<int, string>
        {
            [101136] = "conhost",
            [101192] = "powershell",
            [202202] = "powershell",
            [116616] = "grok",
        };
        var session = ActiveAppDetector.ExpandTerminalSessionPids(101136, "conhost", parents, names);

        Assert.False(ActiveAppDetector.IsInTerminalSession(116616, 202202, session, parents));
    }

    [Fact]
    public void ReadActiveGrokSessionPids_ReadsPidFromFile()
    {
        var grokDir = Path.Combine(_tempDir, ".grok");
        Directory.CreateDirectory(grokDir);
        var sessionsPath = Path.Combine(grokDir, "active_sessions.json");
        File.WriteAllText(sessionsPath, """
            [
              { "session_id": "abc", "pid": 4242, "cwd": "C:\\\\work", "opened_at": "2026-06-10T12:00:00Z" }
            ]
            """);

        Environment.SetEnvironmentVariable("GROK_HOME", grokDir);
        try
        {
            var pids = ActiveAppDetector.ReadActiveGrokSessionPids();
            Assert.Single(pids);
            Assert.Equal(4242, pids[0]);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GROK_HOME", null);
        }
    }

    [Fact]
    public void PickForegroundCli_PrefersCliAttachedToFocusedShell()
    {
        var parents = new Dictionary<int, int>
        {
            [500] = 400,
            [501] = 401,
            [400] = 300,
            [401] = 300,
            [300] = 100,
        };

        var candidates = new List<(ProviderId id, DateTime started, int pid, int parentPid)>
        {
            (ProviderId.Grok, new DateTime(2026, 6, 10, 12, 0, 0, DateTimeKind.Utc), 501, 401),
            (ProviderId.Claude, new DateTime(2026, 6, 10, 11, 0, 0, DateTimeKind.Utc), 500, 400),
        };

        var picked = ActiveAppDetector.PickForegroundCli(candidates, sessionRootPid: 400, parents);
        Assert.Equal(ProviderId.Claude, picked);
    }

    [Fact]
    public void ShouldReuseCliCacheState_InvalidatesWhenForegroundWindowChanges()
    {
        var hwndA = new IntPtr(1001);
        var hwndB = new IntPtr(1002);
        var ttl = TimeSpan.FromMilliseconds(250);
        var cachedAt = DateTime.UtcNow;

        Assert.True(ActiveAppDetector.ShouldReuseCliCacheState(
            hwndA, 900, 400, terminalFocused: true, openCodeModelChanged: false,
            ProviderId.Grok, hwndA, 900, 400, cachedAt, ttl));

        Assert.False(ActiveAppDetector.ShouldReuseCliCacheState(
            hwndB, 900, 400, terminalFocused: true, openCodeModelChanged: false,
            ProviderId.Grok, hwndA, 900, 400, cachedAt, ttl));
    }

    [Fact]
    public void ShouldReuseCliCacheState_InvalidatesWhenFocusedPaneChanges()
    {
        var hwnd = new IntPtr(1001);
        var ttl = TimeSpan.FromMilliseconds(250);
        var cachedAt = DateTime.UtcNow;

        Assert.False(ActiveAppDetector.ShouldReuseCliCacheState(
            hwnd, 900, 401, terminalFocused: true, openCodeModelChanged: false,
            ProviderId.Claude, hwnd, 900, 400, cachedAt, ttl));
    }

    [Fact]
    public void ShouldReuseCliCacheState_InvalidatesWhenInnerFocusHwndChanges()
    {
        var hwnd = new IntPtr(1001);
        var focusA = new IntPtr(2001);
        var focusB = new IntPtr(2002);
        var ttl = TimeSpan.FromMilliseconds(250);
        var cachedAt = DateTime.UtcNow;

        // Same top hwnd + session root, but different inner focus (tab switch inside WT) should miss cache.
        Assert.True(ActiveAppDetector.ShouldReuseCliCacheState(
            hwnd, 900, 400, terminalFocused: true, openCodeModelChanged: false,
            ProviderId.Grok, hwnd, 900, 400, cachedAt, ttl,
            focusHwnd: focusA, lastFocusHwnd: focusA));

        Assert.False(ActiveAppDetector.ShouldReuseCliCacheState(
            hwnd, 900, 400, terminalFocused: true, openCodeModelChanged: false,
            ProviderId.Grok, hwnd, 900, 400, cachedAt, ttl,
            focusHwnd: focusB, lastFocusHwnd: focusA));
    }

    [Fact]
    public void ExpandTerminalSessionPids_OpenConsoleForeground_OnlyIncludesFocusedTab()
    {
        var parents = new Dictionary<int, int>
        {
            [700] = 600,  // openconsole (grok tab)
            [701] = 601,  // openconsole (claude tab)
            [600] = 500,  // pwsh
            [601] = 500,  // pwsh
            [610] = 600,  // grok
            [611] = 601,  // claude
            [500] = 100,  // windowsterminal
        };
        var names = new Dictionary<int, string>
        {
            [700] = "openconsole",
            [701] = "openconsole",
            [600] = "pwsh",
            [601] = "pwsh",
            [610] = "grok",
            [611] = "claude",
            [500] = "windowsterminal",
        };

        var grokTab = ActiveAppDetector.ExpandTerminalSessionPids(700, "openconsole", parents, names);
        var claudeTab = ActiveAppDetector.ExpandTerminalSessionPids(701, "openconsole", parents, names);

        Assert.Contains(610, grokTab);
        Assert.DoesNotContain(611, grokTab);
        Assert.Contains(611, claudeTab);
        Assert.DoesNotContain(610, claudeTab);
    }

    [Fact]
    public void ExpandTerminalSessionPids_WindowsTerminalReparentedShell_UsesCreationTimeFallback()
    {
        var started = new DateTime(2026, 6, 11, 13, 15, 52, DateTimeKind.Local);
        var parents = new Dictionary<int, int>
        {
            [53960] = 2000, // WindowsTerminal
            [53916] = 2000, // OpenConsole sibling
            [53840] = 8836, // cmd reparented away from WindowsTerminal
            [53688] = 53840, // claude running inside cmd
            [37024] = 34604, // unrelated older cmd
            [37156] = 37024,
        };
        var names = new Dictionary<int, string>
        {
            [53960] = "WindowsTerminal",
            [53916] = "OpenConsole",
            [53840] = "cmd",
            [53688] = "claude",
            [37024] = "cmd",
            [37156] = "node",
        };
        var starts = new Dictionary<int, DateTime>
        {
            [53960] = started,
            [53916] = started,
            [53840] = started,
            [53688] = started.AddSeconds(18),
            [37024] = started.AddMinutes(-23),
            [37156] = started.AddMinutes(-23),
        };

        var session = ActiveAppDetector.ExpandTerminalSessionPids(53960, "WindowsTerminal", parents, names, starts);

        Assert.Contains(53840, session);
        Assert.Contains(53688, session);
        Assert.DoesNotContain(37024, session);
        Assert.DoesNotContain(37156, session);
    }

    [Fact]
    public void ExpandTerminalSessionPids_WindowsTerminalLaterPane_UsesOpenConsoleCreationTimeFallback()
    {
        var terminalStarted = new DateTime(2026, 6, 11, 13, 15, 52, DateTimeKind.Local);
        var devinPaneStarted = new DateTime(2026, 6, 11, 13, 22, 04, DateTimeKind.Local);
        var parents = new Dictionary<int, int>
        {
            [53960] = 2000, // WindowsTerminal
            [53488] = 2000, // OpenConsole for the later pane
            [59368] = 8836, // powershell reparented away from WindowsTerminal
            [54180] = 59368, // devin running inside powershell
            [37024] = 34604, // unrelated old shell
            [51952] = 13844, // unrelated Claude desktop child
        };
        var names = new Dictionary<int, string>
        {
            [53960] = "WindowsTerminal",
            [53488] = "OpenConsole",
            [59368] = "powershell",
            [54180] = "devin",
            [37024] = "cmd",
            [51952] = "claude",
        };
        var starts = new Dictionary<int, DateTime>
        {
            [53960] = terminalStarted,
            [53488] = devinPaneStarted,
            [59368] = devinPaneStarted,
            [54180] = devinPaneStarted.AddMinutes(5),
            [37024] = terminalStarted.AddMinutes(-30),
            [51952] = terminalStarted.AddSeconds(31),
        };

        var session = ActiveAppDetector.ExpandTerminalSessionPids(53960, "WindowsTerminal", parents, names, starts);

        Assert.Contains(53488, session);
        Assert.Contains(59368, session);
        Assert.Contains(54180, session);
        Assert.DoesNotContain(37024, session);
        Assert.DoesNotContain(51952, session);
    }

    [Fact]
    public void PickForegroundCli_PrefersFocusedTabOverNewerGrokInOtherTab()
    {
        var parents = new Dictionary<int, int>
        {
            [500] = 400,
            [501] = 401,
            [400] = 300,
            [401] = 300,
            [300] = 100,
        };

        var candidates = new List<(ProviderId id, DateTime started, int pid, int parentPid)>
        {
            (ProviderId.Grok, new DateTime(2026, 6, 10, 13, 0, 0, DateTimeKind.Utc), 501, 401),
            (ProviderId.Claude, new DateTime(2026, 6, 10, 11, 0, 0, DateTimeKind.Utc), 500, 400),
        };

        var picked = ActiveAppDetector.PickForegroundCli(candidates, sessionRootPid: 400, parents);
        Assert.Equal(ProviderId.Claude, picked);
    }

    [Fact]
    public void PickTerminalCli_PrefersKnownTerminalWindowProviderWhenSessionIsAmbiguous()
    {
        var parents = new Dictionary<int, int>
        {
            [54180] = 59368, // devin <- old powershell pane
            [59368] = 8836,
            [64076] = 63744, // claude <- newer cmd pane
            [63744] = 8836,
        };

        var foregroundTree = new List<(ProviderId id, DateTime started, int pid, int parentPid)>
        {
            (ProviderId.Devin, new DateTime(2026, 6, 11, 13, 27, 08, DateTimeKind.Local), 54180, 59368),
            (ProviderId.Claude, new DateTime(2026, 6, 11, 13, 30, 06, DateTimeKind.Local), 64076, 63744),
        };

        var picked = ActiveAppDetector.PickTerminalCli(
            foregroundTree,
            foregroundTree,
            foregroundIsWindowsTerminal: true,
            sessionRootPid: 53960,
            parents,
            windowTitleHint: null,
            preferredProvider: ProviderId.Devin);

        Assert.Equal(ProviderId.Devin, picked);
    }

    [Fact]
    public void PickTerminalCli_TitleTieBreakOverridesStaleKnownWindowProvider()
    {
        var parents = new Dictionary<int, int>
        {
            [54180] = 59368,
            [59368] = 8836,
            [66588] = 65136,
            [65136] = 8836,
        };

        var foregroundTree = new List<(ProviderId id, DateTime started, int pid, int parentPid)>
        {
            (ProviderId.Devin, new DateTime(2026, 6, 11, 13, 27, 08, DateTimeKind.Local), 54180, 59368),
            (ProviderId.Claude, new DateTime(2026, 6, 11, 13, 34, 06, DateTimeKind.Local), 66588, 65136),
        };

        var picked = ActiveAppDetector.PickTerminalCli(
            foregroundTree,
            foregroundTree,
            foregroundIsWindowsTerminal: true,
            sessionRootPid: 53960,
            parents,
            windowTitleHint: "Claude Code",
            preferredProvider: ProviderId.Devin);

        Assert.Equal(ProviderId.Claude, picked);
    }

    [Fact]
    public void PickTerminalCli_WindowsTerminalTitleCanRecoverWhenPaneTreePointsAtDifferentCli()
    {
        var parents = new Dictionary<int, int>
        {
            [67660] = 63908, // grok <- the only pane currently exposed by WT process tree
            [63908] = 53960,
            [66588] = 65136, // claude <- another WT window/pane reparented away from WT
            [65136] = 8836,
            [54180] = 59368, // devin <- another WT window/pane reparented away from WT
            [59368] = 8836,
        };

        var allCandidates = new List<(ProviderId id, DateTime started, int pid, int parentPid)>
        {
            (ProviderId.Devin, new DateTime(2026, 6, 11, 13, 27, 08, DateTimeKind.Local), 54180, 59368),
            (ProviderId.Claude, new DateTime(2026, 6, 11, 13, 34, 06, DateTimeKind.Local), 66588, 65136),
            (ProviderId.Grok, new DateTime(2026, 6, 11, 13, 38, 23, DateTimeKind.Local), 67660, 63908),
        };

        var foregroundTree = new List<(ProviderId id, DateTime started, int pid, int parentPid)>
        {
            (ProviderId.Grok, new DateTime(2026, 6, 11, 13, 38, 23, DateTimeKind.Local), 67660, 63908),
        };

        var picked = ActiveAppDetector.PickTerminalCli(
            allCandidates,
            foregroundTree,
            foregroundIsWindowsTerminal: true,
            sessionRootPid: 53960,
            parents,
            windowTitleHint: "Claude Code");

        Assert.Equal(ProviderId.Claude, picked);
    }

    [Fact]
    public void PickTerminalCli_WindowsTerminalTitleCanRecoverAgyAlias()
    {
        var parents = new Dictionary<int, int>
        {
            [67660] = 63908, // grok <- the only pane currently exposed by WT process tree
            [63908] = 53960,
            [71228] = 66976, // agy <- another active WT pane/window
            [66976] = 53960,
        };

        var allCandidates = new List<(ProviderId id, DateTime started, int pid, int parentPid)>
        {
            (ProviderId.Grok, new DateTime(2026, 6, 11, 13, 38, 23, DateTimeKind.Local), 67660, 63908),
            (ProviderId.Antigravity, new DateTime(2026, 6, 11, 13, 43, 43, DateTimeKind.Local), 71228, 66976),
        };

        var foregroundTree = new List<(ProviderId id, DateTime started, int pid, int parentPid)>
        {
            (ProviderId.Grok, new DateTime(2026, 6, 11, 13, 38, 23, DateTimeKind.Local), 67660, 63908),
        };

        var picked = ActiveAppDetector.PickTerminalCli(
            allCandidates,
            foregroundTree,
            foregroundIsWindowsTerminal: true,
            sessionRootPid: 53960,
            parents,
            windowTitleHint: @"C:\Users\ziedk\AppData\Local\agy\bin\agy.exe");

        Assert.Equal(ProviderId.Antigravity, picked);
    }

    [Fact]
    public void PickTerminalCli_WithoutKnownWindowProviderUsesNormalRecency()
    {
        var parents = new Dictionary<int, int>
        {
            [54180] = 59368,
            [59368] = 8836,
            [64076] = 63744,
            [63744] = 8836,
        };

        var foregroundTree = new List<(ProviderId id, DateTime started, int pid, int parentPid)>
        {
            (ProviderId.Devin, new DateTime(2026, 6, 11, 13, 27, 08, DateTimeKind.Local), 54180, 59368),
            (ProviderId.Claude, new DateTime(2026, 6, 11, 13, 30, 06, DateTimeKind.Local), 64076, 63744),
        };

        var picked = ActiveAppDetector.PickTerminalCli(
            foregroundTree,
            foregroundTree,
            foregroundIsWindowsTerminal: true,
            sessionRootPid: 53960,
            parents,
            windowTitleHint: null);

        Assert.Equal(ProviderId.Claude, picked);
    }

    [Fact]
    public void IsGrokPidInFocusedSession_RejectsGrokFromOtherTab()
    {
        var parents = new Dictionary<int, int>
        {
            [700] = 600,
            [701] = 601,
            [600] = 500,
            [601] = 500,
            [610] = 600,
            [611] = 601,
            [500] = 100,
        };
        var names = new Dictionary<int, string>
        {
            [700] = "openconsole",
            [701] = "openconsole",
            [600] = "pwsh",
            [601] = "pwsh",
            [610] = "grok",
            [611] = "claude",
            [500] = "windowsterminal",
        };

        var grokTab = ActiveAppDetector.ExpandTerminalSessionPids(700, "openconsole", parents, names);
        var claudeTab = ActiveAppDetector.ExpandTerminalSessionPids(701, "openconsole", parents, names);

        Assert.True(ActiveAppDetector.IsGrokPidInFocusedSession(610, grokTab, parents));
        Assert.False(ActiveAppDetector.IsGrokPidInFocusedSession(610, claudeTab, parents));
    }

    [Fact]
    public void ExpandTerminalSessionPids_OpenConsoleChildOfShell_IncludesNestedCli()
    {
        var parents = new Dictionary<int, int>
        {
            [701] = 500,
            [601] = 701,
            [620] = 601,
            [621] = 620,
            [611] = 621,
            [500] = 100,
        };
        var names = new Dictionary<int, string>
        {
            [701] = "openconsole",
            [601] = "pwsh",
            [620] = "cmd",
            [621] = "node",
            [611] = "claude",
            [500] = "windowsterminal",
        };

        var session = ActiveAppDetector.ExpandTerminalSessionPids(701, "openconsole", parents, names);

        Assert.Contains(611, session);
        Assert.Contains(621, session);
    }

    [Fact]
    public void SessionProximity_ChildOfFocusedShellScoresHigherThanSiblingTab()
    {
        var parents = new Dictionary<int, int>
        {
            [500] = 400,
            [501] = 401,
            [400] = 300,
            [401] = 300,
        };

        int claude = ActiveAppDetector.SessionProximity(500, 400, parents);
        int grok = ActiveAppDetector.SessionProximity(501, 400, parents);

        Assert.True(claude > grok);
    }

    [Fact]
    public void ExpandTerminalSessionPids_WezTermGuiForeground_IncludesDirectShellAndNestedCli()
    {
        // WezTerm (and similar) often spawn the shell directly as a child of the GUI process.
        var parents = new Dictionary<int, int>
        {
            [8100] = 8000, // wezterm-gui
            [8200] = 8100, // pwsh (direct child of wezterm)
            [8300] = 8200, // grok under pwsh
            [9000] = 8001, // unrelated other wezterm window
            [9100] = 9000,
        };
        var names = new Dictionary<int, string>
        {
            [8100] = "wezterm-gui",
            [8200] = "pwsh",
            [8300] = "grok",
            [9000] = "wezterm-gui",
            [9100] = "claude",
        };

        var session = ActiveAppDetector.ExpandTerminalSessionPids(8100, "wezterm-gui", parents, names);

        Assert.Contains(8100, session);
        Assert.Contains(8200, session);
        Assert.Contains(8300, session);
        Assert.DoesNotContain(9100, session);
    }

    [Fact]
    public void ExpandTerminalSessionPids_AlacrittyForeground_IncludesDirectInteractiveCli()
    {
        // Some terminals may launch a CLI directly (no long-lived shell visible in tree).
        var parents = new Dictionary<int, int>
        {
            [7100] = 7000, // alacritty
            [7200] = 7100, // claude (direct)
            [7300] = 7101, // other alacritty's child (should be excluded)
        };
        var names = new Dictionary<int, string>
        {
            [7100] = "alacritty",
            [7200] = "claude",
            [7101] = "alacritty",
            [7300] = "cursor-agent",
        };

        var session = ActiveAppDetector.ExpandTerminalSessionPids(7100, "alacritty", parents, names);

        Assert.Contains(7100, session);
        Assert.Contains(7200, session);
        Assert.DoesNotContain(7300, session);
    }

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

    [Fact]
    public void TryPickByWindowTitleHint_PrefersMatchingProviderOverRecency()
    {
        var now = DateTime.UtcNow;
        var older = now.AddMinutes(-10);
        var newer = now.AddMinutes(-1);

        var candidates = new List<(ProviderId id, DateTime started, int pid, int parentPid)>
        {
            (ProviderId.Grok, newer, 501, 401),   // newer overall
            (ProviderId.Claude, older, 500, 400),
        };

        // Title of the focused WT window/tab mentions claude (common when that tab is active)
        var picked = ActiveAppDetector.TryPickByWindowTitleHint(candidates, "C:\\src\\myproj - claude - Windows Terminal");
        Assert.Equal(ProviderId.Claude, picked);

        // If title mentions grok, prefer the (even older) grok one over a newer non-matching
        var pickedGrok = ActiveAppDetector.TryPickByWindowTitleHint(candidates, "grok @ /home/user");
        Assert.Equal(ProviderId.Grok, pickedGrok);

        // No useful hint in title → returns null (caller falls back to normal Pick)
        var noHint = ActiveAppDetector.TryPickByWindowTitleHint(candidates, "Windows PowerShell");
        Assert.Null(noHint);

        // Short executable names like "gh" must not match inside ordinary words.
        var embeddedGh = ActiveAppDetector.TryPickByWindowTitleHint(candidates, "thinking through changes");
        Assert.Null(embeddedGh);
    }

    [Fact]
    public void PickTerminalCli_WindowsTerminalActiveTitleCanOverrideFocusedSessionTree()
    {
        // Windows Terminal can expose one pane in the process tree while the active surface is another
        // reparented pane/window. The active terminal title is the best available foreground signal.
        var parents = new Dictionary<int, int>
        {
            [142920] = 43492,    // claude <- powershell
            [43492] = 148228,    // powershell <- WindowsTerminal
            [247240] = 246964,   // grok <- cmd
            [246964] = 9084,     // cmd <- explorer (NOT under WindowsTerminal)
        };

        var allCandidates = new List<(ProviderId id, DateTime started, int pid, int parentPid)>
        {
            (ProviderId.Claude, new DateTime(2026, 6, 11, 10, 0, 0, DateTimeKind.Utc), 142920, 43492),
            (ProviderId.Grok, new DateTime(2026, 6, 11, 11, 0, 0, DateTimeKind.Utc), 247240, 246964),
        };

        // Only claude is reachable through the WT process parent tree.
        var foregroundTree = new List<(ProviderId id, DateTime started, int pid, int parentPid)>
        {
            (ProviderId.Claude, new DateTime(2026, 6, 11, 10, 0, 0, DateTimeKind.Utc), 142920, 43492),
        };

        // The active foreground title says Grok, so Grok wins even though the exposed tree points at Claude.
        var titleMentionsGrok = ActiveAppDetector.PickTerminalCli(
            allCandidates, foregroundTree, foregroundIsWindowsTerminal: true,
            sessionRootPid: 148228, parents, "grok");
        Assert.Equal(ProviderId.Grok, titleMentionsGrok);

        // An unrelated title also stays with the focused session tree.
        var unrelatedTitle = ActiveAppDetector.PickTerminalCli(
            allCandidates, foregroundTree, foregroundIsWindowsTerminal: true,
            sessionRootPid: 148228, parents, "? Replace mistake detector with FastConformer streaming model");
        Assert.Equal(ProviderId.Claude, unrelatedTitle);
    }
}
