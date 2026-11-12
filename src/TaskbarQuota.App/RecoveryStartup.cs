using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using TaskbarQuota.Diagnostics;

namespace TaskbarQuota;

internal static class RecoveryStartup
{
    internal const string ParentArgument = "--recover-after-pid";
    internal const string ParentStartArgument = "--recover-after-start-ticks";
    internal const string AttemptArgument = "--recovery-attempt";
    private const int ParentExitTimeoutMilliseconds = 10000;

    internal sealed record Context(int ParentPid, long ParentStartUtcTicks, int Attempt);

    internal static Context? ReadContext(string[] arguments)
    {
        int? pid = ReadParentPid(arguments);
        var start = ReadValue(arguments, ParentStartArgument);
        var attempt = ReadValue(arguments, AttemptArgument);
        if (pid is null)
        {
            if (start is not null || attempt is not null) throw new ArgumentException("Incomplete recovery context.");
            return null;
        }
        if (!long.TryParse(start, NumberStyles.None, CultureInfo.InvariantCulture, out long ticks)
            || ticks <= 0 || ticks > DateTime.MaxValue.Ticks
            || !int.TryParse(attempt, NumberStyles.None, CultureInfo.InvariantCulture, out int count)
            || count < 1 || count > RecoveryRestartBudget.MaximumAttempts)
            throw new ArgumentException("Invalid recovery identity or attempt count.");
        return new Context(pid.Value, ticks, count);
    }

    private static string? ReadValue(string[] arguments, string name)
    {
        string? value = null;
        for (int index = 0; index < arguments.Length; index++)
            if (string.Equals(arguments[index], name, StringComparison.Ordinal))
            {
                if (value is not null || ++index >= arguments.Length) throw new ArgumentException("Invalid recovery arguments.");
                value = arguments[index];
            }
        return value;
    }

    internal static string CreateArguments(string startupArgument, Context context)
        => string.Join(" ", startupArgument, ParentArgument, context.ParentPid.ToString(CultureInfo.InvariantCulture),
            ParentStartArgument, context.ParentStartUtcTicks.ToString(CultureInfo.InvariantCulture),
            AttemptArgument, context.Attempt.ToString(CultureInfo.InvariantCulture));

    internal static int? ReadParentPid(string[] arguments)
    {
        int? parent = null;
        for (int index = 0; index < arguments.Length; index++)
        {
            if (!string.Equals(arguments[index], ParentArgument, StringComparison.Ordinal))
                continue;
            if (parent.HasValue || ++index >= arguments.Length
                || !int.TryParse(arguments[index], NumberStyles.None, CultureInfo.InvariantCulture, out int pid)
                || pid <= 0)
                throw new ArgumentException("Invalid recovery predecessor PID.");
            parent = pid;
        }
        return parent;
    }

    // Wait before registering the single-instance key. The predecessor keeps ownership until exit,
    // so the replacement cannot redirect into a broken UI or release a key from the wrong thread.
    internal static bool WaitForParent(string[] arguments, int checkpointMilliseconds = ParentExitTimeoutMilliseconds)
    {
        try
        {
            if (ReadContext(arguments) is not { } context)
                return true;
            int parentPid = context.ParentPid;
            if (parentPid == Environment.ProcessId)
                throw new ArgumentException("A recovery process cannot wait for itself.");
            try
            {
                using var parent = Process.GetProcessById(parentPid);
                _ = parent.Handle;
                if (parent.StartTime.ToUniversalTime().Ticks != context.ParentStartUtcTicks)
                {
                    Log.Information("Recovery predecessor identity no longer exists; continuing startup.");
                    return true;
                }
                if (!parent.WaitForExit(checkpointMilliseconds))
                    Log.Warning($"Recovery predecessor pid {parentPid} is still exiting; replacement will keep waiting.");
                parent.WaitForExit();
            }
            catch (ArgumentException)
            {
                // The predecessor can exit before the replacement opens its process handle.
            }
            catch (InvalidOperationException)
            {
                // It can also exit between GetProcessById and WaitForExit opening the handle.
            }
            Log.Information($"Recovery predecessor pid {parentPid} exited; continuing startup.");
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        {
            Log.Error(ex, "Could not wait for recovery predecessor");
            return false;
        }
    }
}
