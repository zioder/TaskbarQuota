using System;
using System.Linq;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using TaskbarQuota.AgentActivity;
using Windows.UI;

namespace TaskbarQuota.Controls;

public sealed partial class AgentActivitySummary : UserControl
{
    public const int DesiredLogicalWidth = 400;
    public event Action? Clicked;
    public AgentActivityItem? FollowedItem { get; private set; }
    private string? _followedId;

    public AgentActivitySummary()
    {
        InitializeComponent();
        Loaded += (_, _) => ApplyForeground();
        PointerPressed += (_, _) => Clicked?.Invoke();
    }

    public void Apply(AgentActivitySnapshot snapshot)
    {
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
        int total = items.Count;
        int completed = items.Count(candidate => candidate.Status == AgentActivityStatus.Completed);
        bool showProgress = total > 1;
        bool allDone = showProgress && completed == total;
        TitleText.Text = allDone ? "All agent tasks" : item.Title;
        var state = allDone ? $"{completed} / {total} completed" : SummaryText(item);
        StepText.Text = showProgress && !allDone ? $"{state} · {completed} / {total} completed" : state;
        ToolTipService.SetToolTip(this, $"{item.Title}: {item.Step}");
        ApplyForeground();
    }

    private static string SummaryText(AgentActivityItem item) => item.Status switch
    {
        AgentActivityStatus.Completed => string.IsNullOrWhiteSpace(item.Step) || item.Step == "Completed"
            ? "Task completed"
            : Condense(item.Step),
        AgentActivityStatus.Failed => "Task needs attention",
        _ => Condense(item.Step),
    };

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

    private void ApplyForeground()
    {
        bool light = Interop.SystemInfos.IsSystemLightThemeUsed() == true;
        var brush = new SolidColorBrush(light ? Color.FromArgb(255, 28, 28, 28) : Colors.White);
        StepText.Foreground = brush;
        TitleText.Foreground = brush;
    }
}
