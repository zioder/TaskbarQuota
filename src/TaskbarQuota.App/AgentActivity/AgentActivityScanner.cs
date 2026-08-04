using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Data.Sqlite;
using TaskbarQuota.ActiveApp;
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
    private CodexThreadNameResolver _threadNames = new();
    private readonly object _candidateGate = new();
    private readonly Dictionary<string, (ProviderId Provider, string Path, DateTimeOffset Modified)> _knownCandidates =
        new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<AgentActivityItem> Scan(CancellationToken cancellationToken = default)
    {
        // This deliberately does not use ActiveAppDetector: foreground-provider detection drives the
        // quota tiles, while activity follows every running agent in the desktop or a terminal.
        var desktopApps = ScanDesktopAgentApps();
        var terminalAgents = ScanTerminalAgentCommands();
        var liveProviders = MergeLiveProviders(desktopApps, terminalAgents);
        Log.Debug($"[activity] agent discovery: desktop={desktopApps.Values.Sum()}, terminal={terminalAgents.Values.Sum()}");
        var files = new List<(ProviderId Provider, string Path, DateTimeOffset Modified)>();
        // Process discovery is intentionally the gate for transcript/database work. Most agent
        // stores are SQLite or recursive file trees, so reopening them every tick when the app is
        // closed wastes the bulk of the activity refresh time.
        if (IsDetected(ProviderId.Codex))
            AddRecent(files, Path.Combine(_home, ".codex", "sessions"), ProviderId.Codex, cancellationToken);
        if (IsDetected(ProviderId.Claude))
            AddRecent(files, Path.Combine(_home, ".claude", "projects"), ProviderId.Claude, cancellationToken);
        if (IsDetected(ProviderId.Grok))
            AddGrokSessions(files, cancellationToken);
        if (IsDetected(ProviderId.Antigravity))
        {
            AddAntigravityDatabases(files, cancellationToken);
            AddAntigravityGuiTranscripts(files, cancellationToken);
        }
        if (IsDetected(ProviderId.OpenCode))
            AddOpenCodeDatabases(files, cancellationToken);
        if (IsDetected(ProviderId.Cline))
            AddClineSessions(files, cancellationToken);
        if (IsDetected(ProviderId.Kimi))
            AddKimiSessions(files, cancellationToken);
        if (IsDetected(ProviderId.Copilot))
            AddCopilotSessions(files, cancellationToken);
        if (IsDetected(ProviderId.Zai))
            AddZcodeDatabase(files, cancellationToken);

        bool IsDetected(ProviderId provider) => liveProviders.ContainsKey(provider);

        // VS Code persists the same Copilot thread twice: the canonical session snapshot under
        // chatSessions (with customTitle/modelState) and an extension transcript under transcripts.
        // Keep one source per session so the prompt-only transcript cannot replace the titled state.
        var copilotFiles = files
            .Where(file => file.Provider == ProviderId.Copilot)
            .GroupBy(file => Path.GetFileNameWithoutExtension(file.Path), StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderBy(file => file.Path.Contains("\\chatSessions\\", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenByDescending(file => file.Modified)
                .First())
            .ToArray();
        files.RemoveAll(file => file.Provider == ProviderId.Copilot);
        files.AddRange(copilotFiles);

        AddRememberedCandidates(files);
        var candidates = SelectCandidates(files);
        RememberCandidates(candidates);

        var parsed = new List<AgentActivityItem>();
        var liveClaims = new Dictionary<ProviderId, int>();
        foreach (var file in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int claim = liveClaims.TryGetValue(file.Provider, out var currentClaim) ? currentClaim : 0;
            bool claimLive = liveProviders.TryGetValue(file.Provider, out var liveCount) && claim < liveCount;
            var candidateItems = new List<AgentActivityItem>();
            if (file.Provider == ProviderId.Grok)
            {
                if (TryReadGrokSession(file.Path, file.Modified, claimLive, out var grokItem))
                    candidateItems.Add(grokItem);
            }
            else if (file.Provider == ProviderId.Antigravity)
            {
                var read = IsAntigravityGuiTranscript(file.Path)
                    ? TryReadAntigravityGuiSession(file.Path, file.Modified, claimLive, out var antigravityItem)
                    : TryReadAntigravitySession(file.Path, file.Modified, claimLive, out antigravityItem);
                if (read)
                    candidateItems.Add(antigravityItem);
            }
            else if (file.Provider == ProviderId.OpenCode)
            {
                candidateItems.AddRange(ReadOpenCodeSessions(file.Path, file.Modified, claimLive));
            }
            else if (file.Provider == ProviderId.Cline)
            {
                if (TryReadClineSession(file.Path, file.Modified, claimLive, out var clineItem))
                    candidateItems.Add(clineItem);
            }
            else if (file.Provider == ProviderId.Kimi)
            {
                if (TryReadKimiSession(file.Path, file.Modified, claimLive, out var kimiItem))
                    candidateItems.Add(kimiItem);
            }
            else if (file.Provider == ProviderId.Copilot)
            {
                if (TryReadCopilotSession(file.Path, file.Modified, claimLive, out var copilotItem))
                    candidateItems.Add(copilotItem);
            }
            else if (file.Provider == ProviderId.Zai)
            {
                candidateItems.AddRange(ReadZcodeSessions(file.Path, file.Modified, claimLive));
            }
            else if (TryRead(file.Provider, file.Path, file.Modified, claimLive, out var item))
            {
                candidateItems.Add(item);
            }

            // A concurrently-written or malformed newest file must not consume the only live-process
            // claim and force the next valid session to look completed.
            if (candidateItems.Count == 0)
                continue;

            liveClaims[file.Provider] = claim + 1;
            parsed.AddRange(candidateItems);
        }

        return ApplyActiveHostFallback(GroupSessions(parsed))
            .Where(item => item.IsLive || DateTimeOffset.Now - item.UpdatedAt < RecentWindow)
            .OrderByDescending(item => item.IsLive)
            .ThenByDescending(item => item.UpdatedAt)
            .ToArray();
    }

    private void AddRememberedCandidates(
        List<(ProviderId Provider, string Path, DateTimeOffset Modified)> files)
    {
        lock (_candidateGate)
        {
            var existingPaths = files.Select(file => file.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var (path, candidate) in _knownCandidates.ToArray())
            {
                if (!File.Exists(path) || DateTimeOffset.Now - candidate.Modified > RecentWindow)
                {
                    _knownCandidates.Remove(path);
                    continue;
                }
                if (!existingPaths.Contains(path))
                    files.Add(candidate);
            }
        }
    }

    private void RememberCandidates(
        IReadOnlyList<(ProviderId Provider, string Path, DateTimeOffset Modified)> candidates)
    {
        lock (_candidateGate)
        {
            foreach (var candidate in candidates)
                _knownCandidates[candidate.Path] = candidate;

            var retainedPaths = _knownCandidates.Values
                .GroupBy(candidate => candidate.Provider)
                .SelectMany(group => group.OrderByDescending(candidate => candidate.Modified).Take(20))
                .Select(candidate => candidate.Path)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var path in _knownCandidates.Keys.Where(path => !retainedPaths.Contains(path)).ToArray())
                _knownCandidates.Remove(path);
        }
    }

    public void ClearCache()
    {
        lock (_candidateGate)
            _knownCandidates.Clear();
        _threadNames = new CodexThreadNameResolver();
    }

    internal static IReadOnlyList<(ProviderId Provider, string Path, DateTimeOffset Modified)> SelectCandidates(
        IEnumerable<(ProviderId Provider, string Path, DateTimeOffset Modified)> files,
        int maxPerProvider = 20)
        => files
            .GroupBy(file => file.Provider)
            .SelectMany(group => group.OrderByDescending(file => file.Modified).Take(maxPerProvider))
            .OrderByDescending(file => file.Modified)
            .ToArray();

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

    private void AddClineSessions(List<(ProviderId, string, DateTimeOffset)> output,
        CancellationToken cancellationToken)
    {
        var configuredRoot = Environment.GetEnvironmentVariable("CLINE_DATA_DIR");
        var root = string.IsNullOrWhiteSpace(configuredRoot)
            ? Path.Combine(_home, ".cline", "data")
            : configuredRoot;
        var sessions = Path.Combine(root, "sessions");
        if (!Directory.Exists(sessions))
            return;

        try
        {
            foreach (var path in Directory.EnumerateFiles(sessions, "*.json", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (path.EndsWith(".messages.json", StringComparison.OrdinalIgnoreCase))
                    continue;

                var modified = File.GetLastWriteTimeUtc(path);
                var messagesPath = Path.Combine(
                    Path.GetDirectoryName(path) ?? "",
                    Path.GetFileNameWithoutExtension(path) + ".messages.json");
                if (File.Exists(messagesPath))
                {
                    var messagesModified = File.GetLastWriteTimeUtc(messagesPath);
                    if (messagesModified > modified)
                        modified = messagesModified;
                }

                if (DateTime.UtcNow - modified <= RecentWindow)
                    output.Add((ProviderId.Cline, path,
                        new DateTimeOffset(modified, TimeSpan.Zero).ToLocalTime()));
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private void AddKimiSessions(List<(ProviderId, string, DateTimeOffset)> output,
        CancellationToken cancellationToken)
    {
        foreach (var root in KimiHomeCandidates())
        {
            var sessions = Path.Combine(root, "sessions");
            if (!Directory.Exists(sessions))
                continue;

            try
            {
                foreach (var statePath in Directory.EnumerateFiles(sessions, "state.json", SearchOption.AllDirectories))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var sessionDirectory = Path.GetDirectoryName(statePath);
                    if (string.IsNullOrWhiteSpace(sessionDirectory))
                        continue;

                    var modified = File.GetLastWriteTimeUtc(statePath);
                    var agentsDirectory = Path.Combine(sessionDirectory, "agents");
                    if (Directory.Exists(agentsDirectory))
                    {
                        foreach (var wirePath in Directory.EnumerateFiles(
                            agentsDirectory, "wire.jsonl", SearchOption.AllDirectories))
                        {
                            var wireModified = File.GetLastWriteTimeUtc(wirePath);
                            if (wireModified > modified)
                                modified = wireModified;
                        }
                    }
                    foreach (var companion in new[] { "wire.jsonl", "context.jsonl" })
                    {
                        var companionPath = Path.Combine(sessionDirectory, companion);
                        if (File.Exists(companionPath))
                        {
                            var companionModified = File.GetLastWriteTimeUtc(companionPath);
                            if (companionModified > modified)
                                modified = companionModified;
                        }
                    }

                    if (DateTime.UtcNow - modified <= RecentWindow)
                        output.Add((ProviderId.Kimi, statePath,
                            new DateTimeOffset(modified, TimeSpan.Zero).ToLocalTime()));
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private IEnumerable<string> KimiHomeCandidates()
    {
        var explicitHome = Environment.GetEnvironmentVariable("KIMI_CODE_HOME");
        if (!string.IsNullOrWhiteSpace(explicitHome))
            yield return explicitHome;

        var legacyHome = Environment.GetEnvironmentVariable("KIMI_SHARE_DIR");
        if (!string.IsNullOrWhiteSpace(legacyHome))
            yield return legacyHome;

        yield return Path.Combine(_home, ".kimi-code");
        yield return Path.Combine(_home, ".kimi");
    }

    private void AddCopilotSessions(List<(ProviderId, string, DateTimeOffset)> output,
        CancellationToken cancellationToken)
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var configuredUserData = Environment.GetEnvironmentVariable("VSCODE_USER_DATA_DIR");
        var userRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.Combine(appData, "Code", "User"),
            Path.Combine(appData, "Code - Insiders", "User"),
            Path.Combine(appData, "VSCodium", "User"),
        };
        if (!string.IsNullOrWhiteSpace(configuredUserData))
        {
            var configured = configuredUserData.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            userRoots.Add(Path.GetFileName(configured).Equals("User", StringComparison.OrdinalIgnoreCase)
                ? configured
                : Path.Combine(configured, "User"));
        }

        try
        {
            foreach (var userRoot in userRoots)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!Directory.Exists(userRoot))
                    continue;

                var transcriptDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var workspaceStorage = Path.Combine(userRoot, "workspaceStorage");
                if (Directory.Exists(workspaceStorage))
                {
                    foreach (var workspace in Directory.EnumerateDirectories(workspaceStorage, "*", SearchOption.TopDirectoryOnly))
                    {
                        transcriptDirectories.Add(Path.Combine(workspace, "chatSessions"));
                        transcriptDirectories.Add(Path.Combine(workspace, "GitHub.copilot-chat", "transcripts"));
                    }
                }
                transcriptDirectories.Add(Path.Combine(userRoot, "globalStorage", "emptyWindowChatSessions"));

                foreach (var directory in transcriptDirectories)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!Directory.Exists(directory))
                        continue;
                    foreach (var path in Directory.EnumerateFiles(directory, "*.*", SearchOption.TopDirectoryOnly))
                    {
                        var extension = Path.GetExtension(path);
                        if (!extension.Equals(".json", StringComparison.OrdinalIgnoreCase)
                            && !extension.Equals(".jsonl", StringComparison.OrdinalIgnoreCase))
                            continue;

                        var modified = File.GetLastWriteTimeUtc(path);
                        if (DateTime.UtcNow - modified <= RecentWindow)
                        {
                            output.Add((ProviderId.Copilot, path,
                                new DateTimeOffset(modified, TimeSpan.Zero).ToLocalTime()));
                        }
                    }
                }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    internal static bool TryReadCopilotSession(string path, DateTimeOffset modified, bool claimLive,
        out AgentActivityItem item)
    {
        item = default!;
        if (!File.Exists(path))
            return false;

        try
        {
            var transcript = new CopilotTranscript(Path.GetFileNameWithoutExtension(path));
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length == 0)
                return false;

            if (Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase))
            {
                using var document = JsonDocument.Parse(stream);
                ParseCopilotDocument(document.RootElement, transcript);
            }
            else
            {
                using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                string? line;
                while ((line = reader.ReadLine()) is not null)
                {
                    if (string.IsNullOrWhiteSpace(line))
                        continue;
                    try
                    {
                        using var document = JsonDocument.Parse(line);
                        ParseCopilotEvent(document.RootElement, transcript);
                    }
                    catch (JsonException) { }
                }
            }

            transcript.LastActivity ??= modified;
            transcript.StartedAt ??= transcript.LastActivity;
            // VS Code being open is not proof that Copilot is currently running a turn. Require a
            // pending/waiting request or a fresh transcript write so an old chat does not stay live for
            // the lifetime of the editor process.
            var live = claimLive
                && (transcript.Pending
                    || transcript.Waiting
                    || DateTimeOffset.Now - transcript.LastActivity.Value < BusyWindow);
            var status = !live
                ? transcript.Failed ? AgentActivityStatus.Failed : AgentActivityStatus.Completed
                : transcript.Failed ? AgentActivityStatus.Failed
                : transcript.Waiting ? AgentActivityStatus.Waiting
                : transcript.Pending ? AgentActivityStatus.Working
                : AgentActivityStatus.Idle;
            var step = status switch
            {
                AgentActivityStatus.Completed => "Completed",
                AgentActivityStatus.Failed => "Failed",
                AgentActivityStatus.Waiting => FirstNonEmpty(transcript.Step, "Waiting for input"),
                AgentActivityStatus.Idle => "Waiting for the next prompt",
                _ => FirstNonEmpty(transcript.Step, "Working"),
            };
            var host = path.Contains("Code - Insiders", StringComparison.OrdinalIgnoreCase)
                ? "VS Code Insiders"
                : path.Contains("VSCodium", StringComparison.OrdinalIgnoreCase) ? "VSCodium" : "VS Code";
            item = new AgentActivityItem(
                $"copilot:{transcript.SessionId}", ProviderId.Copilot,
                Trim(Clean(FirstNonEmpty(transcript.Title, "GitHub Copilot Session")), 72),
                Trim(Clean(step), 96), status, transcript.StartedAt.Value, transcript.LastActivity.Value,
                Detail: transcript.Prompt, Model: FirstNonEmpty(transcript.Model, "GitHub Copilot"),
                ThreadId: transcript.SessionId, Host: host);
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
        catch (JsonException) { return false; }
    }

    private static void ParseCopilotDocument(JsonElement root, CopilotTranscript transcript)
    {
        var value = root.TryGetProperty("v", out var versioned) && versioned.ValueKind == JsonValueKind.Object
            ? versioned : root;
        transcript.SessionId = FirstNonEmpty(FirstString(value, "sessionId", "session_id"), transcript.SessionId);
        transcript.Title = FirstNonEmpty(FirstString(value, "customTitle", "title"), transcript.Title);
        transcript.StartedAt = FirstTimestampAny(value, "creationDate", "createdAt", "created_at") ?? transcript.StartedAt;
        transcript.LastActivity = FirstTimestampAny(value, "lastMessageDate", "updatedAt", "updated_at") ?? transcript.LastActivity;
        transcript.Model = FirstNonEmpty(ParseCopilotModel(value), transcript.Model);
        if (value.TryGetProperty("requests", out var requests) && requests.ValueKind == JsonValueKind.Array)
        {
            var requestIndex = 0;
            foreach (var request in requests.EnumerateArray())
                ParseCopilotRequest(request, transcript, requestIndex++);
        }
        else
        {
            ParseCopilotEvent(value, transcript);
        }
    }

    private static void ParseCopilotRequest(JsonElement request, CopilotTranscript transcript, int requestIndex = -1)
    {
        if (requestIndex >= 0)
            transcript.LatestRequestIndex = Math.Max(transcript.LatestRequestIndex, requestIndex);
        var prompt = FirstStringDeep(request, "text", "prompt", "message", "content");
        if (prompt.Length > 0)
        {
            transcript.Prompt = prompt;
            transcript.Title = FirstNonEmpty(transcript.Title, SummarizeTitle(prompt) ?? prompt);
            transcript.HasUser = true;
        }
        transcript.Model = FirstNonEmpty(ParseCopilotModel(request), transcript.Model);
        var requestActivity = FirstTimestampAny(request, "timestamp", "createdAt", "created_at", "completedAt");
        if (requestActivity is { } requestTime)
            transcript.LastActivity = transcript.LastActivity is { } previous ? Max(previous, requestTime) : requestTime;
        var status = FirstString(request, "status", "state", "finishReason", "finish_reason").ToLowerInvariant();
        if (status is "error" or "failed" or "cancelled")
            transcript.Failed = true;
        if (status is "running" or "pending" or "in_progress")
            transcript.Pending = true;
        if (status is "waiting" or "awaiting_approval" or "needs_input")
            transcript.Waiting = true;
        if (request.TryGetProperty("toolRequests", out var tools) && tools.ValueKind == JsonValueKind.Array && tools.GetArrayLength() > 0)
        {
            transcript.Pending = true;
            transcript.Step = $"Running {FirstStringDeep(tools[tools.GetArrayLength() - 1], "name", "tool", "id")}";
        }
        var hasResponse = request.TryGetProperty("response", out var response)
            && response.ValueKind is JsonValueKind.Array or JsonValueKind.Object
            && (response.ValueKind != JsonValueKind.Array || response.GetArrayLength() > 0);
        if (hasResponse)
        {
            transcript.HasAssistant = true;
            var completed = CopilotRequestCompleted(request);
            transcript.Pending = completed is false;
            transcript.Waiting = false;
            transcript.Step = FirstNonEmpty(
                response.ValueKind == JsonValueKind.Array ? ExtractContentText(response) : FirstStringDeep(response, "text", "value", "content"),
                transcript.Step);
        }
        else if (transcript.HasUser)
        {
            transcript.Pending = true;
        }
    }

    private static void ParseCopilotEvent(JsonElement root, CopilotTranscript transcript)
    {
        if (root.TryGetProperty("kind", out var kindNode) && kindNode.ValueKind == JsonValueKind.Number
            && kindNode.TryGetInt32(out var kind))
        {
            if (kind == 0 && root.TryGetProperty("v", out var snapshot) && snapshot.ValueKind == JsonValueKind.Object)
            {
                ParseCopilotDocument(root, transcript);
                return;
            }
            if (root.TryGetProperty("k", out var key) && key.ValueKind == JsonValueKind.Array)
            {
                ParseCopilotDelta(root, key, transcript);
                return;
            }
        }

        transcript.SessionId = FirstNonEmpty(FirstString(root, "sessionId", "session_id"), transcript.SessionId);
        var eventActivity = FirstTimestampAny(root, "timestamp", "time", "createdAt", "created_at", "ts");
        if (eventActivity is { } eventTime)
            transcript.LastActivity = transcript.LastActivity is { } previous ? Max(previous, eventTime) : eventTime;
        transcript.Model = FirstNonEmpty(FirstStringDeep(root, "model", "modelId", "model_id", "selectedModel"), transcript.Model);
        var type = FirstString(root, "type", "event", "kind").ToLowerInvariant();
        var payload = root.TryGetProperty("data", out var data) ? data : root;
        if (type is "session.start" or "session_started")
        {
            transcript.SessionId = FirstNonEmpty(FirstString(payload, "sessionId", "session_id"), transcript.SessionId);
            transcript.StartedAt = FirstTimestampAny(payload, "startTime", "startedAt", "createdAt") ?? transcript.StartedAt;
            return;
        }

        var role = FirstString(root, "role").ToLowerInvariant();
        if (type is "user.message" or "user_message" or "user" or "request" || role == "user")
        {
            var prompt = FirstStringDeep(payload, "text", "prompt", "content", "message");
            if (prompt.Length > 0)
            {
                transcript.Prompt = prompt;
                transcript.Title = FirstNonEmpty(transcript.Title, SummarizeTitle(prompt) ?? prompt);
                transcript.HasUser = true;
            }
            transcript.Pending = true;
            return;
        }

        if (type is "assistant.message" or "assistant_message" or "assistant" or "response" || role == "assistant")
        {
            transcript.HasAssistant = true;
            var tool = payload.TryGetProperty("toolRequests", out var tools) && tools.ValueKind == JsonValueKind.Array
                ? tools : default;
            if (tool.ValueKind == JsonValueKind.Array && tool.GetArrayLength() > 0)
            {
                transcript.Pending = true;
                var name = FirstStringDeep(tool[tool.GetArrayLength() - 1], "name", "tool", "id");
                transcript.Step = string.IsNullOrWhiteSpace(name) ? "Running tool" : $"Running {name}";
            }
            else
            {
                transcript.Pending = false;
                transcript.Waiting = false;
                transcript.Step = FirstNonEmpty(
                    FirstStringDeep(payload, "reasoningText", "reasoning", "text", "content"), transcript.Step);
            }
            var status = FirstString(payload, "status", "state", "finishReason", "finish_reason").ToLowerInvariant();
            if (status is "error" or "failed") transcript.Failed = true;
            if (status is "running" or "pending" or "in_progress") transcript.Pending = true;
            if (status is "waiting" or "awaiting_approval" or "needs_input") transcript.Waiting = true;
        }
    }

    private static void ParseCopilotDelta(JsonElement root, JsonElement key, CopilotTranscript transcript)
    {
        if (key.GetArrayLength() < 3
            || !string.Equals(key[0].GetString(), "requests", StringComparison.OrdinalIgnoreCase)
            || !key[1].TryGetInt32(out var requestIndex))
            return;

        // Deltas can arrive for an earlier request after a newer request has
        // already been persisted. They must not overwrite the live status of
        // the newest request.
        if (requestIndex < transcript.LatestRequestIndex)
            return;
        transcript.LatestRequestIndex = requestIndex;
        var field = key[2].GetString() ?? "";
        var value = root.TryGetProperty("v", out var valueNode) ? valueNode : default;
        if (field.Equals("modelState", StringComparison.OrdinalIgnoreCase))
        {
            if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty("completedAt", out _))
            {
                transcript.Pending = false;
                transcript.Waiting = false;
            }
            else if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty("value", out var state)
                && state.ValueKind == JsonValueKind.Number && state.TryGetInt32(out var stateValue))
            {
                transcript.Pending = stateValue != 1;
            }
        }
        else if (field.Equals("response", StringComparison.OrdinalIgnoreCase))
        {
            transcript.HasAssistant = true;
            if (value.ValueKind == JsonValueKind.Array)
                transcript.Step = FirstNonEmpty(ExtractContentText(value), transcript.Step);
        }
        else if (field.Equals("result", StringComparison.OrdinalIgnoreCase))
        {
            transcript.Pending = false;
            transcript.Waiting = false;
        }
    }

    private static bool? CopilotRequestCompleted(JsonElement request)
    {
        if (!request.TryGetProperty("modelState", out var state) || state.ValueKind != JsonValueKind.Object)
            return null;
        if (state.TryGetProperty("completedAt", out _))
            return true;
        if (state.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var numeric))
            return numeric == 1;
        return null;
    }

    private static string ParseCopilotModel(JsonElement value)
    {
        if (!value.TryGetProperty("selectedModel", out var model))
            return FirstString(value, "model", "modelId", "model_id");
        return model.ValueKind == JsonValueKind.Object
            ? FirstString(model, "family", "identifier", "id")
            : model.ValueKind == JsonValueKind.String ? Clean(model.GetString()) : "";
    }

    private void AddGrokSessions(List<(ProviderId, string, DateTimeOffset)> output,
        CancellationToken cancellationToken)
    {
        var grokHome = Environment.GetEnvironmentVariable("GROK_HOME");
        if (string.IsNullOrWhiteSpace(grokHome))
            grokHome = Path.Combine(_home, ".grok");

        var root = Path.Combine(grokHome, "sessions");
        if (!Directory.Exists(root))
            return;

        try
        {
            foreach (var summaryPath in Directory.EnumerateFiles(root, "summary.json", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sessionDirectory = Path.GetDirectoryName(summaryPath);
                if (string.IsNullOrWhiteSpace(sessionDirectory))
                    continue;

                var modified = File.GetLastWriteTimeUtc(summaryPath);
                foreach (var name in new[] { "chat_history.jsonl", "events.jsonl", "prompt_context.json" })
                {
                    var companion = Path.Combine(sessionDirectory, name);
                    if (File.Exists(companion))
                    {
                        var companionModified = File.GetLastWriteTimeUtc(companion);
                        if (companionModified > modified)
                            modified = companionModified;
                    }
                }

                if (DateTime.UtcNow - modified <= RecentWindow)
                    output.Add((ProviderId.Grok, summaryPath,
                        new DateTimeOffset(modified, TimeSpan.Zero).ToLocalTime()));
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private void AddAntigravityDatabases(List<(ProviderId, string, DateTimeOffset)> output,
        CancellationToken cancellationToken)
    {
        var root = GetAntigravityCliHome();
        var conversations = Path.Combine(root, "conversations");
        if (!Directory.Exists(conversations))
            return;

        try
        {
            foreach (var path in Directory.EnumerateFiles(conversations, "*.db", SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var modified = File.GetLastWriteTimeUtc(path);
                var wal = path + "-wal";
                if (File.Exists(wal))
                {
                    var walModified = File.GetLastWriteTimeUtc(wal);
                    if (walModified > modified)
                        modified = walModified;
                }

                if (DateTime.UtcNow - modified <= RecentWindow)
                    output.Add((ProviderId.Antigravity, path,
                        new DateTimeOffset(modified, TimeSpan.Zero).ToLocalTime()));
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static string GetAntigravityCliHome()
    {
        var root = Environment.GetEnvironmentVariable("ANTIGRAVITY_CLI_HOME");
        return string.IsNullOrWhiteSpace(root)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gemini", "antigravity-cli")
            : root;
    }

    private static string GetAntigravityGuiHome()
    {
        var root = Environment.GetEnvironmentVariable("ANTIGRAVITY_GUI_HOME");
        return string.IsNullOrWhiteSpace(root)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gemini", "antigravity")
            : root;
    }

    private void AddAntigravityGuiTranscripts(List<(ProviderId, string, DateTimeOffset)> output,
        CancellationToken cancellationToken)
    {
        var root = Path.Combine(GetAntigravityGuiHome(), "brain");
        if (!Directory.Exists(root))
            return;

        try
        {
            foreach (var path in Directory.EnumerateFiles(root, "transcript.jsonl", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var modified = File.GetLastWriteTimeUtc(path);
                if (DateTime.UtcNow - modified <= RecentWindow)
                    output.Add((ProviderId.Antigravity, path,
                        new DateTimeOffset(modified, TimeSpan.Zero).ToLocalTime()));
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static bool IsAntigravityGuiTranscript(string path)
        => Path.GetFileName(path).Equals("transcript.jsonl", StringComparison.OrdinalIgnoreCase)
            && path.Contains($"{Path.DirectorySeparatorChar}brain{Path.DirectorySeparatorChar}",
                StringComparison.OrdinalIgnoreCase);

    private static AntigravityConversationMetadata? ReadAntigravityMetadata(string root, string id)
    {
        var path = Path.Combine(root, "cache", "conversation_metadata.json");
        try
        {
            if (!File.Exists(path))
                return null;
            return ParseAntigravityMetadata(File.ReadAllText(path), id);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        catch (JsonException) { return null; }
    }

    private static AntigravityConversationMetadata? ParseAntigravityMetadata(string json, string id)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (!root.TryGetProperty("conversations", out var conversations)
            || conversations.ValueKind != JsonValueKind.Object
            || !conversations.TryGetProperty(id, out var entry)
            || entry.ValueKind != JsonValueKind.Object)
            return null;

        var summary = entry.TryGetProperty("summary", out var summaryNode)
            && summaryNode.ValueKind == JsonValueKind.Object
            ? summaryNode
            : entry;
        var workspace = "";
        if (summary.TryGetProperty("WorkspaceURIs", out var workspaceNodes)
            && workspaceNodes.ValueKind == JsonValueKind.Array)
        {
            workspace = workspaceNodes.EnumerateArray()
                .Where(value => value.ValueKind == JsonValueKind.String)
                .Select(value => value.GetString() ?? "")
                .Select(ToAntigravityWorkspacePath)
                .FirstOrDefault(value => value.Length > 0) ?? "";
        }

        return new AntigravityConversationMetadata(
            FirstString(summary, "ID", "id"),
            Clean(FirstString(summary, "Title", "title")),
            Clean(FirstString(summary, "Preview", "preview")),
            workspace,
            FirstTimestamp(summary, "UpdatedAt", "updatedAt", "updated_at"),
            Clean(FirstString(summary, "AgentName", "agentName")));
    }

    internal static (string Title, string Preview, string Workspace)? ParseAntigravityMetadataForTesting(
        string json, string id)
    {
        var metadata = ParseAntigravityMetadata(json, id);
        return metadata is null ? null : (metadata.Title, metadata.Preview, metadata.Workspace);
    }

    private static string ToAntigravityWorkspacePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.IsFile)
            return NormalizeAntigravityPath(uri.LocalPath);
        return NormalizeAntigravityPath(value.StartsWith("file://", StringComparison.OrdinalIgnoreCase)
            ? Uri.UnescapeDataString(value[7..]).Replace('/', '\\')
            : value);
    }

    private static string NormalizeAntigravityPath(string value)
    {
        if (value.Length >= 2 && value[1] == ':' && char.IsLetter(value[0]))
            return char.ToUpperInvariant(value[0]) + value[1..];
        return value;
    }

    private static bool TryReadAntigravitySession(string dbPath, DateTimeOffset modified,
        bool claimLive, out AgentActivityItem item)
    {
        item = default!;
        try
        {
            var id = Path.GetFileNameWithoutExtension(dbPath);
            var metadata = ReadAntigravityMetadata(GetAntigravityCliHome(), id);
            var rows = ReadAntigravitySteps(dbPath);
            if (rows.Count == 0 && metadata is null)
                return false;

            var blobs = rows.SelectMany(row => row.Blobs).ToArray();
            var toolSummary = ExtractAntigravityField(blobs, "toolSummary");
            var toolAction = ExtractAntigravityField(blobs, "toolAction");
            var toolName = ExtractAntigravityField(blobs, "toolName", "tool_name", "name");
            var prompt = FirstNonEmpty(
                metadata?.Preview ?? "",
                ExtractAntigravityField(blobs, "prompt", "userMessage", "user_message"));
            var model = ExtractAntigravityField(blobs, "model", "model_id", "modelId");
            var parentId = ExtractAntigravityField(blobs,
                "parent_conversation_id", "parentConversationId", "parent_thread_id");
            var lastActivity = Max(modified, metadata?.UpdatedAt ?? modified);
            var fresh = DateTimeOffset.Now - lastActivity < BusyWindow;
            var live = claimLive && modified > DateTimeOffset.Now - RecentWindow;
            var waiting = AntigravityContainsAny(rows.Select(row => row.Permissions),
                "permission", "approval", "confirmation", "waiting_for_user", "request_user_input")
                || AntigravityContainsAny(blobs,
                    "waiting_for_user", "request_confirmation", "permission_requested", "needs_confirmation");
            var failed = AntigravityContainsAny(rows.Select(row => row.ErrorDetails),
                "error", "failed", "failure", "exception");
            var state = !live
                ? failed ? AgentActivityStatus.Failed : AgentActivityStatus.Completed
                : waiting
                    ? AgentActivityStatus.Waiting
                    : fresh ? AgentActivityStatus.Working : AgentActivityStatus.Idle;

            var action = FirstNonEmpty(Clean(toolSummary), Clean(toolAction));
            var step = state == AgentActivityStatus.Completed
                ? "Completed"
                : state == AgentActivityStatus.Failed
                    ? "Failed"
                    : state == AgentActivityStatus.Waiting
                        ? "Waiting for input"
                    : state == AgentActivityStatus.Idle
                        ? "Waiting for the next prompt"
                    : action.Length > 0
                        ? action
                        : DescribeAntigravityAction(toolName, ExtractAntigravityField(blobs, "command", "input"));
            var title = FirstNonEmpty(
                metadata?.Title ?? "",
                metadata?.Preview ?? "",
                SummarizeTitle(prompt) ?? "",
                metadata?.AgentName ?? "",
                "Antigravity");
            item = new AgentActivityItem(
                $"antigravity:{id}", ProviderId.Antigravity, Trim(Clean(title), 72), step, state,
                metadata?.UpdatedAt ?? modified, lastActivity,
                SubagentCount: rows.Count(row => row.HasSubtrajectory),
                Detail: prompt,
                Model: Clean(model),
                ThreadId: id,
                ParentThreadId: Clean(parentId));
            return true;
        }
        catch (SqliteException) { return false; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch (JsonException) { return false; }
    }

    internal static IReadOnlyList<AgentActivityItem> ReadAntigravityForTesting(string dbPath,
        bool claimLive = true)
    {
        var modified = File.GetLastWriteTimeUtc(dbPath);
        return TryReadAntigravitySession(dbPath,
            new DateTimeOffset(modified, TimeSpan.Zero).ToLocalTime(), claimLive,
            out var item)
            ? new[] { item }
            : Array.Empty<AgentActivityItem>();
    }

    private static bool TryReadAntigravityGuiSession(string transcriptPath, DateTimeOffset modified,
        bool claimLive, out AgentActivityItem item)
    {
        item = default!;
        try
        {
            var transcript = ReadAntigravityGuiTranscript(transcriptPath);
            var info = ParseAntigravityGuiTranscript(transcript);
            if (!info.HasConversation)
                return false;

            var lastActivity = Max(modified, info.LastActivity ?? modified);
            var fresh = DateTimeOffset.Now - lastActivity < BusyWindow;
            var live = claimLive && modified > DateTimeOffset.Now - RecentWindow;
            var status = !live
                ? info.State == TranscriptState.Failed ? AgentActivityStatus.Failed : AgentActivityStatus.Completed
                : info.State == TranscriptState.Waiting
                    ? AgentActivityStatus.Waiting
                    : fresh ? AgentActivityStatus.Working : AgentActivityStatus.Idle;
            var title = FirstNonEmpty(info.ConversationTitle, SummarizeTitle(info.Prompt) ?? "", "Antigravity");
            var step = status == AgentActivityStatus.Completed
                ? "Completed"
                : status == AgentActivityStatus.Failed
                    ? "Failed"
                    : status == AgentActivityStatus.Waiting
                        ? "Waiting for input"
                    : status == AgentActivityStatus.Idle
                        ? "Waiting for the next prompt"
                    : FirstNonEmpty(info.Step, info.Summary, "Thinking");
            var threadId = GetAntigravityGuiConversationId(transcriptPath);
            item = new AgentActivityItem(
                $"antigravity-gui:{threadId}", ProviderId.Antigravity, Trim(Clean(title), 72), step, status,
                info.StartedAt ?? modified, lastActivity,
                SubagentCount: info.SubagentCount,
                Detail: info.Prompt,
                Model: info.Model,
                ThreadId: threadId);
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
        catch (JsonException) { return false; }
    }

    internal static IReadOnlyList<AgentActivityItem> ReadAntigravityGuiForTesting(
        string transcriptPath, bool claimLive = true)
    {
        var modified = File.GetLastWriteTimeUtc(transcriptPath);
        return TryReadAntigravityGuiSession(transcriptPath,
            new DateTimeOffset(modified, TimeSpan.Zero).ToLocalTime(), claimLive,
            out var item)
            ? new[] { item }
            : Array.Empty<AgentActivityItem>();
    }

    private static string ReadAntigravityGuiTranscript(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length <= 524_288)
        {
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return reader.ReadToEnd();
        }

        const int headBytes = 131_072;
        using var headReader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true,
            bufferSize: 16_384, leaveOpen: true);
        var head = new char[headBytes];
        var headCount = headReader.ReadBlock(head, 0, head.Length);
        stream.Seek(-393_216, SeekOrigin.End);
        using var tailReader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false);
        return new string(head, 0, headCount) + Environment.NewLine + tailReader.ReadToEnd();
    }

    private static string GetAntigravityGuiConversationId(string transcriptPath)
    {
        var logs = Directory.GetParent(transcriptPath);
        var systemGenerated = logs?.Parent;
        var brain = systemGenerated?.Parent;
        return brain?.Name ?? Path.GetFileNameWithoutExtension(transcriptPath);
    }

    private static AntigravityGuiTranscriptInfo ParseAntigravityGuiTranscript(string text)
    {
        var prompt = "";
        var step = "";
        var summary = "";
        var conversationTitle = "";
        var model = "";
        var state = TranscriptState.Unknown;
        var hasConversation = false;
        var subagents = 0;
        DateTimeOffset? started = null;
        DateTimeOffset? activity = null;

        foreach (var line in text.Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                var type = FirstString(root, "type");
                var timestamp = FirstTimestamp(root, "created_at", "createdAt", "timestamp", "time");
                if (timestamp is { } parsedTimestamp)
                {
                    started ??= parsedTimestamp;
                    activity = activity is { } previous
                        ? Max(previous, parsedTimestamp)
                        : parsedTimestamp;
                }

                if (root.TryGetProperty("status", out var statusNode)
                    && statusNode.ValueKind == JsonValueKind.String)
                {
                    var statusText = statusNode.GetString() ?? "";
                    if (statusText.Contains("error", StringComparison.OrdinalIgnoreCase)
                        || statusText.Contains("fail", StringComparison.OrdinalIgnoreCase))
                        state = TranscriptState.Failed;
                    else if (statusText.Contains("wait", StringComparison.OrdinalIgnoreCase)
                        || statusText.Contains("pending", StringComparison.OrdinalIgnoreCase))
                        state = TranscriptState.Waiting;
                }

                switch (type.ToUpperInvariant())
                {
                    case "CHECKPOINT":
                        var checkpoint = FirstString(root, "content");
                        var checkpointTitle = ExtractAntigravityConversationTitle(checkpoint);
                        if (checkpointTitle.Length > 0)
                            conversationTitle = checkpointTitle;
                        break;

                    case "USER_INPUT":
                        var content = root.TryGetProperty("content", out var contentNode)
                            ? ExtractAntigravityGuiContent(contentNode)
                            : "";
                        if (content.Length > 0)
                            prompt = content;
                        hasConversation = true;
                        if (state is TranscriptState.Unknown or TranscriptState.Finished)
                            state = TranscriptState.Action;
                        step = "Thinking";
                        break;

                    case "PLANNER_RESPONSE":
                        hasConversation = true;
                        if (root.TryGetProperty("tool_calls", out var toolCalls)
                            && toolCalls.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var call in toolCalls.EnumerateArray().Reverse())
                            {
                                var name = FirstString(call, "name");
                                if (name.Length == 0)
                                    continue;
                                var args = call.TryGetProperty("args", out var argsNode)
                                    ? argsNode
                                    : default;
                                var toolSummary = FirstString(args, "toolSummary", "toolAction");
                                var action = string.IsNullOrWhiteSpace(toolSummary)
                                    ? DescribeAntigravityGuiAction(name, JsonText(args))
                                    : Clean(toolSummary);
                                var waiting = name.Contains("permission", StringComparison.OrdinalIgnoreCase)
                                    || name.Contains("question", StringComparison.OrdinalIgnoreCase)
                                    || name.Contains("approval", StringComparison.OrdinalIgnoreCase)
                                    || name.Equals("ask_permission", StringComparison.OrdinalIgnoreCase)
                                    || name.Equals("ask_question", StringComparison.OrdinalIgnoreCase);
                                state = waiting ? TranscriptState.Waiting : TranscriptState.Action;
                                step = waiting ? "Waiting for input" : action;
                                if (name.Contains("subagent", StringComparison.OrdinalIgnoreCase)
                                    || name.Equals("invoke_subagent", StringComparison.OrdinalIgnoreCase))
                                {
                                    if (args.ValueKind == JsonValueKind.Object
                                        && args.TryGetProperty("Subagents", out var subagentNodes)
                                        && subagentNodes.ValueKind == JsonValueKind.Array)
                                        subagents += subagentNodes.GetArrayLength();
                                    else
                                        subagents++;
                                }
                                break;
                            }
                        }
                        else
                        {
                            var thinking = FirstString(root, "thinking");
                            if (thinking.Length > 0)
                                summary = Clean(thinking);
                            if (state == TranscriptState.Unknown)
                                state = TranscriptState.Action;
                            if (step.Length == 0)
                                step = "Thinking";
                        }
                        model = FirstNonEmpty(model, FirstString(root, "model", "model_id", "modelId"));
                        break;

                    case "ERROR_MESSAGE":
                        hasConversation = true;
                        state = TranscriptState.Failed;
                        step = "Failed";
                        break;

                    case "RUN_COMMAND":
                    case "COMMAND_STATUS":
                        hasConversation = true;
                        state = TranscriptState.Action;
                        step = "Running command";
                        break;

                    case "VIEW_FILE":
                    case "LIST_DIRECTORY":
                    case "GREP_SEARCH":
                    case "FIND_BY_NAME":
                        hasConversation = true;
                        state = TranscriptState.Action;
                        step = "Inspected files";
                        break;

                    case "CODE_ACTION":
                    case "WRITE_TO_FILE":
                    case "REPLACE_FILE_CONTENT":
                    case "MULTI_REPLACE_FILE_CONTENT":
                        hasConversation = true;
                        state = TranscriptState.Action;
                        step = "Edited code";
                        break;
                }
            }
            catch (JsonException) { }
        }

        return new AntigravityGuiTranscriptInfo(prompt, step, summary, conversationTitle, model, started, activity,
            state, hasConversation, subagents);
    }

    private static string ExtractAntigravityConversationTitle(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return "";

        var match = Regex.Match(content,
            @"^\s*#\s*USER\s+OBJECTIVE\s*:\s*(?<title>[^\r\n]+)",
            RegexOptions.IgnoreCase | RegexOptions.Multiline);
        return match.Success ? Clean(match.Groups["title"].Value) : "";
    }

    internal static AgentActivityScanner.TranscriptState ParseAntigravityGuiStateForTesting(string text)
        => ParseAntigravityGuiTranscript(text).State;

    private static string ExtractAntigravityGuiContent(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String)
            return Clean(value.GetString());
        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var name in new[] { "text", "message", "content", "prompt" })
            {
                if (value.TryGetProperty(name, out var nested))
                {
                    var text = ExtractAntigravityGuiContent(nested);
                    if (text.Length > 0)
                        return text;
                }
            }
        }
        if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var part in value.EnumerateArray().Reverse())
            {
                var text = ExtractAntigravityGuiContent(part);
                if (text.Length > 0)
                    return text;
            }
        }
        return "";
    }

    private static string DescribeAntigravityGuiAction(string name, string details)
    {
        if (name is "run_command" or "command_status")
            return DescribeAction("shell_command", details);
        if (name.Contains("write", StringComparison.OrdinalIgnoreCase)
            || name.Contains("replace", StringComparison.OrdinalIgnoreCase)
            || name.Contains("edit", StringComparison.OrdinalIgnoreCase))
            return "Edited code";
        if (name.Contains("read", StringComparison.OrdinalIgnoreCase)
            || name.Contains("view", StringComparison.OrdinalIgnoreCase)
            || name.Contains("grep", StringComparison.OrdinalIgnoreCase)
            || name.Contains("search", StringComparison.OrdinalIgnoreCase)
            || name.Contains("list", StringComparison.OrdinalIgnoreCase)
            || name.Contains("find", StringComparison.OrdinalIgnoreCase))
            return "Inspected files";
        if (name.Contains("subagent", StringComparison.OrdinalIgnoreCase)
            || name.Contains("task", StringComparison.OrdinalIgnoreCase))
            return "Running subagent";
        return $"Running {Clean(name)}";
    }

    private static IReadOnlyList<AntigravityStepRow> ReadAntigravitySteps(string path)
    {
        var rows = new List<AntigravityStepRow>();
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Shared,
            Pooling = false,
        }.ToString();
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT idx, step_type, status, has_subtrajectory,
                   metadata, error_details, permissions, task_details,
                   render_info, step_payload
            FROM steps
            ORDER BY idx DESC
            LIMIT 80;
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new AntigravityStepRow(
                reader.GetInt32(0),
                reader.GetInt32(1),
                reader.GetInt32(2),
                !reader.IsDBNull(3) && reader.GetBoolean(3),
                ReadAntigravityBlob(reader, 4),
                ReadAntigravityBlob(reader, 5),
                ReadAntigravityBlob(reader, 6),
                ReadAntigravityBlob(reader, 7),
                ReadAntigravityBlob(reader, 8),
                ReadAntigravityBlob(reader, 9)));
        }
        return rows;
    }

    private static byte[]? ReadAntigravityBlob(SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetFieldValue<byte[]>(ordinal);

    private static string ExtractAntigravityField(IEnumerable<byte[]?> blobs, params string[] names)
    {
        foreach (var blob in blobs)
        {
            if (blob is null || blob.Length == 0)
                continue;
            var text = Encoding.UTF8.GetString(blob);
            foreach (var name in names)
            {
                var pattern = $"\\\"{Regex.Escape(name)}\\\"\\s*:\\s*\\\"(?<value>(?:\\\\.|[^\\\"\\\\])*)\\\"";
                var match = Regex.Match(text, pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);
                if (!match.Success)
                    continue;
                var raw = match.Groups["value"].Value;
                try
                {
                    return Clean(JsonSerializer.Deserialize<string>($"\\\"{raw}\\\"") ?? raw);
                }
                catch (JsonException)
                {
                    return Clean(raw);
                }
            }
        }
        return "";
    }

    private static bool AntigravityContainsAny(IEnumerable<byte[]?> blobs, params string[] markers)
    {
        foreach (var blob in blobs)
        {
            if (blob is null || blob.Length == 0)
                continue;
            var text = Encoding.UTF8.GetString(blob);
            if (markers.Any(marker => text.Contains(marker, StringComparison.OrdinalIgnoreCase)))
                return true;
        }
        return false;
    }

    private static string DescribeAntigravityAction(string name, string details)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "Thinking";
        var normalized = name.ToLowerInvariant();
        if (normalized.Contains("shell") || normalized.Contains("terminal")
            || normalized.Contains("command") || normalized is "bash" or "cmd" or "powershell")
            return DescribeAction("shell_command", details);
        if (normalized.Contains("edit") || normalized.Contains("write") || normalized.Contains("patch"))
            return "Edited code";
        if (normalized.Contains("read") || normalized.Contains("search") || normalized.Contains("grep")
            || normalized.Contains("glob") || normalized.Contains("list"))
            return "Inspected files";
        if (normalized.Contains("agent") || normalized.Contains("subagent"))
            return "Running subagent";
        return $"Running {Clean(name)}";
    }

    private void AddOpenCodeDatabases(List<(ProviderId, string, DateTimeOffset)> output,
        CancellationToken cancellationToken)
    {
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var overridePath = Environment.GetEnvironmentVariable("OPENCODE_DB_PATH");
        if (!string.IsNullOrWhiteSpace(overridePath))
            candidates.Add(overridePath);

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var xdgData = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        candidates.Add(Path.Combine(localAppData, "opencode", "opencode.db"));
        candidates.Add(Path.Combine(appData, "opencode", "opencode.db"));
        candidates.Add(Path.Combine(_home, ".local", "share", "opencode", "opencode.db"));
        if (!string.IsNullOrWhiteSpace(xdgData))
            candidates.Add(Path.Combine(xdgData, "opencode", "opencode.db"));

        foreach (var path in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (!File.Exists(path))
                    continue;
                var modified = File.GetLastWriteTimeUtc(path);
                // SQLite WAL mode can leave the main database timestamp unchanged while the
                // companion -wal file receives the active session writes. Use the newest companion
                // timestamp so an active OpenCode desktop session stays in the scan's recent set.
                foreach (var companion in new[] { path + "-wal", path + "-shm" })
                {
                    if (File.Exists(companion))
                    {
                        var companionModified = File.GetLastWriteTimeUtc(companion);
                        if (companionModified > modified)
                            modified = companionModified;
                    }
                }
                output.Add((ProviderId.OpenCode, path, new DateTimeOffset(modified, TimeSpan.Zero).ToLocalTime()));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private void AddZcodeDatabase(List<(ProviderId, string, DateTimeOffset)> output,
        CancellationToken cancellationToken)
    {
        var configured = Environment.GetEnvironmentVariable("ZCODE_DB_PATH");
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            string.IsNullOrWhiteSpace(configured)
                ? Path.Combine(_home, ".zcode", "cli", "db", "db.sqlite")
                : configured,
        };

        foreach (var path in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (!File.Exists(path))
                    continue;
                var modified = File.GetLastWriteTimeUtc(path);
                foreach (var companion in new[] { path + "-wal", path + "-shm" })
                {
                    if (File.Exists(companion))
                    {
                        var companionModified = File.GetLastWriteTimeUtc(companion);
                        if (companionModified > modified)
                            modified = companionModified;
                    }
                }
                if (DateTime.UtcNow - modified <= RecentWindow)
                    output.Add((ProviderId.Zai, path, new DateTimeOffset(modified, TimeSpan.Zero).ToLocalTime()));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static IReadOnlyList<AgentActivityItem> ReadZcodeSessions(
        string path, DateTimeOffset modified, bool claimLive)
    {
        var items = new List<AgentActivityItem>();
        try
        {
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadOnly,
                Cache = SqliteCacheMode.Shared,
                Pooling = false,
            }.ToString();
            using var connection = new SqliteConnection(connectionString);
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT id, parent_id, title, time_created, time_updated,
                       COALESCE((SELECT status FROM session_target st WHERE st.session_id = session.id), '')
                FROM session
                WHERE COALESCE(time_updated, time_created, 0) >= $cutoff
                ORDER BY time_updated DESC, time_created DESC
                LIMIT 80;
                """;
            command.Parameters.AddWithValue("$cutoff", DateTimeOffset.Now.Add(-RecentWindow).ToUnixTimeMilliseconds());

            var sessions = new List<ZcodeSessionRow>();
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    sessions.Add(new ZcodeSessionRow(
                        reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1),
                        reader.IsDBNull(2) ? "" : reader.GetString(2),
                        reader.IsDBNull(3) ? 0 : reader.GetInt64(3),
                        reader.IsDBNull(4) ? 0 : reader.GetInt64(4),
                        reader.IsDBNull(5) ? "" : reader.GetString(5)));
                }
            }

            bool claimed = false;
            foreach (var session in sessions)
            {
                var tail = ReadZcodeTail(connection, session.Id);
                var lastActivity = Max(FromUnixMilliseconds(session.UpdatedAt, modified), tail.LastActivity ?? modified);
                var fresh = DateTimeOffset.Now - lastActivity < BusyWindow;
                var live = claimLive && !claimed;
                if (live) claimed = true;

                var status = !live
                    ? tail.State == TranscriptState.Failed ? AgentActivityStatus.Failed : AgentActivityStatus.Completed
                    : session.TargetStatus is "paused" or "complete" || tail.State == TranscriptState.Waiting
                        ? AgentActivityStatus.Waiting
                        : fresh ? AgentActivityStatus.Working : AgentActivityStatus.Idle;
                if (session.TargetStatus == "complete" && !live)
                    status = AgentActivityStatus.Completed;

                var step = status switch
                {
                    AgentActivityStatus.Completed => "Completed",
                    AgentActivityStatus.Failed => "Failed",
                    AgentActivityStatus.Waiting => FirstNonEmpty(tail.Step, "Waiting for input"),
                    AgentActivityStatus.Idle => "Waiting for the next prompt",
                    _ => FirstNonEmpty(tail.Step, fresh ? "Working" : "Thinking"),
                };
                items.Add(new AgentActivityItem(
                    $"zcode:{session.Id}", ProviderId.Zai,
                    string.IsNullOrWhiteSpace(session.Title) ? SummarizeTitle(tail.Prompt) ?? "ZCode" : Trim(Clean(session.Title), 72),
                    step, status, FromUnixMilliseconds(session.CreatedAt, modified), lastActivity,
                    Detail: tail.Prompt, Model: tail.Model, ThreadId: session.Id, ParentThreadId: session.ParentId));
            }
        }
        catch (SqliteException) { }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return items;
    }

    private static ZcodeTail ReadZcodeTail(SqliteConnection connection, string sessionId)
    {
        var messages = new List<(string Role, string Data, long Created, long Updated)>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT data, time_created, time_updated FROM message WHERE session_id = $session ORDER BY time_created DESC LIMIT 100;";
            command.Parameters.AddWithValue("$session", sessionId);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var data = reader.GetString(0);
                try
                {
                    using var doc = JsonDocument.Parse(data);
                    var role = FirstString(doc.RootElement, "role").ToLowerInvariant();
                    messages.Add((role, data, reader.GetInt64(1), reader.GetInt64(2)));
                }
                catch (JsonException) { }
            }
        }

        var user = messages.FirstOrDefault(x => x.Role == "user");
        var assistant = messages.FirstOrDefault(x => x.Role == "assistant");
        var prompt = user.Data.Length == 0 ? "" : ReadZcodeText(user.Data, "content", "text");
        var model = assistant.Data.Length == 0 ? "" : FirstJsonString(assistant.Data, "modelID", "modelId", "model");
        var assistantActivity = assistant.Data.Length == 0
            ? DateTimeOffset.MinValue
            : FromUnixMilliseconds(Math.Max(assistant.Created, assistant.Updated), DateTimeOffset.MinValue);
        var activity = messages.Select(x => FromUnixMilliseconds(Math.Max(x.Created, x.Updated), DateTimeOffset.MinValue)).DefaultIfEmpty().Max();
        var state = assistant.Data.Length == 0 || user.Created > assistant.Updated ? TranscriptState.Action : TranscriptState.Finished;
        var step = state == TranscriptState.Action ? "Thinking" : "";

        using var partCommand = connection.CreateCommand();
        partCommand.CommandText = "SELECT data, time_created, time_updated FROM part WHERE session_id = $session ORDER BY time_created DESC LIMIT 200;";
        partCommand.Parameters.AddWithValue("$session", sessionId);
        using var parts = partCommand.ExecuteReader();
        while (parts.Read())
        {
            var data = parts.GetString(0);
            try
            {
                using var doc = JsonDocument.Parse(data);
                var root = doc.RootElement;
                var type = FirstString(root, "type").ToLowerInvariant();
                var partActivity = FromUnixMilliseconds(Math.Max(parts.GetInt64(1), parts.GetInt64(2)), DateTimeOffset.MinValue);
                activity = Max(activity, partActivity);
                if (type == "tool" && partActivity >= assistantActivity)
                {
                    var tool = FirstString(root, "tool", "name", "toolName");
                    var statusText = root.TryGetProperty("state", out var stateNode) ? FirstString(stateNode, "status") : "";
                    state = statusText is "error" or "failed" ? TranscriptState.Failed
                        : statusText is "pending" or "waiting" ? TranscriptState.Waiting : TranscriptState.Action;
                    step = DescribeAction(tool, FirstStringDeep(root, "command", "path", "input"));
                    break;
                }
                if (type is "reasoning" or "text")
                    step = FirstNonEmpty(FirstString(root, "text", "reasoning"), step);
            }
            catch (JsonException) { }
        }
        return new ZcodeTail(prompt, step, model, activity == DateTimeOffset.MinValue ? null : activity, state,
            FirstStringDeepFromJson(messages.FirstOrDefault(x => x.Role == "assistant").Data, "error"));
    }

    private static string ReadZcodeText(string json, params string[] names)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return FirstStringDeep(doc.RootElement, names);
        }
        catch (JsonException) { return ""; }
    }

    private static string FirstJsonString(string json, params string[] names) => ReadZcodeText(json, names);
    private static string FirstStringDeepFromJson(string json, params string[] names) => string.IsNullOrWhiteSpace(json) ? "" : ReadZcodeText(json, names);

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
            ClaudeSessionContext? claude = provider == ProviderId.Claude
                ? ReadClaudeSessionContext(path, info.ThreadId)
                : null;
            bool freshAgentEvent = info.LastActivity is { } activity && DateTimeOffset.Now - activity < BusyWindow;
            // One desktop provider process can host several concurrent threads. The process claim marks
            // the session live; transcript timestamps then distinguish working from quiet/idle.
            bool live = claimLive && modified > DateTimeOffset.Now - RecentWindow;
            bool busy = live && freshAgentEvent;
            // Process liveness is authoritative: a quiet live process is idle, not completed. This is the
            // same busy/idle/done model used by agent-notch and avoids treating Claude's interactive shell
            // as finished merely because its last assistant turn ended.
            var status = !live
                ? AgentActivityStatus.Completed
                : info.State == TranscriptState.Waiting
                    ? AgentActivityStatus.Waiting
                    : freshAgentEvent ? AgentActivityStatus.Working : AgentActivityStatus.Idle;
            string? threadId = session.ThreadId ?? info.ThreadId;
            if (provider == ProviderId.Claude && string.IsNullOrWhiteSpace(threadId))
                threadId = claude?.ThreadId;

            var title = provider == ProviderId.Codex
                ? _threadNames.GetName(threadId) ?? SummarizeTitle(info.Prompt) ?? provider.ToString()
                : SummarizeTitle(info.Prompt) ?? claude?.ProjectTitle ?? provider.ToString();
            // A reasoning summary is useful while the agent is working, but becomes stale and misleading
            // once a final response has landed. Completion is deliberately a stable, unambiguous state.
            var step = status == AgentActivityStatus.Completed
                ? "Completed"
                : status == AgentActivityStatus.Waiting
                    ? string.IsNullOrWhiteSpace(info.Step) ? "Waiting for input" : info.Step
                : status == AgentActivityStatus.Idle ? "Waiting for the next prompt"
                : info.State == TranscriptState.Action && !string.IsNullOrWhiteSpace(info.Step)
                    ? info.Step
                : !string.IsNullOrWhiteSpace(info.Summary) ? info.Summary
                    : string.IsNullOrWhiteSpace(info.Step) ? (busy ? "Working" : "Thinking") : info.Step;
            item = new AgentActivityItem(
                path, provider, title, step, status,
                info.StartedAt ?? modified, info.LastActivity ?? modified,
                SubagentCount: 0,
                Detail: info.Prompt,
                Model: info.Model,
                ThreadId: threadId,
                ParentThreadId: provider == ProviderId.Claude
                    ? claude?.ParentThreadId ?? info.ParentThreadId
                    : session.ParentThreadId ?? info.ParentThreadId,
                Host: session.Host ?? info.Host);
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
        catch (JsonException) { return false; }
    }

    private static bool TryReadClineSession(string metadataPath, DateTimeOffset modified,
        bool claimLive, out AgentActivityItem item)
    {
        item = default!;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(metadataPath));
            var root = document.RootElement;
            var sessionId = FirstNonEmpty(
                FirstString(root, "session_id", "sessionId"),
                Path.GetFileNameWithoutExtension(metadataPath));
            var metadata = root.TryGetProperty("metadata", out var metadataNode)
                && metadataNode.ValueKind == JsonValueKind.Object
                ? metadataNode
                : default;
            var prompt = FirstNonEmpty(FirstString(metadata, "prompt"), FirstString(root, "prompt"));
            var title = FirstNonEmpty(FirstString(metadata, "title", "name"), FirstString(root, "title"));
            var model = FirstString(root, "model");
            var statusText = FirstString(root, "status").ToLowerInvariant();
            var startedAt = FirstTimestampAny(root, "started_at", "startedAt", "created_at", "createdAt")
                ?? modified;
            var messagesPath = FirstString(root, "messages_path", "messagesPath");
            if (string.IsNullOrWhiteSpace(messagesPath))
            {
                messagesPath = Path.Combine(
                    Path.GetDirectoryName(metadataPath) ?? "",
                    Path.GetFileNameWithoutExtension(metadataPath) + ".messages.json");
            }
            else if (!Path.IsPathRooted(messagesPath))
            {
                messagesPath = Path.Combine(Path.GetDirectoryName(metadataPath) ?? "", messagesPath);
            }

            var tail = ReadClineMessages(messagesPath);
            prompt = FirstNonEmpty(prompt, tail.Prompt);
            title = FirstNonEmpty(title, SummarizeTitle(prompt) ?? "Cline");
            model = FirstNonEmpty(model, tail.Model);
            var lastActivity = Max(modified, tail.LastActivity ?? modified);
            var live = claimLive && modified > DateTimeOffset.Now - RecentWindow;
            var fresh = DateTimeOffset.Now - lastActivity < BusyWindow;
            var failed = statusText.Contains("fail", StringComparison.Ordinal)
                || statusText.Contains("error", StringComparison.Ordinal)
                || tail.State == TranscriptState.Failed;
            var status = !live
                ? failed ? AgentActivityStatus.Failed : AgentActivityStatus.Completed
                : tail.State == TranscriptState.Waiting
                    ? AgentActivityStatus.Waiting
                    : fresh ? AgentActivityStatus.Working : AgentActivityStatus.Idle;
            var step = status switch
            {
                AgentActivityStatus.Completed => "Completed",
                AgentActivityStatus.Failed => "Failed",
                AgentActivityStatus.Waiting => FirstNonEmpty(tail.Step, "Waiting for input"),
                AgentActivityStatus.Idle => "Waiting for the next prompt",
                _ => FirstNonEmpty(tail.Step, tail.Summary, "Thinking"),
            };
            item = new AgentActivityItem(
                $"cline:{sessionId}", ProviderId.Cline, Trim(Clean(title), 72), step, status,
                startedAt, lastActivity,
                Detail: prompt,
                Model: model,
                ThreadId: sessionId);
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
        catch (JsonException) { return false; }
    }

    private static ClineTail ReadClineMessages(string path)
    {
        if (!File.Exists(path))
            return new ClineTail();

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            var messages = root.ValueKind == JsonValueKind.Array
                ? root
                : root.TryGetProperty("messages", out var messagesNode)
                    && messagesNode.ValueKind == JsonValueKind.Array
                    ? messagesNode
                    : default;
            if (messages.ValueKind != JsonValueKind.Array)
                return new ClineTail();

            var result = new ClineTail();
            foreach (var message in messages.EnumerateArray())
            {
                var timestamp = FirstTimestampAny(message, "ts", "timestamp", "created_at", "createdAt");
                if (timestamp is { } activity
                    && (result.LastActivity is null || activity > result.LastActivity))
                    result.LastActivity = activity;

                var role = (FirstString(message, "role") ?? "").ToLowerInvariant();
                if (!message.TryGetProperty("content", out var content))
                    continue;

                if (content.ValueKind == JsonValueKind.String)
                {
                    ApplyClineText(role, content.GetString(), result);
                    continue;
                }
                if (content.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (var part in content.EnumerateArray())
                {
                    if (part.ValueKind != JsonValueKind.Object)
                        continue;
                    var type = (FirstString(part, "type") ?? "").ToLowerInvariant();
                    if (type is "text" or "thinking" or "reasoning")
                    {
                        var text = FirstString(part, "text", "thinking", "reasoning") ?? "";
                        ApplyClineText(role, text, result, type != "text");
                    }
                    else if (type is "tool_use" or "tool_call" or "function_call")
                    {
                        var name = FirstString(part, "name", "tool_name", "toolName", "function") ?? "tool";
                        var details = GetPayloadDetails(part);
                        result.State = IsWaitingTool(name) ? TranscriptState.Waiting : TranscriptState.Action;
                        result.Step = result.State == TranscriptState.Waiting
                            ? "Waiting for input"
                            : DescribeAction(name, details);
                    }
                    else if (type is "tool_result" or "tool_output" or "function_result")
                    {
                        if (IsTrue(part, "is_error", "isError", "error"))
                        {
                            result.State = TranscriptState.Failed;
                            result.Step = "Failed";
                        }
                        else
                        {
                            result.State = TranscriptState.Action;
                            result.Step = "Processing tool result";
                        }
                    }
                }
            }
            return result;
        }
        catch (IOException) { return new ClineTail(); }
        catch (UnauthorizedAccessException) { return new ClineTail(); }
        catch (JsonException) { return new ClineTail(); }
    }

    private static void ApplyClineText(string role, string? rawText, ClineTail result, bool thinking = false)
    {
        var text = Clean(rawText);
        if (text.Length == 0)
            return;
        if (role == "user")
        {
            var prompt = ExtractUserPrompt(text);
            if (result.Prompt.Length == 0 && prompt.Length > 0)
                result.Prompt = prompt;
            result.State = TranscriptState.Action;
            result.Step = "Thinking";
        }
        else if (thinking)
        {
            result.State = TranscriptState.Action;
            result.Step = "Thinking";
        }
        else if (role == "assistant")
        {
            result.State = TranscriptState.Finished;
            result.Summary = text;
        }
    }

    private static string ExtractUserPrompt(string text)
    {
        var match = Regex.Match(text, "<user_input(?:\\s+[^>]*)?>(?<prompt>.*?)</user_input>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        return match.Success ? Clean(match.Groups["prompt"].Value) : text;
    }

    private static bool IsWaitingTool(string name)
        => name.Equals("AskUserQuestion", StringComparison.OrdinalIgnoreCase)
            || name.Equals("ExitPlanMode", StringComparison.OrdinalIgnoreCase)
            || name.Contains("approval", StringComparison.OrdinalIgnoreCase)
            || name.Contains("confirm", StringComparison.OrdinalIgnoreCase);

    private static bool TryReadKimiSession(string statePath, DateTimeOffset modified,
        bool claimLive, out AgentActivityItem item)
    {
        item = default!;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(statePath));
            var root = document.RootElement;
            var sessionDirectory = Path.GetDirectoryName(statePath) ?? "";
            var sessionId = Path.GetFileName(sessionDirectory);
            var title = FirstString(root, "title") ?? "";
            var startedAt = FirstTimestampAny(root, "createdAt", "created_at") ?? modified;
            var agents = root.TryGetProperty("agents", out var agentsNode)
                && agentsNode.ValueKind == JsonValueKind.Object
                ? agentsNode.EnumerateObject().Count()
                : 0;
            var wirePath = Path.Combine(sessionDirectory, "agents", "main", "wire.jsonl");
            if (!File.Exists(wirePath))
                wirePath = File.Exists(Path.Combine(sessionDirectory, "wire.jsonl"))
                    ? Path.Combine(sessionDirectory, "wire.jsonl")
                    : Directory.Exists(Path.Combine(sessionDirectory, "agents"))
                    ? Directory.EnumerateFiles(Path.Combine(sessionDirectory, "agents"), "wire.jsonl", SearchOption.AllDirectories).FirstOrDefault() ?? ""
                    : "";

            var tail = ReadKimiWire(wirePath);
            title = FirstNonEmpty(
                string.Equals(title, "New Session", StringComparison.OrdinalIgnoreCase) ? "" : title,
                SummarizeTitle(tail.Prompt) ?? "",
                "Kimi");
            var lastActivity = Max(modified, tail.LastActivity ?? modified);
            var live = claimLive && modified > DateTimeOffset.Now - RecentWindow;
            var fresh = DateTimeOffset.Now - lastActivity < BusyWindow;
            var status = !live
                ? tail.State == TranscriptState.Failed ? AgentActivityStatus.Failed : AgentActivityStatus.Completed
                : tail.State == TranscriptState.Waiting
                    ? AgentActivityStatus.Waiting
                    : fresh ? AgentActivityStatus.Working : AgentActivityStatus.Idle;
            var step = status switch
            {
                AgentActivityStatus.Completed => "Completed",
                AgentActivityStatus.Failed => "Failed",
                AgentActivityStatus.Waiting => FirstNonEmpty(tail.Step, "Waiting for input"),
                AgentActivityStatus.Idle => "Waiting for the next prompt",
                _ => FirstNonEmpty(tail.Step, tail.Summary, "Thinking"),
            };
            item = new AgentActivityItem(
                $"kimi:{sessionId}", ProviderId.Kimi, Trim(Clean(title), 72), step, status,
                startedAt, lastActivity,
                SubagentCount: Math.Max(0, agents - 1),
                Detail: tail.Prompt,
                Model: tail.Model,
                ThreadId: sessionId);
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
        catch (JsonException) { return false; }
    }

    private static KimiTail ReadKimiWire(string path)
    {
        var result = new KimiTail();
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return result;

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            stream.Seek(Math.Max(0, stream.Length - 262_144), SeekOrigin.Begin);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            while (reader.ReadLine() is { } line)
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;
                try
                {
                    using var document = JsonDocument.Parse(line);
                    ParseKimiRecord(document.RootElement, result);
                }
                catch (JsonException) { }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return result;
    }

    private static void ParseKimiRecord(JsonElement value, KimiTail result)
    {
        if (value.ValueKind != JsonValueKind.Object)
            return;

        var timestamp = FirstTimestampAny(value, "time", "timestamp", "created_at", "createdAt", "ts");
        if (timestamp is { } activity
            && (result.LastActivity is null || activity > result.LastActivity))
            result.LastActivity = activity;

        var nested = FirstObject(value, "event", "payload", "data", "params");
        if (nested.ValueKind == JsonValueKind.Object)
            ParseKimiRecord(nested, result);

        var type = FirstString(value, "type", "event", "kind", "method", "role") ?? "";
        var normalized = type.ToLowerInvariant();
        var text = FirstStringDeep(value, "prompt", "user_input", "text", "content", "message");
        var name = FirstStringDeep(value, "tool_name", "toolName", "function_name", "function", "tool", "name");
        var details = FirstStringDeep(value, "command", "path");
        if (details.Length == 0)
            details = FirstJsonTextDeep(value, "arguments", "input", "params");

        if (normalized.Contains("user", StringComparison.Ordinal)
            || normalized.Contains("prompt", StringComparison.Ordinal)
            || normalized.Contains("input", StringComparison.Ordinal))
        {
            if (result.Prompt.Length == 0 && text.Length > 0)
                result.Prompt = text;
            result.State = TranscriptState.Action;
            result.Step = "Thinking";
        }
        else if (normalized.Contains("approval", StringComparison.Ordinal)
            || normalized.Contains("permission", StringComparison.Ordinal)
            || normalized.Contains("ask_user", StringComparison.Ordinal)
            || normalized.Contains("request", StringComparison.Ordinal))
        {
            result.State = TranscriptState.Waiting;
            result.Step = "Waiting for input";
        }
        else if (normalized.Contains("error", StringComparison.Ordinal)
            || normalized.Contains("fail", StringComparison.Ordinal))
        {
            result.State = TranscriptState.Failed;
            result.Step = "Failed";
        }
        else if (normalized.Contains("tool", StringComparison.Ordinal)
            || normalized.Contains("function_call", StringComparison.Ordinal)
            || normalized.Contains("shell", StringComparison.Ordinal)
            || normalized.Contains("command", StringComparison.Ordinal))
        {
            result.State = TranscriptState.Action;
            result.Step = string.IsNullOrWhiteSpace(name) ? "Running tool" : DescribeAction(name, details);
        }
        else if (normalized.Contains("complete", StringComparison.Ordinal)
            || normalized.Contains("finish", StringComparison.Ordinal)
            || normalized.Contains("turn_end", StringComparison.Ordinal)
            || normalized.Contains("session_end", StringComparison.Ordinal))
        {
            result.State = TranscriptState.Finished;
            if (text.Length > 0)
                result.Summary = text;
        }
        else if (normalized.Contains("assistant", StringComparison.Ordinal)
            || normalized.Contains("response", StringComparison.Ordinal)
            || normalized.Contains("message", StringComparison.Ordinal))
        {
            if (text.Length > 0)
                result.Summary = text;
            result.State = TranscriptState.Finished;
        }

        if (result.Model.Length == 0)
            result.Model = FirstStringDeep(value, "model", "model_name", "modelName");
    }

    private static bool TryReadGrokSession(string summaryPath, DateTimeOffset modified,
        bool claimLive, out AgentActivityItem item)
    {
        item = default!;
        try
        {
            var sessionDirectory = Path.GetDirectoryName(summaryPath);
            if (string.IsNullOrWhiteSpace(sessionDirectory))
                return false;

            using var summaryDocument = JsonDocument.Parse(File.ReadAllText(summaryPath));
            var summary = summaryDocument.RootElement;
            var info = summary.TryGetProperty("info", out var infoNode) && infoNode.ValueKind == JsonValueKind.Object
                ? infoNode
                : default;
            var threadId = FirstNonEmpty(
                FirstString(info, "id"),
                FirstString(summary, "session_id", "sessionId", "id"),
                Path.GetFileName(sessionDirectory));
            var parentThreadId = FirstString(summary, "parent_session_id", "parentSessionId", "parent_thread_id", "parentThreadId");
            var model = FirstNonEmpty(
                FirstString(summary, "current_model_id", "model_id", "model"),
                FirstString(info, "current_model_id", "model_id", "model"));
            var createdAt = FirstTimestamp(summary, "created_at", "createdAt")
                ?? FirstTimestamp(info, "created_at", "createdAt")
                ?? modified;
            var summaryUpdatedAt = FirstTimestamp(summary, "last_active_at", "updated_at", "updatedAt")
                ?? FirstTimestamp(info, "last_active_at", "updated_at", "updatedAt");

            var history = ReadGrokTail(Path.Combine(sessionDirectory, "chat_history.jsonl"));
            var events = ReadGrokTail(Path.Combine(sessionDirectory, "events.jsonl"));
            var historyInfo = ParseGrokHistory(history);
            var eventInfo = ParseGrokEvents(events);
            var lastActivity = Max(modified, summaryUpdatedAt ?? modified,
                historyInfo.LastActivity ?? modified, eventInfo.LastActivity ?? modified);
            var fresh = DateTimeOffset.Now - lastActivity < BusyWindow;
            var live = claimLive && modified > DateTimeOffset.Now - RecentWindow;
            var state = eventInfo.State != TranscriptState.Unknown ? eventInfo.State : historyInfo.State;
            var status = !live
                ? state == TranscriptState.Failed ? AgentActivityStatus.Failed : AgentActivityStatus.Completed
                : state == TranscriptState.Waiting
                    ? AgentActivityStatus.Waiting
                    : fresh ? AgentActivityStatus.Working : AgentActivityStatus.Idle;

            var prompt = historyInfo.Prompt;
            var title = FirstNonEmpty(
                SummarizeTitle(prompt) ?? "",
                SummarizeTitle(FirstString(summary, "session_summary", "summary")) ?? "",
                FirstString(summary, "agent_name", "agentName"),
                "Grok");
            var step = status == AgentActivityStatus.Completed
                ? "Completed"
                : status == AgentActivityStatus.Failed
                    ? "Failed"
                    : status == AgentActivityStatus.Waiting
                        ? string.IsNullOrWhiteSpace(eventInfo.Step) ? "Waiting for input" : eventInfo.Step
                    : status == AgentActivityStatus.Idle ? "Waiting for the next prompt"
                    : !string.IsNullOrWhiteSpace(eventInfo.Step) ? eventInfo.Step
                    : !string.IsNullOrWhiteSpace(historyInfo.Step) ? historyInfo.Step
                    : !string.IsNullOrWhiteSpace(historyInfo.Summary) ? historyInfo.Summary
                    : "Thinking";
            item = new AgentActivityItem(
                $"grok:{threadId}", ProviderId.Grok, Trim(Clean(title), 72), step, status,
                createdAt, lastActivity,
                Detail: prompt,
                Model: Clean(model),
                ThreadId: threadId,
                ParentThreadId: parentThreadId,
                Host: DetectHost(summary));
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
        catch (JsonException) { return false; }
    }

    internal static IReadOnlyList<AgentActivityItem> ReadGrokForTesting(string summaryPath,
        bool claimLive = true)
    {
        var modified = File.GetLastWriteTimeUtc(summaryPath);
        return TryReadGrokSession(summaryPath,
            new DateTimeOffset(modified, TimeSpan.Zero).ToLocalTime(), claimLive,
            out var item)
            ? new[] { item }
            : Array.Empty<AgentActivityItem>();
    }

    private static string ReadGrokTail(string path)
    {
        try
        {
            if (!File.Exists(path))
                return "";
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            stream.Seek(Math.Max(0, stream.Length - 262_144), SeekOrigin.Begin);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return reader.ReadToEnd();
        }
        catch (IOException) { return ""; }
        catch (UnauthorizedAccessException) { return ""; }
    }

    private static GrokTranscriptInfo ParseGrokHistory(string text)
    {
        string prompt = "", step = "", summary = "";
        var state = TranscriptState.Unknown;
        DateTimeOffset? activity = null;
        foreach (var line in text.Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                var type = FirstString(root, "type");
                activity = FirstTimestamp(root, "timestamp", "ts", "created_at") ?? activity;
                if (type == "user")
                {
                    var synthetic = FirstString(root, "synthetic_reason", "syntheticReason");
                    var content = root.TryGetProperty("content", out var contentNode)
                        ? ExtractGrokText(contentNode)
                        : "";
                    if (synthetic.Length == 0 && content.Length > 0)
                        prompt = content;
                    if (state == TranscriptState.Unknown || content.Length > 0)
                    {
                        state = TranscriptState.Action;
                        step = "Thinking";
                    }
                }
                else if (type == "assistant")
                {
                    if (root.TryGetProperty("tool_calls", out var calls) && calls.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var call in calls.EnumerateArray().Reverse())
                        {
                            var name = FirstString(call, "name");
                            if (name.Length == 0 && call.TryGetProperty("function", out var function))
                                name = FirstString(function, "name");
                            if (name.Length > 0)
                            {
                                step = DescribeGrokAction(name, GetPayloadDetails(call));
                                state = TranscriptState.Action;
                                break;
                            }
                        }
                    }
                    else if (root.TryGetProperty("content", out var content))
                    {
                        summary = ExtractGrokText(content);
                    }
                }
                else if (type is "reasoning" or "backend_tool_call")
                {
                    summary = ExtractGrokText(root);
                    state = TranscriptState.Action;
                }
                else if (type is "tool_result" or "backend_tool_result")
                {
                    state = TranscriptState.Action;
                }
            }
            catch (JsonException) { }
        }

        return new GrokTranscriptInfo(prompt, step, summary, state, activity);
    }

    private static GrokTranscriptInfo ParseGrokEvents(string text)
    {
        string step = "";
        var state = TranscriptState.Unknown;
        DateTimeOffset? activity = null;
        foreach (var line in text.Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                var type = FirstString(root, "type");
                activity = FirstTimestamp(root, "ts", "timestamp", "created_at") ?? activity;
                switch (type)
                {
                    case "turn_started":
                    case "loop_started":
                        state = TranscriptState.Action;
                        step = "Thinking";
                        break;
                    case "phase_changed":
                        var phase = FirstString(root, "phase");
                        if (phase.Contains("permission", StringComparison.OrdinalIgnoreCase))
                        {
                            state = TranscriptState.Waiting;
                            step = "Waiting for input";
                        }
                        else if (phase.Length > 0)
                        {
                            state = TranscriptState.Action;
                            step = phase switch
                            {
                                "tool_execution" => "Running command",
                                "streaming_reasoning" => "Thinking",
                                "streaming_text" => "Writing response",
                                _ => step,
                            };
                        }
                        break;
                    case "tool_started":
                    case "mcp_tool_call_started":
                        var tool = FirstString(root, "tool_name", "tool", "name");
                        state = TranscriptState.Action;
                        step = tool.Length == 0 ? "Running command" : DescribeGrokAction(tool, GetPayloadDetails(root));
                        break;
                    case "permission_requested":
                        state = TranscriptState.Waiting;
                        step = "Waiting for input";
                        break;
                    case "permission_resolved":
                        var decision = FirstString(root, "decision");
                        state = decision.Contains("deny", StringComparison.OrdinalIgnoreCase)
                            || decision.Contains("cancel", StringComparison.OrdinalIgnoreCase)
                            ? TranscriptState.Failed
                            : TranscriptState.Action;
                        break;
                    case "turn_ended":
                        var outcome = FirstString(root, "outcome");
                        state = outcome.Contains("error", StringComparison.OrdinalIgnoreCase)
                            ? TranscriptState.Failed
                            : TranscriptState.Finished;
                        break;
                }
            }
            catch (JsonException) { }
        }

        return new GrokTranscriptInfo("", step, "", state, activity);
    }

    private static string ExtractGrokText(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String)
            return Clean(value.GetString());
        if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var part in value.EnumerateArray().Reverse())
            {
                var text = ExtractGrokText(part);
                if (text.Length > 0)
                    return text;
            }
            return "";
        }
        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var name in new[] { "text", "content", "summary", "summary_text", "message" })
            {
                if (!value.TryGetProperty(name, out var nested))
                    continue;
                var text = ExtractGrokText(nested);
                if (text.Length > 0)
                    return text;
            }
        }
        return "";
    }

    private static string DescribeGrokAction(string name, string? details)
    {
        var normalized = name.ToLowerInvariant();
        if (normalized.Contains("shell") || normalized.Contains("terminal")
            || normalized.Contains("command") || normalized is "bash" or "cmd" or "powershell")
            return DescribeAction("shell_command", details);
        if (normalized.Contains("patch") || normalized.Contains("edit") || normalized.Contains("write"))
            return "Edited code";
        if (normalized.Contains("read") || normalized.Contains("search") || normalized.Contains("grep")
            || normalized.Contains("glob") || normalized.Contains("list"))
            return "Inspected files";
        if (normalized.Contains("agent") || normalized.Contains("subagent"))
            return "Running subagent";
        return DescribeAction(name, details);
    }

    private static DateTimeOffset? FirstTimestamp(JsonElement value, params string[] names)
    {
        if (value.ValueKind != JsonValueKind.Object)
            return null;
        foreach (var name in names)
        {
            if (!value.TryGetProperty(name, out var property))
                continue;
            if (property.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(property.GetString(), out var parsed))
                return parsed.ToLocalTime();
        }
        return null;
    }

    private static DateTimeOffset? FirstTimestampAny(JsonElement value, params string[] names)
    {
        if (value.ValueKind != JsonValueKind.Object)
            return null;
        foreach (var name in names)
        {
            if (!value.TryGetProperty(name, out var property))
                continue;
            if (property.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(property.GetString(), out var parsed))
                return parsed.ToLocalTime();
            if (property.ValueKind == JsonValueKind.Number
                && property.TryGetInt64(out var numeric))
            {
                try
                {
                    return numeric > 10_000_000_000
                        ? DateTimeOffset.FromUnixTimeMilliseconds(numeric).ToLocalTime()
                        : DateTimeOffset.FromUnixTimeSeconds(numeric).ToLocalTime();
                }
                catch (ArgumentOutOfRangeException) { }
            }
        }
        return null;
    }

    private static JsonElement FirstObject(JsonElement value, params string[] names)
    {
        if (value.ValueKind != JsonValueKind.Object)
            return default;
        foreach (var name in names)
            if (value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Object)
                return property;
        return default;
    }

    private static string FirstStringDeep(JsonElement value, params string[] names)
    {
        if (value.ValueKind == JsonValueKind.String)
            return Clean(value.GetString());
        if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in value.EnumerateArray())
            {
                var result = FirstStringDeep(child, names);
                if (result.Length > 0)
                    return result;
            }
            return "";
        }
        if (value.ValueKind != JsonValueKind.Object)
            return "";

        foreach (var name in names)
        {
            if (!value.TryGetProperty(name, out var property))
                continue;
            var result = property.ValueKind == JsonValueKind.String
                ? Clean(property.GetString())
                : ExtractContentText(property);
            if (result.Length > 0)
                return result;
        }

        foreach (var property in value.EnumerateObject())
        {
            if (property.Value.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
                continue;
            var result = FirstStringDeep(property.Value, names);
            if (result.Length > 0)
                return result;
        }
        return "";
    }

    private static string FirstJsonTextDeep(JsonElement value, params string[] names)
    {
        if (value.ValueKind != JsonValueKind.Object)
            return "";
        foreach (var name in names)
        {
            if (!value.TryGetProperty(name, out var property))
                continue;
            if (property.ValueKind == JsonValueKind.String)
                return property.GetString() ?? "";
            if (property.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                return property.ToString();
        }
        foreach (var property in value.EnumerateObject())
        {
            if (property.Value.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
                continue;
            var result = FirstJsonTextDeep(property.Value, names);
            if (result.Length > 0)
                return result;
        }
        return "";
    }

    private static bool IsTrue(JsonElement value, params string[] names)
    {
        if (value.ValueKind != JsonValueKind.Object)
            return false;
        foreach (var name in names)
        {
            if (!value.TryGetProperty(name, out var property))
                continue;
            if (property.ValueKind == JsonValueKind.True)
                return true;
            if (property.ValueKind == JsonValueKind.String
                && bool.TryParse(property.GetString(), out var parsed))
                return parsed;
            if (property.ValueKind == JsonValueKind.Object)
                return true;
        }
        return false;
    }

    private static IReadOnlyList<AgentActivityItem> ReadOpenCodeSessions(
        string path, DateTimeOffset modified, bool claimLive)
    {
        var items = new List<AgentActivityItem>();
        try
        {
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadOnly,
                Cache = SqliteCacheMode.Shared,
                Pooling = false,
            }.ToString();
            using var connection = new SqliteConnection(connectionString);
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT id, parent_id, title, model, time_created, time_updated
                FROM session
                WHERE COALESCE(time_updated, time_created, 0) >= $cutoff
                ORDER BY time_updated DESC, time_created DESC
                LIMIT 80;
                """;
            command.Parameters.AddWithValue("$cutoff", DateTimeOffset.Now.Add(-RecentWindow).ToUnixTimeMilliseconds());

            var sessions = new List<OpenCodeSessionRow>();
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    sessions.Add(new OpenCodeSessionRow(
                        reader.GetString(0),
                        reader.IsDBNull(1) ? null : reader.GetString(1),
                        reader.IsDBNull(2) ? "" : reader.GetString(2),
                        reader.IsDBNull(3) ? "" : reader.GetString(3),
                        reader.IsDBNull(4) ? 0 : reader.GetInt64(4),
                        reader.IsDBNull(5) ? 0 : reader.GetInt64(5)));
                }
            }

            // OpenCode normally has one process serving several stored sessions. The newest session
            // receives the process claim; older sessions remain visible as recent/completed history.
            bool claimed = false;
            foreach (var session in sessions)
            {
                var tail = ReadOpenCodeTail(connection, session.Id);
                var lastActivity = Max(
                    FromUnixMilliseconds(session.UpdatedAt, modified),
                    tail.LastActivity ?? modified);
                var fresh = DateTimeOffset.Now - lastActivity < BusyWindow;
                var live = claimLive && !claimed;
                if (live)
                    claimed = true;

                var status = !live
                    ? tail.State == TranscriptState.Failed ? AgentActivityStatus.Failed : AgentActivityStatus.Completed
                    : tail.State == TranscriptState.Waiting
                        ? AgentActivityStatus.Waiting
                        : fresh ? AgentActivityStatus.Working : AgentActivityStatus.Idle;
                var step = status == AgentActivityStatus.Completed
                    ? "Completed"
                    : status == AgentActivityStatus.Failed
                        ? "Failed"
                        : status == AgentActivityStatus.Waiting
                            ? string.IsNullOrWhiteSpace(tail.Step) ? "Waiting for input" : tail.Step
                        : status == AgentActivityStatus.Idle ? "Waiting for the next prompt"
                        : !string.IsNullOrWhiteSpace(tail.Step) ? tail.Step
                        : !string.IsNullOrWhiteSpace(tail.Summary) ? tail.Summary
                        : fresh ? "Working" : "Thinking";

                items.Add(new AgentActivityItem(
                    $"opencode:{session.Id}", ProviderId.OpenCode,
                    string.IsNullOrWhiteSpace(session.Title)
                        ? SummarizeTitle(tail.Prompt) ?? "OpenCode"
                        : Trim(Clean(session.Title), 72),
                    step, status,
                    FromUnixMilliseconds(session.CreatedAt, modified), lastActivity,
                    Detail: tail.Prompt,
                    Model: ParseOpenCodeModel(session.Model),
                    ThreadId: session.Id,
                    ParentThreadId: session.ParentId));
            }
        }
        catch (SqliteException) { }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        return items;
    }

    internal static IReadOnlyList<AgentActivityItem> ReadOpenCodeForTesting(string path, bool claimLive = true)
        => ReadOpenCodeSessions(path, DateTimeOffset.Now, claimLive);

    internal static IReadOnlyList<AgentActivityItem> ReadZcodeForTesting(string path, bool claimLive = true)
        => ReadZcodeSessions(path, DateTimeOffset.Now, claimLive);

    internal static IReadOnlyList<AgentActivityItem> ReadCopilotForTesting(string path, bool claimLive = true)
    {
        var modified = File.GetLastWriteTimeUtc(path);
        return TryReadCopilotSession(path,
            new DateTimeOffset(modified, TimeSpan.Zero).ToLocalTime(), claimLive,
            out var item)
            ? new[] { item }
            : Array.Empty<AgentActivityItem>();
    }

    private static OpenCodeTail ReadOpenCodeTail(SqliteConnection connection, string sessionId)
    {
        var messages = new List<OpenCodeMessageRow>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT id, data, time_created, time_updated
                FROM message
                WHERE session_id = $session
                ORDER BY time_created DESC, id DESC
                LIMIT 100;
                """;
            command.Parameters.AddWithValue("$session", sessionId);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                messages.Add(new OpenCodeMessageRow(
                    reader.GetString(0), reader.GetString(1),
                    reader.IsDBNull(2) ? 0 : reader.GetInt64(2),
                    reader.IsDBNull(3) ? 0 : reader.GetInt64(3)));
            }
        }

        var parts = new List<OpenCodePartRow>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT id, message_id, data, time_created, time_updated
                FROM part
                WHERE session_id = $session
                ORDER BY time_created DESC, id DESC
                LIMIT 200;
                """;
            command.Parameters.AddWithValue("$session", sessionId);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                parts.Add(new OpenCodePartRow(
                    reader.GetString(0), reader.GetString(1), reader.GetString(2),
                    reader.IsDBNull(3) ? 0 : reader.GetInt64(3),
                    reader.IsDBNull(4) ? 0 : reader.GetInt64(4)));
            }
        }

        var parsedMessages = messages
            .Select(ParseOpenCodeMessage)
            .Where(message => message is not null)
            .Cast<OpenCodeMessageInfo>()
            .ToArray();
        var parsedParts = parts
            .Select(ParseOpenCodePart)
            .Where(part => part is not null)
            .Cast<OpenCodePartInfo>()
            .ToArray();
        var latestUser = parsedMessages.FirstOrDefault(message => message.Role == "user");
        var latestAssistant = parsedMessages.FirstOrDefault(message => message.Role == "assistant");
        var latestTool = parsedParts.FirstOrDefault(part => part.Type == "tool");
        var latestReasoning = parsedParts.FirstOrDefault(part => part.Type == "reasoning");

        var prompt = latestUser is null ? "" : ExtractOpenCodeMessageText(latestUser.Id, parts);
        var summary = latestAssistant is null ? "" : ExtractOpenCodeMessageText(latestAssistant.Id, parts);
        if (summary.Length == 0 && latestReasoning is not null)
            summary = latestReasoning.Text;

        var state = TranscriptState.Unknown;
        var step = "";
        if (latestTool is not null
            && (latestAssistant is null || latestTool.ActivityAt >= latestAssistant.CreatedAt))
        {
            state = latestTool.Status switch
            {
                "pending" => TranscriptState.Waiting,
                "running" => TranscriptState.Action,
                "error" => TranscriptState.Failed,
                _ => TranscriptState.Action,
            };
            step = DescribeOpenCodeTool(latestTool.Tool, latestTool.Input);
        }
        else if (latestUser is not null
            && (latestAssistant is null || latestUser.CreatedAt > latestAssistant.CreatedAt || !latestAssistant.Completed))
        {
            state = TranscriptState.Action;
            step = "Thinking";
        }
        else if (latestAssistant?.Failed == true)
        {
            state = TranscriptState.Failed;
        }
        else if (latestAssistant is not null)
        {
            state = TranscriptState.Finished;
        }

        var activity = parsedMessages.Select(message => message.ActivityAt)
            .Concat(parsedParts.Select(part => part.ActivityAt))
            .DefaultIfEmpty()
            .Max();
        return new OpenCodeTail(step, summary, prompt,
            activity == default ? null : activity, state);
    }

    private static OpenCodeMessageInfo? ParseOpenCodeMessage(OpenCodeMessageRow row)
    {
        try
        {
            using var document = JsonDocument.Parse(row.Data);
            var root = document.RootElement;
            var role = root.TryGetProperty("role", out var roleNode) ? roleNode.GetString() : null;
            if (role is not ("user" or "assistant"))
                return null;

            var created = FromUnixMilliseconds(row.CreatedAt, DateTimeOffset.MinValue);
            var completed = 0L;
            if (root.TryGetProperty("time", out var time) && time.ValueKind == JsonValueKind.Object
                && time.TryGetProperty("completed", out var completedNode)
                && completedNode.TryGetInt64(out var parsedCompleted))
                completed = parsedCompleted;
            var finish = root.TryGetProperty("finish", out var finishNode) ? finishNode.GetString() : null;
            var failed = root.TryGetProperty("error", out var errorNode) && errorNode.ValueKind != JsonValueKind.Null;
            var isComplete = completed > 0 || !string.IsNullOrWhiteSpace(finish) && finish != "tool-calls";
            var activity = Max(
                FromUnixMilliseconds(row.CreatedAt, DateTimeOffset.MinValue),
                FromUnixMilliseconds(row.UpdatedAt, DateTimeOffset.MinValue),
                FromUnixMilliseconds(completed, DateTimeOffset.MinValue));
            return new OpenCodeMessageInfo(row.Id, role!, created, activity, isComplete, failed);
        }
        catch (JsonException) { return null; }
    }

    private static OpenCodePartInfo? ParseOpenCodePart(OpenCodePartRow row)
    {
        try
        {
            using var document = JsonDocument.Parse(row.Data);
            var root = document.RootElement;
            var type = root.TryGetProperty("type", out var typeNode) ? typeNode.GetString() : null;
            if (string.IsNullOrWhiteSpace(type))
                return null;

            var activity = Max(
                FromUnixMilliseconds(row.CreatedAt, DateTimeOffset.MinValue),
                FromUnixMilliseconds(row.UpdatedAt, DateTimeOffset.MinValue));
            if (root.TryGetProperty("time", out var time) && time.ValueKind == JsonValueKind.Object)
            {
                if (time.TryGetProperty("start", out var start) && start.TryGetInt64(out var startTime))
                    activity = Max(activity, FromUnixMilliseconds(startTime, DateTimeOffset.MinValue));
                if (time.TryGetProperty("end", out var end) && end.TryGetInt64(out var endTime))
                    activity = Max(activity, FromUnixMilliseconds(endTime, DateTimeOffset.MinValue));
            }

            if (type == "tool")
            {
                var tool = root.TryGetProperty("tool", out var toolNode) ? toolNode.GetString() ?? "tool" : "tool";
                var status = "";
                var input = "";
                if (root.TryGetProperty("state", out var state) && state.ValueKind == JsonValueKind.Object)
                {
                    status = state.TryGetProperty("status", out var statusNode) ? statusNode.GetString() ?? "" : "";
                    if (state.TryGetProperty("input", out var inputNode))
                        input = JsonText(inputNode);
                }
                return new OpenCodePartInfo(row.MessageId, type, activity, tool, status, input, "");
            }

            var text = root.TryGetProperty("text", out var textNode) ? Clean(textNode.GetString()) : "";
            return new OpenCodePartInfo(row.MessageId, type, activity, "", "", "", text);
        }
        catch (JsonException) { return null; }
    }

    private static string ExtractOpenCodeMessageText(string messageId, IEnumerable<OpenCodePartRow> rows)
    {
        var text = rows
            .Where(row => row.MessageId == messageId)
            .Select(ParseOpenCodePart)
            .Where(part => part is not null)
            .Select(part => part!.Text)
            .Where(value => value.Length > 0)
            .LastOrDefault();
        return text ?? "";
    }

    private static string DescribeOpenCodeTool(string name, string input)
    {
        if (name is "bash" or "shell" or "terminal" or "run_terminal_command")
            return DescribeAction("shell_command", input);
        if (name.Contains("edit", StringComparison.OrdinalIgnoreCase)
            || name.Contains("write", StringComparison.OrdinalIgnoreCase)
            || name.Contains("patch", StringComparison.OrdinalIgnoreCase))
            return "Edited code";
        if (name is "read" or "grep" or "glob" or "list" or "search")
            return "Inspected files";
        if (name is "task" or "subtask" or "agent")
            return "Running subagent";
        if (name.Contains("question", StringComparison.OrdinalIgnoreCase)
            || name.Contains("approval", StringComparison.OrdinalIgnoreCase))
            return "Waiting for input";
        return $"Running {name}";
    }

    private static string ParseOpenCodeModel(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return "";
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty("id", out var id)
                ? Clean(id.GetString())
                : "";
        }
        catch (JsonException) { return ""; }
    }

    private static DateTimeOffset FromUnixMilliseconds(long value, DateTimeOffset fallback)
    {
        if (value <= 0)
            return fallback;
        try { return DateTimeOffset.FromUnixTimeMilliseconds(value).ToLocalTime(); }
        catch (ArgumentOutOfRangeException) { return fallback; }
    }

    private static DateTimeOffset Max(params DateTimeOffset[] values)
        => values.Max();

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
            return new SessionMetadata(
                payload.TryGetProperty("id", out var id) ? id.GetString() : null,
                payload.TryGetProperty("parent_thread_id", out var parent) ? parent.GetString() : null,
                DetectHost(payload));
        }
        catch (IOException) { return default; }
        catch (UnauthorizedAccessException) { return default; }
        catch (JsonException) { return default; }
    }

    private static TailInfo ParseTail(ProviderId provider, string text)
    {
        string step = "", summary = "", model = "", threadId = "", parentId = "", prompt = "", host = "";
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
                    if (started is null || parsedTime < started.Value)
                        started = parsedTime;
                    if (activity is null && IsConversationEvent(root, provider)) activity = parsedTime;
                }

                if (provider == ProviderId.Claude)
                    ParseClaudeEvent(root, ref step, ref summary, ref model, ref threadId,
                        ref parentId, ref prompt, ref host, ref state);

                if (host.Length == 0)
                    host = DetectHost(root) ?? "";

                if (root.TryGetProperty("payload", out var payload) && payload.ValueKind == JsonValueKind.Object)
                {
                    if (host.Length == 0)
                        host = DetectHost(payload) ?? "";
                    if (parentId.Length == 0 && payload.TryGetProperty("parent_thread_id", out var parent)) parentId = JsonText(parent);
                    if (model.Length == 0 && payload.TryGetProperty("model", out var modelNode)) model = JsonText(modelNode);

                    var type = payload.TryGetProperty("type", out var typeNode) ? typeNode.GetString() : null;
                    // Tool-call and message ids are not thread ids. Codex's session_meta entry is the
                    // authoritative source; the caller also reads it from the transcript header.
                    if (threadId.Length == 0 && type == "session_meta" && payload.TryGetProperty("id", out var id))
                        threadId = JsonText(id);
                    if (prompt.Length == 0 && type is "user_message")
                        prompt = ExtractPayloadText(payload);
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
                            step = DescribeAction(name!, GetPayloadDetails(payload));
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

            if (activity is not null && (step.Length > 0 || summary.Length > 0)
                && prompt.Length > 0 && (provider == ProviderId.Claude || threadId.Length > 0))
                break;
        }

        return new TailInfo(step, summary, model, threadId, parentId, prompt, host, started, activity, state);
    }

    internal static TailInfo ParseTranscriptForTesting(ProviderId provider, string text)
        => ParseTail(provider, text);

    internal static string? SummarizeTitleForTesting(string? prompt)
        => SummarizeTitle(prompt);

    internal static AgentActivityItem? ReadClineForTesting(string metadataPath, bool claimLive = true)
        => TryReadClineSession(
            metadataPath,
            new DateTimeOffset(File.GetLastWriteTimeUtc(metadataPath), TimeSpan.Zero).ToLocalTime(),
            claimLive,
            out var item) ? item : null;

    internal static AgentActivityItem? ReadKimiForTesting(string statePath, bool claimLive = true)
        => TryReadKimiSession(
            statePath,
            new DateTimeOffset(File.GetLastWriteTimeUtc(statePath), TimeSpan.Zero).ToLocalTime(),
            claimLive,
            out var item) ? item : null;

    private static void ParseClaudeEvent(JsonElement root, ref string step, ref string summary,
        ref string model, ref string threadId, ref string parentId, ref string prompt,
        ref string host, ref TranscriptState state)
    {
        var type = root.TryGetProperty("type", out var typeNode) ? typeNode.GetString() : null;
        if (type is not ("user" or "assistant"))
            return;

        if (host.Length == 0)
            host = DetectHost(root) ?? "";

        if (threadId.Length == 0)
            threadId = FirstString(root, "sessionId", "session_id", "uuid");
        if (parentId.Length == 0)
            parentId = FirstString(root, "parentThreadId", "parent_thread_id");
        if (!root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object)
            return;

        if (model.Length == 0)
            model = FirstString(message, "model");

        if (type == "user")
        {
            if (prompt.Length == 0 && message.TryGetProperty("content", out var content))
                prompt = ExtractContentText(content);
            if (state == TranscriptState.Unknown)
            {
                state = TranscriptState.Action;
                if (step.Length == 0)
                    step = "Thinking";
            }
            return;
        }

        if (!message.TryGetProperty("content", out var assistantContent))
            return;

        if (FindClaudeToolAction(assistantContent) is { } action)
        {
            if (step.Length == 0)
                step = action.Step;
            state = action.Waiting ? TranscriptState.Waiting : TranscriptState.Action;
            return;
        }

        if (summary.Length == 0)
            summary = ExtractContentText(assistantContent);
    }

    private static string ExtractPayloadText(JsonElement payload)
    {
        if (payload.TryGetProperty("message", out var message))
        {
            var value = message.ValueKind == JsonValueKind.Object && message.TryGetProperty("content", out var nested)
                ? nested
                : message;
            var text = ExtractContentText(value);
            if (text.Length > 0)
                return text;
        }

        if (payload.TryGetProperty("content", out var content))
            return ExtractContentText(content);
        return "";
    }

    private static string ExtractContentText(JsonElement content)
    {
        if (content.ValueKind == JsonValueKind.String)
            return Clean(content.GetString());

        if (content.ValueKind == JsonValueKind.Array)
        {
            foreach (var part in content.EnumerateArray().Reverse())
            {
                if (part.ValueKind != JsonValueKind.Object)
                    continue;
                if (part.TryGetProperty("text", out var text))
                {
                    var value = Clean(text.GetString());
                    if (value.Length > 0)
                        return value;
                }
            }
        }

        return "";
    }

    private static ClaudeToolAction? FindClaudeToolAction(JsonElement content)
    {
        if (content.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var part in content.EnumerateArray().Reverse())
        {
            if (part.ValueKind != JsonValueKind.Object
                || !part.TryGetProperty("type", out var type)
                || type.GetString() != "tool_use")
            {
                continue;
            }

            var name = part.TryGetProperty("name", out var nameNode) ? nameNode.GetString() : null;
            if (string.IsNullOrWhiteSpace(name))
                continue;

            var input = part.TryGetProperty("input", out var inputNode) ? inputNode.ToString() : "";
            bool waiting = name.Equals("AskUserQuestion", StringComparison.OrdinalIgnoreCase)
                || name.Equals("ExitPlanMode", StringComparison.OrdinalIgnoreCase)
                || name.Contains("approval", StringComparison.OrdinalIgnoreCase);
            return new ClaudeToolAction(waiting ? "Waiting for input" : DescribeAction(name, input), waiting);
        }

        return null;
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

    private static string DescribeAction(string name, string? rawDetails)
    {
        var details = rawDetails ?? "";
        if (name.Equals("apply_patch", StringComparison.OrdinalIgnoreCase) || details.Contains("apply_patch", StringComparison.OrdinalIgnoreCase))
            return "Edited code";
        if (name is "Write" or "Edit" or "MultiEdit" or "NotebookEdit")
            return "Edited code";
        if (name is "Read" or "Glob" or "Grep" or "LS")
            return "Inspected files";
        if (name is "Task" or "Agent")
            return "Running subagent";
        if (name is "WebFetch" or "WebSearch")
            return "Fetching information";
        if (name is "TodoWrite")
            return "Updating plan";
        if (details.Contains("Get-Content", StringComparison.OrdinalIgnoreCase))
            return "Ran Get-Content";
        if (details.Contains("rg ", StringComparison.OrdinalIgnoreCase) || details.Contains("rg -", StringComparison.OrdinalIgnoreCase))
            return "Searched files";
        if (details.Contains("dotnet build", StringComparison.OrdinalIgnoreCase))
            return "Built the app";
        if (details.Contains("dotnet test", StringComparison.OrdinalIgnoreCase))
            return "Ran tests";
        return name.Equals("shell_command", StringComparison.OrdinalIgnoreCase)
                || name.Equals("exec", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Bash", StringComparison.OrdinalIgnoreCase)
            ? "Running command"
            : $"Running {name}";
    }

    private static string GetPayloadDetails(JsonElement payload)
    {
        if (payload.TryGetProperty("arguments", out var arguments))
            return JsonText(arguments);
        if (payload.TryGetProperty("input", out var input))
            return JsonText(input);
        return "";
    }

    private static string JsonText(JsonElement value)
        => value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.ToString();

    private static string? DetectHost(JsonElement value)
    {
        foreach (var name in new[] { "originator", "host", "host_app", "source", "origin", "runtime" })
        {
            if (!value.TryGetProperty(name, out var property))
                continue;

            if (property.ValueKind == JsonValueKind.Object)
            {
                if (DetectHost(property) is { } nested)
                    return nested;
                continue;
            }

            if (property.ValueKind == JsonValueKind.String && MapHost(property.GetString()) is { } host)
                return host;
        }

        return null;
    }

    private static string? MapHost(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        if (value.Contains("t3code", StringComparison.OrdinalIgnoreCase)
            || value.Contains("t3 code", StringComparison.OrdinalIgnoreCase))
            return "T3 Code";
        if (value.Contains("synara", StringComparison.OrdinalIgnoreCase)
            || value.Contains("dpcode", StringComparison.OrdinalIgnoreCase))
            return "Synara";
        return null;
    }

    private static string FirstString(JsonElement value, params string[] names)
    {
        if (value.ValueKind != JsonValueKind.Object)
            return "";
        foreach (var name in names)
            if (value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String)
                return property.GetString() ?? "";
        return "";
    }

    private static string FirstNonEmpty(params string[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "";

    private static string? SummarizeTitle(string? prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt))
            return null;

        var firstLine = prompt
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim().TrimStart('-', '*', '#').Trim())
            .FirstOrDefault(line => line.Length > 0);
        var cleaned = Clean(firstLine);
        return cleaned.Length == 0 ? null : Trim(cleaned, 72);
    }

    private static ClaudeSessionContext ReadClaudeSessionContext(string path, string threadId)
    {
        try
        {
            var file = new FileInfo(path);
            var directory = file.Directory;
            DirectoryInfo? project = directory;
            string? parent = null;

            if (directory?.Name.Equals("subagents", StringComparison.OrdinalIgnoreCase) == true)
            {
                var sessionDirectory = directory.Parent;
                parent = sessionDirectory is null ? null : Path.GetFileNameWithoutExtension(sessionDirectory.Name);
                project = sessionDirectory?.Parent;
            }

            var id = string.IsNullOrWhiteSpace(threadId)
                ? Path.GetFileNameWithoutExtension(file.Name)
                : threadId;
            var projectName = project?.Name;
            if (!string.IsNullOrWhiteSpace(projectName))
            {
                var parts = projectName.Split('-', StringSplitOptions.RemoveEmptyEntries);
                projectName = parts.Length == 0 ? projectName : parts[^1];
            }

            return new ClaudeSessionContext(id, parent, projectName);
        }
        catch (ArgumentException)
        {
            return new ClaudeSessionContext(null, null, null);
        }
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
                if (TryGetDesktopProvider(process.ProcessName, out var provider))
                    Add(provider);
            }
            catch { }
            finally { process.Dispose(); }
        }
        return live;

        // Electron-style apps may have many helper processes. They represent one desktop agent,
        // not several simultaneous tasks.
        void Add(ProviderId provider) => live[provider] = 1;
    }

    private static bool TryGetDesktopProvider(string processName, out ProviderId provider)
    {
        provider = default;
        switch (processName.ToLowerInvariant())
        {
            case "codex":
            case "chatgpt": provider = ProviderId.Codex; return true;
            case "claude": provider = ProviderId.Claude; return true;
            case "grok": provider = ProviderId.Grok; return true;
            case "cursor": provider = ProviderId.Cursor; return true;
            case "antigravity": provider = ProviderId.Antigravity; return true;
            case "opencode":
            case "opencode beta":
            case "opencode-beta": provider = ProviderId.OpenCode; return true;
            case "devin": provider = ProviderId.Devin; return true;
            case "cline": provider = ProviderId.Cline; return true;
            case "kimi": provider = ProviderId.Kimi; return true;
            case "zcode": provider = ProviderId.Zai; return true;
            case "code":
            case "code-insiders": provider = ProviderId.Copilot; return true;
            case "code - insiders": provider = ProviderId.Copilot; return true;
            case "copilot":
            case "github copilot":
            case "github-copilot": provider = ProviderId.Copilot; return true;
            default: return false;
        }
    }

    internal static ProviderId? DetectDesktopProviderForTesting(string processName)
        => TryGetDesktopProvider(processName, out var provider) ? provider : null;

    /// <summary>
    /// Finds agents launched from CMD, PowerShell, Windows Terminal, or a script runtime. This is
    /// intentionally a separate scan from desktop apps and never consults the foreground window.
    /// </summary>
    private static IReadOnlyDictionary<ProviderId, int> ScanTerminalAgentCommands()
    {
        var live = new Dictionary<ProviderId, int>();
        var matches = new List<(int ProcessId, int ParentProcessId, ProviderId Provider)>();
        const string query = "SELECT ProcessId, ParentProcessId, Name, CommandLine FROM Win32_Process WHERE " +
            "Name = 'cmd.exe' OR Name = 'powershell.exe' OR Name = 'pwsh.exe' OR Name = 'node.exe' OR " +
            "Name = 'bun.exe' OR Name = 'deno.exe' OR Name = 'npm.exe' OR Name = 'npx.exe' OR " +
            "Name = 'pnpm.exe' OR Name = 'yarn.exe' OR Name = 'wsl.exe' OR Name = 'bash.exe' OR " +
            "Name = 'codex.exe' OR Name = 'claude.exe' OR Name = 'cursor-agent.exe' OR " +
            "Name = 'opencode.exe' OR Name = 'cline.exe' OR Name = 'kimi.exe' OR Name = 'agy.exe' OR Name = 'grok.exe' OR Name = 'zcode.exe' OR Name = 'copilot.exe' OR Name = 'github-copilot.exe'";

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
                    {
                        var processId = Convert.ToInt32(process["ProcessId"] ?? 0);
                        if (processId <= 0)
                            continue;
                        matches.Add((
                            processId,
                            Convert.ToInt32(process["ParentProcessId"] ?? 0),
                            provider));
                    }
                }
            }
        }
        catch (ManagementException) { }
        catch (UnauthorizedAccessException) { }

        var byId = matches.ToDictionary(match => match.ProcessId);
        foreach (var match in matches)
        {
            // npm/npx/node and shell launchers can all describe the same agent process tree. Count only
            // the highest matching ancestor so a single task receives one transcript claim.
            var parentId = match.ParentProcessId;
            var hasMatchingAncestor = false;
            var visited = new HashSet<int>();
            while (parentId != 0 && visited.Add(parentId) && byId.TryGetValue(parentId, out var parent))
            {
                if (parent.Provider == match.Provider)
                {
                    hasMatchingAncestor = true;
                    break;
                }
                parentId = parent.ParentProcessId;
            }
            if (!hasMatchingAncestor)
                Add(match.Provider);
        }

        return live;
        void Add(ProviderId provider) => live[provider] = live.TryGetValue(provider, out var count) ? count + 1 : 1;
    }

    private static bool TryGetTerminalProvider(string executable, string commandLine, out ProviderId provider)
    {
        provider = default;
        var exe = executable.ToLowerInvariant();
        if (TryGetDirectTerminalProvider(exe, out provider))
            return true;

        // Shell command lines routinely contain paths such as .codex or user-entered search terms. Do
        // not infer an agent from arbitrary substrings. Runtime wrappers are accepted only when their
        // command line names a known package/entry point.
        if (exe is not ("node" or "bun" or "deno" or "npm" or "npx" or "pnpm" or "yarn" or "wsl" or "bash"))
            return false;

        var text = commandLine.ToLowerInvariant().Replace('/', '\\');
        if (ContainsPackage(text, "@anthropic-ai\\claude-code", "claude-code\\cli", "\\claude-code.")) { provider = ProviderId.Claude; return true; }
        if (ContainsPackage(text, "@openai\\codex", "\\codex\\bin\\codex", "\\codex-cli")) { provider = ProviderId.Codex; return true; }
        if (ContainsPackage(text, "cursor-agent", "@cursor\\agent")) { provider = ProviderId.Cursor; return true; }
        if (ContainsPackage(text, "opencode-ai", "\\opencode\\bin", "\\opencode-ai")) { provider = ProviderId.OpenCode; return true; }
        if (ContainsPackage(text, "@cline\\", "\\cline\\bin", "cline-cli")) { provider = ProviderId.Cline; return true; }
        if (ContainsPackage(text, "kimi-cli", "\\kimi\\cli")) { provider = ProviderId.Kimi; return true; }
        if (ContainsPackage(text, "antigravity-cli", "\\agy\\cli")) { provider = ProviderId.Antigravity; return true; }
        if (ContainsPackage(text, "grok-cli", "@xai\\grok")) { provider = ProviderId.Grok; return true; }
        if (ContainsPackage(text, "zcode-cli", "\\zcode\\cli")) { provider = ProviderId.Zai; return true; }
        if (ContainsPackage(text, "github-copilot", "@github\\copilot", "copilot-cli")) { provider = ProviderId.Copilot; return true; }
        return false;
    }

    private static bool TryGetDirectTerminalProvider(string executable, out ProviderId provider)
    {
        provider = executable switch
        {
            "claude" => ProviderId.Claude,
            "codex" => ProviderId.Codex,
            "cursor-agent" => ProviderId.Cursor,
            "opencode" => ProviderId.OpenCode,
            "cline" => ProviderId.Cline,
            "kimi" => ProviderId.Kimi,
            "agy" => ProviderId.Antigravity,
            "grok" => ProviderId.Grok,
            "zcode" => ProviderId.Zai,
            "copilot" or "github-copilot" => ProviderId.Copilot,
            _ => default,
        };
        return executable is "claude" or "codex" or "cursor-agent" or "opencode" or "cline" or
            "kimi" or "agy" or "grok" or "zcode" or "copilot" or "github-copilot";
    }

    private static bool ContainsPackage(string commandLine, params string[] markers)
        => markers.Any(marker => commandLine.Contains(marker, StringComparison.Ordinal));

    internal static ProviderId? DetectTerminalProviderForTesting(string executable, string commandLine)
        => TryGetTerminalProvider(executable, commandLine, out var provider) ? provider : null;

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

    private static IReadOnlyList<AgentActivityItem> GroupSessions(IReadOnlyList<AgentActivityItem> items)
    {
        var groups = new List<AgentActivityItem>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in items.Where(item => string.IsNullOrWhiteSpace(item.ParentThreadId)))
        {
            if (string.IsNullOrWhiteSpace(root.ThreadId))
            {
                groups.Add(root);
                used.Add(root.Id);
                continue;
            }

            var children = items
                .Where(item => item.Provider == root.Provider && item.ParentThreadId == root.ThreadId)
                .ToArray();
            used.Add(root.Id);
            foreach (var child in children) used.Add(child.Id);
            var grouped = root with { SubagentCount = children.Length };
            var liveMember = children
                .Append(root)
                .Where(item => item.IsLive)
                .OrderByDescending(item => item.UpdatedAt)
                .FirstOrDefault();
            if (liveMember is { } member && member.Id != root.Id)
            {
                grouped = grouped with
                {
                    Status = member.Status,
                    Step = member.Step,
                    UpdatedAt = member.UpdatedAt,
                    Model = string.IsNullOrWhiteSpace(root.Model) ? member.Model : root.Model,
                    Detail = string.IsNullOrWhiteSpace(root.Detail) ? member.Detail : root.Detail,
                };
            }
            groups.Add(grouped);
        }
        groups.AddRange(items.Where(item => !used.Contains(item.Id)));
        return groups;
    }

    private static IReadOnlyList<AgentActivityItem> ApplyActiveHostFallback(IReadOnlyList<AgentActivityItem> items)
    {
        var selection = UsageCoordinator.Instance.ActiveSynaraHost;
        if (selection is null)
            return items;

        var host = selection.Host == HostApp.T3Code ? "T3 Code" : "Synara";
        var candidates = items
            .Where(item => item.Provider == selection.Provider && string.IsNullOrWhiteSpace(item.Host))
            .ToArray();
        if (candidates.Length == 0)
            return items;

        AgentActivityItem? selected = null;
        if (!string.IsNullOrWhiteSpace(selection.ThreadTitle))
            selected = candidates.FirstOrDefault(item => string.Equals(
                item.Title, selection.ThreadTitle, StringComparison.OrdinalIgnoreCase));
        selected ??= candidates.Where(item => item.IsLive)
            .OrderByDescending(item => item.UpdatedAt)
            .FirstOrDefault()
            ?? candidates.OrderByDescending(item => item.UpdatedAt).First();

        return items.Select(item => item.Id == selected.Id ? item with { Host = host } : item).ToArray();
    }

    internal enum TranscriptState { Unknown, Action, Waiting, Finished, Failed }

    internal sealed record TailInfo(string Step, string Summary, string Model, string ThreadId,
        string ParentThreadId, string Prompt, string Host, DateTimeOffset? StartedAt,
        DateTimeOffset? LastActivity, TranscriptState State);
    private sealed record ClaudeToolAction(string Step, bool Waiting);
    private sealed record ClaudeSessionContext(string? ThreadId, string? ParentThreadId, string? ProjectTitle);
    private readonly record struct SessionMetadata(string? ThreadId, string? ParentThreadId, string? Host);
    private sealed record OpenCodeSessionRow(string Id, string? ParentId, string Title, string Model, long CreatedAt, long UpdatedAt);
    private sealed record OpenCodeMessageRow(string Id, string Data, long CreatedAt, long UpdatedAt);
    private sealed record OpenCodePartRow(string Id, string MessageId, string Data, long CreatedAt, long UpdatedAt);
    private sealed record GrokTranscriptInfo(string Prompt, string Step, string Summary,
        TranscriptState State, DateTimeOffset? LastActivity);
    private sealed record OpenCodeMessageInfo(
        string Id, string Role, DateTimeOffset CreatedAt, DateTimeOffset ActivityAt, bool Completed, bool Failed);
    private sealed record OpenCodePartInfo(
        string MessageId, string Type, DateTimeOffset ActivityAt, string Tool, string Status, string Input, string Text);
    private sealed record OpenCodeTail(
        string Step, string Summary, string Prompt, DateTimeOffset? LastActivity, TranscriptState State);
    private sealed record ZcodeTail(
        string Prompt, string Step, string Model, DateTimeOffset? LastActivity, TranscriptState State, string Error);
    private sealed record ZcodeSessionRow(
        string Id, string? ParentId, string Title, long CreatedAt, long UpdatedAt, string TargetStatus);
    private sealed class CopilotTranscript(string sessionId)
    {
        public string SessionId { get; set; } = sessionId;
        public string Title { get; set; } = "";
        public string Prompt { get; set; } = "";
        public string Model { get; set; } = "";
        public string Step { get; set; } = "";
        public DateTimeOffset? StartedAt { get; set; }
        public DateTimeOffset? LastActivity { get; set; }
        public bool HasUser { get; set; }
        public bool HasAssistant { get; set; }
        public bool Pending { get; set; }
        public bool Waiting { get; set; }
        public bool Failed { get; set; }
        public int LatestRequestIndex { get; set; } = -1;
    }
    private sealed class ClineTail
    {
        public string Prompt { get; set; } = "";
        public string Step { get; set; } = "";
        public string Summary { get; set; } = "";
        public string Model { get; set; } = "";
        public DateTimeOffset? LastActivity { get; set; }
        public TranscriptState State { get; set; }
    }
    private sealed class KimiTail
    {
        public string Prompt { get; set; } = "";
        public string Step { get; set; } = "";
        public string Summary { get; set; } = "";
        public string Model { get; set; } = "";
        public DateTimeOffset? LastActivity { get; set; }
        public TranscriptState State { get; set; }
    }
    private sealed record AntigravityConversationMetadata(
        string Id, string Title, string Preview, string Workspace, DateTimeOffset? UpdatedAt, string AgentName);
    private sealed record AntigravityStepRow(
        int Index, int StepType, int Status, bool HasSubtrajectory,
        byte[]? Metadata, byte[]? ErrorDetails, byte[]? Permissions,
        byte[]? TaskDetails, byte[]? RenderInfo, byte[]? Payload)
    {
        public IEnumerable<byte[]?> Blobs => new[]
            { Payload, RenderInfo, TaskDetails, Metadata, ErrorDetails, Permissions };
    }
    private sealed record AntigravityGuiTranscriptInfo(
        string Prompt, string Step, string Summary, string ConversationTitle, string Model,
        DateTimeOffset? StartedAt, DateTimeOffset? LastActivity,
        TranscriptState State, bool HasConversation, int SubagentCount);
}
