using System;
using System.Reflection;
using Windows.ApplicationModel;

namespace TaskbarQuota.Helpers;

/// <summary>App version for unpackaged installs (assembly informational version).</summary>
internal static class AppVersion
{
    public static string GetDisplayLabel()
    {
        if (TryGetPackageVersion() is { } packageVersion)
            return packageVersion;

        var informational = GetInformationalVersionLabel();
        if (!string.IsNullOrWhiteSpace(informational))
            return informational;

        var version = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0);
        return $"{version.Major}.{version.Minor}.{version.Build}";
    }

    private static string? TryGetPackageVersion()
    {
        try
        {
            var version = Package.Current.Id.Version;
            return version.Major == 0 && version.Minor == 0 && version.Build == 0 && version.Revision == 0
                ? null
                : $"{version.Major}.{version.Minor}.{version.Build}";
        }
        catch (InvalidOperationException)
        {
            // Unpackaged GitHub installs do not have a Package identity.
            return null;
        }
    }

    private static string? GetInformationalVersionLabel()
    {
        var raw = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;

        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var trimmed = raw.Trim();
        var plus = trimmed.IndexOf('+');
        return plus >= 0 ? trimmed[..plus] : trimmed;
    }
}
