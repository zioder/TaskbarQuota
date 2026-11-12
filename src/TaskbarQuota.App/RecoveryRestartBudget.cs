using System;

namespace TaskbarQuota;

internal sealed class RecoveryRestartBudget
{
    internal const int MaximumAttempts = 3;
    internal static readonly TimeSpan HealthyWindow = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan MaximumTickGap = TimeSpan.FromSeconds(15);
    private readonly object _sync = new();
    private int _attempt;
    private TimeSpan? _healthySince;
    private TimeSpan? _lastTick;

    internal RecoveryRestartBudget(int attempt)
    {
        if (attempt < 0 || attempt > MaximumAttempts) throw new ArgumentOutOfRangeException(nameof(attempt));
        _attempt = attempt;
    }

    // Reading the next attempt doesn't spend another attempt when Process.Start fails.
    internal bool TryGetNextAttempt(out int next)
    {
        lock (_sync)
        {
            next = _attempt + 1;
            return _attempt < MaximumAttempts;
        }
    }

    // Called only by the XAML health timer, not by the dispatcher heartbeat: a live queue alone
    // doesn't establish that WinUI's rendering clock has recovered. Time comes from Stopwatch.
    internal bool ReportHealthyTick(TimeSpan elapsed)
    {
        lock (_sync)
        {
            if (_lastTick is not { } last || elapsed < last || elapsed - last > MaximumTickGap)
                _healthySince = elapsed;
            _lastTick = elapsed;
            if (_attempt == 0 || elapsed - _healthySince!.Value < HealthyWindow) return false;
            _attempt = 0;
            return true;
        }
    }
}
