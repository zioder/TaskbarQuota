using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

public static class LayoutTraceCollector
{
    const long MaximumBytes = 4_000_000;
    [StructLayout(LayoutKind.Explicit, Size = 176)] struct DebugEvent
    {
        [FieldOffset(0)] public uint Code;
        [FieldOffset(4)] public uint ProcessId;
        [FieldOffset(8)] public uint ThreadId;
        [FieldOffset(16)] public IntPtr FirstHandle;
        [FieldOffset(16)] public uint ExceptionCode;
        [FieldOffset(168)] public uint FirstChance;
        [FieldOffset(16)] public IntPtr StringAddress;
        [FieldOffset(24)] public ushort Unicode;
        [FieldOffset(26)] public ushort StringLength;
    }

    // Preserve only layout operations and structural identifiers. Never retain text, binding values,
    // application logs, exception messages, filenames, or account information from debug output.
    public static string Sanitize(string value)
    {
        value = value.TrimStart();
        if (!value.StartsWith("[LayoutCycleTracing]", StringComparison.Ordinal)) return "";
        value = value.Substring(20).Trim();
        if (Regex.IsMatch(value, @"^Layout Iteration Countdown:\s*\d+\.?(?: Launching (?:Measure|Arrange) Pass\.)?$"))
            return "[LayoutCycleTracing] " + value;
        var fields = new List<string>();
        int position = 0;
        while (position < value.Length)
        {
            while (position < value.Length && char.IsWhiteSpace(value[position])) position++;
            if (position >= value.Length || value[position++] != '"') return "";
            int start = position;
            while (position < value.Length && value[position] != '"') position++;
            if (position >= value.Length) return "";
            string field = value.Substring(start, position++ - start);
            if (!IsStructuralField(field)) return "";
            fields.Add(field);
            while (position < value.Length && char.IsWhiteSpace(value[position])) position++;
            if (position == value.Length) break;
            if (value[position++] != ',') return "";
        }
        return fields.Count == 0 ? "" : "[LayoutCycleTracing] " + string.Join(" | ", fields);
    }

    const string Number = @"(?:-?(?:\d+(?:\.\d*)?|\.\d+)(?:[eE][+-]?\d+)?|-?inf(?:inity)?|NaN)";
    static bool IsStructuralField(string field)
    {
        if (Regex.IsMatch(field, @"^(?:LayoutCycleCountdown:\s*\d+|TypeIndex:\s*\d+|ClassName:\s*(?:Microsoft\.UI\.|TaskbarQuota\.)[A-Za-z0-9_.]+|Name:\s*[A-Za-z_][A-Za-z0-9_]{0,80}|Instance:\s*0x[0-9A-Fa-f]+)$")) return true;
        if (Regex.IsMatch(field, @"^(?:Measure|Arrange)\(" + Number + @"(?:[,x\s]+" + Number + @")*\)$", RegexOptions.IgnoreCase)) return true;
        if (Regex.IsMatch(field, @"^DesiredSize changed, old: " + Number + "x" + Number + " new: " + Number + "x" + Number + "$", RegexOptions.IgnoreCase)) return true;
        if (Regex.IsMatch(field, @"^SetValue\((?:Width|Height|MinWidth|MaxWidth|MinHeight|MaxHeight)=(?:" + Number + @"\*?|Auto)\)$", RegexOptions.IgnoreCase)) return true;
        if (Regex.IsMatch(field, @"^SetValue\((?:Margin|Padding)=" + Number + @"(?:\s*,\s*" + Number + @"){3}\)$", RegexOptions.IgnoreCase)) return true;
        if (Regex.IsMatch(field, @"^SetValue\((?:Visibility=(?:0|1|Visible|Collapsed)|UseLayoutRounding=(?:0|1|True|False)|HorizontalAlignment=(?:[0-3]|Left|Center|Right|Stretch)|VerticalAlignment=(?:[0-3]|Top|Center|Bottom|Stretch))\)$")) return true;
        const string numericKeys = "DesiredSize|Old ScrollData|New ScrollData|Viewport|Extent|Offset|IsChildActualSizeUsedAsExtent|IsChildActualSizeUpdated|trackLength|repeatButtonsLength|minimum|maximum|value|viewport|mouseIndicatorSize|touchIndicatorSize|mouseMinSize|touchMinSize|actualSize";
        if (!Regex.IsMatch(field, @"^(?:" + numericKeys + @")\s*[:=]")) return false;
        string remaining = Regex.Replace(field, @"\b(?:" + numericKeys + @")\b(?=\s*[:=])", "");
        remaining = Regex.Replace(remaining, @"(?<![A-WYZa-wyz])" + Number + @"(?![A-WYZa-wyz])", "", RegexOptions.IgnoreCase);
        return Regex.IsMatch(remaining, @"^[\s,:=xX()]*$");
    }

    public static string Run(int targetId, string expectedExecutable, string outputFile, int maximumSeconds, string stopFile = null)
    {
        if (maximumSeconds < 1 || maximumSeconds > 1800) throw new ArgumentOutOfRangeException(nameof(maximumSeconds));
        if (!Environment.Is64BitProcess) throw new PlatformNotSupportedException("The capture ABI requires x64 PowerShell.");
        using var target = Process.GetProcessById(targetId);
        // Retain identity before inspecting modules: MainModule itself queries by PID.
        var readHandle = target.Handle;
        if (!string.Equals(target.MainModule.FileName, Path.GetFullPath(expectedExecutable), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The target isn't the explicitly selected executable.");
        // Acquire our independent read handle before attaching. Debug-event thread/process handles are
        // owned by ContinueDebugEvent and mustn't be closed or confused with this Process handle.
        using var stream = new FileStream(outputFile, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.NewLine = "\n";
        string result = "timeout";
        bool attached = false;
        long bytes = 0;
        int messages = 0;
        var watch = Stopwatch.StartNew();
        double? firstTrace = null;
        bool initialBreakpointHandled = false;
        bool WriteBounded(string line)
        {
            int size = Encoding.UTF8.GetByteCount(line) + 1;
            if (bytes + size > MaximumBytes) return false;
            writer.WriteLine(line);
            bytes += size;
            return true;
        }
        try
        {
            if (!DebugActiveProcess((uint)targetId)) throw new Win32Exception(Marshal.GetLastWin32Error());
            attached = true;
            if (!DebugSetProcessKillOnExit(false)) throw new Win32Exception(Marshal.GetLastWin32Error());
            WriteBounded("capture-start UTC=" + DateTime.UtcNow.ToString("O") + " pid=" + targetId);
            writer.Flush();
            while (watch.Elapsed.TotalSeconds < maximumSeconds)
            {
                if (stopFile != null && File.Exists(stopFile)) { result = "stopped"; break; }
                if (!WaitForDebugEventEx(out var e, 100))
                {
                    int waitError = Marshal.GetLastWin32Error();
                    if (target.HasExited) { result = "process-exited"; break; }
                    if (waitError != 121) { result = "wait-error"; throw new Win32Exception(waitError); }
                    if (firstTrace is { } first && watch.Elapsed.TotalSeconds - first > 10) { result = "layout-trace-captured"; break; }
                    continue;
                }
                uint continuation = 0x00010002;
                try
                {
                    if (e.Code == 8 && e.ProcessId == (uint)targetId)
                    {
                        int length = Math.Min(32768, (int)e.StringLength);
                        if (e.Unicode != 0) length &= ~1;
                        var data = new byte[length];
                        if (length > 0 && ReadProcessMemory(readHandle, e.StringAddress, data, (UIntPtr)length, out var read))
                        {
                            string text = (e.Unicode == 0 ? Encoding.Default : Encoding.Unicode).GetString(data, 0, Math.Min(length, (int)read));
                            int terminator = text.IndexOf('\0');
                            if (terminator >= 0) text = text.Substring(0, terminator);
                            foreach (var line in text.Split('\n'))
                            {
                                string safe = Sanitize(line);
                                if (safe.Length == 0) continue;
                                firstTrace ??= watch.Elapsed.TotalSeconds;
                                if (!WriteBounded(safe)) { result = "size-limit"; break; }
                                messages++;
                            }
                        }
                    }
                    else if (e.Code == 1)
                    {
                        // Only the debugger's initial breakpoint is handled; application exceptions
                        // retain their ordinary runtime handlers and crash/recovery behavior.
                        continuation = 0x80010001;
                        if (e.ExceptionCode == 0x80000003 && !initialBreakpointHandled)
                        {
                            continuation = 0x00010002;
                            initialBreakpointHandled = true;
                        }
                        if (e.ExceptionCode is 0x802B0014 or 0xC000027B or 0xC0000409)
                            if (!WriteBounded("native-exception=0x" + e.ExceptionCode.ToString("X8") + " first=" + e.FirstChance)) result = "size-limit";
                    }
                    else if (e.Code is 3 or 6)
                    {
                        if (e.FirstHandle != IntPtr.Zero && e.FirstHandle != new IntPtr(-1)) CloseHandle(e.FirstHandle);
                    }
                }
                finally
                {
                    if (!ContinueDebugEvent(e.ProcessId, e.ThreadId, continuation)) throw new Win32Exception(Marshal.GetLastWin32Error());
                }
                if (e.Code == 5) { result = "process-exited"; break; }
                if (result == "size-limit") break;
            }
        }
        finally
        {
            if (attached)
            {
                bool detached = DebugActiveProcessStop((uint)targetId);
                int detachError = Marshal.GetLastWin32Error();
                if (!detached && !target.HasExited) { result = "detach-error"; WriteBounded("detach-error=" + detachError); }
            }
            WriteBounded("capture-end status=" + result + " messages=" + messages + " bytes=" + bytes);
        }
        // No restart, termination, input, settings writes, or automatic reattachment to another PID.
        return result;
    }

    [DllImport("kernel32.dll", SetLastError = true)] static extern bool DebugActiveProcess(uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool DebugActiveProcessStop(uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool DebugSetProcessKillOnExit(bool kill);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool WaitForDebugEventEx(out DebugEvent value, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool ContinueDebugEvent(uint pid, uint tid, uint status);
    [DllImport("kernel32.dll")] static extern bool ReadProcessMemory(IntPtr process, IntPtr address, byte[] bytes, UIntPtr size, out UIntPtr read);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
}
