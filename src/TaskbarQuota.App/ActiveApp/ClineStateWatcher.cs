using System;
using System.IO;
using System.Threading;

namespace TaskbarQuota.ActiveApp
{
    /// <summary>
    /// Watches the Cline settings file (~/.cline/data/settings/providers.json) and signals when the
    /// active surface (lastUsedProvider: "cline" usage-billing vs "cline-pass" subscription) may have
    /// changed, so the coordinator can switch the highlighted card in realtime.
    /// </summary>
    internal sealed class ClineStateWatcher : IDisposable
    {
        private static readonly TimeSpan DebounceDelay = TimeSpan.FromMilliseconds(50);

        private FileSystemWatcher? _watcher;
        private readonly object _debounceLock = new();
        private Timer? _debounceTimer;

        public event Action? StateChanged;

        public void Start()
        {
            if (_watcher != null) return;

            var path = Usage.Providers.ClineAccount.ProvidersJsonPath();
            var dir = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                return;

            _watcher = new FileSystemWatcher(dir)
            {
                Filter = "providers.json",
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                EnableRaisingEvents = true,
            };
            _watcher.Changed += OnFileEvent;
            _watcher.Created += OnFileEvent;
            _watcher.Renamed += OnFileEvent;
        }

        private void OnFileEvent(object sender, FileSystemEventArgs e) => ScheduleNotify();

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

                    StateChanged?.Invoke();
                }, null, DebounceDelay, Timeout.InfiniteTimeSpan);
            }
        }

        public void Dispose()
        {
            if (_watcher != null)
            {
                _watcher.EnableRaisingEvents = false;
                _watcher.Dispose();
                _watcher = null;
            }

            lock (_debounceLock)
            {
                _debounceTimer?.Dispose();
                _debounceTimer = null;
            }
        }
    }
}
