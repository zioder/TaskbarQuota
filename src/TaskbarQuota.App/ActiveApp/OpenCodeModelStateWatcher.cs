using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace TaskbarQuota.ActiveApp
{
    /// <summary>
    /// Watches OpenCode model-selection files and signals when Zen/Go may have changed.
    /// </summary>
    internal sealed class OpenCodeModelStateWatcher : IDisposable
    {
        private static readonly TimeSpan DebounceDelay = TimeSpan.FromMilliseconds(50);

        private readonly List<FileSystemWatcher> _watchers = new();
        private readonly object _debounceLock = new();
        private Timer? _debounceTimer;

        public event Action? ModelStateChanged;

        public void Start()
        {
            var modelJsonPath = ActiveAppDetector.GetModelJsonPath();
            var modelDir = Path.GetDirectoryName(modelJsonPath)!;
            if (Directory.Exists(modelDir))
                AddWatcher(modelDir, "model.json");
            else if (Directory.Exists(ActiveAppDetector.GetStateRoot()))
                AddWatcher(ActiveAppDetector.GetStateRoot(), "model.json", includeSubdirectories: true);

            foreach (var desktopDir in ActiveAppDetector.GetDesktopDirs())
                AddWatcher(desktopDir, "*.dat");
        }

        private void AddWatcher(string directory, string filter, bool includeSubdirectories = false)
        {
            var watcher = new FileSystemWatcher(directory)
            {
                Filter = filter,
                IncludeSubdirectories = includeSubdirectories,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                EnableRaisingEvents = true,
            };
            watcher.Changed += OnFileEvent;
            watcher.Created += OnFileEvent;
            watcher.Renamed += OnRenamed;
            _watchers.Add(watcher);
        }

        private void OnFileEvent(object sender, FileSystemEventArgs e) => NotifyIfRelevant(e.FullPath);

        private void OnRenamed(object sender, RenamedEventArgs e) => NotifyIfRelevant(e.FullPath);

        private void NotifyIfRelevant(string fullPath)
        {
            if (!ActiveAppDetector.IsOpenCodeModelStatePath(fullPath))
                return;

            ScheduleNotify();
        }

        private void ScheduleNotify()
        {
            lock (_debounceLock)
            {
                _debounceTimer?.Dispose();
                _debounceTimer = new Timer(_ =>
                {
                    lock (_debounceLock)
                    {
                        _debounceTimer?.Dispose();
                        _debounceTimer = null;
                    }

                    ModelStateChanged?.Invoke();
                }, null, DebounceDelay, Timeout.InfiniteTimeSpan);
            }
        }

        public void Dispose()
        {
            foreach (var watcher in _watchers)
            {
                watcher.EnableRaisingEvents = false;
                watcher.Dispose();
            }

            _watchers.Clear();

            lock (_debounceLock)
            {
                _debounceTimer?.Dispose();
                _debounceTimer = null;
            }
        }
    }
}
