using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
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
    // A separate taskbar island should start compact. The wider value above remains the maximum when
    // space is abundant, but using it as the initial native-window width makes narrow drag lanes look
    // unavailable and causes a large resize the first time the activity island is moved.
    public const int DefaultLogicalWidth = 120;
    // The navigator and provider glyph need to remain usable, but the text area may shrink below the
    // preferred width so the host never reserves more space than the taskbar gap actually provides.
    public const int MinimumLogicalWidth = 120;
    public event Action<AgentActivityItem?>? Clicked;
    public bool SuppressNextClick { get; set; }
    public AgentActivityItem? FollowedItem { get; private set; }
    private string? _followedId;
    private AgentActivitySnapshot _snapshot = new(Array.Empty<AgentActivityItem>());
    private ProviderId? _activeProvider;
    private Storyboard? _selectionStoryboard;
    private Storyboard? _appearanceStoryboard;
    private bool _hasAppeared;
    private readonly DispatcherTimer _wheelGestureEndTimer;
    private bool _wheelGestureActive;

    public AgentActivitySummary()
    {
        InitializeComponent();
        _wheelGestureEndTimer = new DispatcherTimer
        {
            // Precision touchpads keep emitting wheel events while momentum decays. A short fixed
            // cooldown prevents a swipe from racing through agents without making the next swipe feel
            // blocked.
            Interval = TimeSpan.FromMilliseconds(140),
        };
        _wheelGestureEndTimer.Tick += (_, _) =>
        {
            _wheelGestureEndTimer.Stop();
            _wheelGestureActive = false;
        };
        Loaded += (_, _) => ApplyForeground();
        PointerWheelChanged += AgentActivitySummary_PointerWheelChanged;
    }

    private void OpenActivityButton_Click(object sender, RoutedEventArgs e)
    {
        if (SuppressNextClick)
        {
            SuppressNextClick = false;
            return;
        }
        Clicked?.Invoke(FollowedItem);
    }

    public void SetLogicalWidth(int logicalWidth)
    {
        Root.Width = Math.Clamp(logicalWidth, 1, DesiredLogicalWidth);
    }

    public void Apply(AgentActivitySnapshot snapshot, ProviderId? activeProvider = null)
    {
        _snapshot = snapshot;
        _activeProvider = activeProvider;
        var items = snapshot.TrackedItems;
        var item = items.FirstOrDefault(candidate => candidate.Id == _followedId)
            ?? items.FirstOrDefault(candidate => candidate.IsLive)
            ?? items.OrderByDescending(candidate => candidate.UpdatedAt).FirstOrDefault();
        if (item is null)
        {
            Visibility = Visibility.Collapsed;
            FollowedItem = null;
            _followedId = null;
            _hasAppeared = false;
            return;
        }

        bool shouldReveal = !_hasAppeared;
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
        bool showProviderMarker = !IsSameProvider(item.Provider, activeProvider);
        ProviderIcon.Visibility = showProviderMarker ? Visibility.Visible : Visibility.Collapsed;
        UpdateNavigation(items, item.Id);
        TitleText.Text = ActivityTitle(item);
        StepText.Text = SummaryText(item);
        var accessibleSummary = $"{ActivityTitle(item)}, {providerName}, {item.StatusText}. {SummaryText(item)}";
        AutomationProperties.SetName(OpenActivityButton, $"Open agent activity. {accessibleSummary}");
        AutomationProperties.SetName(ProviderIcon, $"{providerName}, {item.StatusText}");
        ApplyForeground();
        if (shouldReveal)
        {
            _hasAppeared = true;
            AnimateReveal();
        }
    }

    private void AgentActivitySummary_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var properties = e.GetCurrentPoint(this).Properties;
        int delta = properties.MouseWheelDelta;
        if (delta == 0 || properties.IsHorizontalMouseWheel)
            return;

        // Consume every vertical wheel event over the widget, including momentum events and attempts at
        // either end of the list. Letting those events bubble can scroll an enclosing surface instead.
        e.Handled = true;
        if (_wheelGestureActive)
            return;

        _wheelGestureActive = true;
        _wheelGestureEndTimer.Stop();
        _wheelGestureEndTimer.Start();
        // Wheel-up moves toward the earlier agent; wheel-down moves toward the later agent.
        int direction = delta > 0 ? -1 : 1;
        MoveSelection(direction);
    }

    private void PreviousButton_Click(object sender, RoutedEventArgs e) => MoveSelection(-1);

    private void NextButton_Click(object sender, RoutedEventArgs e) => MoveSelection(1);

    private bool MoveSelection(int direction)
    {
        var items = NavigationItems(_snapshot.TrackedItems);
        int currentIndex = items.FindIndex(item => item.Id == _followedId);
        if (currentIndex < 0)
            return false;

        int nextIndex = currentIndex + direction;
        if (nextIndex < 0 || nextIndex >= items.Count)
            return false;

        _followedId = items[nextIndex].Id;
        Apply(_snapshot, _activeProvider);
        AnimateSelection(direction);
        return true;
    }

    private void UpdateNavigation(IReadOnlyList<AgentActivityItem> items, string selectedId)
    {
        var navigationItems = NavigationItems(items);
        int index = navigationItems.FindIndex(item => item.Id == selectedId);
        bool hasSelection = index >= 0;
        AgentPositionText.Text = hasSelection ? $"{index + 1}/{navigationItems.Count}" : "—";
        AgentPositionText.Visibility = navigationItems.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        PreviousButton.IsEnabled = hasSelection && index > 0;
        NextButton.IsEnabled = hasSelection && index < navigationItems.Count - 1;
        PreviousButton.Opacity = PreviousButton.IsEnabled ? 1 : 0.35;
        NextButton.Opacity = NextButton.IsEnabled ? 1 : 0.35;
    }

    private static List<AgentActivityItem> NavigationItems(IReadOnlyList<AgentActivityItem> items)
        => items
            .OrderByDescending(item => item.IsLive)
            .ThenBy(item => item.StartedAt)
            .ThenBy(item => item.Id, StringComparer.Ordinal)
            .ToList();

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

    private void AnimateReveal()
    {
        _appearanceStoryboard?.Stop();
        Root.Opacity = 1;
        RootTransform.TranslateY = 0;

        var fade = new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = new Duration(TimeSpan.FromMilliseconds(240)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(fade, Root);
        Storyboard.SetTargetProperty(fade, "Opacity");

        var slide = new DoubleAnimation
        {
            From = 5,
            To = 0,
            Duration = new Duration(TimeSpan.FromMilliseconds(260)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            EnableDependentAnimation = true,
        };
        Storyboard.SetTarget(slide, RootTransform);
        Storyboard.SetTargetProperty(slide, "TranslateY");

        var storyboard = new Storyboard();
        storyboard.Children.Add(fade);
        storyboard.Children.Add(slide);
        _appearanceStoryboard = storyboard;
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
        ProviderId.Copilot => "GitHub Copilot",
        _ => provider.ToString(),
    };

    private static bool IsSameProvider(ProviderId activityProvider, ProviderId? activeProvider)
        => activeProvider is { } active
            && (activityProvider == active
                || (activityProvider is ProviderId.Cline && active is ProviderId.ClinePass)
                || (activityProvider is ProviderId.ClinePass && active is ProviderId.Cline)
                || (activityProvider is ProviderId.OpenCode && active is ProviderId.OpenCodeGo)
                || (activityProvider is ProviderId.OpenCodeGo && active is ProviderId.OpenCode));

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
