namespace TaskbarQuota.Tests;

public class RecoveryStartupTests
{
    [Fact]
    public void OrdinaryLaunchDoesNotWaitForAnotherProcess()
        => Assert.Null(RecoveryStartup.ReadParentPid(["--startup-widget"]));

    [Fact]
    public void RecoveryLaunchRetainsThePredecessorPid()
        => Assert.Equal(123, RecoveryStartup.ReadParentPid(["--startup-widget", RecoveryStartup.ParentArgument, "123"]));

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("2147483648")]
    [InlineData("not-a-pid")]
    public void InvalidPredecessorCannotEnterStartup(string value)
        => Assert.Throws<System.ArgumentException>(() => RecoveryStartup.ReadParentPid([RecoveryStartup.ParentArgument, value]));

    [Fact]
    public void MissingPredecessorIsRejected()
        => Assert.Throws<System.ArgumentException>(() => RecoveryStartup.ReadParentPid([RecoveryStartup.ParentArgument]));

    [Fact]
    public void ConflictingPredecessorsAreRejected()
        => Assert.Throws<System.ArgumentException>(() => RecoveryStartup.ReadParentPid([RecoveryStartup.ParentArgument, "123", RecoveryStartup.ParentArgument, "456"]));

    [Fact]
    public void RecoveryCannotWaitForItself()
    {
        using var self = System.Diagnostics.Process.GetCurrentProcess();
        Assert.False(RecoveryStartup.WaitForParent(ArgumentsFor(self)));
    }

    [Fact]
    public void AnAlreadyExitedPredecessorDoesNotBlockStartup()
    {
        using var child = StartWaitingProcess();
        var arguments = ArgumentsFor(child);
        child.StandardInput.Close();
        Assert.True(child.WaitForExit(5000));
        Assert.True(RecoveryStartup.WaitForParent(arguments));
    }

    [Fact]
    public async System.Threading.Tasks.Task WaitsForRunningPredecessorBeforeContinuing()
    {
        using var child = StartWaitingProcess();
        try
        {
            Assert.False(child.HasExited);
            var release = System.Threading.Tasks.Task.Run(async () =>
            {
                await System.Threading.Tasks.Task.Delay(250);
                child.StandardInput.Close();
            });
            Assert.True(RecoveryStartup.WaitForParent(ArgumentsFor(child)));
            Assert.True(child.HasExited);
            await release;
        }
        finally
        {
            child.StandardInput.Close();
            Assert.True(child.WaitForExit(5000));
        }
    }

    [Fact]
    public async Task SlowPredecessorKeepsReplacementWaitingPastTheInitialCheckpoint()
    {
        using var child = StartWaitingProcess();
        try
        {
            var arguments = ArgumentsFor(child);
            var waiting = Task.Run(() => RecoveryStartup.WaitForParent(arguments, checkpointMilliseconds: 10));
            await Task.Delay(100);
            Assert.False(waiting.IsCompleted);
            Assert.False(child.HasExited);
            child.StandardInput.Close();
            Assert.True(await waiting.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            child.StandardInput.Close();
            Assert.True(child.WaitForExit(5000));
        }
    }

    private static string[] ArgumentsFor(System.Diagnostics.Process process)
        => RecoveryStartup.CreateArguments("--startup-widget",
            new RecoveryStartup.Context(process.Id, process.StartTime.ToUniversalTime().Ticks, 1)).Split(' ');

    [Fact]
    public async Task ReusedPidWithDifferentStartTimeIsNotWaitedOn()
    {
        using var child = StartWaitingProcess();
        try
        {
            var arguments = RecoveryStartup.CreateArguments("--startup-widget",
                new RecoveryStartup.Context(child.Id, child.StartTime.ToUniversalTime().Ticks - 1, 1)).Split(' ');
            var waiting = Task.Run(() => RecoveryStartup.WaitForParent(arguments));
            Assert.True(await waiting.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.False(child.HasExited);
        }
        finally
        {
            child.StandardInput.Close();
            Assert.True(child.WaitForExit(5000));
        }
    }

    [Theory]
    [InlineData("0", "1")]
    [InlineData("-1", "1")]
    [InlineData("3155378976000000000", "1")]
    [InlineData("1", "0")]
    [InlineData("1", "4")]
    [InlineData("1", "bad")]
    public void InvalidRecoveryContextIsRejected(string start, string attempt)
        => Assert.Throws<ArgumentException>(() => RecoveryStartup.ReadContext([
            RecoveryStartup.ParentArgument, "123", RecoveryStartup.ParentStartArgument, start, RecoveryStartup.AttemptArgument, attempt]));

    [Fact]
    public void IncompleteOrDuplicateRecoveryContextIsRejected()
    {
        Assert.Throws<ArgumentException>(() => RecoveryStartup.ReadContext([RecoveryStartup.ParentArgument, "123"]));
        Assert.Throws<ArgumentException>(() => RecoveryStartup.ReadContext([RecoveryStartup.AttemptArgument, "1"]));
        Assert.Throws<ArgumentException>(() => RecoveryStartup.ReadContext([RecoveryStartup.ParentArgument, "123",
            RecoveryStartup.ParentStartArgument, "1", RecoveryStartup.AttemptArgument, "1", RecoveryStartup.AttemptArgument, "2"]));
    }

    [Fact]
    public void RecoveryContextRoundTripsThroughBothLauncherArguments()
    {
        var context = new RecoveryStartup.Context(123, DateTime.UtcNow.Ticks, 3);
        Assert.Equal(context, RecoveryStartup.ReadContext(RecoveryStartup.CreateArguments("--startup-widget", context).Split(' ')));
        Assert.Null(RecoveryStartup.ReadContext(["--startup-widget"]));
    }

    private static System.Diagnostics.Process StartWaitingProcess()
        => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
            System.IO.Path.Combine(System.Environment.SystemDirectory, "cmd.exe"), "/d /c set /p TASKBARQUOTA_RECOVERY_TEST=")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
            RedirectStandardInput = true,
        })!;
}
