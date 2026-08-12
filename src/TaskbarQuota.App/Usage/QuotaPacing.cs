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
        if (Math.Round(100 - used) <= 0)
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
        if (!projection.HasForecast)
            return projection.Severity == PaceSeverity.Spent ? "Limit reached" : null;
        return projection.Severity switch
        {
            PaceSeverity.Healthy when projection.ProjectedUsedPercent is { } p
                => $"~{Math.Max(0, Math.Round(100 - p)):0}% left at reset" + FormatDelta(projection),
            PaceSeverity.Close when projection.ProjectedUsedPercent is { } p
                => $"~{Math.Round(p):0}% used at reset ({Math.Max(1, Math.Round(100 - p)):0}% spare)",
            PaceSeverity.RunningOut when projection.RunOutAt is { } at
                => $"Runs out {FormatAbsolute(at, now)}; ~{Math.Round(projection.ProjectedUsedPercent ?? 100):0}% used at reset",
            PaceSeverity.RunningOut
                => $"Projected to run out before reset (~{Math.Round(projection.ProjectedUsedPercent ?? 100):0}% used at reset)",
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
            return $"~{Math.Max(1, Math.Round(100 - p)):0}% spare";
        return null;
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
