using Microsoft.UI.Xaml.Media;
using TaskbarQuota.AgentActivity;
using Windows.UI;

namespace TaskbarQuota.Controls;

internal static class AgentActivityVisuals
{
    public static Brush StatusBrush(AgentActivityStatus? status, Brush fallback) => status switch
    {
        AgentActivityStatus.Working => StatusGradient(Color.FromArgb(255, 82, 196, 255), Color.FromArgb(255, 45, 94, 232)),
        AgentActivityStatus.Waiting => StatusGradient(Color.FromArgb(255, 255, 202, 92), Color.FromArgb(255, 224, 141, 33)),
        AgentActivityStatus.Completed => StatusGradient(Color.FromArgb(255, 100, 222, 130), Color.FromArgb(255, 20, 142, 73)),
        AgentActivityStatus.Failed => StatusGradient(Color.FromArgb(255, 255, 130, 130), Color.FromArgb(255, 207, 55, 62)),
        _ => fallback,
    };

    private static LinearGradientBrush StatusGradient(Color start, Color end) => new()
    {
        StartPoint = new Windows.Foundation.Point(0, 0),
        EndPoint = new Windows.Foundation.Point(1, 1),
        GradientStops =
        {
            new GradientStop { Color = start, Offset = 0 },
            new GradientStop { Color = end, Offset = 1 },
        },
    };
}
