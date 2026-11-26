using System;
using System.Globalization;

namespace TaskbarQuota.Usage;

internal static class QuotaResetDisplay
{
    internal static string FormatLocalDateTime(DateTimeOffset timestamp)
        => timestamp.ToLocalTime().ToString("MMM d, yyyy HH:mm", CultureInfo.CurrentCulture);

    internal static string WindowCaption(ProviderId provider, RateWindow window)
    {
        // Codex can advertise a full-period reset before the quota window has started.
        if (!window.IsIncluded || (provider == ProviderId.Codex && window.UsedPercent == 0
            && string.IsNullOrWhiteSpace(window.ResetDescription)))
            return string.Empty;

        if (window.ResetAt is { } reset)
            return FormatLocalDateTime(reset);

        string? description = window.ResetDescription;
        if (string.IsNullOrWhiteSpace(description))
            return string.Empty;
        return description == "now" ? "resets now" : $"resets in {description}";
    }
}
