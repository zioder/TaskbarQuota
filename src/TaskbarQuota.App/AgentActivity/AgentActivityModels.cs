using System;
using System.Collections.Generic;
using System.Linq;
using TaskbarQuota.Usage;

namespace TaskbarQuota.AgentActivity;

public enum AgentActivityStatus
{
    Working,
    Waiting,
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
    public bool IsLive => Status is AgentActivityStatus.Working or AgentActivityStatus.Waiting;
    public string StatusText => Status switch
    {
        AgentActivityStatus.Working => "Working",
        AgentActivityStatus.Waiting => "Waiting",
        AgentActivityStatus.Completed => "Completed",
        AgentActivityStatus.Failed => "Failed",
        _ => "Unknown",
    };
}

public sealed record AgentActivitySnapshot(IReadOnlyList<AgentActivityItem> Items, IReadOnlyList<AgentActivityItem>? RunItems = null)
{
    public IReadOnlyList<AgentActivityItem> TrackedItems => RunItems is { Count: > 0 } ? RunItems : Items;
    public AgentActivityItem? Primary => Items
        .Where(item => item.IsLive)
        .OrderByDescending(item => item.UpdatedAt)
        .FirstOrDefault()
        ?? Items.OrderByDescending(item => item.UpdatedAt).FirstOrDefault();

    public bool HasLiveItems => Items.Any(item => item.IsLive);
    public bool HasUnreadCompletions => Items.Any(item => item.Status is AgentActivityStatus.Completed or AgentActivityStatus.Failed);
}
