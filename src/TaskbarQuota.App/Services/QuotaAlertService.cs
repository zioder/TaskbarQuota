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
    private readonly object _lock = new();
    private QuotaAlertState _state;
    private bool _started;

    internal QuotaAlertService(IQuotaAlertNotifier notifier)
    {
        _notifier = notifier;
        _state = QuotaAlertState.Load();
    }

    public void Start()
    {
        lock (_lock)
        {
            if (_started)
                return;

            UsageCoordinator.Instance.StateChanged += OnStateChanged;
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
            App.Quitting -= Stop;
            _started = false;
        }
    }

    private void OnStateChanged(UsageResult result)
    {
        if (!QuotaAlertSettingsService.Current.Enabled || !result.Ok || result.Fetch is null)
            return;

        List<QuotaAlertNotification> notifications;
        lock (_lock)
        {
            notifications = QuotaAlertEvaluator.Evaluate(
                result,
                QuotaAlertSettingsService.Current,
                _state,
                DateTimeOffset.Now).ToList();

            if (_state.HasUnsavedChanges)
                _state.Save();
        }

        foreach (var notification in notifications)
            _notifier.Show(notification);
    }
}

internal interface IQuotaAlertNotifier
{
    void Register();
    void Show(QuotaAlertNotification notification);
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

    public void Show(QuotaAlertNotification notification)
    {
        try
        {
            Register();
            var appNotification = new AppNotificationBuilder()
                .AddText(notification.Title)
                .AddText(notification.Body)
                .BuildNotification();

            AppNotificationManager.Default.Show(appNotification);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to show quota alert notification");
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
        DateTimeOffset now)
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
