using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using global::Interop.UIAutomationClient;

namespace TaskbarQuota.ActiveApp
{
    internal static class BrowserUiaTextReader
    {
        private static readonly CUIAutomation Automation = new();

        private const int UIA_TextControlTypeId = 50020;
        private const int UIA_DocumentControlTypeId = 50030;
        private const int UIA_EditControlTypeId = 50004;
        private const int UIA_PaneControlTypeId = 50033;
        private const int UIA_HyperlinkControlTypeId = 50005;
        private const int UIA_ButtonControlTypeId = 50000;

        public static string? TryRead(string urlFragment)
        {
            if (string.IsNullOrWhiteSpace(urlFragment))
                return null;

            foreach (var hwnd in EnumerateVisibleTopLevelWindows())
            {
                if (!IsBrowserWindow(hwnd))
                    continue;
                if (!WindowMentions(hwnd, urlFragment))
                    continue;
                var text = TryReadWindow(hwnd);
                if (!string.IsNullOrWhiteSpace(text))
                    return text;
            }
            return null;
        }

        public static string? TryReadAnyBrowser()
        {
            foreach (var hwnd in EnumerateVisibleTopLevelWindows())
            {
                if (!IsBrowserWindow(hwnd))
                    continue;
                var text = TryReadWindow(hwnd);
                if (!string.IsNullOrWhiteSpace(text))
                    return text;
            }
            return null;
        }

        private static string? TryReadWindow(IntPtr hwnd)
        {
            try
            {
                var root = Automation.ElementFromHandle(hwnd);
                if (root is null)
                    return null;

                var names = new List<string>(128);
                CollectText(root, names, depth: 0);
                if (names.Count == 0)
                    return null;

                var sb = new StringBuilder(capacity: names.Count * 32);
                foreach (var n in names)
                    sb.Append(n).Append('\n');
                return sb.ToString();
            }
            catch
            {
                return null;
            }
        }

        private static void CollectText(IUIAutomationElement element, List<string> sink, int depth)
        {
            if (depth > 64 || element is null)
                return;

            IUIAutomationElementArray? children;
            try
            {
                children = element.FindAll(
                    TreeScope.TreeScope_Children,
                    Automation.CreateTrueCondition());
            }
            catch
            {
                return;
            }
            if (children is null)
                return;

            int count;
            try { count = children.Length; }
            catch { return; }

            for (int i = 0; i < count; i++)
            {
                IUIAutomationElement? child = null;
                try { child = children.GetElement(i); }
                catch { continue; }
                if (child is null)
                    continue;

                int controlType = SafeControlType(child);
                string? name = SafeName(child);

                if (!string.IsNullOrWhiteSpace(name) && IsTextBearing(controlType))
                {
                    sink.Add(name.Trim());
                }

                if (controlType == UIA_PaneControlTypeId ||
                    controlType == UIA_DocumentControlTypeId)
                {
                    CollectText(child, sink, depth + 1);
                }
            }
        }

        private static bool IsTextBearing(int controlType) => controlType is
            UIA_TextControlTypeId or
            UIA_DocumentControlTypeId or
            UIA_EditControlTypeId or
            UIA_HyperlinkControlTypeId or
            UIA_ButtonControlTypeId;

        private static int SafeControlType(IUIAutomationElement el)
        {
            try { return el.CurrentControlType; }
            catch { return 0; }
        }

        private static string? SafeName(IUIAutomationElement el)
        {
            try { return el.CurrentName; }
            catch { return null; }
        }

        private static bool IsBrowserWindow(IntPtr hwnd)
        {
            var className = GetClassName(hwnd);
            return className.StartsWith("Chrome_WidgetWin_", StringComparison.Ordinal)
                || className.StartsWith("Mozilla", StringComparison.Ordinal);
        }

        private static bool WindowMentions(IntPtr hwnd, string fragment)
        {
            var title = GetWindowText(hwnd);
            if (title.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0)
                return true;

            var urlBar = TryReadAddressBar(hwnd);
            if (urlBar is not null && urlBar.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0)
                return true;

            return false;
        }

        private static string? TryReadAddressBar(IntPtr hwnd)
        {
            try
            {
                var root = Automation.ElementFromHandle(hwnd);
                if (root is null)
                    return null;

                var editCondition = Automation.CreatePropertyCondition(
                    UIA_PropertyIds.UIA_ControlTypePropertyId,
                    UIA_ControlTypeIds.UIA_EditControlTypeId);

                IUIAutomationElementArray? edits;
                try
                {
                    edits = root.FindAll(TreeScope.TreeScope_Descendants, editCondition);
                }
                catch
                {
                    return null;
                }
                if (edits is null)
                    return null;

                int count;
                try { count = edits.Length; }
                catch { return null; }

                for (int i = 0; i < count; i++)
                {
                    IUIAutomationElement? edit = null;
                    try { edit = edits.GetElement(i); }
                    catch { continue; }
                    if (edit is null)
                        continue;

                    string? value = null;
                    try { value = edit.CurrentName; } catch { }
                    if (!string.IsNullOrWhiteSpace(value) &&
                        (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                         value.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
                    {
                        return value;
                    }
                }
            }
            catch
            {
            }
            return null;
        }

        private static IEnumerable<IntPtr> EnumerateVisibleTopLevelWindows()
        {
            var result = new List<IntPtr>(8);
            EnumWindows((hwnd, _) =>
            {
                if (!IsWindowVisible(hwnd))
                    return true;
                if (GetWindow(hwnd, GW_OWNER) != IntPtr.Zero)
                    return true;
                if (IsIconic(hwnd))
                    return true;
                result.Add(hwnd);
                return true;
            }, IntPtr.Zero);
            return result;
        }

        private static string GetClassName(IntPtr hwnd)
        {
            var sb = new StringBuilder(256);
            _ = GetClassNameNative(hwnd, sb, sb.Capacity);
            return sb.ToString();
        }

        private static string GetWindowText(IntPtr hwnd)
        {
            var sb = new StringBuilder(512);
            _ = NativeGetWindowText(hwnd, sb, sb.Capacity);
            return sb.ToString();
        }

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        private const int GW_OWNER = 4;

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc enumProc, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int GetClassNameNative(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll", EntryPoint = "GetWindowTextW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int NativeGetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll")]
        private static extern IntPtr GetWindow(IntPtr hWnd, int uCmd);
    }
}
