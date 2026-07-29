using System;
using System.Collections.Generic;
using System.Diagnostics;
using global::Interop.UIAutomationClient;
using static TaskbarQuota.Interop.User32;

namespace TaskbarQuota.ActiveApp
{
    /// <summary>
    /// Detects a running Codex Desktop turn from the app's accessibility tree. The long-lived
    /// <c>codex.exe app-server</c> process is deliberately not enough: it remains present while the
    /// application is idle. Codex exposes a Stop button for the selected running turn and a distinct
    /// status glyph in every background task row that is still running.
    ///
    /// Only control names and CSS class tokens needed to identify those controls are read. Thread
    /// titles, prompts, responses and command text are never collected.
    /// </summary>
    internal sealed class CodexDesktopActivityDetector
    {
        private const string CodexDocumentName = "Codex";
        private const string RootDocumentAutomationId = "RootWebArea";
        private const string TaskRowClassToken = "cursor-grab";
        private const string TaskRowDraggingClassToken = "active:cursor-grabbing";
        private const string RunningStatusSizeClassToken = "icon-xs";
        private const string RunningStatusLayoutClassToken = "shrink-0";
        private const string CompletedStatusClassToken = "no-drag";
        private const string ComposerButtonClassToken = "size-token-button-composer";
        private static readonly TimeSpan FailureLogInterval = TimeSpan.FromMinutes(1);

        private IUIAutomation? _automation;
        private readonly object _failureLogGate = new();
        private DateTime _lastFailureLogAtUtc = DateTime.MinValue;

        internal bool HasRunningTurn()
        {
            int candidateCount = 0;
            try
            {
                var processIds = GetCandidateProcessIds();
                candidateCount = processIds.Count;
                if (processIds.Count == 0)
                    return false;

                _automation ??= new CUIAutomation();
                bool running = false;
                EnumWindows((hwnd, _) =>
                {
                    GetWindowThreadProcessId(hwnd, out uint processId);
                    if (!processIds.Contains(processId))
                        return true;

                    if (!WindowHasRunningTurn(hwnd))
                        return true;

                    running = true;
                    return false;
                }, IntPtr.Zero);
                return running;
            }
            catch (Exception ex)
            {
                bool shouldLog;
                var now = DateTime.UtcNow;
                lock (_failureLogGate)
                {
                    shouldLog = ShouldLogProbeFailure(now, _lastFailureLogAtUtc);
                    if (shouldLog)
                        _lastFailureLogAtUtc = now;
                }
                if (shouldLog)
                {
                    Diagnostics.Log.Debug(
                        $"[codex-activity] running-turn probe failed candidates={candidateCount}: {ex.Message}");
                }
                return false;
            }
        }

        internal static bool ShouldLogProbeFailure(DateTime now, DateTime lastLoggedAt)
            => now - lastLoggedAt >= FailureLogInterval;

        private bool WindowHasRunningTurn(IntPtr hwnd)
        {
            var automation = _automation;
            if (automation is null)
                return false;

            IUIAutomationElement root;
            try { root = automation.ElementFromHandle(hwnd); }
            catch { return false; }
            if (root is null)
                return false;

            IUIAutomationElement? document;
            try
            {
                var rootDocumentCondition = automation.CreatePropertyCondition(
                    UIA_PropertyIds.UIA_AutomationIdPropertyId,
                    RootDocumentAutomationId);
                document = root.FindFirst(TreeScope.TreeScope_Descendants, rootDocumentCondition);
            }
            catch
            {
                return false;
            }

            if (document is null)
                return false;

            try
            {
                if (document.CurrentControlType != UIA_ControlTypeIds.UIA_DocumentControlTypeId
                    || !string.Equals(document.CurrentName, CodexDocumentName, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }
            catch
            {
                return false;
            }

            IUIAutomationElementArray buttons;
            try
            {
                var buttonCondition = automation.CreatePropertyCondition(
                    UIA_PropertyIds.UIA_ControlTypePropertyId,
                    UIA_ControlTypeIds.UIA_ButtonControlTypeId);
                buttons = document.FindAll(TreeScope.TreeScope_Descendants, buttonCondition);
            }
            catch
            {
                return false;
            }
            if (buttons is null)
                return false;

            int count;
            try { count = buttons.Length; }
            catch { return false; }

            for (int index = 0; index < count; index++)
            {
                IUIAutomationElement button;
                try { button = buttons.GetElement(index); }
                catch { continue; }
                if (button is null)
                    continue;

                string? name;
                string? className;
                try
                {
                    name = button.CurrentName;
                    className = button.CurrentClassName;
                }
                catch
                {
                    continue;
                }

                if (IsRunningComposerButton(name, className))
                    return true;

                if (IsTaskRowClass(className) && TaskRowHasRunningStatus(button, automation))
                    return true;
            }

            return false;
        }

        private static bool TaskRowHasRunningStatus(
            IUIAutomationElement taskRow,
            IUIAutomation automation)
        {
            IUIAutomationElementArray images;
            try
            {
                var imageCondition = automation.CreatePropertyCondition(
                    UIA_PropertyIds.UIA_ControlTypePropertyId,
                    UIA_ControlTypeIds.UIA_ImageControlTypeId);
                images = taskRow.FindAll(TreeScope.TreeScope_Descendants, imageCondition);
            }
            catch
            {
                return false;
            }
            if (images is null)
                return false;

            int count;
            try { count = images.Length; }
            catch { return false; }

            for (int index = 0; index < count; index++)
            {
                try
                {
                    var image = images.GetElement(index);
                    if (image is not null && IsRunningTaskStatusClass(image.CurrentClassName))
                        return true;
                }
                catch
                {
                }
            }

            return false;
        }

        private static HashSet<uint> GetCandidateProcessIds()
        {
            var result = new HashSet<uint>();
            foreach (var processName in new[] { "ChatGPT", "Codex" })
            {
                Process[] processes;
                try { processes = Process.GetProcessesByName(processName); }
                catch { continue; }

                foreach (var process in processes)
                {
                    using (process)
                    {
                        try { result.Add((uint)process.Id); }
                        catch
                        {
                        }
                    }
                }
            }

            return result;
        }

        internal static bool IsTaskRowClass(string? className)
            => HasClassToken(className, TaskRowClassToken)
                && HasClassToken(className, TaskRowDraggingClassToken);

        internal static bool IsRunningTaskStatusClass(string? className)
            => HasClassToken(className, RunningStatusSizeClassToken)
                && HasClassToken(className, RunningStatusLayoutClassToken)
                && !HasClassToken(className, CompletedStatusClassToken);

        internal static bool IsRunningComposerButton(string? name, string? className)
        {
            if (!HasClassToken(className, ComposerButtonClassToken)
                || string.IsNullOrWhiteSpace(name))
            {
                return false;
            }

            string normalized = name.Trim().ToLowerInvariant();
            return normalized is "stop" or "detener" or "interrupt" or "interrumpir"
                || normalized.StartsWith("stop ", StringComparison.Ordinal)
                || normalized.StartsWith("detener ", StringComparison.Ordinal);
        }

        private static bool HasClassToken(string? className, string token)
        {
            if (string.IsNullOrWhiteSpace(className))
                return false;

            foreach (string candidate in className.Split(
                (char[]?)null,
                StringSplitOptions.RemoveEmptyEntries))
            {
                if (candidate.Equals(token, StringComparison.Ordinal))
                    return true;
            }

            return false;
        }
    }
}
