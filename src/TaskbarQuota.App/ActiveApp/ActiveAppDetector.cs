using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Text.Json;
using TaskbarQuota.Interop;
using TaskbarQuota.Usage;

namespace TaskbarQuota.ActiveApp
{
    /// <summary>
    /// Determines which AI tool the user is currently using based on the foreground window.
    /// - GUI apps (Cursor, Antigravity) are matched by process name.
    /// - CLIs (Claude Code, Codex, Cursor Agent, OpenCode) run inside a terminal: when a known terminal is
    ///   focused we look for a matching CLI process anywhere in the session and pick the most
    ///   recently started one.
    /// Returns null when the foreground app is unrelated (caller keeps the last active provider).
    /// </summary>
    public sealed class ActiveAppDetector
    {
        // GUI process name (lower-case, no extension) -> provider.
        // These are desktop apps; when one is focused we attribute usage to it directly.
        private static readonly Dictionary<string, ProviderId> GuiApps = new(StringComparer.OrdinalIgnoreCase)
        {
            ["cursor"] = ProviderId.Cursor,
            ["antigravity"] = ProviderId.Antigravity,
            ["codex"] = ProviderId.Codex,        // OpenAI Codex desktop app (Codex.exe)
            ["claude"] = ProviderId.Claude,      // Claude desktop app, if installed
            ["code"] = ProviderId.Copilot,       // Visual Studio Code (GitHub Copilot in-editor)
            ["code-insiders"] = ProviderId.Copilot,
        };

        private static readonly HashSet<string> Terminals = new(StringComparer.OrdinalIgnoreCase)
        {
            "windowsterminal", "openconsole", "conhost", "powershell", "pwsh", "cmd",
            "wezterm-gui", "wezterm", "alacritty", "mintty", "bash", "wt", "tabby", "hyper",
        };

        // Shell processes that host interactive CLIs. On Windows, conhost/OpenConsole are
        // siblings of the CLI under the shell, not ancestors — matching must include the
        // full shell session, not just descendants of the foreground PID.
        private static readonly HashSet<string> ShellHosts = new(StringComparer.OrdinalIgnoreCase)
        {
            "powershell", "pwsh", "cmd", "bash", "wsl", "wslhost",
        };

        private static readonly HashSet<string> InteractiveClis = new(StringComparer.OrdinalIgnoreCase)
        {
            "grok", "claude", "codex", "cursor", "cursor-agent", "opencode", "copilot", "antigravity",
        };

        // CLI command-line markers -> provider, checked in order. Claude is checked
        // before Codex because terminal launchers and paths can contain both names.
        private static readonly (string marker, ProviderId id)[] CliMarkers =
        {
            ("claude-code", ProviderId.Claude),
            ("claude code", ProviderId.Claude),
            ("antigravity", ProviderId.Antigravity),
            ("cursor-agent", ProviderId.Cursor),
            ("cursor agent", ProviderId.Cursor),
            ("cursor.cmd", ProviderId.Cursor),
            ("cursor\\resources\\app\\out\\cli.js", ProviderId.Cursor),
            ("cursor/resources/app/out/cli.js", ProviderId.Cursor),
            ("opencode", ProviderId.OpenCode),
            ("gh copilot", ProviderId.Copilot),
            ("github-copilot", ProviderId.Copilot),
            ("copilot", ProviderId.Copilot),
            (".grok\\bin\\grok", ProviderId.Grok),
            (".grok/bin/grok", ProviderId.Grok),
            ("grok", ProviderId.Grok),
            ("claude", ProviderId.Claude),
            ("codex", ProviderId.Codex),
        };

        // The WMI process scan is comparatively expensive, so cache its result briefly.
        private static readonly TimeSpan CliCacheTtl = TimeSpan.FromSeconds(3);
        private static readonly TimeSpan RunningToolCacheTtl = TimeSpan.FromSeconds(15);
        private int _lastForegroundPid;
        private string? _lastForegroundProcessName;
        private ProviderId? _lastForegroundResult;
        private ProviderId? _cliCache;
        private DateTime _cliCacheAt = DateTime.MinValue;
        private int? _cliCachePid;
        private bool _runningToolCache;
        private DateTime _runningToolCacheAt = DateTime.MinValue;
        private volatile bool _openCodeModelStateDirty;
        private OpenCodeModelStateWatcher? _modelStateWatcher;

        public event Action? OpenCodeModelStateChanged;

        public void StartOpenCodeModelStateWatcher()
        {
            if (_modelStateWatcher != null) return;

            _modelStateWatcher = new OpenCodeModelStateWatcher();
            _modelStateWatcher.ModelStateChanged += OnOpenCodeModelStateChanged;
            _modelStateWatcher.Start();
        }

        private void OnOpenCodeModelStateChanged()
        {
            _openCodeModelStateDirty = true;
            OpenCodeModelStateChanged?.Invoke();
        }

        private bool ConsumeOpenCodeModelStateChange()
        {
            if (!_openCodeModelStateDirty)
                return false;

            _openCodeModelStateDirty = false;
            return true;
        }

        public ProviderId? Detect()
        {
            var hwnd = User32.GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return null;

            User32.GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0) return null;

            var processId = (int)pid;
            var openCodeModelChanged = ConsumeOpenCodeModelStateChange();
            if (processId == _lastForegroundPid
                && !openCodeModelChanged
                && (_lastForegroundProcessName is null
                    || !Terminals.Contains(_lastForegroundProcessName)
                    || DateTime.UtcNow - _cliCacheAt < CliCacheTtl))
                return _lastForegroundResult;

            string? procName = processId == _lastForegroundPid
                ? _lastForegroundProcessName
                : TryGetProcessName(processId);
            _lastForegroundPid = processId;
            _lastForegroundProcessName = procName;
            if (procName == null) return null;

            // Fast path: GUI desktop apps resolve from the foreground process name (~instant).
            if (TryResolveGuiProcess(procName) is { } gui)
                return _lastForegroundResult = gui;

            // Interactive CLI TUI focused directly (e.g. grok.exe owns the window).
            if (InteractiveClis.Contains(procName))
            {
                if (TryDetectCliProvider($"{procName}.exe", null) is { } cli)
                    return _lastForegroundResult = cli;
            }

            // Terminal focused: find the CLI running inside, but throttle the WMI scan.
            if (Terminals.Contains(procName))
            {
                if (!openCodeModelChanged
                    && _cliCachePid == processId
                    && DateTime.UtcNow - _cliCacheAt < CliCacheTtl)
                    return _lastForegroundResult = _cliCache;
                _cliCache = DetectCliFromProcesses(processId, procName);
                _cliCachePid = processId;
                _cliCacheAt = DateTime.UtcNow;
                return _lastForegroundResult = _cliCache;
            }

            return _lastForegroundResult = null;
        }

        private int _prewarmed;

        /// <summary>
        /// Run one throwaway WMI process scan so the process-wide COM/WMI cold start (~1-3s) happens
        /// off the UI/timer thread at launch instead of stalling the user's first terminal detection.
        /// Safe to call repeatedly; only the first call does work.
        /// </summary>
        public void Prewarm()
        {
            if (System.Threading.Interlocked.Exchange(ref _prewarmed, 1) != 0)
                return;

            try
            {
                DetectCliFromProcesses();
            }
            catch (Exception ex)
            {
                Diagnostics.Log.Warning(ex, "Active-app detector prewarm failed");
            }
        }

        public bool HasAnyKnownToolRunning()
        {
            if (DateTime.UtcNow - _runningToolCacheAt < RunningToolCacheTtl)
                return _runningToolCache;

            _runningToolCache = HasAnyKnownGuiProcessRunning() || DetectCliFromProcesses() != null;
            _runningToolCacheAt = DateTime.UtcNow;
            return _runningToolCache;
        }

        private static bool HasAnyKnownGuiProcessRunning()
        {
            try
            {
                foreach (var process in Process.GetProcesses())
                {
                    using (process)
                    {
                        if (IsKnownGuiProcessName(process.ProcessName))
                            return true;
                    }
                }
            }
            catch (Exception ex)
            {
                Diagnostics.Log.Warning(ex, "Failed to enumerate GUI tool processes");
            }

            return false;
        }

        internal static ProviderId? TryResolveGuiProcess(string? processName)
        {
            if (string.IsNullOrWhiteSpace(processName))
                return null;

            if (IsOpenCodeGuiProcessName(processName))
                return DetectOpenCodeProviderFromModelState() ?? ProviderId.OpenCode;

            return GuiApps.TryGetValue(processName, out var gui) ? gui : null;
        }

        internal static bool IsKnownGuiProcessName(string? processName)
        {
            if (string.IsNullOrWhiteSpace(processName))
                return false;

            return TryResolveGuiProcess(processName) != null;
        }

        private static bool IsOpenCodeGuiProcessName(string processName) =>
            processName.Equals("opencode", StringComparison.OrdinalIgnoreCase)
            || processName.Equals("opencode beta", StringComparison.OrdinalIgnoreCase)
            || processName.Equals("opencode-beta", StringComparison.OrdinalIgnoreCase);

        private static string? TryGetProcessName(int pid)
        {
            try { return Process.GetProcessById(pid).ProcessName; }
            catch { return null; }
        }

        private static ProviderId? DetectCliFromProcesses(int? foregroundTerminalPid = null, string? foregroundProcName = null)
        {
            // Inspect command lines of running processes for CLI markers.
            // node-based CLIs show up as "node ... claude", codex/opencode may be native exes.
            var candidates = new List<(ProviderId id, DateTime started, int pid, int parentPid)>();
            var processParents = new Dictionary<int, int>();
            var processNames = new Dictionary<int, string>();
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    "SELECT ProcessId, ParentProcessId, Name, CommandLine FROM Win32_Process WHERE " +
                    "Name = 'node.exe' OR Name = 'antigravity.exe' OR Name = 'claude.exe' OR Name = 'codex.exe' OR Name = 'cursor.exe' OR Name = 'cursor-agent.exe' OR Name = 'opencode.exe' OR Name = 'copilot.exe' OR Name = 'gh.exe' OR Name = 'grok.exe' OR Name = 'bun.exe' OR Name = 'deno.exe' OR Name = 'npm.exe' OR Name = 'npx.exe' OR Name = 'pnpm.exe' OR Name = 'yarn.exe' OR Name = 'cmd.exe' OR Name = 'powershell.exe' OR Name = 'pwsh.exe' OR Name = 'windowsterminal.exe' OR Name = 'openconsole.exe' OR Name = 'conhost.exe'");
                foreach (ManagementObject mo in searcher.Get())
                {
                    var pid = TryParseInt(mo["ProcessId"]);
                    var parentPid = TryParseInt(mo["ParentProcessId"]);
                    if (pid != null && parentPid != null)
                        processParents[pid.Value] = parentPid.Value;
                    if (pid != null && mo["Name"] is string rawName)
                        processNames[pid.Value] = NormalizeProcessName(rawName);

                    if (TryDetectCliProvider(mo["Name"] as string, mo["CommandLine"] as string) is { } id)
                    {
                        var detectedId = id == ProviderId.OpenCode
                            ? DetectOpenCodeProviderFromModelState() ?? ProviderId.OpenCode
                            : id;

                        DateTime started = DateTime.MinValue;
                        try
                        {
                            if (pid is int p)
                                started = Process.GetProcessById(p).StartTime;
                        }
                        catch { }
                        candidates.Add((detectedId, started, pid ?? 0, parentPid ?? 0));
                    }
                }
            }
            catch (Exception ex)
            {
                Diagnostics.Log.Warning(ex, "Failed to enumerate CLI processes");
                return null;
            }

            if (candidates.Count == 0) return null;

            if (foregroundTerminalPid is int rootPid)
            {
                var sessionPids = ExpandTerminalSessionPids(rootPid, foregroundProcName, processParents, processNames);
                var foregroundTree = candidates
                    .Where(c => IsInTerminalSession(c.pid, c.parentPid, sessionPids, processParents))
                    .ToList();
                if (foregroundTree.Count > 0)
                    return foregroundTree.OrderByDescending(c => c.started).First().id;

                if (TryDetectGrokFromActiveSessions(sessionPids, processParents) is { } grok)
                    return grok;
            }

            return candidates.OrderByDescending(c => c.started).First().id;
        }

        internal static HashSet<int> ExpandTerminalSessionPids(
            int foregroundPid,
            string? foregroundProcName,
            IReadOnlyDictionary<int, int> parents,
            IReadOnlyDictionary<int, string> names)
        {
            var session = new HashSet<int> { foregroundPid };

            void AddShellSession(int shellPid)
            {
                session.Add(shellPid);
                foreach (var (pid, parentPid) in parents)
                {
                    if (parentPid == shellPid)
                        session.Add(pid);
                }
            }

            if (IsShellHostName(foregroundProcName))
            {
                AddShellSession(foregroundPid);
                return session;
            }

            if (IsConsoleHostName(foregroundProcName) && parents.TryGetValue(foregroundPid, out var shellPid))
            {
                AddShellSession(shellPid);
                return session;
            }

            if (IsWindowsTerminalName(foregroundProcName))
            {
                foreach (var (pid, parentPid) in parents)
                {
                    if (pid == foregroundPid || IsDescendantOf(pid, foregroundPid, parents))
                        session.Add(pid);
                }

                foreach (var pid in session.ToArray())
                {
                    if (names.TryGetValue(pid, out var name) && IsShellHostName(name))
                        AddShellSession(pid);
                }
            }

            return session;
        }

        internal static bool IsInTerminalSession(
            int candidatePid,
            int candidateParentPid,
            IReadOnlySet<int> sessionPids,
            IReadOnlyDictionary<int, int> parents) =>
            sessionPids.Contains(candidatePid)
            || sessionPids.Contains(candidateParentPid)
            || sessionPids.Any(rootPid => IsDescendantOf(candidatePid, rootPid, parents));

        private static ProviderId? TryDetectGrokFromActiveSessions(
            IReadOnlySet<int> sessionPids,
            IReadOnlyDictionary<int, int> parents)
        {
            foreach (var grokPid in ReadActiveGrokSessionPids())
            {
                if (!sessionPids.Contains(grokPid)
                    && !sessionPids.Any(rootPid => IsDescendantOf(grokPid, rootPid, parents))
                    && !(parents.TryGetValue(grokPid, out var grokParent) && sessionPids.Contains(grokParent)))
                    continue;

                try
                {
                    using var process = Process.GetProcessById(grokPid);
                    if (!process.HasExited)
                        return ProviderId.Grok;
                }
                catch { }
            }

            return null;
        }

        internal static IReadOnlyList<int> ReadActiveGrokSessionPids()
        {
            var path = GetGrokActiveSessionsPath();
            if (!File.Exists(path))
                return Array.Empty<int>();

            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                if (doc.RootElement.ValueKind != JsonValueKind.Array)
                    return Array.Empty<int>();

                var pids = new List<int>();
                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    if (item.TryGetProperty("pid", out var pidProp) && pidProp.TryGetInt32(out var pid) && pid > 0)
                        pids.Add(pid);
                }

                return pids;
            }
            catch (Exception ex)
            {
                Diagnostics.Log.Warning(ex, "Failed to read Grok active_sessions.json");
                return Array.Empty<int>();
            }
        }

        internal static string GetGrokActiveSessionsPath()
        {
            var grokHome = Environment.GetEnvironmentVariable("GROK_HOME")?.Trim();
            var root = !string.IsNullOrEmpty(grokHome)
                ? grokHome
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".grok");
            return Path.Combine(root, "active_sessions.json");
        }

        private static string NormalizeProcessName(string processName) =>
            processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? processName[..^4]
                : processName;

        private static bool IsShellHostName(string? processName) =>
            processName != null && ShellHosts.Contains(processName);

        private static bool IsConsoleHostName(string? processName) =>
            processName is "conhost" or "openconsole";

        private static bool IsWindowsTerminalName(string? processName) =>
            processName is "windowsterminal" or "wt";

        internal static ProviderId? TryDetectCliProvider(string? processName, string? commandLine)
        {
            var name = (processName ?? "").ToLowerInvariant();
            if (name is "antigravity.exe") return ProviderId.Antigravity;
            if (name is "claude.exe") return ProviderId.Claude;
            if (name is "codex.exe") return ProviderId.Codex;
            if (name is "cursor.exe") return ProviderId.Cursor;
            if (name is "cursor-agent.exe") return ProviderId.Cursor;
            if (name is "opencode.exe") return ProviderId.OpenCode;
            if (name is "copilot.exe" or "gh.exe") return ProviderId.Copilot;
            if (name is "grok.exe") return ProviderId.Grok;

            var haystack = ((commandLine ?? "") + " " + name).ToLowerInvariant();
            foreach (var (marker, id) in CliMarkers)
            {
                if (ContainsCliMarker(haystack, marker))
                    return id;
            }

            return null;
        }

        private static bool ContainsCliMarker(string haystack, string marker)
        {
            var start = 0;
            while (start < haystack.Length)
            {
                var index = haystack.IndexOf(marker, start, StringComparison.Ordinal);
                if (index < 0) return false;

                var beforeOk = index == 0 || !IsMarkerChar(haystack[index - 1]);
                var afterIndex = index + marker.Length;
                var afterOk = afterIndex >= haystack.Length || !IsMarkerChar(haystack[afterIndex]);
                if (beforeOk && afterOk) return true;

                start = index + marker.Length;
            }

            return false;
        }

        private static bool IsMarkerChar(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '-';

        private static int? TryParseInt(object? value) =>
            int.TryParse(value?.ToString(), out var parsed) ? parsed : null;

        private static bool IsDescendantOf(int pid, int ancestorPid, IReadOnlyDictionary<int, int> parents)
        {
            var seen = new HashSet<int>();
            while (pid != 0 && seen.Add(pid) && parents.TryGetValue(pid, out var parentPid))
            {
                if (parentPid == ancestorPid) return true;
                pid = parentPid;
            }

            return false;
        }

        internal static DateTime GetOpenCodeModelStateWriteUtc()
        {
            var latest = DateTime.MinValue;

            var modelJsonPath = GetModelJsonPath();
            if (File.Exists(modelJsonPath))
                latest = MaxWriteUtc(latest, modelJsonPath);

            foreach (var desktopDir in GetDesktopDirs())
            {
                var globalDatPath = Path.Combine(desktopDir, "opencode.global.dat");
                if (File.Exists(globalDatPath))
                    latest = MaxWriteUtc(latest, globalDatPath);

                if (!Directory.Exists(desktopDir)) continue;
                foreach (var workspaceDat in Directory.GetFiles(desktopDir, "opencode.workspace.*.dat"))
                    latest = MaxWriteUtc(latest, workspaceDat);
            }

            return latest;
        }

        internal static bool IsOpenCodeModelStatePath(string fullPath)
        {
            var name = Path.GetFileName(fullPath);
            if (name.Equals("model.json", StringComparison.OrdinalIgnoreCase))
                return fullPath.Replace('\\', '/').Contains("/opencode/", StringComparison.OrdinalIgnoreCase);

            if (name.Equals("opencode.global.dat", StringComparison.OrdinalIgnoreCase))
                return true;

            return name.StartsWith("opencode.workspace.", StringComparison.OrdinalIgnoreCase)
                && name.EndsWith(".dat", StringComparison.OrdinalIgnoreCase);
        }

        internal static string GetModelJsonPath()
        {
            return Path.Combine(GetStateRoot(), "opencode", "model.json");
        }

        internal static string GetStateRoot()
        {
            var stateRoot = Environment.GetEnvironmentVariable("XDG_STATE_HOME");
            if (string.IsNullOrWhiteSpace(stateRoot))
                stateRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "state");
            return stateRoot;
        }

        internal static IEnumerable<string> GetDesktopDirs()
        {
            var appData = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME")
                ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            foreach (var name in new[] { "ai.opencode.desktop.beta", "ai.opencode.desktop" })
            {
                var dir = Path.Combine(appData, name);
                if (Directory.Exists(dir)) yield return dir;
            }
        }

        private static DateTime MaxWriteUtc(DateTime current, string path)
        {
            var writeUtc = File.GetLastWriteTimeUtc(path);
            return writeUtc > current ? writeUtc : current;
        }

        internal static ProviderId? DetectOpenCodeProviderFromModelState()
        {
            var candidates = new List<(ProviderId provider, DateTime timestamp)>();

            var modelJsonPath = GetModelJsonPath();
            if (File.Exists(modelJsonPath))
            {
                try
                {
                    var ts = File.GetLastWriteTimeUtc(modelJsonPath);
                    var p = DetectFromModelJson(modelJsonPath);
                    if (p != null) candidates.Add((p.Value, ts));
                }
                catch (Exception ex) { Diagnostics.Log.Warning(ex, "Failed model.json detection"); }
            }

            foreach (var desktopDir in GetDesktopDirs())
            {
                var globalDatPath = Path.Combine(desktopDir, "opencode.global.dat");
                if (!File.Exists(globalDatPath)) continue;
                try
                {
                    var ts = File.GetLastWriteTimeUtc(globalDatPath);
                    var p = DetectFromGlobalDat(globalDatPath);
                    if (p != null) candidates.Add((p.Value, ts));
                }
                catch (Exception ex) { Diagnostics.Log.Warning(ex, "Failed global.dat detection"); }
            }

            try
            {
                var (wsProvider, wsTimestamp) = DetectFromDesktopWorkspaces();
                if (wsProvider != null) candidates.Add((wsProvider.Value, wsTimestamp));
            }
            catch (Exception ex) { Diagnostics.Log.Warning(ex, "Failed workspace detection"); }

            if (candidates.Count > 0)
                return candidates.OrderByDescending(c => c.timestamp).First().provider;

            return HasOpenCodeGoAuth() ? ProviderId.OpenCodeGo : null;
        }

        private static ProviderId? DetectFromModelJson(string path)
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("recent", out var recent) || recent.ValueKind != JsonValueKind.Array)
                return null;

            foreach (var item in recent.EnumerateArray())
            {
                if (!item.TryGetProperty("providerID", out var provider) || provider.ValueKind != JsonValueKind.String)
                    continue;

                return provider.GetString()?.ToLowerInvariant() switch
                {
                    "opencode-go" => ProviderId.OpenCodeGo,
                    "opencode" => ProviderId.OpenCode,
                    _ => null,
                };
            }

            return null;
        }

        private static ProviderId? DetectFromGlobalDat(string path)
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("model", out var modelProp) || modelProp.ValueKind != JsonValueKind.String)
                return null;

            var modelJson = modelProp.GetString();
            if (string.IsNullOrEmpty(modelJson)) return null;

            using var modelDoc = JsonDocument.Parse(modelJson);
            if (!modelDoc.RootElement.TryGetProperty("recent", out var recent) || recent.ValueKind != JsonValueKind.Array)
                return null;

            foreach (var item in recent.EnumerateArray())
            {
                if (!item.TryGetProperty("providerID", out var provider) || provider.ValueKind != JsonValueKind.String)
                    continue;

                return provider.GetString()?.ToLowerInvariant() switch
                {
                    "opencode-go" => ProviderId.OpenCodeGo,
                    "opencode" => ProviderId.OpenCode,
                    _ => null,
                };
            }

            return null;
        }

        private static (ProviderId? Provider, DateTime Timestamp) DetectFromDesktopWorkspaces()
        {
            try
            {
                ProviderId? best = null;
                DateTime bestTime = default;

                foreach (var desktopDir in GetDesktopDirs())
                {
                    var datFiles = Directory.GetFiles(desktopDir, "opencode.workspace.*.dat");
                    if (datFiles.Length == 0) continue;

                    foreach (var fi in datFiles.Select(f => new FileInfo(f)).OrderByDescending(f => f.LastWriteTimeUtc))
                    {
                        if (fi.LastWriteTimeUtc <= bestTime) break;

                        var content = File.ReadAllText(fi.FullName);
                        using var doc = JsonDocument.Parse(content);

                        if (!doc.RootElement.TryGetProperty("workspace:model-selection", out var selProp) ||
                            selProp.ValueKind != JsonValueKind.String)
                            continue;

                        var selJson = selProp.GetString();
                        if (string.IsNullOrEmpty(selJson)) continue;

                        using var selDoc = JsonDocument.Parse(selJson);
                        var root = selDoc.RootElement;

                        if (root.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.Object)
                        {
                            if (TryGetProviderFromModelObj(draft, out var draftProvider))
                            {
                                best = draftProvider;
                                bestTime = fi.LastWriteTimeUtc;
                                break;
                            }
                        }

                        if (root.TryGetProperty("session", out var sessions) && sessions.ValueKind == JsonValueKind.Object)
                        {
                            var latestSession = sessions.EnumerateObject()
                                .OrderByDescending(p => p.Name)
                                .FirstOrDefault();

                            if (latestSession.Value.ValueKind == JsonValueKind.Object &&
                                TryGetProviderFromModelObj(latestSession.Value, out var sessionProvider))
                            {
                                best = sessionProvider;
                                bestTime = fi.LastWriteTimeUtc;
                                break;
                            }
                        }
                    }
                }

                return (best, bestTime);
            }
            catch (Exception ex)
            {
                Diagnostics.Log.Warning(ex, "Failed to read desktop app model selection");
            }

            return (null, default);
        }

        private static bool TryGetProviderFromModelObj(JsonElement obj, out ProviderId provider)
        {
            provider = default;
            if (obj.TryGetProperty("model", out var model) && model.ValueKind == JsonValueKind.Object &&
                model.TryGetProperty("providerID", out var pid) && pid.ValueKind == JsonValueKind.String)
            {
                var pidStr = pid.GetString()?.ToLowerInvariant();
                if (pidStr == "opencode-go") { provider = ProviderId.OpenCodeGo; return true; }
                if (pidStr == "opencode") { provider = ProviderId.OpenCode; return true; }
            }
            return false;
        }

        private static bool HasOpenCodeGoAuth()
        {
            try
            {
                var shareRoot = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
                if (string.IsNullOrWhiteSpace(shareRoot))
                    shareRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");

                var authPath = Path.Combine(shareRoot, "opencode", "auth.json");
                if (!File.Exists(authPath)) return false;

                using var doc = JsonDocument.Parse(File.ReadAllText(authPath));
                return doc.RootElement.TryGetProperty("opencode-go", out _);
            }
            catch { return false; }
        }
    }
}
