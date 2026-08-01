using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Text;
using System.Text.Json;
using System.Threading;
using TaskbarQuota.Diagnostics;
using TaskbarQuota.Usage;

namespace TaskbarQuota.AgentActivity;

/// <summary>
/// Windows equivalent of agent-notch's process-plus-transcript scanner. Process liveness is authoritative;
/// transcript timestamps only distinguish busy from alive-but-quiet.
/// </summary>
internal sealed class AgentActivityScanner
{
    private static readonly TimeSpan RecentWindow = TimeSpan.FromHours(6);
    private static readonly TimeSpan BusyWindow = TimeSpan.FromSeconds(30);
    private readonly string _home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private readonly CodexThreadNameResolver _threadNames = new();

    public IReadOnlyList<AgentActivityItem> Scan(CancellationToken cancellationToken = default)
    {
        // This deliberately does not use ActiveAppDetector: foreground-provider detection drives the
        // quota tiles, while activity follows every running agent in the desktop or a terminal.
        var desktopApps = ScanDesktopAgentApps();
        var terminalAgents = ScanTerminalAgentCommands();
        var liveProviders = MergeLiveProviders(desktopApps, terminalAgents);
        Log.Information($"[activity] agent discovery: desktop={desktopApps.Values.Sum()}, terminal={terminalAgents.Values.Sum()}");
        var files = new List<(ProviderId Provider, string Path, DateTimeOffset Modified)>();
        AddRecent(files, Path.Combine(_home, ".codex", "sessions"), ProviderId.Codex, cancellationToken);
        AddRecent(files, Path.Combine(_home, ".claude", "projects"), ProviderId.Claude, cancellationToken);

        var parsed = new List<AgentActivityItem>();
        var liveClaims = new Dictionary<ProviderId, int>();
        foreach (var file in files.OrderByDescending(f => f.Modified).Take(80))
        {
            cancellationToken.ThrowIfCancellationRequested();
            int claim = liveClaims.TryGetValue(file.Provider, out var currentClaim) ? currentClaim : 0;
            bool claimLive = liveProviders.TryGetValue(file.Provider, out var liveCount) && claim < liveCount;
            liveClaims[file.Provider] = claim + 1;
            if (TryRead(file.Provider, file.Path, file.Modified, claimLive, out var item))
                parsed.Add(item);
        }

        return GroupCodex(parsed)
            .Where(item => item.IsLive || DateTimeOffset.Now - item.UpdatedAt < RecentWindow)
            .OrderByDescending(item => item.IsLive)
            .ThenByDescending(item => item.UpdatedAt)
            .ToArray();
    }

    private void AddRecent(List<(ProviderId, string, DateTimeOffset)> output, string root,
        ProviderId provider, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(root))
            return;

        try
        {
            foreach (var path in Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var modified = File.GetLastWriteTimeUtc(path);
                if (DateTime.UtcNow - modified <= RecentWindow)
                    output.Add((provider, path, new DateTimeOffset(modified, TimeSpan.Zero).ToLocalTime()));
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private bool TryRead(ProviderId provider, string path, DateTimeOffset modified,
        bool claimLive, out AgentActivityItem item)
    {
        item = default!;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length == 0)
                return false;

            stream.Seek(Math.Max(0, stream.Length - 131_072), SeekOrigin.Begin);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var text = reader.ReadToEnd();
            var info = ParseTail(provider, text);
            var session = provider == ProviderId.Codex ? ReadSessionMetadata(path) : default;
            bool freshAgentEvent = info.LastActivity is { } activity && DateTimeOffset.Now - activity < BusyWindow;
            // One desktop provider process can host several concurrent threads. A fresh reasoning/tool
            // event is therefore also a liveness signal, not just the process-count claim.
            bool live = (claimLive || (info.State == TranscriptState.Action && freshAgentEvent))
                && modified > DateTimeOffset.Now - RecentWindow;
            bool busy = live && freshAgentEvent;
            // A desktop process can stay alive after the agent's final response. The transcript is the
            // authority for completion, mirroring agent-notch's event-based state model.
            var status = info.State == TranscriptState.Finished || !live
                ? AgentActivityStatus.Completed
                : AgentActivityStatus.Working;
            // The user's initial prompt is intentionally not mirrored into the taskbar or activity panel.
            // It is often long and belongs to the provider conversation, not the compact activity signal.
            var threadId = session.ThreadId ?? info.ThreadId;
            var title = provider == ProviderId.Codex ? _threadNames.GetName(threadId) ?? provider.ToString() : provider.ToString();
            // A reasoning summary is useful while the agent is working, but becomes stale and misleading
            // once a final response has landed. Completion is deliberately a stable, unambiguous state.
            var step = status == AgentActivityStatus.Completed
                ? "Completed"
                : info.State == TranscriptState.Action && !string.IsNullOrWhiteSpace(info.Step)
                    ? info.Step
                    : !string.IsNullOrWhiteSpace(info.Summary) ? info.Summary
                    : string.IsNullOrWhiteSpace(info.Step) ? (busy ? "Working" : "Thinking") : info.Step;
            item = new AgentActivityItem(
                path, provider, title, step, status,
                info.StartedAt ?? modified, info.LastActivity ?? modified,
                SubagentCount: 0,
                Detail: null,
                Model: info.Model,
                ThreadId: threadId,
                ParentThreadId: session.ParentThreadId ?? info.ParentThreadId,
                Host: session.Host);
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
        catch (JsonException) { return false; }
    }

    private static SessionMetadata ReadSessionMetadata(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var line = reader.ReadLine();
            if (string.IsNullOrWhiteSpace(line))
                return default;
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (!root.TryGetProperty("type", out var type) || type.GetString() != "session_meta"
                || !root.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object)
                return default;
            var originator = payload.TryGetProperty("originator", out var originatorNode)
                ? originatorNode.GetString()
                : null;
            var host = originator?.Equals("t3code_desktop", StringComparison.OrdinalIgnoreCase) == true
                ? "T3 Code"
                : originator?.Contains("synara", StringComparison.OrdinalIgnoreCase) == true
                    ? "Synara"
                    : null;
            return new SessionMetadata(
                payload.TryGetProperty("id", out var id) ? id.GetString() : null,
                payload.TryGetProperty("parent_thread_id", out var parent) ? parent.GetString() : null,
                host);
        }
        catch (IOException) { return default; }
        catch (UnauthorizedAccessException) { return default; }
        catch (JsonException) { return default; }
    }

    private static TailInfo ParseTail(ProviderId provider, string text)
    {
        string step = "", summary = "", model = "", threadId = "", parentId = "";
        DateTimeOffset? started = null, activity = null;
        var state = TranscriptState.Unknown;
        foreach (var line in text.Split('\n').Reverse())
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (root.TryGetProperty("timestamp", out var timestamp)
                    && timestamp.ValueKind == JsonValueKind.String
                    && DateTimeOffset.TryParse(timestamp.GetString(), out var parsedTime))
                {
                    started ??= parsedTime;
                    if (activity is null && IsConversationEvent(root, provider)) activity = parsedTime;
                }

                if (root.TryGetProperty("payload", out var payload) && payload.ValueKind == JsonValueKind.Object)
                {
                    if (threadId.Length == 0 && payload.TryGetProperty("id", out var id)) threadId = id.GetString() ?? "";
                    if (parentId.Length == 0 && payload.TryGetProperty("parent_thread_id", out var parent)) parentId = parent.GetString() ?? "";
                    if (model.Length == 0 && payload.TryGetProperty("model", out var modelNode)) model = modelNode.GetString() ?? "";

                    var type = payload.TryGetProperty("type", out var typeNode) ? typeNode.GetString() : null;
                    if (summary.Length == 0 && type is "agent_reasoning" && payload.TryGetProperty("text", out var summaryText))
                        summary = Clean(summaryText.GetString());
                    if (summary.Length == 0 && type is "reasoning" && payload.TryGetProperty("summary", out var reasoningSummary))
                        summary = ExtractSummary(reasoningSummary);
                    if (state == TranscriptState.Unknown && type is "task_complete")
                        state = TranscriptState.Finished;
                    if (state == TranscriptState.Unknown && type is "message"
                        && payload.TryGetProperty("role", out var role) && role.GetString() == "assistant"
                        && payload.TryGetProperty("phase", out var messagePhase) && messagePhase.GetString() == "final_answer")
                        state = TranscriptState.Finished;
                    if (type is "user_message")
                    {
                        // A new user turn always supersedes an older completed turn in the same transcript.
                        if (state == TranscriptState.Unknown)
                        {
                            state = TranscriptState.Action;
                            step = "Thinking";
                        }
                    }
                    if (state == TranscriptState.Unknown && type is "agent_reasoning" or "reasoning")
                        state = TranscriptState.Action;
                    if (step.Length == 0 && type is "custom_tool_call" or "function_call")
                    {
                        var name = payload.TryGetProperty("name", out var nameNode) ? nameNode.GetString() : null;
                        if (IsAction(name))
                        {
                            state = state == TranscriptState.Unknown ? TranscriptState.Action : state;
                            step = DescribeAction(name!, payload);
                        }
                    }
                    if (step.Length == 0 && type is "agent_reasoning" && payload.TryGetProperty("text", out var reasoning))
                        step = Clean(reasoning.GetString());
                }

                if (state == TranscriptState.Unknown && root.TryGetProperty("payload", out var eventPayload)
                    && eventPayload.ValueKind == JsonValueKind.Object
                    && eventPayload.TryGetProperty("phase", out var phase)
                    && phase.GetString() == "final_answer")
                    state = TranscriptState.Finished;

            }
            catch (JsonException) { }

            if (state != TranscriptState.Unknown && (step.Length > 0 || summary.Length > 0)
                && activity is not null && threadId.Length > 0)
                break;
        }

        return new TailInfo(step, summary, model, threadId, parentId, started, activity, state);
    }

    private static string ExtractSummary(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array)
            return "";

        foreach (var part in value.EnumerateArray().Reverse())
            if (part.ValueKind == JsonValueKind.Object && part.TryGetProperty("summary_text", out var text))
                return Clean(text.GetString());
        return "";
    }

    private static bool IsAction(string? name)
        => !string.IsNullOrWhiteSpace(name) && name is not "wait";

    private static string DescribeAction(string name, JsonElement payload)
    {
        var details = payload.TryGetProperty("arguments", out var arguments) ? arguments.GetString()
            : payload.TryGetProperty("input", out var input) ? input.GetString() : "";
        details ??= "";
        if (name.Equals("apply_patch", StringComparison.OrdinalIgnoreCase) || details.Contains("apply_patch", StringComparison.OrdinalIgnoreCase))
            return "Edited code";
        if (details.Contains("Get-Content", StringComparison.OrdinalIgnoreCase))
            return "Ran Get-Content";
        if (details.Contains("rg ", StringComparison.OrdinalIgnoreCase) || details.Contains("rg -", StringComparison.OrdinalIgnoreCase))
            return "Searched files";
        if (details.Contains("dotnet build", StringComparison.OrdinalIgnoreCase))
            return "Built the app";
        if (details.Contains("dotnet test", StringComparison.OrdinalIgnoreCase))
            return "Ran tests";
        return name.Equals("shell_command", StringComparison.OrdinalIgnoreCase) || name.Equals("exec", StringComparison.OrdinalIgnoreCase)
            ? "Running command"
            : $"Running {name}";
    }

    private static bool IsConversationEvent(JsonElement root, ProviderId provider)
    {
        if (provider == ProviderId.Claude)
            return root.TryGetProperty("type", out var type) && type.GetString() is "user" or "assistant";
        return root.TryGetProperty("payload", out var payload)
            && payload.TryGetProperty("type", out var typeNode)
            && typeNode.GetString() is "user_message" or "message" or "reasoning" or "agent_reasoning" or "custom_tool_call" or "function_call";
    }

    private static string ExtractText(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String) return Clean(value.GetString());
        if (value.ValueKind == JsonValueKind.Array)
            foreach (var part in value.EnumerateArray().Reverse())
                if (part.ValueKind == JsonValueKind.Object && part.TryGetProperty("text", out var text))
                    return Clean(text.GetString());
        return "";
    }

    private static string Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var result = value.Trim().Replace('\n', ' ')
            .Replace("**", "", StringComparison.Ordinal)
            .Replace("__", "", StringComparison.Ordinal)
            .Replace("`", "", StringComparison.Ordinal);
        if (result.StartsWith('<') || result.StartsWith('{')) return "";
        return Trim(result, 120);
    }

    private static string Trim(string value, int length)
        => value.Length <= length ? value : value[..length].TrimEnd() + "…";

    /// <summary>Finds installed desktop agent apps, regardless of which window is foreground.</summary>
    private static IReadOnlyDictionary<ProviderId, int> ScanDesktopAgentApps()
    {
        var live = new Dictionary<ProviderId, int>();
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                switch (process.ProcessName.ToLowerInvariant())
                {
                    case "codex":
                    case "chatgpt": Add(ProviderId.Codex); break;
                    case "claude": Add(ProviderId.Claude); break;
                    case "cursor": Add(ProviderId.Cursor); break;
                    case "antigravity": Add(ProviderId.Antigravity); break;
                    case "devin": Add(ProviderId.Devin); break;
                }
            }
            catch { }
            finally { process.Dispose(); }
        }
        return live;

        // Electron-style apps may have many helper processes. They represent one desktop agent,
        // not several simultaneous tasks.
        void Add(ProviderId provider) => live[provider] = 1;
    }

    /// <summary>
    /// Finds agents launched from CMD, PowerShell, Windows Terminal, or a script runtime. This is
    /// intentionally a separate scan from desktop apps and never consults the foreground window.
    /// </summary>
    private static IReadOnlyDictionary<ProviderId, int> ScanTerminalAgentCommands()
    {
        var live = new Dictionary<ProviderId, int>();
        const string query = "SELECT Name, CommandLine FROM Win32_Process WHERE " +
            "Name = 'cmd.exe' OR Name = 'powershell.exe' OR Name = 'pwsh.exe' OR Name = 'node.exe' OR " +
            "Name = 'bun.exe' OR Name = 'deno.exe' OR Name = 'npm.exe' OR Name = 'npx.exe' OR " +
            "Name = 'pnpm.exe' OR Name = 'yarn.exe' OR Name = 'wsl.exe' OR Name = 'bash.exe' OR " +
            "Name = 'codex.exe' OR Name = 'claude.exe' OR Name = 'cursor-agent.exe' OR " +
            "Name = 'opencode.exe' OR Name = 'cline.exe' OR Name = 'agy.exe'";

        try
        {
            using var searcher = new ManagementObjectSearcher(query);
            using var processes = searcher.Get();
            foreach (ManagementObject process in processes)
            {
                using (process)
                {
                    var name = Path.GetFileNameWithoutExtension(process["Name"]?.ToString() ?? "");
                    var commandLine = process["CommandLine"]?.ToString() ?? "";
                    if (TryGetTerminalProvider(name, commandLine, out var provider))
                        Add(provider);
                }
            }
        }
        catch (ManagementException) { }
        catch (UnauthorizedAccessException) { }

        return live;
        void Add(ProviderId provider) => live[provider] = live.TryGetValue(provider, out var count) ? count + 1 : 1;
    }

    private static bool TryGetTerminalProvider(string executable, string commandLine, out ProviderId provider)
    {
        provider = default;
        var text = (executable + " " + commandLine).ToLowerInvariant();
        if (text.Contains("claude-code") || text.Contains(" claude") || executable.Equals("claude", StringComparison.OrdinalIgnoreCase)) { provider = ProviderId.Claude; return true; }
        if (text.Contains("cursor-agent") || text.Contains("cursor agent")) { provider = ProviderId.Cursor; return true; }
        if (text.Contains("antigravity") || executable.Equals("agy", StringComparison.OrdinalIgnoreCase)) { provider = ProviderId.Antigravity; return true; }
        if (text.Contains("opencode")) { provider = ProviderId.OpenCode; return true; }
        if (text.Contains("cline")) { provider = ProviderId.Cline; return true; }
        if (text.Contains(" codex") || executable.Equals("codex", StringComparison.OrdinalIgnoreCase)) { provider = ProviderId.Codex; return true; }
        return false;
    }

    private static IReadOnlyDictionary<ProviderId, int> MergeLiveProviders(
        IReadOnlyDictionary<ProviderId, int> desktopApps,
        IReadOnlyDictionary<ProviderId, int> terminalAgents)
    {
        var merged = new Dictionary<ProviderId, int>(desktopApps);
        foreach (var (provider, count) in terminalAgents)
            // A native CLI can be visible to both scans. Keep the greater count rather than
            // claiming two transcript tasks for that one process.
            merged[provider] = merged.TryGetValue(provider, out var current) ? Math.Max(current, count) : count;
        return merged;
    }

    private static IReadOnlyList<AgentActivityItem> GroupCodex(IReadOnlyList<AgentActivityItem> items)
    {
        var codex = items.Where(item => item.Provider == ProviderId.Codex).ToArray();
        if (codex.Length < 2) return items;
        var groups = new List<AgentActivityItem>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in codex.Where(item => string.IsNullOrWhiteSpace(item.ParentThreadId)))
        {
            var children = codex.Where(item => item.ParentThreadId == root.ThreadId).ToArray();
            used.Add(root.Id);
            foreach (var child in children) used.Add(child.Id);
            groups.Add(root with { SubagentCount = children.Length });
        }
        groups.AddRange(codex.Where(item => !used.Contains(item.Id)));
        groups.AddRange(items.Where(item => item.Provider != ProviderId.Codex));
        return groups;
    }

    private enum TranscriptState { Unknown, Action, Finished }

    private sealed record TailInfo(string Step, string Summary, string Model, string ThreadId,
        string ParentThreadId, DateTimeOffset? StartedAt, DateTimeOffset? LastActivity, TranscriptState State);
    private readonly record struct SessionMetadata(string? ThreadId, string? ParentThreadId, string? Host);
}
