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

    [Fact]
    public void Select_unsigned_installer_prefers_matching_arch()
    {
        var actual = UpdateCheckerService.SelectUnsignedInstallerName(
        [
            "TaskbarQuotaSetup-1.0.6-x64.exe",
            "TaskbarQuotaSetup-1.0.6-arm64-unsigned.exe",
            "TaskbarQuotaSetup-1.0.6-x64-unsigned.exe",
        ],
            "arm64");

        Assert.Equal("TaskbarQuotaSetup-1.0.6-arm64-unsigned.exe", actual);
    }

    [Fact]
    public void Select_unsigned_installer_falls_back_to_unsigned_x64()
    {
        var actual = UpdateCheckerService.SelectUnsignedInstallerName(
        [
            "TaskbarQuotaSetup-1.0.6-arm64.exe",
            "TaskbarQuotaSetup-1.0.6-x64-unsigned.exe",
        ],
            "arm64");

        Assert.Equal("TaskbarQuotaSetup-1.0.6-x64-unsigned.exe", actual);
    }
}
