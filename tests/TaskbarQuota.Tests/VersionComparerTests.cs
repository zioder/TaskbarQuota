using TaskbarQuota.Services;

namespace TaskbarQuota.Tests;

public sealed class VersionComparerTests
{
    [Theory]
    [InlineData("1.0.0", "1.0.0", 0)]
    [InlineData("v1.0.0", "1.0.0", 0)]
    [InlineData("1.0.1", "1.0.0", 1)]
    [InlineData("1.0.0", "1.0.1", -1)]
    [InlineData("2.0.0", "1.9.9", 1)]
    public void Compare_versions(string left, string right, int expected)
    {
        var actual = VersionComparer.Compare(
            VersionComparer.Normalize(left),
            VersionComparer.Normalize(right));
        Assert.Equal(expected, actual);
    }
}
