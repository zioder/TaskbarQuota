using TaskbarQuota.ViewModels;

namespace TaskbarQuota.Tests;

public class DashboardLoadStageGateTests
{
    [Fact]
    public async Task DelayedPartialCannotReplaceCompletedLoad()
    {
        var stages = new DashboardLoadStageGate();
        long partial = stages.Reserve();
        long final = stages.Reserve();
        var mergeReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string visible = "Pending";
        var delayed = Task.Run(async () =>
        {
            await mergeReady.Task;
            if (stages.TryAccept(partial))
                visible = "Pending";
        });
        Assert.True(stages.TryAccept(final));
        visible = "Success";
        mergeReady.SetResult();
        await delayed;
        Assert.Equal("Success", visible);
    }

    [Fact]
    public void NewerPartialsAdvanceWhileDuplicateOrLateStagesDoNot()
    {
        var stages = new DashboardLoadStageGate();
        long first = stages.Reserve();
        long second = stages.Reserve();
        long third = stages.Reserve();
        Assert.True(stages.TryAccept(first));
        Assert.True(stages.TryAccept(third));
        Assert.False(stages.TryAccept(second));
        Assert.False(stages.TryAccept(third));
    }

    [Fact]
    public void IndependentLoadsDoNotShareAppliedStages()
    {
        var oldLoad = new DashboardLoadStageGate();
        var nextLoad = new DashboardLoadStageGate();
        oldLoad.Reserve();
        Assert.True(oldLoad.TryAccept(oldLoad.Reserve()));
        Assert.True(nextLoad.TryAccept(nextLoad.Reserve()));
    }
}
