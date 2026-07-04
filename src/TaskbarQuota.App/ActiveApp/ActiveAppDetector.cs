using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Text.Json;
using TaskbarQuota.Interop;
using TaskbarQuota.Usage;
using static TaskbarQuota.Interop.User32;

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
            ["devin"] = ProviderId.Devin,        // Devin desktop app (VS Code fork)
            ["devin - next"] = ProviderId.Devin,
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
            "grok", "claude", "codex", "cursor", "cursor-agent", "opencode", "copilot", "antigravity", "agy", "devin", "cline",
        };

        // CLI command-line markers -> provider, checked in order. Claude is checked
        // before Codex because terminal launchers and paths can contain both names.
        private static readonly (string marker, ProviderId id)[] CliMarkers =
        {
            ("claude-code", ProviderId.Claude),
            ("claude code", ProviderId.Claude),
            ("\\agy\\bin\\agy", ProviderId.Antigravity),
            ("/agy/bin/agy", ProviderId.Antigravity),
            ("antigravity", ProviderId.Antigravity),
            ("agy", ProviderId.Antigravity),
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
            ("devin", ProviderId.Devin),
            ("cline", ProviderId.Cline),
            ("claude", ProviderId.Claude),
            ("codex", ProviderId.Codex),
        };

        // Native CLI executables resolvable by process name alone (no CommandLine read needed).
        // Reading Win32_Process.CommandLine forces a per-process PEB read and is the slow part of the
        // scan, so we resolve these by Name and only pay the CommandLine cost for node-style hosts below.
        private static readonly Dictionary<string, ProviderId> NativeCliExes = new(StringComparer.OrdinalIgnoreCase)
        {
            ["antigravity"] = ProviderId.Antigravity,
            ["agy"] = ProviderId.Antigravity,
            ["claude"] = ProviderId.Claude,
            ["codex"] = ProviderId.Codex,
            ["cursor"] = ProviderId.Cursor,
            ["cursor-agent"] = ProviderId.Cursor,
            ["opencode"] = ProviderId.OpenCode,
            ["copilot"] = ProviderId.Copilot,
            ["gh"] = ProviderId.Copilot,
            ["grok"] = ProviderId.Grok,
            ["devin"] = ProviderId.Devin,
            ["cline"] = ProviderId.Cline,
        };

        // Script-runtime hosts that run a CLI as an argument (e.g. "node ... claude"); only these need
        // their CommandLine inspected, and only when they sit inside the focused terminal session.
        private static readonly HashSet<string> CmdLineHostExes = new(StringComparer.OrdinalIgnoreCase)
        {
            "node", "bun", "deno", "npm", "npx", "pnpm", "yarn", "cmd",
        };

        // Bulk scan covers native CLIs + script hosts + shells/terminals (needed to build the session
        // tree). CommandLine is deliberately excluded here — it is fetched per-PID only when required.
        private const string BulkProcessQuery =
            "SELECT ProcessId, ParentProcessId, Name, CreationDate FROM Win32_Process WHERE " +
            "Name = 'node.exe' OR Name = 'antigravity.exe' OR Name = 'agy.exe' OR Name = 'claude.exe' OR Name = 'codex.exe' OR Name = 'cursor.exe' OR Name = 'cursor-agent.exe' OR Name = 'opencode.exe' OR Name = 'copilot.exe' OR Name = 'gh.exe' OR Name = 'grok.exe' OR Name = 'devin.exe' OR Name = 'cline.exe' OR Name = 'bun.exe' OR Name = 'deno.exe' OR Name = 'npm.exe' OR Name = 'npx.exe' OR Name = 'pnpm.exe' OR Name = 'yarn.exe' OR Name = 'cmd.exe' OR Name = 'powershell.exe' OR Name = 'pwsh.exe' OR Name = 'windowsterminal.exe' OR Name = 'openconsole.exe' OR Name = 'conhost.exe' OR Name = 'wezterm-gui.exe' OR Name = 'wezterm.exe' OR Name = 'alacritty.exe' OR Name = 'mintty.exe' OR Name = 'tabby.exe' OR Name = 'hyper.exe' OR Name = 'wt.exe' OR Name = 'wsl.exe' OR Name = 'wslhost.exe'";

        // Brief cache so tab switches inside one terminal stay responsive without hammering WMI.
        private static readonly TimeSpan CliCacheTtl = TimeSpan.FromMilliseconds(250);
        private static readonly TimeSpan RunningToolCacheTtl = TimeSpan.FromSeconds(15);
        private IntPtr _lastForegroundHwnd;
        private int _lastForegroundPid;
        private string? _lastForegroundProcessName;
        private ProviderId? _lastForegroundResult;
        private ProviderId? _cliCache;
        private DateTime _cliCacheAt = DateTime.MinValue;
        private int? _cliCachePid;
        private int? _cliCacheSessionRootPid;
        private IntPtr _cliCacheFocusHwnd;
        private readonly Dictionary<IntPtr, ProviderId> _terminalWindowCliCache = new();
        private bool _runningToolCache;
        private DateTime _runningToolCacheAt = DateTime.MinValue;
        private volatile bool _openCodeModelStateDirty;
        private volatile bool _clineStateDirty;
        private OpenCodeModelStateWatcher? _modelStateWatcher;
        private ClineStateWatcher? _clineStateWatcher;
        private SynaraStateWatcher? _synaraStateWatcher;
        private SynaraStateReader.SynaraSelection? _synaraHost;
        private ProviderSource _activeSource = ProviderSource.Unknown;
        private readonly SynaraUiaReader _synaraUia = new();
        private readonly BrowserActiveTabDetector _browserTabs = new();

        /// <summary>Raised when Synara's localStorage changes (provider switch / thread navigation).</summary>
        public event Action? SynaraStateChanged;

        public void StartSynaraStateWatcher()
        {
            if (_synaraStateWatcher != null) return;

            _synaraStateWatcher = new SynaraStateWatcher();
            _synaraStateWatcher.StateChanged += () =>
            {
                SynaraStateReader.InvalidateDraftCache();
                SynaraStateChanged?.Invoke();
            };
            _synaraStateWatcher.Start();
        }

        /// <summary>
        /// When the last <see cref="Detect"/> resolved a provider via the Synara host app, the active
        /// thread's selection (inner provider + model); null otherwise. Lets the UI show a Synara badge
        /// over the inner provider's icon.
        /// </summary>
        public SynaraStateReader.SynaraSelection? ActiveSynaraHost => _synaraHost;

        public ProviderSource ActiveSource => _activeSource;

        /// <summary>
        /// Lightweight Synara-only detection: checks if Synara is foreground and reads its active
        /// selection directly from LevelDB. Skips the full <see cref="Detect"/> pipeline (WMI, terminal
        /// session resolution, CLI cache) so the file-watcher hot path resolves in microseconds.
        /// Returns null when Synara is not foreground or has no usable selection.
        /// </summary>
        public SynaraStateReader.SynaraSelection? DetectSynaraSelectionFast(string? onScreenModel = null)
        {
            var hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return null;

            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0) return null;

            var foregroundPid = (int)pid;
            string? procName;
            lock (_detectGate)
            {
                procName = foregroundPid == _lastForegroundPid
                    ? _lastForegroundProcessName
                    : null;
            }

            procName ??= TryGetProcessName(foregroundPid);
            if (SynaraStateReader.ResolveHost(procName) is not { } host)
                return null;

            // Read outside the detect lock so a slow full Detect() (WMI/terminal scan) never blocks
            // the Synara file-watcher hot path.
            var selection = SynaraStateReader.GetActiveSelection(
                includeThreadTitle: false,
                preferStickyComposerSelection: true,
                onScreenModel: onScreenModel,
                host: host);

            lock (_detectGate)
            {
                _lastForegroundHwnd = hwnd;
                _lastForegroundPid = foregroundPid;
                _lastForegroundProcessName = procName;
                _synaraHost = selection;
                _activeSource = selection is { } s
                    ? new ProviderSource(ProviderSourceKind.HostApp, HostName(s.Host), s.Host == HostApp.T3Code ? "t3code" : "synara")
                    : ProviderSource.Unknown;
            }

            return selection;
        }

        /// <summary>
        /// The foreground window handle when Synara is the focused app, else <see cref="IntPtr.Zero"/>.
        /// Lets the UIA reader attach to the right window without running the full detect pipeline.
        /// </summary>
        public IntPtr TryGetForegroundSynaraWindow()
        {
            var hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero)
                return IntPtr.Zero;

            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0)
                return IntPtr.Zero;

            var foregroundPid = (int)pid;
            string? procName;
            lock (_detectGate)
            {
                procName = foregroundPid == _lastForegroundPid
                    ? _lastForegroundProcessName
                    : null;
            }
            procName ??= TryGetProcessName(foregroundPid);

            return SynaraStateReader.IsSynaraProcessName(procName) ? hwnd : IntPtr.Zero;
        }

        /// <summary>Which host fork (Synara / T3 Code) is foreground right now, or null when neither is.</summary>
        public HostApp? TryGetForegroundHost()
        {
            var hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero)
                return null;

            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0)
                return null;

            var foregroundPid = (int)pid;
            string? procName;
            lock (_detectGate)
            {
                procName = foregroundPid == _lastForegroundPid
                    ? _lastForegroundProcessName
                    : null;
            }
            procName ??= TryGetProcessName(foregroundPid);

            return SynaraStateReader.ResolveHost(procName);
        }

        /// <summary>
        /// The model name shown live on Synara's composer (via UI Automation) when Synara is foreground,
        /// else null. Used to disambiguate which stored selection is the active one. Releases the UIA
        /// reader's cached handles when Synara is not foreground.
        /// </summary>
        public string? TryReadForegroundSynaraModel()
        {
            var hwnd = TryGetForegroundSynaraWindow();
            if (hwnd == IntPtr.Zero)
            {
                _synaraUia.Reset();
                return null;
            }
            return _synaraUia.TryReadActiveModelName(hwnd);
        }

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

        public event Action? ClineProviderStateChanged;

        public void StartClineStateWatcher()
        {
            if (_clineStateWatcher != null) return;

            _clineStateWatcher = new ClineStateWatcher();
            _clineStateWatcher.StateChanged += OnClineStateChanged;
            _clineStateWatcher.Start();
        }

        private string? _lastClineProviderKey;
        private bool _clineProviderKeySeen;

        private void OnClineStateChanged()
        {
            // The Cline hub daemon rewrites providers.json for unrelated reasons (token refresh, activity),
            // so only react when the active surface (lastUsedProvider) actually changes — otherwise every
            // write would thrash the CLI cache and trigger needless re-detects and network fetches.
            Usage.Providers.ClineAccount.InvalidateActiveProviderKeyCache();
            var key = Usage.Providers.ClineAccount.ActiveProviderKey();
            if (_clineProviderKeySeen && string.Equals(key, _lastClineProviderKey, StringComparison.Ordinal))
                return;

            _clineProviderKeySeen = true;
            _lastClineProviderKey = key;
            _clineStateDirty = true;
            ClineProviderStateChanged?.Invoke();
        }

        private bool ConsumeClineStateChange()
        {
            if (!_clineStateDirty)
                return false;

            _clineStateDirty = false;
            return true;
        }

        private bool ConsumeOpenCodeModelStateChange()
        {
            if (!_openCodeModelStateDirty)
                return false;

            _openCodeModelStateDirty = false;
            return true;
        }

        // Detect() mutates per-instance caches (_synaraHost, _cliCache, _lastForeground*). It is called
        // from the poll timer AND the OpenCode/Synara file-watcher handlers on different threads, so
        // serialize it — concurrent runs corrupt that state and make the active provider flip-flop.
        private readonly object _detectGate = new();

        public ProviderId? Detect()
        {
            lock (_detectGate)
                return DetectCore();
        }

        private ProviderId? DetectCore()
        {
            var hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return null;

            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0) return null;

            var foregroundPid = (int)pid;
            // Either watcher firing invalidates the CLI cache so the next detect re-resolves the active
            // surface (OpenCode Zen/Go, Cline usage-billing/ClinePass) instead of returning the stale pick.
            var openCodeModelChanged = ConsumeOpenCodeModelStateChange() | ConsumeClineStateChange();

            string? procName = foregroundPid == _lastForegroundPid
                ? _lastForegroundProcessName
                : TryGetProcessName(foregroundPid);

            var focusHwnd = GetThreadFocusHwnd(hwnd);
            var windowTitle = TryGetWindowTitle(hwnd);

            var sessionRootPid = ResolveTerminalSessionRootPid(hwnd, foregroundPid, procName);
            string? sessionRootName = sessionRootPid == foregroundPid
                ? procName
                : TryGetProcessName(sessionRootPid);

            bool terminalFocused = IsTerminalRelatedProcess(procName)
                || IsTerminalRelatedProcess(sessionRootName);

            if (ShouldReuseCliCache(hwnd, foregroundPid, sessionRootPid, terminalFocused, openCodeModelChanged, _cliCache, focusHwnd))
            {
                _lastForegroundHwnd = hwnd;
                _lastForegroundPid = foregroundPid;
                _lastForegroundProcessName = procName;
                if (_cliCache is { })
                    _activeSource = ResolveCliSource(sessionRootName ?? procName);
                return _lastForegroundResult = _cliCache;
            }

            _lastForegroundHwnd = hwnd;
            _lastForegroundPid = foregroundPid;
            _lastForegroundProcessName = procName;
            _synaraHost = null;
            _activeSource = ProviderSource.Unknown;
            if (procName == null) return null;

            // Synara host: a meta-app wrapping many providers. Resolve the inner provider from the active
            // thread's persisted model selection. A null selection (unsupported provider / no thread) falls
            // through to the last-active provider, so we don't show Synara for providers we can't track.
            if (SynaraStateReader.ResolveHost(procName) is { } host)
            {
                // Same parameters as the dedicated Synara poll (DetectSynaraSelectionFast) so this tick
                // path resolves the identical provider — otherwise the two fight and the widget flaps.
                var selection = SynaraStateReader.GetActiveSelection(
                    includeThreadTitle: true,
                    preferStickyComposerSelection: true,
                    onScreenModel: _synaraUia.TryReadActiveModelName(hwnd),
                    host: host);
                _synaraHost = selection;
                if (selection is { } s)
                {
                    _activeSource = new ProviderSource(ProviderSourceKind.HostApp, HostName(s.Host), s.Host == HostApp.T3Code ? "t3code" : "synara");
                    return _lastForegroundResult = s.Provider;
                }
                return _lastForegroundResult = null;
            }

            // Fast path: GUI desktop apps resolve from the foreground process name (~instant).
            if (TryResolveGuiProcess(procName) is { } gui)
            {
                _activeSource = ResolveDesktopSource(procName);
                return _lastForegroundResult = gui;
            }

            // Normal browser chat surfaces have their own account-level usage semantics and should not be
            // folded into coding-client providers like Codex or Antigravity.
            if (_browserTabs.Detect(hwnd, procName, windowTitle) is { } browserProvider)
            {
                _activeSource = browserProvider.Source;
                return _lastForegroundResult = browserProvider.Provider;
            }

            // Interactive CLI TUI focused directly (e.g. grok.exe owns the window).
            if (InteractiveClis.Contains(procName))
            {
                if (TryDetectCliProvider($"{procName}.exe", null) is { } cli)
                {
                    _activeSource = new ProviderSource(ProviderSourceKind.Cli, procName, "terminal");
                    return _lastForegroundResult = cli;
                }
            }

            if (terminalFocused)
            {
                Diagnostics.Log.Debug($"[detect] terminal focused: fgPid={foregroundPid} proc={procName} rootPid={sessionRootPid} rootName={sessionRootName} title='{windowTitle}'");
                bool hasPreferred = _terminalWindowCliCache.TryGetValue(hwnd, out var preferredProvider);
                _cliCache = DetectCliFromProcesses(
                    sessionRootPid,
                    sessionRootName ?? procName,
                    windowTitle,
                    hasPreferred ? preferredProvider : null);
                Diagnostics.Log.Debug($"[detect] result={_cliCache?.ToString() ?? "null"}");
                _cliCachePid = foregroundPid;
                _cliCacheSessionRootPid = sessionRootPid;
                _cliCacheFocusHwnd = focusHwnd;
                _cliCacheAt = DateTime.UtcNow;
                if (_cliCache is { } detectedCli)
                {
                    _terminalWindowCliCache[hwnd] = detectedCli;
                    _activeSource = ResolveCliSource(sessionRootName ?? procName);
                }
                return _lastForegroundResult = _cliCache;
            }

            _activeSource = ProviderSource.Unknown;
            return _lastForegroundResult = null;
        }

        private static string HostName(HostApp host) => host == HostApp.T3Code ? "T3 Code" : "Synara";

        private static ProviderSource ResolveDesktopSource(string? processName)
        {
            var normalized = NormalizeProcessName(processName ?? "");
            var name = normalized.ToLowerInvariant() switch
            {
                "claude" => "Claude app",
                "cursor" => "Cursor",
                "antigravity" => "Antigravity",
                "codex" => "Codex app",
                "devin" or "devin - next" => "Devin app",
                "code" or "code-insiders" => "VS Code",
                _ => string.IsNullOrWhiteSpace(normalized) ? "desktop app" : normalized,
            };
            return new ProviderSource(ProviderSourceKind.DesktopApp, name, "desktop");
        }

        private static ProviderSource ResolveCliSource(string? processName)
        {
            var normalized = NormalizeProcessName(processName ?? "");
            var name = normalized.ToLowerInvariant() switch
            {
                "powershell" => "PowerShell",
                "pwsh" => "PowerShell",
                "cmd" => "Command Prompt",
                "windowsterminal" or "wt" => "Windows Terminal",
                "wezterm" or "wezterm-gui" => "WezTerm",
                "alacritty" => "Alacritty",
                "bash" or "wsl" or "wslhost" => "WSL",
                _ => string.IsNullOrWhiteSpace(normalized) ? "terminal" : normalized,
            };
            return new ProviderSource(ProviderSourceKind.Cli, name, "terminal");
        }

        private static bool IsTerminalRelatedProcess(string? processName)
            => processName != null
            && (Terminals.Contains(processName)
                || IsConsoleHostName(processName)
                || IsShellHostName(processName)
                || InteractiveClis.Contains(processName));

        private bool ShouldReuseCliCache(
            IntPtr hwnd,
            int foregroundPid,
            int sessionRootPid,
            bool terminalFocused,
            bool openCodeModelChanged,
            ProviderId? cached,
            IntPtr focusHwnd)
            => ShouldReuseCliCacheState(
                hwnd,
                foregroundPid,
                sessionRootPid,
                terminalFocused,
                openCodeModelChanged,
                cached,
                _lastForegroundHwnd,
                _cliCachePid,
                _cliCacheSessionRootPid,
                _cliCacheAt,
                CliCacheTtl,
                focusHwnd,
                _cliCacheFocusHwnd);

        internal static bool ShouldReuseCliCacheState(
            IntPtr hwnd,
            int foregroundPid,
            int sessionRootPid,
            bool terminalFocused,
            bool openCodeModelChanged,
            ProviderId? cached,
            IntPtr lastForegroundHwnd,
            int? cachedPid,
            int? cachedSessionRootPid,
            DateTime cachedAt,
            TimeSpan ttl,
            IntPtr focusHwnd = default,
            IntPtr lastFocusHwnd = default)
            => terminalFocused
            && !openCodeModelChanged
            && cached is not null
            && hwnd == lastForegroundHwnd
            && foregroundPid == cachedPid
            && sessionRootPid == cachedSessionRootPid
            && (focusHwnd == IntPtr.Zero || lastFocusHwnd == IntPtr.Zero || focusHwnd == lastFocusHwnd)
            && DateTime.UtcNow - cachedAt < ttl;

        internal static int ResolveTerminalSessionRootPid(IntPtr hwnd, int foregroundPid, string? foregroundProcName)
        {
            if (hwnd == IntPtr.Zero)
                return foregroundPid;

            // Local cache to avoid repeated Process.GetProcessById during HWND ancestor/child walks.
            var nameCache = new Dictionary<int, string?>();

            foreach (var candidateHwnd in GetFocusWindowCandidates(hwnd))
            {
                if (TryGetTerminalSessionPidFromHwndChain(candidateHwnd, nameCache) is int sessionPid)
                    return sessionPid;
            }

            if (IsWindowsTerminalName(foregroundProcName)
                && TryFindFocusedTerminalPanePid(hwnd, nameCache) is int panePid)
                return panePid;

            if (IsConsoleHostName(foregroundProcName)
                || IsShellHostName(foregroundProcName)
                || InteractiveClis.Contains(foregroundProcName ?? ""))
                return foregroundPid;

            return foregroundPid;
        }

        private static IEnumerable<IntPtr> GetFocusWindowCandidates(IntPtr hwnd)
        {
            uint threadId = GetWindowThreadProcessId(hwnd, IntPtr.Zero);
            if (threadId != 0)
            {
                var info = new GUITHREADINFO { cbSize = Marshal.SizeOf<GUITHREADINFO>() };
                if (GetGUIThreadInfo(threadId, ref info))
                {
                    if (info.hwndFocus != IntPtr.Zero)
                        yield return info.hwndFocus;
                    if (info.hwndCaret != IntPtr.Zero && info.hwndCaret != info.hwndFocus)
                        yield return info.hwndCaret;
                    if (info.hwndActive != IntPtr.Zero && info.hwndActive != info.hwndFocus)
                        yield return info.hwndActive;
                }
            }

            yield return hwnd;

            for (var current = hwnd; current != IntPtr.Zero; current = GetAncestor(current, GetAncestorFlags.GA_PARENT))
                yield return current;
        }

        private static int? TryGetTerminalSessionPidFromHwndChain(IntPtr hwnd, Dictionary<int, string?>? cache = null)
        {
            cache ??= new Dictionary<int, string?>();
            for (var current = hwnd; current != IntPtr.Zero; current = GetAncestor(current, GetAncestorFlags.GA_PARENT))
            {
                GetWindowThreadProcessId(current, out uint pid);
                if (pid == 0)
                    continue;

                var name = TryGetProcessNameCached((int)pid, cache);
                if (IsConsoleHostName(name) || IsShellHostName(name) || InteractiveClis.Contains(name ?? ""))
                    return (int)pid;
            }

            return null;
        }

        private static int? TryFindFocusedTerminalPanePid(IntPtr hwnd, Dictionary<int, string?>? cache = null)
        {
            cache ??= new Dictionary<int, string?>();
            var focusHwnd = GetThreadFocusHwnd(hwnd);
            if (focusHwnd != IntPtr.Zero
                && TryGetTerminalSessionPidFromHwndChain(focusHwnd, cache) is int focusedPid)
                return focusedPid;

            int? best = null;
            int bestDepth = int.MaxValue;

            void Visit(IntPtr child, int depth)
            {
                if (focusHwnd != IntPtr.Zero && !IsHwndInSubtree(child, focusHwnd))
                    return;

                GetWindowThreadProcessId(child, out uint childPid);
                if (childPid == 0)
                    return;

                var name = TryGetProcessNameCached((int)childPid, cache);
                if (!IsConsoleHostName(name) && !IsShellHostName(name))
                    return;

                if (depth < bestDepth)
                {
                    bestDepth = depth;
                    best = (int)childPid;
                }
            }

            void WalkChildren(IntPtr parent, int depth)
            {
                try
                {
                    EnumChildWindows(parent, (child, _) =>
                    {
                        Visit(child, depth);
                        WalkChildren(child, depth + 1);
                        return true;
                    }, IntPtr.Zero);
                }
                catch
                {
                    // Best effort.
                }
            }

            WalkChildren(hwnd, 0);
            return best;
        }

        private static IntPtr GetThreadFocusHwnd(IntPtr hwnd)
        {
            uint threadId = GetWindowThreadProcessId(hwnd, IntPtr.Zero);
            if (threadId == 0)
                return IntPtr.Zero;

            var info = new GUITHREADINFO { cbSize = Marshal.SizeOf<GUITHREADINFO>() };
            return GetGUIThreadInfo(threadId, ref info) ? info.hwndFocus : IntPtr.Zero;
        }

        private static bool IsHwndInSubtree(IntPtr ancestor, IntPtr descendant)
        {
            for (var current = descendant; current != IntPtr.Zero; current = GetAncestor(current, GetAncestorFlags.GA_PARENT))
            {
                if (current == ancestor)
                    return true;
            }

            return false;
        }

        private static string? TryGetProcessNameStatic(int pid)
        {
            try { return Process.GetProcessById(pid).ProcessName; }
            catch { return null; }
        }

        private static string? TryGetProcessNameCached(int pid, Dictionary<int, string?> cache)
        {
            if (cache.TryGetValue(pid, out var cached))
                return cached;
            var name = TryGetProcessNameStatic(pid);
            cache[pid] = name;
            return name;
        }

        private static string? TryGetWindowTitle(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero)
                return null;
            try
            {
                var sb = new System.Text.StringBuilder(512);
                int len = GetWindowText(hwnd, sb, sb.Capacity);
                if (len > 0)
                    return sb.ToString();
            }
            catch { }
            return null;
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
                        if (IsKnownGuiProcessName(process.ProcessName)
                            || SynaraStateReader.IsSynaraProcessName(process.ProcessName))
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

        private static ProviderId? DetectCliFromProcesses(
            int? foregroundTerminalPid = null,
            string? foregroundProcName = null,
            string? windowTitleHint = null,
            ProviderId? preferredProvider = null)
        {
            // One cheap, name-only scan builds the process tree and resolves native CLI exes. Script-host
            // CommandLines (node/bun/npm/...) are read separately and only for the focused session, so a
            // terminal full of unrelated node.exe children no longer stalls detection.
            var candidates = new List<(ProviderId id, DateTime started, int pid, int parentPid)>();
            var processParents = new Dictionary<int, int>();
            var processNames = new Dictionary<int, string>();
            var processStarts = new Dictionary<int, DateTime>();
            var hostPids = new List<int>();
            try
            {
                using var searcher = new ManagementObjectSearcher(BulkProcessQuery);
                foreach (ManagementObject mo in searcher.Get())
                {
                    var pid = TryParseInt(mo["ProcessId"]);
                    if (pid is not int p) continue;
                    var parentPid = TryParseInt(mo["ParentProcessId"]) ?? 0;
                    processParents[p] = parentPid;
                    var name = NormalizeProcessName(mo["Name"] as string ?? "");
                    processNames[p] = name;
                    var started = ParseWmiDate(mo["CreationDate"] as string);
                    processStarts[p] = started;

                    if (NativeCliExes.TryGetValue(name, out var nativeId))
                    {
                        var detectedId = ResolveDynamicCliProvider(nativeId);
                        candidates.Add((detectedId, started, p, parentPid));
                    }
                    else if (CmdLineHostExes.Contains(name))
                    {
                        hostPids.Add(p);
                    }
                }
            }
            catch (Exception ex)
            {
                Diagnostics.Log.Warning(ex, "Failed to enumerate CLI processes");
                return null;
            }

            if (foregroundTerminalPid is int rootPid)
            {
                var sessionPids = ExpandTerminalSessionPids(rootPid, foregroundProcName, processParents, processNames, processStarts);
                Diagnostics.Log.Debug($"[detect] session pids=[{string.Join(",", sessionPids)}] candidates=[{string.Join(",", candidates.Select(c => $"{c.id}:{c.pid}<-{c.parentPid}"))}] hosts=[{string.Join(",", hostPids)}]");

                // Only the script hosts inside the focused session warrant the costly CommandLine read.
                var sessionHostPids = hostPids
                    .Where(p => IsInTerminalSession(p, processParents.TryGetValue(p, out var pp) ? pp : 0, sessionPids, processParents))
                    .ToList();
                ResolveHostCommandLines(sessionHostPids, processStarts, processParents, candidates);

                var foregroundTree = candidates
                    .Where(c => IsInTerminalSession(c.pid, c.parentPid, sessionPids, processParents))
                    .ToList();
                Diagnostics.Log.Debug($"[detect] foregroundTree=[{string.Join(",", foregroundTree.Select(c => $"{c.id}:{c.pid}"))}] sessionHosts=[{string.Join(",", sessionHostPids)}]");

                var picked = PickTerminalCli(
                    candidates,
                    foregroundTree,
                    IsWindowsTerminalName(foregroundProcName),
                    rootPid,
                    processParents,
                    windowTitleHint,
                    preferredProvider);
                if (picked is { } pk)
                    return pk;

                // From here foregroundTree is empty (PickTerminalCli always resolves a non-null pick when it
                // has session candidates). Node/bun hosts in this tab but no CLI resolved — don't fall back
                // to Grok or a global pick.
                if (sessionHostPids.Count > 0)
                    return null;

                if (TryDetectGrokFromActiveSessions(sessionPids, processParents) is { } grok)
                    return grok;

                return null;
            }
            else if (candidates.Count == 0)
            {
                // Presence probe with no native CLI found: fall back to inspecting every script host.
                ResolveHostCommandLines(hostPids, processStarts, processParents, candidates);
            }

            if (candidates.Count == 0) return null;
            return PickForegroundCli(candidates, foregroundTerminalPid ?? -1, processParents);
        }

        internal static ProviderId PickForegroundCli(
            IReadOnlyList<(ProviderId id, DateTime started, int pid, int parentPid)> candidates,
            int sessionRootPid,
            IReadOnlyDictionary<int, int> parents)
        {
            return candidates
                .OrderByDescending(c => SessionProximity(c.pid, sessionRootPid, parents))
                .ThenByDescending(c => DescendantDepth(c.pid, sessionRootPid, parents))
                .ThenByDescending(c => c.started)
                .First().id;
        }

        // Resolves which CLI a focused terminal is running from process/session evidence.
        //
        // Window titles are ignored here because they can contain arbitrary project or prompt text.
        // disambiguation signal. We trust it first against the in-session tree, then — for Windows
        // Terminal only — against the global candidate list. The latter matters because separate WT
        // windows share one WindowsTerminal.exe process and ConPTY child shells are frequently
        // reparented away from it (e.g. under explorer.exe), so the parent-process session tree can
        // both miss the focused window's CLI and over-include CLIs from other windows. Only when the
        // title yields nothing do we fall back to the process-tree proximity/recency pick.
        //
        // Returns null only when there are no session candidates and no Windows Terminal title match,
        // leaving the caller free to try host/Grok fallbacks.
        internal static ProviderId? PickTerminalCli(
            IReadOnlyList<(ProviderId id, DateTime started, int pid, int parentPid)> allCandidates,
            IReadOnlyList<(ProviderId id, DateTime started, int pid, int parentPid)> foregroundTree,
            bool foregroundIsWindowsTerminal,
            int sessionRootPid,
            IReadOnlyDictionary<int, int> parents,
            string? windowTitleHint,
            ProviderId? preferredProvider = null)
        {
            if (foregroundIsWindowsTerminal
                && TryPickByWindowTitleHint(allCandidates, windowTitleHint) is { } activeTitlePick)
            {
                return activeTitlePick;
            }

            if (foregroundTree.Count > 0)
            {
                if (TryPickByWindowTitleHint(foregroundTree, windowTitleHint) is { } titlePick)
                    return titlePick;

                if (preferredProvider is { } preferred
                    && foregroundTree.Any(c => c.id == preferred))
                {
                    return preferred;
                }

                return PickForegroundCli(foregroundTree, sessionRootPid, parents);
            }

            return null;
        }

        internal static ProviderId? TryPickByWindowTitleHint(
            IReadOnlyList<(ProviderId id, DateTime started, int pid, int parentPid)> candidates,
            string? windowTitle)
        {
            if (candidates.Count == 0 || string.IsNullOrWhiteSpace(windowTitle))
                return null;

            var haystack = windowTitle.ToLowerInvariant();

            // Check command-line style markers first (Claude before Codex etc.)
            foreach (var (marker, id) in CliMarkers)
            {
                if (ContainsCliMarker(haystack, marker))
                {
                    var matches = candidates.Where(c => c.id == id).ToList();
                    if (matches.Count > 0)
                        return PickForegroundCli(matches, -1, new Dictionary<int, int>());
                }
            }

            // Also honor direct exe name mentions that may appear in terminal/tab titles.
            foreach (var kvp in NativeCliExes)
            {
                if (ContainsCliMarker(haystack, kvp.Key))
                {
                    var matches = candidates.Where(c => c.id == kvp.Value).ToList();
                    if (matches.Count > 0)
                        return PickForegroundCli(matches, -1, new Dictionary<int, int>());
                }
            }

            return null;
        }

        internal static int SessionProximity(int candidatePid, int sessionRootPid, IReadOnlyDictionary<int, int> parents)
        {
            if (sessionRootPid <= 0)
                return 0;

            if (candidatePid == sessionRootPid)
                return 1_000;

            if (parents.TryGetValue(candidatePid, out var parentPid) && parentPid == sessionRootPid)
                return 900;

            if (IsDescendantOf(candidatePid, sessionRootPid, parents))
                return 800 - DescendantDepth(candidatePid, sessionRootPid, parents);

            return 0;
        }

        private static int DescendantDepth(int pid, int ancestorPid, IReadOnlyDictionary<int, int> parents)
        {
            int depth = 0;
            var seen = new HashSet<int>();
            while (pid != 0 && seen.Add(pid) && parents.TryGetValue(pid, out var parentPid))
            {
                if (parentPid == ancestorPid)
                    return depth + 1;

                pid = parentPid;
                depth++;
            }

            return depth;
        }

        // Reads CommandLine for a small set of script-host PIDs and adds any CLI matches as candidates.
        private static void ResolveHostCommandLines(
            IEnumerable<int> pids,
            IReadOnlyDictionary<int, DateTime> starts,
            IReadOnlyDictionary<int, int> parents,
            List<(ProviderId id, DateTime started, int pid, int parentPid)> candidates)
        {
            var list = new List<int>(new HashSet<int>(pids));
            if (list.Count == 0) return;

            const int chunkSize = 60;
            for (int i = 0; i < list.Count; i += chunkSize)
            {
                var where = string.Join(" OR ", list.GetRange(i, Math.Min(chunkSize, list.Count - i)).ConvertAll(p => "ProcessId = " + p));
                try
                {
                    using var searcher = new ManagementObjectSearcher(
                        "SELECT ProcessId, Name, CommandLine FROM Win32_Process WHERE " + where);
                    foreach (ManagementObject mo in searcher.Get())
                    {
                        var pid = TryParseInt(mo["ProcessId"]) ?? 0;
                        if (TryDetectCliProvider(mo["Name"] as string, mo["CommandLine"] as string) is not { } id)
                            continue;
                        var detectedId = ResolveDynamicCliProvider(id);
                        starts.TryGetValue(pid, out var started);
                        parents.TryGetValue(pid, out var parentPid);
                        candidates.Add((detectedId, started, pid, parentPid));
                    }
                }
                catch (Exception ex)
                {
                    Diagnostics.Log.Warning(ex, "Failed to read CLI command lines");
                }
            }
        }

        private static DateTime ParseWmiDate(string? wmiDate)
        {
            if (string.IsNullOrEmpty(wmiDate)) return DateTime.MinValue;
            try { return ManagementDateTimeConverter.ToDateTime(wmiDate); }
            catch { return DateTime.MinValue; }
        }

        internal static HashSet<int> ExpandTerminalSessionPids(
            int foregroundPid,
            string? foregroundProcName,
            IReadOnlyDictionary<int, int> parents,
            IReadOnlyDictionary<int, string> names,
            IReadOnlyDictionary<int, DateTime>? starts = null)
        {
            var session = new HashSet<int> { foregroundPid };

            void AddShellSession(int shellPid)
            {
                var queue = new Queue<int>();
                queue.Enqueue(shellPid);
                while (queue.Count > 0)
                {
                    var pid = queue.Dequeue();
                    if (!session.Add(pid))
                        continue;

                    foreach (var (childPid, parentPid) in parents)
                    {
                        if (parentPid == pid)
                            queue.Enqueue(childPid);
                    }
                }
            }

            if (IsShellHostName(foregroundProcName))
            {
                AddShellSession(foregroundPid);
                return session;
            }

            if (IsConsoleHostName(foregroundProcName))
            {
                session.Add(foregroundPid);
                if (parents.TryGetValue(foregroundPid, out var parentPid)
                    && IsShellHostName(names.GetValueOrDefault(parentPid)))
                {
                    AddShellSession(parentPid);
                }
                else
                {
                    foreach (var (pid, ppid) in parents)
                    {
                        if (ppid != foregroundPid || !IsShellHostName(names.GetValueOrDefault(pid)))
                            continue;

                        AddShellSession(pid);
                    }
                }

                return session;
            }

            if (IsWindowsTerminalName(foregroundProcName))
            {
                // Avoid attributing every tab in Windows Terminal to the foreground window.
                foreach (var (pid, _) in parents)
                {
                    if (!names.TryGetValue(pid, out var name) || !IsConsoleHostName(name))
                        continue;

                    if (!IsDescendantOf(pid, foregroundPid, parents))
                        continue;

                    session.Add(pid);
                    if (parents.TryGetValue(pid, out var paneShellPid) && IsShellHostName(names.GetValueOrDefault(paneShellPid)))
                        AddShellSession(paneShellPid);
                }

                if (session.Count == 1 && starts is not null)
                {
                    var paneStarts = new List<DateTime>();
                    if (parents.TryGetValue(foregroundPid, out var terminalParentPid))
                    {
                        foreach (var (pid, parentPid) in parents)
                        {
                            if (parentPid != terminalParentPid)
                                continue;

                            if (names.TryGetValue(pid, out var name)
                                && IsConsoleHostName(name)
                                && starts.TryGetValue(pid, out var paneStarted))
                            {
                                paneStarts.Add(paneStarted);
                                session.Add(pid);
                            }
                        }
                    }

                    if (paneStarts.Count == 0 && starts.TryGetValue(foregroundPid, out var terminalStarted))
                        paneStarts.Add(terminalStarted);

                    foreach (var (pid, name) in names)
                    {
                        if (!IsShellHostName(name) || !starts.TryGetValue(pid, out var shellStarted))
                            continue;

                        if (paneStarts.Any(paneStarted => Math.Abs((shellStarted - paneStarted).TotalSeconds) <= 3))
                            AddShellSession(pid);
                    }
                }
            }

            // Generic terminal GUI containers (WezTerm, Alacritty, Tabby, Hyper, mintty, etc.).
            // Collect any hosted console hosts, direct or descendant shell hosts (so their children are included),
            // and any interactive CLIs launched directly inside this terminal instance.
            if (IsTerminalGuiContainer(foregroundProcName) && !IsWindowsTerminalName(foregroundProcName))
            {
                foreach (var (pid, _) in parents)
                {
                    if (!IsDescendantOf(pid, foregroundPid, parents))
                        continue;

                    var name = names.GetValueOrDefault(pid);
                    if (IsConsoleHostName(name))
                    {
                        session.Add(pid);
                        if (parents.TryGetValue(pid, out var paneShellPid) && IsShellHostName(names.GetValueOrDefault(paneShellPid)))
                            AddShellSession(paneShellPid);
                    }
                    else if (IsShellHostName(name))
                    {
                        AddShellSession(pid);
                    }
                    else if (InteractiveClis.Contains(name ?? ""))
                    {
                        session.Add(pid);
                    }
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
                if (!IsGrokPidInFocusedSession(grokPid, sessionPids, parents))
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

        internal static bool IsGrokPidInFocusedSession(
            int grokPid,
            IReadOnlySet<int> sessionPids,
            IReadOnlyDictionary<int, int> parents)
        {
            if (sessionPids.Contains(grokPid))
                return true;

            return sessionPids.Any(rootPid => IsDescendantOf(grokPid, rootPid, parents));
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
            processName != null
            && (processName.Equals("conhost", StringComparison.OrdinalIgnoreCase)
                || processName.Equals("openconsole", StringComparison.OrdinalIgnoreCase));

        private static bool IsWindowsTerminalName(string? processName) =>
            processName != null
            && (processName.Equals("windowsterminal", StringComparison.OrdinalIgnoreCase)
                || processName.Equals("wt", StringComparison.OrdinalIgnoreCase));

        private static bool IsTerminalGuiContainer(string? processName) =>
            processName != null
            && Terminals.Contains(processName)
            && !IsConsoleHostName(processName)
            && !IsShellHostName(processName);

        internal static ProviderId? TryDetectCliProvider(string? processName, string? commandLine)
        {
            var name = (processName ?? "").ToLowerInvariant();
            if (name is "antigravity.exe" or "agy.exe") return ProviderId.Antigravity;
            if (name is "claude.exe") return ProviderId.Claude;
            if (name is "codex.exe") return ProviderId.Codex;
            if (name is "cursor.exe") return ProviderId.Cursor;
            if (name is "cursor-agent.exe") return ProviderId.Cursor;
            if (name is "opencode.exe") return ProviderId.OpenCode;
            if (name is "copilot.exe" or "gh.exe") return ProviderId.Copilot;
            if (name is "grok.exe") return ProviderId.Grok;
            if (name is "devin.exe") return ProviderId.Devin;
            if (name is "cline.exe") return ProviderId.Cline;

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

        /// <summary>Resolves native CLI ids that map to more than one surface to the currently-active one.</summary>
        private static ProviderId ResolveDynamicCliProvider(ProviderId nativeId) => nativeId switch
        {
            ProviderId.OpenCode => DetectOpenCodeProviderFromModelState() ?? ProviderId.OpenCode,
            ProviderId.Cline or ProviderId.ClinePass => DetectClineProviderFromState() ?? ProviderId.Cline,
            _ => nativeId,
        };

        /// <summary>
        /// The active Cline surface, from providers.json <c>lastUsedProvider</c>: "cline-pass" → ClinePass
        /// subscription, "cline" → Cline usage-billing. Defaults to ClinePass when a subscription is the
        /// only configured surface, else usage-billing.
        /// </summary>
        internal static ProviderId? DetectClineProviderFromState()
        {
            var key = Usage.Providers.ClineAccount.ActiveProviderKey();
            return key switch
            {
                Usage.Providers.ClineAccount.SubscriptionKey => ProviderId.ClinePass,
                Usage.Providers.ClineAccount.UsageBillingKey => ProviderId.Cline,
                _ => null,
            };
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
