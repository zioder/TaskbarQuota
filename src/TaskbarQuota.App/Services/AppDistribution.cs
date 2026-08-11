using System;
using Windows.ApplicationModel;

namespace TaskbarQuota.Services;

internal enum AppDistributionChannel
{
    UnsignedGitHub,
    MicrosoftStore,
}

internal static class AppDistribution
{
    public const string StorePackageFamilyName = "ZiedKallel.TaskbarQuota_q2e4dm2bjnsne";

    public static AppDistributionChannel CurrentChannel
    {
        get
        {
            try
            {
                return DetectChannel(Package.Current.Id.FamilyName);
            }
            catch (InvalidOperationException)
            {
                // Unpackaged GitHub installers have no Package identity.
                return AppDistributionChannel.UnsignedGitHub;
            }
        }
    }

    internal static AppDistributionChannel DetectChannel(string? packageFamilyName) =>
        string.Equals(packageFamilyName, StorePackageFamilyName, StringComparison.OrdinalIgnoreCase)
            ? AppDistributionChannel.MicrosoftStore
            : AppDistributionChannel.UnsignedGitHub;
}
