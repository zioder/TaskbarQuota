using System;
using System.IO;
using System.Reflection;
using TaskbarQuota;
using TaskbarQuota.ActiveApp;
using Xunit;

namespace TaskbarQuota.Tests;

public class SynaraPerformanceFixTests : IDisposable
{
    private readonly string _tempDir;

    public SynaraPerformanceFixTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"TaskbarQuotaSynaraFix_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        SynaraComposerDraftReader.ResetCacheForTesting();
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    // The fix: Invalidate() must NOT wipe the incremental log scan cache. The size-delta check in
    // ScanLogValues relies on _logScanCache.FileSizes to skip already-read log ranges — blowing it
    // away on every FS event forced a full multi-MB re-parse per refresh.
    [Fact]
    public void Invalidate_preserves_log_scan_cache()
    {
        var field = typeof(SynaraComposerDraftReader)
            .GetField("_logScanCache", BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(field);

        SynaraComposerDraftReader.ResetCacheForTesting();
        Assert.Null(field!.GetValue(null));

        var cacheType = typeof(SynaraComposerDraftReader)
            .GetNestedType("LogScanCache", BindingFlags.NonPublic | BindingFlags.Public);
        Assert.NotNull(cacheType);
        var instance = Activator.CreateInstance(cacheType!, nonPublic: true);
        Assert.NotNull(instance);
        field.SetValue(null, instance);

        SynaraComposerDraftReader.Invalidate();

        Assert.Same(instance, field.GetValue(null));
    }

    [Fact]
    public void Invalidate_clears_only_snapshot_state()
    {
        var snapshotField = typeof(SynaraComposerDraftReader)
            .GetField("_cachedSnapshot", BindingFlags.NonPublic | BindingFlags.Static);
        var forceRebuildField = typeof(SynaraComposerDraftReader)
            .GetField("_forceRebuild", BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(snapshotField);
        Assert.NotNull(forceRebuildField);

        SynaraComposerDraftReader.ResetCacheForTesting();
        // After reset, the snapshot slot is empty and _forceRebuild is false.
        Assert.Null(snapshotField!.GetValue(null));
        Assert.Equal(false, forceRebuildField!.GetValue(null));

        // After Invalidate (without any prior read), the snapshot must still be cleared AND
        // _forceRebuild must be true so the next read rebuilds instead of returning stale data.
        SynaraComposerDraftReader.Invalidate();
        Assert.Null(snapshotField.GetValue(null));
        Assert.Equal(true, forceRebuildField.GetValue(null));
    }

    // The previous behavior: Invalidate() wiped the log scan cache (the bug).
    // The new behavior: only ResetCacheForTesting() (used in tests) wipes it.
    [Fact]
    public void ResetCacheForTesting_wipes_log_scan_cache()
    {
        var field = typeof(SynaraComposerDraftReader)
            .GetField("_logScanCache", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(field);

        var cacheType = typeof(SynaraComposerDraftReader)
            .GetNestedType("LogScanCache", BindingFlags.NonPublic | BindingFlags.Public);
        var instance = Activator.CreateInstance(cacheType!, nonPublic: true);
        field.SetValue(null, instance);

        SynaraComposerDraftReader.ResetCacheForTesting();
        Assert.Null(field.GetValue(null));
    }

    [Fact]
    public void WarmSnapshot_handles_missing_dir_gracefully()
    {
        // No exception when the path does not exist.
        SynaraComposerDraftReader.ResetCacheForTesting();
        var bogus = Path.Combine(_tempDir, "does-not-exist");
        var ex = Record.Exception(() => SynaraComposerDraftReader.WarmSnapshot(bogus));
        Assert.Null(ex);
    }

    [Fact]
    public void WarmSnapshot_handles_empty_dir_gracefully()
    {
        // No exception when the dir exists but has no .log files.
        SynaraComposerDraftReader.ResetCacheForTesting();
        var ex = Record.Exception(() => SynaraComposerDraftReader.WarmSnapshot(_tempDir));
        Assert.Null(ex);
    }

    [Fact]
    public void WarmSnapshot_uses_the_provided_leveldb_directory()
    {
        var cachedDir = typeof(SynaraComposerDraftReader)
            .GetField("_cachedDir", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(cachedDir);

        SynaraComposerDraftReader.ResetCacheForTesting();
        SynaraComposerDraftReader.WarmSnapshot(_tempDir);

        Assert.Equal(_tempDir, cachedDir!.GetValue(null));
    }

    // The watcher debounce is now 1 ms (was 10 ms) — tight enough to collapse Chromium's
    // intra-batch bursts while staying well below the 25 ms poll interval.
    [Fact]
    public void SynaraStateWatcher_uses_tight_debounce()
    {
        var field = typeof(SynaraStateWatcher)
            .GetField("DebounceDelay", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(field);
        var debounce = (TimeSpan)field!.GetValue(null)!;
        Assert.True(debounce <= TimeSpan.FromMilliseconds(2),
            $"Debounce should be tight (≤2 ms) to collapse Chromium batched writes, was {debounce.TotalMilliseconds:0.##} ms");
    }

    // Synara writes composer state through a 300 ms debounce. The filesystem watcher remains immediate;
    // the steady poll should be a light fallback, not a constantly reentering hot loop.
    [Fact]
    public void UsageCoordinator_synara_poll_is_aligned_to_synara_debounce()
    {
        var field = typeof(UsageCoordinator)
            .GetField("SynaraPollInterval", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(field);
        var interval = (TimeSpan)field!.GetValue(null)!;
        Assert.InRange(interval.TotalMilliseconds, 75, 150);
    }

    [Fact]
    public void UsageCoordinator_has_synara_reentrancy_guard()
    {
        var field = typeof(UsageCoordinator)
            .GetField("_synaraSwitchHandling", BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.NotNull(field);
        Assert.Equal(typeof(int), field!.FieldType);
    }

    [Fact]
    public void UsageCoordinator_synara_stable_retry_is_configured()
    {
        var maxAttempts = typeof(UsageCoordinator)
            .GetField("SynaraStableMaxAttempts", BindingFlags.NonPublic | BindingFlags.Static);
        var delay = typeof(UsageCoordinator)
            .GetField("SynaraStableDelay", BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(maxAttempts);
        Assert.NotNull(delay);
        Assert.InRange((int)maxAttempts!.GetValue(null)!, 2, 8);
        Assert.InRange(((TimeSpan)delay!.GetValue(null)!).TotalMilliseconds, 2, 25);
    }
}
