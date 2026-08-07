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
    private const int HostLogicalHeight = 40;
    private const int ActivityLogicalWidth = 200;

    private readonly WidgetSummary[] _tiles = new WidgetSummary[UsageCoordinator.MaxWidgetTiles];
    private readonly TextBlock[] _separators = new TextBlock[UsageCoordinator.MaxWidgetTiles - 1];
    private readonly ProviderId?[] _tileProviders = new ProviderId?[UsageCoordinator.MaxWidgetTiles];
    private ProviderId? _activeProvider;
    private AgentActivitySnapshot _activitySnapshot = new(Array.Empty<AgentActivityItem>());
    private int _desiredLogicalWidth = DefaultHostLogicalWidth;
    private bool _built;

    public event Action? Clicked;
    public event Action<AgentActivityItem?>? ActivityClicked;
    public event Action<int, int>? DesiredSizeChanged;

    /// <summary>Supplies a seed snapshot when a slot is reassigned so the tile paints immediately.</summary>
    public Func<ProviderId, UsageResult?>? HydrateProvider { get; set; }

    public int DesiredLogicalWidth => _desiredLogicalWidth;
    public int DesiredLogicalHeight => HostLogicalHeight;

    public QuotaWidgetContent()
    {
        InitializeComponent();
        ActivitySummary.UseApplicationChromeColors = true;
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

        bool showActivity = WidgetSettingsService.ShowAgentActivityInWidget
            && _activitySnapshot.Primary is not null;
        ActivitySummary.Visibility = showActivity ? Visibility.Visible : Visibility.Collapsed;
        if (showActivity)
            ActivitySummary.Apply(_activitySnapshot, _activeProvider);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        WidgetSettingsService.Changed -= OnWidgetSettingsChanged;
        ActivitySummary.Clicked -= OnActivityClicked;
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

        ActivitySummary.Apply(_activitySnapshot, _activeProvider);
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
        _activitySnapshot = snapshot;
        ActivitySummary.Apply(snapshot, _activeProvider);
        foreach (var tile in _tiles)
            tile?.SetAgentActivity(snapshot);
        RecomputeLayout();
    }

    public void MeasureDesiredSize(out int logicalWidth, out int logicalHeight)
    {
        RecomputeLayout();
        logicalWidth = _desiredLogicalWidth;
        logicalHeight = HostLogicalHeight;
    }

    private void RecomputeLayout()
    {
        if (!_built)
            return;

        bool showActivity = WidgetSettingsService.ShowAgentActivityInWidget
            && _activitySnapshot.Primary is not null;

        int total = 0;
        int shownCount = 0;
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

            total += width + TileHorizontalMarginLogicalPx + (shownCount > 0 ? TileSeparatorLogicalPx : 0);
            shownCount++;
        }

        var separatorBrush = SeparatorBrush();
        for (int i = 0; i < _separators.Length; i++)
        {
            bool showSep = _tileProviders[i] is not null && _tileProviders[i + 1] is not null;
            _separators[i].Visibility = showSep ? Visibility.Visible : Visibility.Collapsed;
            _separators[i].Foreground = separatorBrush;
        }

        if (showActivity)
        {
            ActivitySummary.Visibility = Visibility.Visible;
            ActivitySummary.SetLogicalWidth(ActivityLogicalWidth);
            total += ActivityLogicalWidth + ActivitySummaryMarginLogicalPx;
        }
        else
        {
            ActivitySummary.Visibility = Visibility.Collapsed;
        }

        if (total <= 0)
            total = DefaultHostLogicalWidth;

        if (total == _desiredLogicalWidth)
            return;

        _desiredLogicalWidth = total;
        DesiredSizeChanged?.Invoke(_desiredLogicalWidth, HostLogicalHeight);
    }
}
