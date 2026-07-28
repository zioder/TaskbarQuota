using System;
using System.Collections.Generic;
using System.Linq;
using TaskbarQuota.Diagnostics;
using TaskbarQuota.Usage;

namespace TaskbarQuota.Services;

internal enum QuotaReplenishmentKind
{
    AvailabilityIncrease,
    FullReplenishment,
    ConfirmedCycleRenewal,
}

internal readonly record struct QuotaWindowKey(ProviderId Provider, string WindowId);

internal sealed record QuotaWindowObservation(
    QuotaWindowKey Key,
    string Title,
    double UsedPercent,
    int? WindowMinutes,
    DateTimeOffset? ResetAt,
    long Sequence,
    DateTimeOffset ObservedAt)
{
    public double AvailablePercent => 100 - UsedPercent;
}

internal sealed record QuotaReplenishmentEvent(
    QuotaWindowObservation Previous,
    QuotaWindowObservation Current,
    QuotaReplenishmentKind Kind,
    bool IsCrossSession = false)
{
    public double Increase => Current.AvailablePercent - Previous.AvailablePercent;
}

internal static class QuotaReplenishmentEvaluator
{
    internal const double MinimumAvailabilityIncrease = 10;
    private const double FullAvailabilityTolerance = 0.0001;

    public static QuotaReplenishmentEvent? Evaluate(
        QuotaWindowObservation previous,
        QuotaWindowObservation current,
        DateTimeOffset now,
        double minimumIncrease = MinimumAvailabilityIncrease)
    {
        if (!AreComparable(previous, current)
            || !IsValid(previous.UsedPercent)
            || !IsValid(current.UsedPercent)
            || !double.IsFinite(minimumIncrease)
            || minimumIncrease <= 0)
        {
            return null;
        }

        var increase = current.AvailablePercent - previous.AvailablePercent;
        if (increase < minimumIncrease)
            return null;

        var kind = IsConfirmedCycleRenewal(previous, current, now)
            ? QuotaReplenishmentKind.ConfirmedCycleRenewal
            : current.AvailablePercent >= 100 - FullAvailabilityTolerance
                ? QuotaReplenishmentKind.FullReplenishment
                : QuotaReplenishmentKind.AvailabilityIncrease;

        return new QuotaReplenishmentEvent(previous, current, kind);
    }

    public static bool AreComparable(QuotaWindowObservation previous, QuotaWindowObservation current)
        => previous.Key == current.Key
        && previous.Sequence < current.Sequence
        && string.Equals(previous.Title, current.Title, StringComparison.Ordinal)
        && previous.WindowMinutes == current.WindowMinutes
        && previous.ResetAt.HasValue == current.ResetAt.HasValue;

    public static bool IsValid(double usedPercent)
        => double.IsFinite(usedPercent) && usedPercent is >= 0 and <= 100;

    public static string Reason(QuotaReplenishmentKind kind) => kind switch
    {
        QuotaReplenishmentKind.ConfirmedCycleRenewal => "cycle-renewal",
        QuotaReplenishmentKind.FullReplenishment => "full-replenishment",
        _ => "availability-increase",
    };

    private static bool IsConfirmedCycleRenewal(
        QuotaWindowObservation previous,
        QuotaWindowObservation current,
        DateTimeOffset now)
        => previous.ResetAt is { } previousReset
        && current.ResetAt is { } currentReset
        && previousReset <= now
        && currentReset > now
        && currentReset > previousReset;
}

internal sealed class QuotaReplenishmentTracker
{
    private readonly Dictionary<QuotaWindowKey, WindowState> _windows = new();
    private readonly Dictionary<ProviderId, long> _lastSequences = new();
    private readonly Dictionary<ProviderId, string> _providerIdentities = new();

    public IReadOnlyList<QuotaReplenishmentEvent> Observe(UsageResult result, DateTimeOffset now)
    {
        if (result.ObservationOrigin != UsageObservationOrigin.Live)
        {
            if (result.ObservationOrigin == UsageObservationOrigin.FailureFallback)
            {
                ResetProvider(result.Id, "failure-fallback");
            }
            else if (!result.Ok && !result.IsPending)
            {
                ResetProvider(result.Id, "failure");
            }

            return Array.Empty<QuotaReplenishmentEvent>();
        }

        if (result.Fetch?.Usage is not { } usage || result.ObservedAt is not { } observedAt)
        {
            ClearProvider(result.Id);
            return Array.Empty<QuotaReplenishmentEvent>();
        }

        if (_lastSequences.TryGetValue(result.Id, out var lastSequence)
            && result.ObservationSequence <= lastSequence)
        {
            return Array.Empty<QuotaReplenishmentEvent>();
        }

        var identity = NormalizeIdentity(usage.Email);
        if (identity is not null
            && _providerIdentities.TryGetValue(result.Id, out var previousIdentity)
            && !string.Equals(previousIdentity, identity, StringComparison.OrdinalIgnoreCase))
        {
            ResetProvider(result.Id, "identity-changed");
        }

        if (identity is not null)
            _providerIdentities[result.Id] = identity;

        _lastSequences[result.Id] = result.ObservationSequence;
        var currentWindows = QuotaAlertWindowCatalog.Enumerate(result).ToArray();
        var currentKeys = currentWindows.Select(window => new QuotaWindowKey(result.Id, window.Id)).ToHashSet();

        foreach (var missing in _windows.Keys
                     .Where(key => key.Provider == result.Id && !currentKeys.Contains(key))
                     .ToArray())
        {
            _windows.Remove(missing);
        }

        var events = new List<QuotaReplenishmentEvent>();
        foreach (var window in currentWindows)
        {
            var key = new QuotaWindowKey(result.Id, window.Id);
            var current = new QuotaWindowObservation(
                key,
                window.Title,
                window.Window.UsedPercent,
                window.Window.WindowMinutes,
                window.Window.ResetAt,
                result.ObservationSequence,
                observedAt);

            if (!QuotaReplenishmentEvaluator.IsValid(current.UsedPercent))
            {
                _windows.Remove(key);
                continue;
            }

            if (!_windows.TryGetValue(key, out var state))
            {
                _windows[key] = WindowState.From(current);
                LogBaseline(current, "first-live");
                continue;
            }

            if (!QuotaReplenishmentEvaluator.AreComparable(state.LowWater, current))
            {
                _windows[key] = WindowState.From(current);
                LogBaseline(current, "window-changed");
                continue;
            }

            if (state.IsLatched
                && current.AvailablePercent <= state.LastNotifiedAvailable - QuotaReplenishmentEvaluator.MinimumAvailabilityIncrease)
            {
                state.IsLatched = false;
                state.LowWater = current;
            }

            if (!state.IsLatched)
            {
                var replenishment = QuotaReplenishmentEvaluator.Evaluate(state.LowWater, current, now);
                if (replenishment is not null)
                {
                    events.Add(replenishment);
                    state.IsLatched = true;
                    state.LastNotifiedAvailable = current.AvailablePercent;
                    state.LowWater = current;
                    Log.Information(
                        $"[quota-replenishment] detected provider={result.Id} window={window.Id} " +
                        $"from={FormatPercent(replenishment.Previous.AvailablePercent)} " +
                        $"to={FormatPercent(current.AvailablePercent)} " +
                        $"reason={QuotaReplenishmentEvaluator.Reason(replenishment.Kind)}");
                    continue;
                }
            }

            if (current.AvailablePercent < state.LowWater.AvailablePercent)
            {
                state.LowWater = current;
            }
            else if (current.ResetAt != state.LowWater.ResetAt)
            {
                // Preserve the low-water percentage so small increases still accumulate, but move the
                // cycle marker forward. A later increase is then described neutrally rather than being
                // mislabelled as a renewal that was already observed without an availability gain.
                state.LowWater = state.LowWater with
                {
                    ResetAt = current.ResetAt,
                    Sequence = current.Sequence,
                    ObservedAt = current.ObservedAt,
                };
            }
        }

        return events;
    }

    public void Reset(string reason)
    {
        _windows.Clear();
        _lastSequences.Clear();
        _providerIdentities.Clear();
    }

    private void ResetProvider(ProviderId provider, string reason)
    {
        foreach (var entry in _windows
                     .Where(entry => entry.Key.Provider == provider)
                     .ToArray())
        {
            _windows.Remove(entry.Key);
            Log.Debug(
                $"[quota-replenishment] baseline invalidated provider={provider} " +
                $"window={entry.Key.WindowId} available={FormatPercent(entry.Value.LowWater.AvailablePercent)} " +
                $"reason={reason}");
        }

        _lastSequences.Remove(provider);
        _providerIdentities.Remove(provider);
    }

    private void ClearProvider(ProviderId provider)
    {
        foreach (var key in _windows.Keys.Where(key => key.Provider == provider).ToArray())
            _windows.Remove(key);

        _lastSequences.Remove(provider);
        _providerIdentities.Remove(provider);
    }

    private static string? NormalizeIdentity(string? identity)
        => string.IsNullOrWhiteSpace(identity) ? null : identity.Trim();

    private static void LogBaseline(QuotaWindowObservation observation, string reason)
        => Log.Debug(
            $"[quota-replenishment] baseline provider={observation.Key.Provider} " +
            $"window={observation.Key.WindowId} available={FormatPercent(observation.AvailablePercent)} " +
            $"reason={reason}");

    private static string FormatPercent(double value)
        => value.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);

    private sealed class WindowState
    {
        public required QuotaWindowObservation LowWater { get; set; }
        public bool IsLatched { get; set; }
        public double LastNotifiedAvailable { get; set; }

        public static WindowState From(QuotaWindowObservation observation)
            => new() { LowWater = observation };
    }
}

internal static class QuotaAlertWindowCatalog
{
    public static IEnumerable<QuotaAlertWindow> Enumerate(UsageResult result)
    {
        if (result.Fetch?.Usage is not { } usage)
            yield break;

        if (usage.HasPrimaryWindow)
            yield return new QuotaAlertWindow("primary", PrimaryTitle(result, usage.Primary), usage.Primary);

        if (usage.Secondary is { } secondary)
            yield return new QuotaAlertWindow("secondary", SecondaryTitle(result, secondary), secondary);

        if (usage.ModelSpecific is { } model)
            yield return new QuotaAlertWindow("model", ModelTitle(result.Id, model), model);

        if (usage.Monthly is { } monthly)
            yield return new QuotaAlertWindow("monthly", MonthlyTitle(result.Id, monthly), monthly);

        var duplicateExtraIds = usage.ExtraRateWindows
            .Where(extra => !string.IsNullOrWhiteSpace(extra.Id))
            .GroupBy(extra => extra.Id, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var extra in usage.ExtraRateWindows)
        {
            if (string.IsNullOrWhiteSpace(extra.Id) || duplicateExtraIds.Contains(extra.Id))
                continue;

            yield return new QuotaAlertWindow($"extra:{extra.Id}", extra.Title, extra.Window);
        }
    }

    private static string PrimaryTitle(UsageResult result, RateWindow window)
        => result.Id == ProviderId.Antigravity
            ? "Gemini Weekly"
            : window.Label ?? result.Provider?.SessionLabel ?? "Session";

    private static string SecondaryTitle(UsageResult result, RateWindow window)
        => result.Id == ProviderId.Antigravity
            ? "Non-Gemini Weekly"
            : window.Label ?? result.Provider?.WeeklyLabel ?? "Weekly";

    private static string ModelTitle(ProviderId provider, RateWindow window)
        => window.Label ?? (provider switch
        {
            ProviderId.Antigravity => "Gemini 5h",
            ProviderId.Cursor => "API Usage",
            ProviderId.Copilot => "Completions",
            _ => "Model",
        });

    private static string MonthlyTitle(ProviderId provider, RateWindow window)
        => window.Label ?? (provider == ProviderId.Antigravity ? "Non-Gemini 5h" : "Monthly");
}
