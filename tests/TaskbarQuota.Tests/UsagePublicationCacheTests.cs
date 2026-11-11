using TaskbarQuota.Usage;
using Xunit;

namespace TaskbarQuota.Tests;

public class UsagePublicationCacheTests
{
    [Fact]
    public void IdenticalSnapshotsAreSuppressedUntilForce()
    {
        var cache = new UsagePublicationCache();
        var result = UsageResult.Failure(ProviderId.Codex, "test");

        Assert.True(cache.ShouldPublish(result, force: false));
        cache.RecordPublished(result);
        Assert.False(cache.ShouldPublish(result, force: false));
        Assert.True(cache.ShouldPublish(result, force: true));
    }
}
