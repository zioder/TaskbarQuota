using System;
using System.Collections.Generic;
using System.Linq;
using TaskbarQuota.Usage;

namespace TaskbarQuota.AgentActivity;

public enum AgentActivityStatus
{
    Working,
    Waiting,
    Idle,
    Completed,
    Failed,
}

public sealed record AgentActivityItem(
    string Id,
    ProviderId Provider,
    string Title,
    string Step,
    AgentActivityStatus Status,
    DateTimeOffset StartedAt,
    DateTimeOffset UpdatedAt,
    int SubagentCount = 0,
    string? Detail = null,
    string? Model = null,
    string? ThreadId = null,
    string? ParentThreadId = null,
    string? Host = null)
{
    public bool IsLive => Status is AgentActivityStatus.Working or AgentActivityStatus.Waiting or AgentActivityStatus.Idle;
    public string StatusText => Status switch
    {
        AgentActivityStatus.Working => "Working",
        AgentActivityStatus.Waiting => "Waiting",
        AgentActivityStatus.Idle => "Idle",
        AgentActivityStatus.Completed => "Completed",
        AgentActivityStatus.Failed => "Failed",
        _ => "Unknown",
    };
}

public sealed record AgentActivitySnapshot(IReadOnlyList<AgentActivityItem> Items, IReadOnlyList<AgentActivityItem>? RunItems = null)
{
    public static readonly TimeSpan CompletedRetention = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan IdleWaitingRetention = TimeSpan.FromSeconds(90);
    public static readonly TimeSpan IdleCompletedRetention = TimeSpan.FromSeconds(120);
    public static readonly TimeSpan FailedRetention = TimeSpan.FromMinutes(5);

    private IReadOnlyList<AgentActivityItem> SourceItems => RunItems is { Count: > 0 } ? RunItems : Items;

    /// <summary>Items appropriate for the compact taskbar surface; full history remains in <see cref="Items"/>.</summary>
    public IReadOnlyList<AgentActivityItem> CompactItems => SourceItems
        .Select(ToCompactItem)
        .Where(item => item is not null)
        .Select(item => item!)
        .ToArray();

    public IReadOnlyList<AgentActivityItem> TrackedItems => CompactItems;
    public AgentActivityItem? Primary => CompactItems
        .Where(item => item.Status is AgentActivityStatus.Working or AgentActivityStatus.Waiting)
        .OrderByDescending(item => item.UpdatedAt)
        .FirstOrDefault()
        ?? CompactItems.OrderByDescending(item => item.UpdatedAt).FirstOrDefault();

    public bool HasLiveItems => Items.Any(item => item.IsLive);
    public bool HasUnreadCompletions => Items.Any(item => item.Status is AgentActivityStatus.Completed or AgentActivityStatus.Failed);

    public IReadOnlyList<AgentActivityItem> ItemsForDisplay(string? selectedId)
    {
        if (string.IsNullOrWhiteSpace(selectedId))
            return Items;

        var selected = Items.FirstOrDefault(item => item.Id == selectedId);
        return selected is null
            ? Items
            : new[] { selected }.Concat(Items.Where(item => item.Id != selectedId)).ToArray();
    }

    private static AgentActivityItem? ToCompactItem(AgentActivityItem item)
    {
        if (item.Status is AgentActivityStatus.Working or AgentActivityStatus.Waiting)
            return item;

        var age = DateTimeOffset.UtcNow - item.UpdatedAt.ToUniversalTime();
        if (item.Status == AgentActivityStatus.Idle)
        {
            if (age <= IdleWaitingRetention)
                return item;
            if (age <= IdleCompletedRetention)
                return item with { Status = AgentActivityStatus.Completed, Step = "Completed" };
            return null;
        }

        var retention = item.Status switch
        {
            AgentActivityStatus.Failed => FailedRetention,
            _ => CompletedRetention,
        };
        return age <= retention ? item : null;
    }
}
