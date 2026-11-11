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
        => Assert.False(RecoveryStartup.WaitForParent([RecoveryStartup.ParentArgument, System.Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture)]));

    [Fact]
    public void AnAlreadyExitedPredecessorDoesNotBlockStartup()
    {
        using var child = StartWaitingProcess();
        child.StandardInput.Close();
        Assert.True(child.WaitForExit(5000));
        Assert.True(RecoveryStartup.WaitForParent(ArgumentsFor(child)));
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
    public void APredecessorThatRemainsAlivePreventsReplacementStartup()
    {
        using var child = StartWaitingProcess();
        try
        {
            Assert.False(RecoveryStartup.WaitForParent(ArgumentsFor(child)));
            Assert.False(child.HasExited);
        }
        finally
        {
            child.StandardInput.Close();
            Assert.True(child.WaitForExit(5000));
        }
    }

    private static string[] ArgumentsFor(System.Diagnostics.Process process)
        => [RecoveryStartup.ParentArgument, process.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)];

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
