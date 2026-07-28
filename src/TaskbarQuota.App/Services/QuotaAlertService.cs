using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using TaskbarQuota.Diagnostics;
using TaskbarQuota.Usage;

namespace TaskbarQuota.Services;

public sealed class QuotaAlertService
{
    public static QuotaAlertService Instance { get; } = new(new AppNotificationQuotaAlertNotifier());

    private readonly IQuotaAlertNotifier _notifier;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Func<QuotaAlertSettings> _settingsProvider;
    private readonly QuotaReplenishmentTracker _replenishmentTracker;
    private readonly QuotaReplenishmentCrossSessionTracker _crossSessionTracker;
    private readonly object _lock = new();
    private QuotaAlertState _state;
    private bool _lastReplenishmentEnabled;
    private bool _lastCrossSessionReplenishmentEnabled;
    private bool _started;

    internal QuotaAlertService(
        IQuotaAlertNotifier notifier,
        Func<DateTimeOffset>? clock = null,
        Func<QuotaAlertSettings>? settingsProvider = null,
        QuotaReplenishmentTracker? replenishmentTracker = null,
        QuotaReplenishmentCrossSessionTracker? crossSessionTracker = null,
        QuotaAlertState? state = null)
    {
        _notifier = notifier;
        _clock = clock ?? (() => DateTimeOffset.Now);
        _settingsProvider = settingsProvider ?? (() => QuotaAlertSettingsService.Current);
        _replenishmentTracker = replenishmentTracker ?? new QuotaReplenishmentTracker();
        _crossSessionTracker = crossSessionTracker ?? new QuotaReplenishmentCrossSessionTracker(
            new QuotaReplenishmentStateStore(QuotaReplenishmentStateStore.DefaultPath));
        _state = state ?? QuotaAlertState.Load();
        var settings = _settingsProvider();
        _lastReplenishmentEnabled = settings.ReplenishmentEnabled;
        _lastCrossSessionReplenishmentEnabled = settings.CrossSessionReplenishmentEnabled;
    }

    public void Start()
    {
        lock (_lock)
        {
            if (_started)
                return;

            UsageCoordinator.Instance.StateChanged += OnStateChanged;
            QuotaAlertSettingsService.Changed += OnSettingsChanged;
            App.Quitting += Stop;
            _started = true;
        }

        _notifier.Register();
    }

    public void Stop()
    {
        lock (_lock)
        {
            if (!_started)
                return;

            UsageCoordinator.Instance.StateChanged -= OnStateChanged;
            QuotaAlertSettingsService.Changed -= OnSettingsChanged;
            App.Quitting -= Stop;
            _started = false;
        }
    }

    internal void OnStateChanged(UsageResult result)
    {
        var settings = _settingsProvider();
        if (!settings.Enabled && !settings.ReplenishmentEnabled)
            return;

        IReadOnlyList<QuotaReplenishmentEvent> replenishments;
        List<QuotaAlertNotification> thresholdNotifications;
        var now = _clock();
        lock (_lock)
        {
            if (settings.ReplenishmentEnabled)
            {
                var currentSession = _replenishmentTracker.Observe(result, now);
                var crossSession = settings.CrossSessionReplenishmentEnabled
                    ? _crossSessionTracker.Observe(result, now)
                    : Array.Empty<QuotaReplenishmentEvent>();
                replenishments = crossSession.Count == 0
                    ? currentSession
                    : crossSession.Concat(currentSession).ToArray();
            }
            else
            {
                replenishments = Array.Empty<QuotaReplenishmentEvent>();
            }

            var replenishedWindowIds = replenishments
                .Select(replenishment => replenishment.Current.Key.WindowId)
                .ToHashSet(StringComparer.Ordinal);

            thresholdNotifications = QuotaAlertEvaluator.Evaluate(
                result,
                settings,
                _state,
                now,
                replenishedWindowIds).ToList();

            if (_state.HasUnsavedChanges)
                _state.Save();
        }

        if (replenishments.Count > 0)
        {
            var notification = QuotaAlertNotification.FromReplenishments(result.DisplayName, replenishments);
            if (_notifier.Show(notification))
            {
                var transitions = string.Join(",", replenishments.Select(item =>
                    $"{item.Current.Key.WindowId}:{LogPercent(item.Previous.AvailablePercent)}" +
                    $"->{LogPercent(item.Current.AvailablePercent)}"));
                Log.Information(
                    $"[quota-replenishment] notification shown provider={result.Id} " +
                    $"transitions={transitions} reason=" +
                    $"{(replenishments.Any(item => item.IsCrossSession) ? "delivered-since-last-session" : "delivered")}");
            }
        }

        foreach (var notification in thresholdNotifications)
            _notifier.Show(notification);
    }

    internal void OnSettingsChanged(object? sender, EventArgs args)
    {
        var settings = _settingsProvider();
        var replenishmentEnabled = settings.ReplenishmentEnabled;
        var crossSessionEnabled = settings.CrossSessionReplenishmentEnabled;
        lock (_lock)
        {
            var replenishmentChanged = replenishmentEnabled != _lastReplenishmentEnabled;
            var crossSessionChanged = crossSessionEnabled != _lastCrossSessionReplenishmentEnabled;

            _lastReplenishmentEnabled = replenishmentEnabled;
            _lastCrossSessionReplenishmentEnabled = crossSessionEnabled;

            if (replenishmentChanged)
                _replenishmentTracker.Reset("setting-changed");

            if (replenishmentChanged || crossSessionChanged)
                _crossSessionTracker.Reset(clearPersisted: true);
        }
    }

    private static string LogPercent(double value)
        => value.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
}

internal interface IQuotaAlertNotifier
{
    void Register();
    bool Show(QuotaAlertNotification notification);
}

internal sealed class AppNotificationQuotaAlertNotifier : IQuotaAlertNotifier
{
    private int _registered;

    public void Register()
    {
        if (Interlocked.Exchange(ref _registered, 1) == 1)
            return;

        try
        {
            AppNotificationManager.Default.Register();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to register app notifications");
        }
    }

    public bool Show(QuotaAlertNotification notification)
    {
        try
        {
            Register();
            var appNotification = new AppNotificationBuilder()
                .AddText(notification.Title)
                .AddText(notification.Body)
                .BuildNotification();

            AppNotificationManager.Default.Show(appNotification);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to show quota alert notification");
            return false;
        }
    }
}

internal static class QuotaAlertEvaluator
{
    private static readonly TimeSpan ResetCreditExpiryWarningWindow = TimeSpan.FromDays(5);
    private static readonly TimeSpan ResetCreditRepeatCooldown = TimeSpan.FromDays(3650);

    public static IEnumerable<QuotaAlertNotification> Evaluate(
        UsageResult result,
        QuotaAlertSettings settings,
        QuotaAlertState state,
        DateTimeOffset now,
        IReadOnlySet<string>? suppressedWindowIds = null)
    {
        if (!settings.Enabled || !result.Ok || result.Fetch?.Usage is not { } usage)
            yield break;

        foreach (var window in EnumerateWindows(usage))
        {
            var thresholds = OrderedThresholds(settings).ToArray();
            foreach (var threshold in thresholds.Where(t => window.Window.UsedPercent < t.Value))
                state.Clear(QuotaAlertStateKey.For(result.Id, window.Id, threshold.Value, window.Window));

            var crossed = thresholds.FirstOrDefault(t => window.Window.UsedPercent >= t.Value);
            if (crossed == default)
                continue;

            var key = QuotaAlertStateKey.For(result.Id, window.Id, crossed.Value, window.Window);
            if (suppressedWindowIds?.Contains(window.Id) == true)
            {
                state.MarkAlerted(key, now);
                continue;
            }

            if (!state.ShouldAlert(key, now, TimeSpan.FromMinutes(settings.CooldownMinutes)))
                continue;

            state.MarkAlerted(key, now);
            yield return QuotaAlertNotification.From(result.DisplayName, window, crossed);
        }

        if (result.Id == ProviderId.Codex
            && usage.ResetCredits?.EarliestExpiresAt is { } oldestExpiry
            && oldestExpiry > now
            && oldestExpiry - now <= ResetCreditExpiryWarningWindow)
        {
            var key = QuotaAlertStateKey.ForResetCreditExpiry(result.Id, oldestExpiry);
            if (state.ShouldAlert(key, now, ResetCreditRepeatCooldown))
            {
                state.MarkAlerted(key, now);
                yield return QuotaAlertNotification.FromResetCreditExpiry(result.DisplayName, oldestExpiry, now);
            }
        }
    }

    private static IEnumerable<QuotaAlertWindow> EnumerateWindows(UsageSnapshot usage)
    {
        if (usage.HasPrimaryWindow)
            yield return new QuotaAlertWindow("primary", "Session", usage.Primary);

        if (usage.Secondary is { } secondary)
            yield return new QuotaAlertWindow("secondary", "Weekly", secondary);

        if (usage.ModelSpecific is { } model)
            yield return new QuotaAlertWindow("model", "Model", model);

        if (usage.Monthly is { } monthly)
            yield return new QuotaAlertWindow("monthly", "Monthly", monthly);

        foreach (var extra in usage.ExtraRateWindows)
            yield return new QuotaAlertWindow($"extra:{extra.Id}", extra.Title, extra.Window);
    }

    private static IEnumerable<QuotaAlertThreshold> OrderedThresholds(QuotaAlertSettings settings)
    {
        yield return new QuotaAlertThreshold("critical", settings.CriticalThreshold);
        yield return new QuotaAlertThreshold("warning", settings.WarningThreshold);
    }
}

internal sealed class QuotaAlertState
{
    private static readonly string StatePath =
        Path.Combine(AppStorage.AppDataDirectory, "quota-alert-state.json");

    public Dictionary<string, DateTimeOffset> LastAlertedAt { get; init; } = new();
    internal bool HasUnsavedChanges { get; private set; }
    internal void ResetUnsavedChangesForTesting() => HasUnsavedChanges = false;

    public static QuotaAlertState Load()
    {
        try
        {
            if (!File.Exists(StatePath))
                return new QuotaAlertState();

            return JsonSerializer.Deserialize<QuotaAlertState>(File.ReadAllText(StatePath)) ?? new QuotaAlertState();
        }
        catch
        {
            return new QuotaAlertState();
        }
    }

    public bool ShouldAlert(string key, DateTimeOffset now, TimeSpan cooldown)
        => !LastAlertedAt.TryGetValue(key, out var previous)
        || now - previous >= cooldown;

    public void MarkAlerted(string key, DateTimeOffset now)
    {
        LastAlertedAt[key] = now;
        HasUnsavedChanges = true;
    }

    public void Clear(string key)
    {
        if (LastAlertedAt.Remove(key))
            HasUnsavedChanges = true;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
            File.WriteAllText(StatePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            HasUnsavedChanges = false;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to write quota alert state");
        }
    }
}

internal readonly record struct QuotaAlertWindow(string Id, string Title, RateWindow Window);

internal readonly record struct QuotaAlertThreshold(string Severity, double Value);

internal sealed record QuotaAlertNotification(string Title, string Body)
{
    public static QuotaAlertNotification FromReplenishments(
        string providerName,
        IReadOnlyList<QuotaReplenishmentEvent> replenishments)
    {
        if (replenishments.Count == 1)
        {
            var replenishment = replenishments[0];
            var window = replenishment.Current.Title.ToLowerInvariant();
            var verb = replenishment.Kind switch
            {
                QuotaReplenishmentKind.ConfirmedCycleRenewal => "renewed",
                QuotaReplenishmentKind.FullReplenishment => "replenished",
                _ => "increased",
            };
            var body = replenishment.Kind == QuotaReplenishmentKind.FullReplenishment
                ? "Available quota is now 100%."
                : $"Available quota increased from {FormatPercent(replenishment.Previous.AvailablePercent)}% " +
                  $"to {FormatPercent(replenishment.Current.AvailablePercent)}%.";

            return new QuotaAlertNotification(
                $"{providerName} {window} quota {verb}",
                body);
        }

        var lines = replenishments
            .Take(3)
            .Select(item =>
                $"{item.Current.Title}: {FormatPercent(item.Previous.AvailablePercent)}% " +
                $"→ {FormatPercent(item.Current.AvailablePercent)}% available.")
            .ToList();
        if (replenishments.Count > 3)
            lines.Add($"And {replenishments.Count - 3} more windows.");

        return new QuotaAlertNotification(
            $"{providerName} quotas replenished",
            string.Join(Environment.NewLine, lines));
    }

    public static QuotaAlertNotification From(
        string providerName,
        QuotaAlertWindow window,
        QuotaAlertThreshold threshold)
    {
        var used = window.Window.UsedPercent;
        var reset = string.IsNullOrWhiteSpace(window.Window.ResetDescription)
            ? string.Empty
            : $" Resets in {window.Window.ResetDescription}.";

        return new QuotaAlertNotification(
            $"{providerName} {window.Title.ToLowerInvariant()} quota is at {used:0}%",
            $"{threshold.Severity.ToUpperInvariant()} threshold crossed ({threshold.Value:0}%).{reset}");
    }

    public static QuotaAlertNotification FromResetCreditExpiry(
        string providerName,
        DateTimeOffset expiresAt,
        DateTimeOffset now)
    {
        var local = expiresAt.ToLocalTime();
        return new QuotaAlertNotification(
            $"{providerName} reset credit expires soon",
            $"Oldest reset credit expires in {FormatTimeUntil(expiresAt, now)} ({local:MMM d 'at' h:mm tt}). Use it before it expires.");
    }

    private static string FormatTimeUntil(DateTimeOffset target, DateTimeOffset now)
    {
        var diff = target - now;
        if (diff <= TimeSpan.Zero)
            return "now";

        int hours = (int)diff.TotalHours;
        int minutes = diff.Minutes;
        if (hours >= 24)
        {
            int days = hours / 24;
            int remHours = hours % 24;
            return remHours == 0 ? $"{days}d" : $"{days}d {remHours}h";
        }

        if (hours > 0)
            return minutes == 0 ? $"{hours}h" : $"{hours}h {minutes}m";

        return $"{minutes}m";
    }

    private static string FormatPercent(double value)
        => value.ToString("0", System.Globalization.CultureInfo.InvariantCulture);
}

internal static class QuotaAlertStateKey
{
    public static string For(ProviderId provider, string windowId, double threshold, RateWindow window)
    {
        var reset = window.ResetAt?.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture)
            ?? "no-reset";

        return $"{provider}:{windowId}:{threshold:0}:{reset}";
    }

    public static string ForResetCreditExpiry(ProviderId provider, DateTimeOffset expiresAt)
        => $"{provider}:reset-credit-expiry:{expiresAt.ToUnixTimeSeconds()}";
}
