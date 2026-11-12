using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using TaskbarQuota.ActiveApp;
using TaskbarQuota.AgentActivity;

namespace TaskbarQuota.Tests;

/// <summary>
/// Differential checks against the live machine: the snapshot-based scans must see exactly what the
/// WMI / Process.GetProcesses scans they replaced saw. A few pids may differ because processes start or
/// exit between the two reads, so differences are tolerated up to a small bound.
/// </summary>
public class ProcessScanEquivalenceTests
{
    private const int RaceTolerance = 4;

    private static string Quote(IEnumerable<string> names)
        => string.Join(" OR ", names.Select(n => $"Name = '{n}'"));

    [Fact]
    public void BulkScan_MatchesWmiPidsParentsNamesAndStartTimes()
    {
        var old = new Dictionary<int, (int Parent, string Name, DateTime Started)>();
        using (var searcher = new ManagementObjectSearcher(
            "SELECT ProcessId, ParentProcessId, Name, CreationDate FROM Win32_Process WHERE "
            + Quote(ActiveAppDetector.BulkProcessNames)))
        using (var results = searcher.Get())
        {
            foreach (ManagementObject mo in results)
            {
                using (mo)
                {
                    var pid = Convert.ToInt32(mo["ProcessId"]);
                    var started = ManagementDateTimeConverter.ToDateTime((string)mo["CreationDate"]);
                    old[pid] = (Convert.ToInt32(mo["ParentProcessId"]), (string)mo["Name"], started);
                }
            }
        }

        var snapshot = ProcessSnapshot.Capture()
            .Where(e => ActiveAppDetector.BulkProcessNames.Contains(e.ExeName))
            .ToDictionary(e => e.Pid);

        var onlyOld = old.Keys.Except(snapshot.Keys).ToList();
        var onlyNew = snapshot.Keys.Except(old.Keys).ToList();
        Assert.True(onlyOld.Count + onlyNew.Count <= RaceTolerance,
            $"pid sets differ: onlyWmi=[{string.Join(",", onlyOld)}] onlySnapshot=[{string.Join(",", onlyNew)}]");

        var startMismatch = new List<string>();
        foreach (var pid in old.Keys.Intersect(snapshot.Keys))
        {
            Assert.Equal(old[pid].Parent, snapshot[pid].ParentPid);
            Assert.Equal(old[pid].Name, snapshot[pid].ExeName, ignoreCase: true);
            var started = ProcessSnapshot.GetStartTime(pid, snapshot[pid].ParentPid);
            if (started == DateTime.MinValue || Math.Abs((started - old[pid].Started).TotalSeconds) > 1)
                startMismatch.Add($"{pid} {old[pid].Name}: wmi={old[pid].Started:O} snapshot={started:O}");
        }
        Assert.True(startMismatch.Count == 0, "start times differ from WMI CreationDate: " + string.Join("; ", startMismatch));
    }

    [Fact]
    public void CommandLines_MatchWmiForTerminalScanProcesses()
    {
        using var child = Process.Start(new ProcessStartInfo(
            Path.Combine(Environment.SystemDirectory, "cmd.exe"), "/d /c set /p TASKBARQUOTA_PROCESS_SCAN_TEST=")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardInput = true,
        })!;
        try
        {
            var wanted = ProcessSnapshot.Capture()
                .Where(e => AgentActivityScanner.TerminalScanProcessNames.Contains(e.ExeName))
                .ToList();
            // Capture shares a 200 ms cache with other scans, so a just-created child may not be listed
            // yet. Seed its known identity to guarantee a non-vacuous command-line/cache comparison.
            if (wanted.All(e => e.Pid != child.Id))
                wanted.Add(new ProcessEntry(child.Id, Environment.ProcessId, "cmd.exe", child.StartTime.ToFileTimeUtc()));

            var fresh = ProcessSnapshot.GetCommandLines(wanted.Select(e => (e.Pid, e.ParentPid)));
            // Second call must be served from the cache and still be identical.
            var cached = ProcessSnapshot.GetCommandLines(wanted.Select(e => (e.Pid, e.ParentPid)));

            var old = new Dictionary<int, (int Parent, long Created, string Name, string CommandLine)>();
            using (var searcher = new ManagementObjectSearcher(
                "SELECT ProcessId, ParentProcessId, CreationDate, Name, CommandLine FROM Win32_Process WHERE " + Quote(AgentActivityScanner.TerminalScanProcessNames)))
            using (var results = searcher.Get())
            {
                foreach (ManagementObject mo in results)
                {
                    using (mo)
                    {
                        var created = ManagementDateTimeConverter.ToDateTime((string)mo["CreationDate"]).ToFileTimeUtc();
                        old[Convert.ToInt32(mo["ProcessId"])] = (Convert.ToInt32(mo["ParentProcessId"]), created,
                            (string)mo["Name"], mo["CommandLine"] as string ?? "");
                    }
                }
            }

            // Only compare identities alive across both observations. WMI timestamps have microsecond
            // precision while the NT snapshot uses 100 ns ticks. PID alone can't exclude PID reuse.
            var stable = wanted.Where(e => old.TryGetValue(e.Pid, out var final)
                && e.ParentPid == final.Parent && e.CreateFileTime > 0
                && Math.Abs(e.CreateFileTime - final.Created) <= 10).ToArray();
            Assert.Contains(stable, e => e.Pid == child.Id);
            foreach (var e in stable)
            {
                var expected = old[e.Pid];
                Assert.True(fresh.TryGetValue(e.Pid, out var actual), $"stable pid {e.Pid} missing from fresh command lines");
                Assert.True(cached.TryGetValue(e.Pid, out var again), $"stable pid {e.Pid} missing from cached command lines");
                // Don't put command-line contents in assertion output: they may contain credentials.
                Assert.True(string.Equals(expected.Name, actual.Name, StringComparison.OrdinalIgnoreCase)
                    && expected.CommandLine == actual.CommandLine, $"stable pid {e.Pid} command line differs from WMI");
                Assert.True(again == actual, $"stable pid {e.Pid} cached command line differs");
            }
        }
        finally
        {
            child.StandardInput.Close();
            if (!child.WaitForExit(5000))
            {
                child.Kill(entireProcessTree: true);
                Assert.True(child.WaitForExit(5000), "owned command-line fixture didn't exit");
            }
        }
    }

    [Fact]
    public void ProcessNames_MatchManagedProcessList()
    {
        var managed = new Dictionary<int, string>();
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try { managed[p.Id] = p.ProcessName; } catch { }
            }
        }

        var snapshot = ProcessSnapshot.Capture()
            .ToDictionary(e => e.Pid, e => e.ExeName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? e.ExeName[..^4] : e.ExeName);

        var bad = new List<string>();
        foreach (var (pid, name) in managed)
        {
            if (pid == 0 || !snapshot.TryGetValue(pid, out var other)) continue;
            if (!string.Equals(name, other, StringComparison.OrdinalIgnoreCase))
                bad.Add($"{pid}: managed='{name}' snapshot='{other}'");
        }
        Assert.True(bad.Count <= RaceTolerance, string.Join("; ", bad));

        var missing = managed.Keys.Except(snapshot.Keys).Count() + snapshot.Keys.Except(managed.Keys).Count(k => k != 0);
        Assert.True(missing <= RaceTolerance * 2, $"{missing} pids present in only one list");
    }

    [Fact]
    public void TryGetProcessName_MatchesManagedNameForEveryRunningProcess()
    {
        var bad = new List<string>();
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                string expected;
                try { expected = p.ProcessName; } catch { continue; }
                if (p.Id == 0) continue;
                var actual = ProcessSnapshot.TryGetProcessName(p.Id);
                if (actual is null) continue; // exited meanwhile
                if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
                    bad.Add($"{p.Id}: managed='{expected}' fast='{actual}'");
            }
        }
        Assert.True(bad.Count <= RaceTolerance, string.Join("; ", bad));
    }
}
