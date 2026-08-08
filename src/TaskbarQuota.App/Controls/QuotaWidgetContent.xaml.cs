using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using TaskbarQuota.AgentActivity;
using TaskbarQuota.Usage;

namespace TaskbarQuota.Controls;

/// <summary>
/// Multi-tile usage strip (and optional agent-activity summary) for the floating always-on-top window.
/// Mirrors the taskbar widget's tile pool, separators, and width math without taskbar gap fitting.
/// </summary>
public sealed partial class QuotaWidgetContent : UserControl
{
    private const int TileHorizontalMarginLogicalPx = 4;
    private const int TileSeparatorLogicalPx = 7;
    private const int ActivitySummaryMarginLogicalPx = 8;
    private const int DefaultHostLogicalWidth = 172;
    /// <summary>Floating host has room for multi-row tiles + activity lines; taskbar stays ~40.</summary>
    private const int HostLogicalHeight = 48;
    /// <summary>Slack so measured glyphs/separators are not flush against the chrome edge.</summary>
    private const int ContentWidthSlackLogicalPx = 8;
    /// <summary>
    /// Floating mode is not gap-constrained — use the full preferred activity width so text is not cut.
    /// </summary>
    private const int ActivityLogicalWidth = AgentActivitySummary.DesiredLogicalWidth;
    /// <summary>
    /// Discovery can publish an empty intermediate snapshot while rescanning. Match the taskbar
    /// widget: keep the last non-empty activity visible briefly instead of blinking out.
    /// </summary>
    private static readonly TimeSpan EmptyActivityGrace = TimeSpan.FromMilliseconds(900);

    private readonly WidgetSummary[] _tiles = new WidgetSummary[UsageCoordinator.MaxWidgetTiles];
    private readonly TextBlock[] _separators = new TextBlock[UsageCoordinator.MaxWidgetTiles - 1];
    private readonly ProviderId?[] _tileProviders = new ProviderId?[UsageCoordinator.MaxWidgetTiles];
    private ProviderId? _activeProvider;
    private AgentActivitySnapshot _activitySnapshot = new(Array.Empty<AgentActivityItem>());
    private AgentActivitySnapshot? _pendingEmptyActivitySnapshot;
    private readonly DispatcherTimer _activityEmptySnapshotTimer;
    private int _desiredLogicalWidth = DefaultHostLogicalWidth;
    private int _desiredLogicalHeight = HostLogicalHeight;
    private bool _built;

    public event Action? Clicked;
    public event Action<AgentActivityItem?>? ActivityClicked;
    public event Action<int, int>? DesiredSizeChanged;

    /// <summary>Supplies a seed snapshot when a slot is reassigned so the tile paints immediately.</summary>
    public Func<ProviderId, UsageResult?>? HydrateProvider { get; set; }

    public int DesiredLogicalWidth => _desiredLogicalWidth;
    public int DesiredLogicalHeight => _desiredLogicalHeight;
    /// <summary>
    /// True when the activity strip is backed by the snapshot currently applied to the control. This may
    /// intentionally remain true during <see cref="EmptyActivityGrace"/> even when the latest service
    /// snapshot is empty.
    /// </summary>
    public bool HasVisibleActivity => WidgetSettingsService.ShowAgentActivityInWidget
        && _activitySnapshot.Primary is not null;
    public bool HasVisibleContent => _tileProviders.Any(provider => provider is not null)
        || HasVisibleActivity;

    public QuotaWidgetContent()
    {
        InitializeComponent();
        ActivitySummary.UseApplicationChromeColors = true;
        _activityEmptySnapshotTimer = new DispatcherTimer { Interval = EmptyActivityGrace };
        _activityEmptySnapshotTimer.Tick += ActivityEmptySnapshotTimer_Tick;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        BuildTiles();
        // Subscribe once per load; Unloaded tears these down.
        ActivitySummary.Clicked -= OnActivityClicked;
        ActivitySummary.Clicked += OnActivityClicked;
        WidgetSettingsService.Changed -= OnWidgetSettingsChanged;
        WidgetSettingsService.Changed += OnWidgetSettingsChanged;
        // A hide/show cycle of the floating window can fire Unloaded without clearing tile state.
        // Re-assert visibility so activity + quota tiles never stay stuck at Opacity 0.
        EnsureVisibleContent();
        RecomputeLayout();
    }

    /// <summary>Forces every assigned tile (and activity, when shown) back onto the screen.</summary>
    public void EnsureVisibleContent()
    {
        if (!_built)
            BuildTiles();

        for (int i = 0; i < _tiles.Length; i++)
        {
            if (_tileProviders[i] is null || _tiles[i] is null)
                continue;

            _tiles[i].Visibility = Visibility.Visible;
            _tiles[i].SetActiveToolVisible(true);
        }

        ApplyActivityToControl();
        // First show / hide thrash can leave the host sized to the empty default; re-measure now.
        RecomputeLayout();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        WidgetSettingsService.Changed -= OnWidgetSettingsChanged;
        ActivitySummary.Clicked -= OnActivityClicked;
        _activityEmptySnapshotTimer.Stop();
    }

    private void OnWidgetSettingsChanged(object? sender, EventArgs e)
        => DispatcherQueue.TryEnqueue(() =>
        {
            EnsureVisibleContent();
            RecomputeLayout();
        });

    private void BuildTiles()
    {
        if (_built)
            return;

        QuotaPanel.Children.Clear();
        for (int i = 0; i < _tiles.Length; i++)
        {
            if (i > 0)
            {
                var separator = CreateSeparator();
                _separators[i - 1] = separator;
                QuotaPanel.Children.Add(separator);
            }

            var tile = CreateTile();
            _tiles[i] = tile;
            QuotaPanel.Children.Add(tile);
        }

        _built = true;
    }

    private WidgetSummary CreateTile()
    {
        var summary = new WidgetSummary
        {
            Margin = new Thickness(2, 0, 2, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Stretch,
            Visibility = Visibility.Collapsed,
            UseApplicationChromeColors = true,
        };
        summary.DesiredHostWidthChanged += _ => RecomputeLayout();
        summary.Clicked += () => Clicked?.Invoke();
        return summary;
    }

    private TextBlock CreateSeparator() => new()
    {
        Text = "|",
        Width = TileSeparatorLogicalPx,
        FontSize = 12,
        Opacity = 0.35,
        TextAlignment = TextAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        Visibility = Visibility.Collapsed,
        IsHitTestVisible = false,
        Foreground = SeparatorBrush(),
    };

    /// <summary>
    /// Separator color from the floating window chrome theme — not Application.Current resources,
    /// which resolve against app/system theme and go light-on-light when the HUD is light.
    /// </summary>
    private Brush SeparatorBrush()
        => new SolidColorBrush(ThemeService.IsLightChrome(this)
            ? Color.FromArgb(255, 28, 28, 28)
            : Colors.White);

    private void OnActivityClicked(AgentActivityItem? item) => ActivityClicked?.Invoke(item);

    /// <summary>
    /// Call when a pointer gesture became a drag so child buttons/tiles do not fire Click after release.
    /// </summary>
    public void SuppressNextClicks()
    {
        ActivitySummary.SuppressNextClick = true;
        for (int i = 0; i < _tiles.Length; i++)
        {
            if (_tiles[i] is { } tile)
                tile.SuppressNextClick = true;
        }
    }

    /// <summary>Clears drag-only click suppression after the release event has finished routing.</summary>
    public void ClearSuppressedClicks()
    {
        ActivitySummary.SuppressNextClick = false;
        for (int i = 0; i < _tiles.Length; i++)
        {
            if (_tiles[i] is { } tile)
                tile.SuppressNextClick = false;
        }
    }

    public void SetDisplayProviders(IReadOnlyList<ProviderId> providers, ProviderId? activeProvider)
    {
        providers = providers.Distinct().Take(UsageCoordinator.MaxWidgetTiles).ToArray();
        _activeProvider = activeProvider;

        if (!_built)
            BuildTiles();

        for (int i = 0; i < _tiles.Length; i++)
        {
            ProviderId? desired = i < providers.Count ? providers[i] : null;
            if (_tileProviders[i] == desired)
                continue;

            _tileProviders[i] = desired;
            if (desired is { } provider && HydrateProvider?.Invoke(provider) is { } seed)
            {
                _tiles[i].SuppressNextTransition = true;
                _tiles[i].Apply(seed, force: true);
            }
        }

        ApplyActivityToControl();
        RecomputeLayout();
    }

    public void ApplyResult(UsageResult result, bool force = false)
    {
        for (int i = 0; i < _tiles.Length; i++)
        {
            if (_tileProviders[i] != result.Id)
                continue;

            _tiles[i].Apply(result, force);
            _tiles[i].SetActiveToolVisible(true);
            RecomputeLayout();
            return;
        }
    }

    public void SetActivitySnapshot(AgentActivitySnapshot snapshot)
    {
        if (!_built)
            BuildTiles();

        // Match TaskBarWidget: discovery can publish an empty intermediate scan. Keep the previous
        // non-empty activity visible for a short grace period so the strip does not blink out.
        if (snapshot.Primary is null && _activitySnapshot.Primary is not null)
        {
            _pendingEmptyActivitySnapshot = snapshot;
            _activityEmptySnapshotTimer.Stop();
            _activityEmptySnapshotTimer.Start();
            return;
        }

        _activityEmptySnapshotTimer.Stop();
        _pendingEmptyActivitySnapshot = null;
        ApplyActivitySnapshot(snapshot);
    }

    private void ActivityEmptySnapshotTimer_Tick(object? sender, object e)
    {
        _activityEmptySnapshotTimer.Stop();
        if (_pendingEmptyActivitySnapshot is not { } snapshot)
            return;

        _pendingEmptyActivitySnapshot = null;
        ApplyActivitySnapshot(snapshot);
    }

    private void ApplyActivitySnapshot(AgentActivitySnapshot snapshot)
    {
        _activitySnapshot = snapshot;
        ApplyActivityToControl();
        foreach (var tile in _tiles)
            tile?.SetAgentActivity(snapshot);
        RecomputeLayout();
    }

    private void ApplyActivityToControl()
    {
        bool showActivity = HasVisibleActivity;

        if (!showActivity)
        {
            ActivitySummary.Visibility = Visibility.Collapsed;
            // Drop any fixed width so a later measure cannot keep the previous activity reservation.
            ActivitySummary.ClearValue(FrameworkElement.WidthProperty);
            return;
        }

        // Apply first so the control sizes its content, then force Visible in case Apply raced.
        ActivitySummary.Apply(_activitySnapshot, _activeProvider);
        ActivitySummary.Visibility = Visibility.Visible;
        ActivitySummary.SetLogicalWidth(ActivityLogicalWidth);
    }

    public void MeasureDesiredSize(out int logicalWidth, out int logicalHeight)
    {
        RecomputeLayout();
        logicalWidth = _desiredLogicalWidth;
        logicalHeight = _desiredLogicalHeight;
    }

    /// <summary>
    /// Pure content width for the floating host: tiles + optional activity + slack.
    /// Does not consult <see cref="FrameworkElement.ActualWidth"/> so hiding activity can shrink the window.
    /// </summary>
    internal static int ComputeContentLogicalWidth(
        IReadOnlyList<int> tileWidths,
        bool showActivity,
        int activityLogicalWidth = ActivityLogicalWidth)
    {
        int total = 0;
        for (int i = 0; i < tileWidths.Count; i++)
        {
            total += tileWidths[i] + TileHorizontalMarginLogicalPx
                + (i > 0 ? TileSeparatorLogicalPx : 0);
        }

        if (showActivity)
            total += Math.Max(1, activityLogicalWidth) + ActivitySummaryMarginLogicalPx;

        if (total <= 0)
            total = DefaultHostLogicalWidth;

        return total + ContentWidthSlackLogicalPx;
    }

    private void RecomputeLayout()
    {
        if (!_built)
            return;

        bool showActivity = HasVisibleActivity;

        var tileWidths = new List<int>(UsageCoordinator.MaxWidgetTiles);
        for (int i = 0; i < _tiles.Length; i++)
        {
            bool shown = _tileProviders[i] is not null;
            _tiles[i].Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
            _tiles[i].SetActiveToolVisible(shown);
            if (!shown)
                continue;

            int width = _tiles[i].MeasureDesiredWidth();
            if (_tileProviders[i] is { } provider)
                Taskbar.TaskbarSpace.RecordTileWidth(provider, width);

            tileWidths.Add(width);
        }

        var separatorBrush = SeparatorBrush();
        for (int i = 0; i < _separators.Length; i++)
        {
            bool showSep = _tileProviders[i] is not null && _tileProviders[i + 1] is not null;
            _separators[i].Visibility = showSep ? Visibility.Visible : Visibility.Collapsed;
            _separators[i].Foreground = separatorBrush;
        }

        // Keep Apply + size in sync with visibility so the strip never reserves space for a
        // collapsed control or shows a blank activity host.
        ApplyActivityToControl();

        // Model width only — never Math.Max against ActualWidth. That kept the previous (wider)
        // layout after activity was hidden and blocked the floating window from shrinking.
        int total = ComputeContentLogicalWidth(tileWidths, showActivity, ActivityLogicalWidth);
        int height = HostLogicalHeight;

        if (total == _desiredLogicalWidth && height == _desiredLogicalHeight)
            return;

        _desiredLogicalWidth = total;
        _desiredLogicalHeight = height;
        DesiredSizeChanged?.Invoke(_desiredLogicalWidth, _desiredLogicalHeight);
    }
}
