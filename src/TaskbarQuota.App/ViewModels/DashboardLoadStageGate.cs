using System.Threading;

namespace TaskbarQuota.ViewModels;

/// <summary>Orders one load's background merges before their callbacks reach the UI queue.</summary>
internal sealed class DashboardLoadStageGate
{
    private long _issued;
    private long _applied;

    internal long Reserve() => Interlocked.Increment(ref _issued);

    // Called only by the UI queue. Completion order may differ from the order stages were reserved.
    internal bool TryAccept(long stage)
    {
        if (stage <= _applied)
            return false;
        _applied = stage;
        return true;
    }
}
