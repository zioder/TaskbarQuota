using System;

namespace TaskbarQuota.Usage;

public enum PaceSeverity { Untracked, Healthy, Close, RunningOut, Spent }

public sealed record PaceProjection(
    PaceSeverity Severity,
    double? ProjectedUsedPercent,
    double? IdealPacePercent,
    DateTimeOffset? RunOutAt,
    double? AheadBehindPercent)
{
    public bool HasForecast => ProjectedUsedPercent is not null;
}

public static class QuotaPacing
{
    public static PaceProjection Evaluate(RateWindow window, DateTimeOffset now, bool isSessionWindow = false)
    {
        var used = Math.Clamp(window.UsedPercent, 0, 100);
        if (used >= 100)
            return new(PaceSeverity.Spent, 100, null, now, null);
        if (window.ResetAt is not { } reset || window.WindowMinutes is not > 0 || reset <= now)
            return Untracked();

        var period = TimeSpan.FromMinutes(window.WindowMinutes.Value);
        if (isSessionWindow && used <= 0 && reset - now >= period - TimeSpan.FromSeconds(1))
            return Untracked();
        var start = reset - period;
        var elapsed = now - start;
        if (elapsed < TimeSpan.FromSeconds(Math.Max(60, period.TotalSeconds * .01)))
            return Untracked();

        var progress = Math.Clamp(elapsed.TotalSeconds / period.TotalSeconds, 0, 1);
        if (progress <= 0) return Untracked();
        var projected = used / progress;
        var ideal = progress * 100;
        var delta = (double?)((used / ideal - 1) * 100);
        if (projected <= 90)
            return new(PaceSeverity.Healthy, projected, ideal, null, delta);
        if (used < 5)
            return Untracked();
        if (projected <= 100)
            return new(PaceSeverity.Close, projected, ideal, null, delta);

        DateTimeOffset? runOut = start + TimeSpan.FromSeconds(elapsed.TotalSeconds * 100 / used);
        if (runOut <= now || runOut >= reset) runOut = null;
        return new(PaceSeverity.RunningOut, projected, ideal, runOut, delta);
    }

    public static string? Tooltip(PaceProjection projection, DateTimeOffset now)
    {
        if (projection.Severity == PaceSeverity.Spent)
            return "Limit reached";
        if (!projection.HasForecast)
            return null;
        return projection.Severity switch
        {
            PaceSeverity.Healthy when projection.ProjectedUsedPercent is { } p
                => $"~{Math.Max(0, Math.Round(100 - p)):0}% left at reset" + FormatDelta(projection),
            PaceSeverity.Close when projection.ProjectedUsedPercent is { } p
                => $"~{Math.Round(p):0}% used at reset ({FormatSpare(p)})",
            PaceSeverity.RunningOut when projection.RunOutAt is { } at
                => $"At your current rate, runs out {FormatRelative(at, now)}, {FormatAbsolute(at, now)} (~{Math.Round(projection.ProjectedUsedPercent ?? 100):0}% used by reset). A forecast, not a certainty.",
            PaceSeverity.RunningOut
                => $"At your current rate, runs out before reset (~{Math.Round(projection.ProjectedUsedPercent ?? 100):0}% used by reset). A forecast, not a certainty.",
            _ => null,
        };
    }

    /// <summary>Short one-line status for cards, e.g. "On pace", "~3% spare", "Runs out today at 3:23 AM". The
    /// longer projection lives in <see cref="Tooltip"/>.</summary>
    public static string? CardText(PaceProjection projection, DateTimeOffset now)
    {
        if (projection.Severity == PaceSeverity.Spent)
            return "Limit reached";
        if (!projection.HasForecast)
            return null;
        return projection.Severity switch
        {
            PaceSeverity.Healthy => "On pace",
            PaceSeverity.Close when projection.ProjectedUsedPercent is { } p
                => FormatSpare(p),
            PaceSeverity.RunningOut when projection.RunOutAt is { } at
                => $"At this rate, runs out {FormatRelative(at, now)}",
            PaceSeverity.RunningOut => "At this rate, runs out before reset",
            _ => null,
        };
    }

    public static string? WarningText(PaceProjection projection, DateTimeOffset now)
    {
        if (projection.Severity == PaceSeverity.Spent)
            return "Limit reached";
        if (projection.Severity == PaceSeverity.RunningOut && projection.RunOutAt is { } at)
            return $"Limit {FormatRelative(at, now)}";
        if (projection.Severity == PaceSeverity.Close && projection.ProjectedUsedPercent is { } p)
            return FormatSpare(p);
        return null;
    }

    private static string FormatSpare(double projectedUsedPercent)
    {
        var spare = 100 - projectedUsedPercent;
        if (spare <= 0)
            return "0% spare";
        if (spare < 1)
            return "<1% spare";
        return $"~{Math.Round(spare):0}% spare";
    }

    private static PaceProjection Untracked() => new(PaceSeverity.Untracked, null, null, null, null);
    private static string FormatDelta(PaceProjection p)
        => p.AheadBehindPercent is { } d && Math.Abs(d) >= 1
            ? $" · {Math.Abs(d):0}% {(d < 0 ? "ahead" : "behind")} ideal pace" : string.Empty;
    private static string FormatAbsolute(DateTimeOffset target, DateTimeOffset now)
    {
        var local = target.ToLocalTime();
        var current = now.ToLocalTime();
        if (local.Date == current.Date) return $"today at {local:h:mm tt}";
        if (local.Date == current.Date.AddDays(1)) return $"tomorrow at {local:h:mm tt}";
        return $"{local:MMM d} at {local:h:mm tt}";
    }

    private static string FormatRelative(DateTimeOffset target, DateTimeOffset now)
    {
        var diff = target - now;
        if (diff <= TimeSpan.Zero) return "soon";
        if (diff.TotalDays >= 1) return $"in {(int)diff.TotalDays}d {diff.Hours}h";
        if (diff.TotalHours >= 1) return $"in {(int)diff.TotalHours}h {diff.Minutes}m";
        return $"in {Math.Max(1, diff.Minutes)}m";
    }
}

/// <summary>Window lengths providers pass to <see cref="RateWindow"/> so pace can be forecast.</summary>
public static class QuotaWindowLength
{
    public const int FiveHours = 300;
    public const int Daily = 1440;
    public const int Weekly = 10080;

    /// <summary>Length of the billing month that ends at <paramref name="resetAt"/> (one calendar month back),
    /// or null when no reset time is known. Months vary, so this is the real cycle, not a flat 30 days.</summary>
    public static int? Monthly(DateTimeOffset? resetAt)
        => resetAt is { } reset ? (int)Math.Round((reset - reset.AddMonths(-1)).TotalMinutes) : null;
}
