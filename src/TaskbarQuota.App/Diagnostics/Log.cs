using System;
using System.Diagnostics;
using System.IO;

namespace TaskbarQuota.Diagnostics
{
    /// <summary>Tiny logging shim so ported code can call Log.X without pulling Serilog.</summary>
    public static class Log
    {
        private static readonly string LogPath =
            Path.Combine(Path.GetTempPath(), "taskbarquota.log");
        private static readonly object FileLock = new();
        public static void Information(string message) => Write("INFO", message);
        public static void Debug(string message) => Write("DEBUG", message);
        public static void Warning(string message) => Write("WARN", message);
        public static void Warning(Exception ex, string message) => Write("WARN", $"{message} :: {ex}");
        public static void Error(Exception ex, string message) => Write("ERROR", $"{message} :: {ex}");
        public static void Error(string message) => Write("ERROR", message);

        private static void Write(string level, string message)
        {
            // PID tag: several instances (installed + dev build) routinely run side by side and all append
            // to this one path, which made interleaved logs unreadable during the issue #70 investigation.
            var line = $"[{DateTime.Now:HH:mm:ss.fff}] [{level}] [pid {Environment.ProcessId}] {message}";
            Trace.WriteLine(line);
            try { lock (FileLock) File.AppendAllText(LogPath, line + Environment.NewLine); } catch { }
        }
    }
}
