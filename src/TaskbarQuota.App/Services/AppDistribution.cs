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

    public static AppDistributionChannel CurrentChannel => IsMicrosoftStorePackage()
        ? AppDistributionChannel.MicrosoftStore
        : AppDistributionChannel.UnsignedGitHub;

    private static bool IsMicrosoftStorePackage()
    {
        try
        {
            return string.Equals(
                Package.Current.Id.FamilyName,
                StorePackageFamilyName,
                StringComparison.OrdinalIgnoreCase);
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}
