using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using TaskbarQuota.Diagnostics;

namespace TaskbarQuota;

internal static class RecoveryStartup
{
    internal const string ParentArgument = "--recover-after-pid";
    private const int ParentExitTimeoutMilliseconds = 10000;

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
    internal static bool WaitForParent(string[] arguments)
    {
        try
        {
            if (ReadParentPid(arguments) is not { } parentPid)
                return true;
            if (parentPid == Environment.ProcessId)
                throw new ArgumentException("A recovery process cannot wait for itself.");
            try
            {
                using var parent = Process.GetProcessById(parentPid);
                if (!parent.WaitForExit(ParentExitTimeoutMilliseconds))
                {
                    Log.Error($"Recovery predecessor pid {parentPid} did not exit within 10 seconds.");
                    return false;
                }
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
