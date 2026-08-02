using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media;
using TaskbarQuota.AgentActivity;
using TaskbarQuota.Usage;
using Windows.UI;

namespace TaskbarQuota.Controls;

public sealed partial class AgentActivitySummary : UserControl
{
    public const int DesiredLogicalWidth = 400;
    public event Action? Clicked;
    public AgentActivityItem? FollowedItem { get; private set; }
    private string? _followedId;
    private AgentActivitySnapshot _snapshot = new(Array.Empty<AgentActivityItem>());
    private ProviderId? _activeProvider;
    private Storyboard? _selectionStoryboard;

    public AgentActivitySummary()
    {
        InitializeComponent();
        Loaded += (_, _) => ApplyForeground();
        PointerPressed += (_, _) => Clicked?.Invoke();
        PointerWheelChanged += AgentActivitySummary_PointerWheelChanged;
    }

    public void Apply(AgentActivitySnapshot snapshot, ProviderId? activeProvider = null)
    {
        _snapshot = snapshot;
        _activeProvider = activeProvider;
        var items = snapshot.TrackedItems;
        var item = items.FirstOrDefault(candidate => candidate.Id == _followedId && candidate.IsLive)
            ?? items.FirstOrDefault(candidate => candidate.IsLive)
            ?? items.OrderByDescending(candidate => candidate.UpdatedAt).FirstOrDefault();
        if (item is null)
        {
            Visibility = Visibility.Collapsed;
            FollowedItem = null;
            _followedId = null;
            return;
        }

        _followedId = item.Id;
        FollowedItem = item;
        Visibility = Visibility.Visible;
        var providerName = ProviderDisplayName(item.Provider);
        ProviderIcon.ProviderId = item.Provider;
        ProviderIcon.Initial = providerName.Length > 0 ? providerName[0].ToString() : "?";
        var statusBrush = AgentActivityVisuals.StatusBrush(
            item.Status,
            ProviderIcon.ForegroundBrush ?? (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"]);
        ProviderIcon.ForegroundBrush = statusBrush;
        bool showProviderMarker = items.Select(candidate => candidate.Provider).Distinct().Count() > 1
            || item.Provider != activeProvider
            || items.Count > 1;
        ProviderMarker.Visibility = showProviderMarker ? Visibility.Visible : Visibility.Collapsed;
        AgentCountBadge.Visibility = items.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        AgentCountText.Text = items.Count > 9 ? "9+" : items.Count.ToString();
        var activitySummary = ActivitySummary(items);
        AgentStatusText.Text = activitySummary;
        ToolTipService.SetToolTip(ProviderMarker,
            $"{items.Count} agents · {activitySummary}. Use the mouse wheel to switch agents.");
        TitleText.Text = ActivityTitle(item);
        StepText.Text = SummaryText(item);
        ToolTipService.SetToolTip(this, $"{ActivityTitle(item)}: {item.Step}");
        ApplyForeground();
    }

    private void AgentActivitySummary_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var liveItems = _snapshot.TrackedItems
            .Where(item => item.IsLive)
            .OrderBy(item => item.StartedAt)
            .ThenBy(item => item.Id, StringComparer.Ordinal)
            .ToList();
        if (liveItems.Count < 2)
            return;

        int delta = e.GetCurrentPoint(this).Properties.MouseWheelDelta;
        if (delta == 0)
            return;

        int currentIndex = liveItems.FindIndex(item => item.Id == _followedId);
        if (currentIndex < 0)
            currentIndex = 0;

        // Wheel-up moves toward the earlier agent; wheel-down moves toward the later agent.
        int direction = delta > 0 ? -1 : 1;
        int nextIndex = (currentIndex + direction + liveItems.Count) % liveItems.Count;
        var next = liveItems[nextIndex];
        if (next.Id == _followedId)
            return;

        _followedId = next.Id;
        Apply(_snapshot, _activeProvider);
        AnimateSelection(direction);
        e.Handled = true;
    }

    private void AnimateSelection(int direction)
    {
        _selectionStoryboard?.Stop();

        Root.Opacity = 0.55;
        RootTransform.TranslateY = direction > 0 ? 8 : -8;

        var storyboard = new Storyboard();
        var fade = new DoubleAnimation
        {
            To = 1,
            Duration = TimeSpan.FromMilliseconds(180),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(fade, Root);
        Storyboard.SetTargetProperty(fade, "Opacity");

        var slide = new DoubleAnimation
        {
            To = 0,
            Duration = TimeSpan.FromMilliseconds(220),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            EnableDependentAnimation = true,
        };
        Storyboard.SetTarget(slide, RootTransform);
        Storyboard.SetTargetProperty(slide, "TranslateY");

        storyboard.Children.Add(fade);
        storyboard.Children.Add(slide);
        _selectionStoryboard = storyboard;
        storyboard.Begin();
    }

    private static string SummaryText(AgentActivityItem item) => item.Status switch
    {
        AgentActivityStatus.Completed => string.IsNullOrWhiteSpace(item.Step) || item.Step == "Completed"
            ? "Task completed"
            : Condense(item.Step),
        AgentActivityStatus.Failed => "Task needs attention",
        _ => Condense(item.Step),
    };

    private static string ActivitySummary(IReadOnlyList<AgentActivityItem> items)
    {
        int working = items.Count(item => item.Status == AgentActivityStatus.Working);
        int waiting = items.Count(item => item.Status == AgentActivityStatus.Waiting);
        int idle = items.Count(item => item.Status == AgentActivityStatus.Idle);
        int completed = items.Count(item => item.Status == AgentActivityStatus.Completed);
        int failed = items.Count(item => item.Status == AgentActivityStatus.Failed);
        var parts = new[]
        {
            working > 0 ? $"{working} working" : "",
            waiting > 0 ? $"{waiting} waiting" : "",
            idle > 0 ? $"{idle} idle" : "",
            completed > 0 ? $"{completed} done" : "",
            failed > 0 ? $"{failed} failed" : "",
        }.Where(part => part.Length > 0);
        return string.Join(" · ", parts);
    }

    private static string Condense(string text)
    {
        var compact = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (compact.Length <= 96)
            return compact;

        int sentenceEnd = compact.IndexOfAny(['.', '!', '?']);
        if (sentenceEnd is > 12 and <= 96)
            return compact[..(sentenceEnd + 1)];

        return compact[..95].TrimEnd() + "…";
    }

    private static string ProviderDisplayName(ProviderId provider) => provider switch
    {
        ProviderId.ClinePass => "Cline Pass",
        ProviderId.OpenCodeGo => "OpenCode Go",
        _ => provider.ToString(),
    };

    private static string ActivityTitle(AgentActivityItem item)
        => !string.IsNullOrWhiteSpace(item.Host)
            && string.Equals(item.Title, ProviderDisplayName(item.Provider), StringComparison.OrdinalIgnoreCase)
            ? $"{ProviderDisplayName(item.Provider)} through {item.Host}"
            : item.Title;

    private void ApplyForeground()
    {
        bool light = Interop.SystemInfos.IsSystemLightThemeUsed() == true;
        var brush = new SolidColorBrush(light ? Color.FromArgb(255, 28, 28, 28) : Colors.White);
        StepText.Foreground = brush;
        TitleText.Foreground = brush;
    }
}
