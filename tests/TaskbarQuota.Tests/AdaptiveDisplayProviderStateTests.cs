using TaskbarQuota.Taskbar;
using TaskbarQuota.Usage;

namespace TaskbarQuota.Tests;

public class AdaptiveDisplayProviderStateTests
{
    [Fact]
    public void FocusingProviderOnAnotherDisplayKeepsTheFirstDisplaysProvider()
    {
        var state = new AdaptiveDisplayProviderState();

        state.Observe(ProviderId.Codex, "DISPLAY2", new IntPtr(2));
        state.Observe(ProviderId.OpenCode, "DISPLAY1", new IntPtr(1));

        Assert.Equal(ProviderId.OpenCode, state.GetProvider("DISPLAY1"));
        Assert.Equal(ProviderId.Codex, state.GetProvider("DISPLAY2"));
    }

    [Fact]
    public void MovingAProviderTransfersItInsteadOfDuplicatingIt()
    {
        var state = new AdaptiveDisplayProviderState();
        state.Observe(ProviderId.Codex, "DISPLAY1", new IntPtr(1));

        state.Observe(ProviderId.Codex, "DISPLAY2", new IntPtr(2));

        Assert.Null(state.GetProvider("DISPLAY1"));
        Assert.Equal(ProviderId.Codex, state.GetProvider("DISPLAY2"));
    }

    [Fact]
    public void ADisplayRestoresItsPreviousValidProvider()
    {
        var state = new AdaptiveDisplayProviderState();
        state.Observe(ProviderId.Codex, "DISPLAY1", new IntPtr(1));

        state.Observe(ProviderId.OpenCode, "display1", new IntPtr(2));

        Assert.Equal(ProviderId.OpenCode, state.GetProvider("DISPLAY1"));
        Assert.Equal(
            ProviderId.Codex,
            state.GetProvider("DISPLAY1", window => window != new IntPtr(2)));
    }

    [Fact]
    public void MovingProviderAwayRevealsTheProviderItDisplaced()
    {
        var state = new AdaptiveDisplayProviderState();
        state.Observe(ProviderId.OpenCode, "DISPLAY2", new IntPtr(20));
        state.Observe(ProviderId.Antigravity, "DISPLAY1", new IntPtr(10));

        state.Observe(ProviderId.Antigravity, "DISPLAY2", new IntPtr(10));
        Assert.Equal(ProviderId.Antigravity, state.GetProvider("DISPLAY2"));

        state.Observe(ProviderId.Antigravity, "DISPLAY1", new IntPtr(10));

        Assert.Equal(ProviderId.Antigravity, state.GetProvider("DISPLAY1"));
        Assert.Equal(ProviderId.OpenCode, state.GetProvider("DISPLAY2"));
    }

    [Fact]
    public void ReclassifyingTheSameWindowDoesNotRetainItsOldProvider()
    {
        var state = new AdaptiveDisplayProviderState();
        var terminal = new IntPtr(10);
        state.Observe(ProviderId.Codex, "DISPLAY1", terminal);

        state.Observe(ProviderId.OpenCode, "DISPLAY1", terminal);
        state.Observe(ProviderId.OpenCode, "DISPLAY2", terminal);

        Assert.Null(state.GetProvider("DISPLAY1"));
        Assert.Equal(ProviderId.OpenCode, state.GetProvider("DISPLAY2"));
    }

    [Fact]
    public void ClosedOrMovedWindowNoLongerCountsAsActiveOnThatDisplay()
    {
        var state = new AdaptiveDisplayProviderState();
        state.Observe(ProviderId.Codex, "DISPLAY1", new IntPtr(10));

        var provider = state.GetProvider("DISPLAY1", window => window != new IntPtr(10));

        Assert.Null(provider);
        Assert.Empty(state.Providers);
    }

    [Fact]
    public void WindowLookupDoesNotBorrowTheLastActiveProviderForAnUnknownWindow()
    {
        var state = new AdaptiveDisplayProviderState();
        state.Observe(ProviderId.Codex, "DISPLAY1", new IntPtr(10));

        Assert.Equal(ProviderId.Codex, state.GetProviderForWindow(new IntPtr(10)));
        Assert.Null(state.GetProviderForWindow(new IntPtr(20)));
    }
}
