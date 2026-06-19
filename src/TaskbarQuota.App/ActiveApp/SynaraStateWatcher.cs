using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using TaskbarQuota.Diagnostics;

namespace TaskbarQuota.ActiveApp
{
    /// <summary>
    /// Watches Synara's Chromium localStorage LevelDB and signals when the composer draft / focused thread
    /// may have changed, so a provider switch reflects instantly instead of waiting for the next poll tick
    /// (mirrors <see cref="OpenCodeModelStateWatcher"/>).
    /// </summary>
    internal sealed class SynaraStateWatcher : IDisposable
    {
        // Tight debounce: Chromium writes the active .log atomically per record, so a single FS event is
        // a strong "something just landed" signal. 1 ms collapses Chromium's intra-batch bursts while
        // staying well below the fallback poll interval.
        private static readonly TimeSpan DebounceDelay = TimeSpan.FromMilliseconds(1);
        private static readonly TimeSpan DiscoveryInterval = TimeSpan.FromSeconds(1);
        // Pre-warm the LevelDB parse off the hot path so the first user-visible refresh is microseconds,
        // not a multi-MB cold parse. Runs once.
        private static readonly TimeSpan PrewarmDelay = TimeSpan.FromMilliseconds(50);

        private readonly List<FileSystemWatcher> _watchers = new();
        private readonly HashSet<string> _watcherKeys = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _debounceLock = new();
        private Timer? _debounceTimer;
        private Timer? _discoveryTimer;
        private Timer? _prewarmTimer;
        private bool _pendingDuringCooldown;
        private bool _prewarmed;

        public event Action? StateChanged;

        public void Start()
        {
            DiscoverWatchTargets();
            _discoveryTimer = new Timer(_ => DiscoverWatchTargets(),
                null, DiscoveryInterval, DiscoveryInterval);
            // Pre-warm a few ms after start so the first hot-path read hits a warm cache. The timer is
            // disposed once fired (see PrewarmOnce) so it never runs again.
            _prewarmTimer = new Timer(_ => PrewarmOnce(), null, PrewarmDelay, Timeout.InfiniteTimeSpan);
        }

        private void PrewarmOnce()
        {
            if (_prewarmed) return;
            _prewarmed = true;
            try { _prewarmTimer?.Dispose(); } catch { }
            _prewarmTimer = null;

            // Build the full snapshot (sstables + log) off-thread so the first foreground refresh doesn't
            // pay the cold-parse cost. The result is cached in the reader; hot-path reads only read the
            // appended tail of the active .log.
            var dirs = SynaraComposerDraftReader.GetLevelDbDirs();
            if (dirs.Count == 0) return;
            Task.Run(() =>
            {
                foreach (var dir in dirs)
                {
                    try { SynaraComposerDraftReader.WarmSnapshot(dir); }
                    catch (Exception ex) { Log.Debug($"[synara] prewarm failed ({dir}): {ex.Message}"); }
                }
            });
        }

        private void DiscoverWatchTargets()
        {
            foreach (var dir in SynaraComposerDraftReader.GetLevelDbDirs())
            {
                if (!Directory.Exists(dir))
                    continue;

                // LevelDB writes the active .log on every draft/navigation change and rewrites .ldb on
                // compaction — watch only these two extensions so MANIFEST/CURRENT/LOCK noise is ignored.
                AddWatcher(dir, "*.log");
                AddWatcher(dir, "*.ldb");
            }

            // SQLite WAL updates when thread metadata changes (e.g. model selection on send). Watching
            // alongside localStorage covers both the live draft and any persisted projection updates.
            var dbPath = SynaraStateReader.GetStateDbPath();
            if (dbPath != null)
            {
                var dbDir = Path.GetDirectoryName(dbPath);
                if (dbDir != null && Directory.Exists(dbDir))
                {
                    AddWatcher(dbDir, Path.GetFileName(dbPath));
                    AddWatcher(dbDir, Path.GetFileName(dbPath) + "-wal");
                    AddWatcher(dbDir, Path.GetFileName(dbPath) + "-shm");
                }
            }
        }

        private void AddWatcher(string directory, string filter)
        {
            var key = $"{Path.GetFullPath(directory)}|{filter}";
            if (!_watcherKeys.Add(key))
                return;

            var watcher = new FileSystemWatcher(directory)
            {
                Filter = filter,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                EnableRaisingEvents = true,
            };
            watcher.Changed += OnEvent;
            watcher.Created += OnEvent;
            watcher.Renamed += OnEvent;
            _watchers.Add(watcher);
        }

        private void OnEvent(object sender, FileSystemEventArgs e) => ScheduleNotify();

        private void ScheduleNotify()
        {
            var notifyNow = false;
            lock (_debounceLock)
            {
                if (_debounceTimer == null)
                {
                    notifyNow = true;
                    _debounceTimer = new Timer(_ => OnCooldownElapsed(),
                        null, DebounceDelay, Timeout.InfiniteTimeSpan);
                }
                else
                {
                    _pendingDuringCooldown = true;
                }
            }

            if (notifyNow)
            {
                Log.Debug("[synara] filesystem change notified");
                StateChanged?.Invoke();
            }
        }

        private void OnCooldownElapsed()
        {
            var notifyAgain = false;
            lock (_debounceLock)
            {
                if (_pendingDuringCooldown)
                {
                    _pendingDuringCooldown = false;
                    notifyAgain = true;
                    _debounceTimer?.Change(DebounceDelay, Timeout.InfiniteTimeSpan);
                }
                else
                {
                    _debounceTimer?.Dispose();
                    _debounceTimer = null;
                }
            }

            if (notifyAgain)
            {
                Log.Debug("[synara] filesystem follow-up notified");
                StateChanged?.Invoke();
            }
        }

        public void Dispose()
        {
            _discoveryTimer?.Dispose();
            _discoveryTimer = null;

            _prewarmTimer?.Dispose();
            _prewarmTimer = null;

            foreach (var watcher in _watchers)
            {
                watcher.EnableRaisingEvents = false;
                watcher.Dispose();
            }
            _watchers.Clear();
            _watcherKeys.Clear();

            lock (_debounceLock)
            {
                _debounceTimer?.Dispose();
                _debounceTimer = null;
                _pendingDuringCooldown = false;
            }
        }
    }
}
