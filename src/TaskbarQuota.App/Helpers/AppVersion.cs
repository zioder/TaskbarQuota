using System;
using System.Reflection;

namespace TaskbarQuota.Helpers;

/// <summary>App version for unpackaged installs (assembly informational version).</summary>
internal static class AppVersion
{
    public static string GetDisplayLabel()
    {
        var informational = GetInformationalVersionLabel();
        if (!string.IsNullOrWhiteSpace(informational))
            return informational;

        var version = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0);
        return $"{version.Major}.{version.Minor}.{version.Build}";
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
