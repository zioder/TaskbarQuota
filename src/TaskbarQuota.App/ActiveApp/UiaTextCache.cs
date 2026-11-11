using System;

namespace TaskbarQuota.ActiveApp;

/// <summary>
/// Rate-limits cross-process UIA reads that would otherwise run on every timer tick (e.g. Synara's
/// 100 ms poll). Returns the last value when still within <paramref name="minIntervalMs"/>; keeps
/// serving it for up to <paramref name="maxStaleMs"/> when a fresh read comes back empty.
/// </summary>
internal sealed class UiaTextCache
{
    private readonly long _minIntervalMs;
    private readonly long _maxStaleMs;
    private readonly object _gate = new();
    private IntPtr _hwnd;
    private string? _discriminator;
    private string? _cached;
    private long _atTicks;
    private int _readGeneration;

    internal UiaTextCache(int minIntervalMs, int maxStaleMs)
    {
        _minIntervalMs = minIntervalMs;
        _maxStaleMs = maxStaleMs;
    }

    internal string? Read(IntPtr hwnd, string? discriminator, Func<string?> read)
    {
        int generation;
        lock (_gate)
        {
            var now = Environment.TickCount64;
            if (_hwnd == hwnd
                && string.Equals(_discriminator, discriminator, StringComparison.Ordinal)
                && now - _atTicks < _minIntervalMs)
                return _cached;

            generation = ++_readGeneration;
        }

        var value = read();

        lock (_gate)
        {
            if (generation != _readGeneration)
                return _cached;

            var now = Environment.TickCount64;
            if (value is null
                && _hwnd == hwnd
                && string.Equals(_discriminator, discriminator, StringComparison.Ordinal)
                && _cached is not null
                && now - _atTicks < _maxStaleMs)
                return _cached;

            _hwnd = hwnd;
            _discriminator = discriminator;
            _cached = value;
            _atTicks = now;
            return value;
        }
    }

    internal void Reset()
    {
        lock (_gate)
        {
            _readGeneration++;
            _hwnd = IntPtr.Zero;
            _discriminator = null;
            _cached = null;
            _atTicks = 0;
        }
    }
}
