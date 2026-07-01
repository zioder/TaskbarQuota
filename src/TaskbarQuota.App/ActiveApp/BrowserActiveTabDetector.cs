using System;
using System.Collections.Generic;
using global::Interop.UIAutomationClient;
using TaskbarQuota.Usage;

namespace TaskbarQuota.ActiveApp
{
    internal sealed class BrowserActiveTabDetector
    {
        private static readonly HashSet<string> BrowserProcesses = new(StringComparer.OrdinalIgnoreCase)
        {
            "chrome",
            "msedge",
            "arc",
            "firefox",
            "zen",
            "brave",
            "brave-browser",
            "vivaldi",
            "opera",
            "opera gx",
            "operagx",
            "chromium",
        };

        private static readonly string[] AddressNameHints =
        [
            "address",
            "search",
            "adresse",
            "url",
        ];

        private static readonly TimeSpan CacheTtl = TimeSpan.FromMilliseconds(350);

        private IUIAutomation? _automation;
        private IntPtr _cachedHwnd;
        private string? _cachedUrl;
        private DateTime _cachedAtUtc = DateTime.MinValue;

        internal static bool IsBrowserProcessName(string? processName)
            => !string.IsNullOrWhiteSpace(processName)
            && BrowserProcesses.Contains(NormalizeProcessName(processName));

        internal ProviderId? DetectProvider(IntPtr hwnd, string? processName, string? windowTitle = null)
            => Detect(hwnd, processName, windowTitle)?.Provider;

        internal BrowserProviderDetection? Detect(IntPtr hwnd, string? processName, string? windowTitle = null)
        {
            if (!IsBrowserProcessName(processName))
                return null;

            var url = TryReadActiveTabUrl(hwnd);
            var provider = TryResolveProviderFromUrl(url)
                ?? TryResolveProviderFromUrl(FirefoxSessionStoreReader.TryReadSelectedTabUrl(processName))
                ?? TryResolveProviderFromTitle(windowTitle);

            return provider is { } p
                ? new BrowserProviderDetection(p, ResolveBrowserSource(processName))
                : null;
        }

        internal string? TryReadActiveTabUrl(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero)
                return null;

            var now = DateTime.UtcNow;
            if (hwnd == _cachedHwnd && now - _cachedAtUtc < CacheTtl)
                return _cachedUrl;

            var url = TryReadActiveTabUrlCore(hwnd);
            _cachedHwnd = hwnd;
            _cachedUrl = url;
            _cachedAtUtc = now;
            return url;
        }

        internal static ProviderId? TryResolveProviderFromUrl(string? rawUrl)
        {
            if (string.IsNullOrWhiteSpace(rawUrl))
                return null;

            var normalized = NormalizeUrl(rawUrl);
            if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri))
                return null;

            if (uri.Scheme is not ("http" or "https"))
                return null;

            var host = uri.Host.ToLowerInvariant();
            if (host == "claude.ai" || host.EndsWith(".claude.ai", StringComparison.Ordinal))
                return ProviderId.Claude;

            return null;
        }

        internal static ProviderId? TryResolveProviderFromTitle(string? windowTitle)
        {
            if (string.IsNullOrWhiteSpace(windowTitle))
                return null;

            var title = windowTitle.ToLowerInvariant();
            if (title.Contains("claude", StringComparison.Ordinal))
                return ProviderId.Claude;

            return null;
        }

        internal static ProviderSource ResolveBrowserSource(string? processName)
        {
            var normalized = string.IsNullOrWhiteSpace(processName)
                ? string.Empty
                : NormalizeProcessName(processName);
            var name = normalized.ToLowerInvariant() switch
            {
                "chrome" or "chromium" => "Chrome",
                "msedge" => "Edge",
                "firefox" => "Firefox",
                "zen" => "Zen",
                "brave" or "brave-browser" => "Brave",
                "arc" => "Arc",
                "vivaldi" => "Vivaldi",
                "opera" or "opera gx" or "operagx" => "Opera",
                _ => "browser",
            };
            return new ProviderSource(ProviderSourceKind.Browser, name, "browser");
        }

        private string? TryReadActiveTabUrlCore(IntPtr hwnd)
        {
            try
            {
                _automation ??= new CUIAutomation();
                var root = _automation.ElementFromHandle(hwnd);
                if (root is null)
                    return null;

                if (TryFindUrlInEdits(root) is { } editUrl)
                    return editUrl;
            }
            catch (Exception ex)
            {
                Diagnostics.Log.Debug($"[browser] URL UIA read failed: {ex.Message}");
            }

            return null;
        }

        private string? TryFindUrlInEdits(IUIAutomationElement root)
        {
            var automation = _automation;
            if (automation is null)
                return null;

            IUIAutomationElementArray edits;
            try
            {
                var editCond = automation.CreatePropertyCondition(
                    UIA_PropertyIds.UIA_ControlTypePropertyId,
                    UIA_ControlTypeIds.UIA_EditControlTypeId);
                edits = root.FindAll(TreeScope.TreeScope_Descendants, editCond);
            }
            catch { return null; }

            int count = edits.Length;
            for (int i = 0; i < count; i++)
            {
                IUIAutomationElement el;
                try { el = edits.GetElement(i); }
                catch { continue; }

                string name;
                try { name = el.CurrentName ?? string.Empty; }
                catch { continue; }

                string? value = TryReadValue(el);
                if (string.IsNullOrWhiteSpace(value))
                    continue;

                if (LooksLikeUrl(value) && LooksLikeAddressField(name, value))
                    return value;
            }

            return null;
        }

        private static bool LooksLikeAddressField(string name, string value)
        {
            var n = name.Trim().ToLowerInvariant();
            foreach (var hint in AddressNameHints)
                if (n.Contains(hint, StringComparison.Ordinal))
                    return true;

            return LooksLikeSupportedChatUrl(value);
        }

        private static string? TryReadValue(IUIAutomationElement el)
        {
            try
            {
                var raw = el.GetCurrentPattern(UIA_PatternIds.UIA_ValuePatternId);
                if (raw is IUIAutomationValuePattern valuePattern)
                    return valuePattern.CurrentValue;
            }
            catch { }

            try { return el.CurrentName; }
            catch { return null; }
        }

        private static bool LooksLikeSupportedChatUrl(string value)
            => TryResolveProviderFromUrl(value) != null;

        private static bool LooksLikeUrl(string value)
        {
            var trimmed = value.Trim();
            return trimmed.Contains('.', StringComparison.Ordinal)
                || trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeUrl(string rawUrl)
        {
            var trimmed = rawUrl.Trim();
            if (!trimmed.Contains("://", StringComparison.Ordinal))
                trimmed = "https://" + trimmed;
            return trimmed;
        }

        private static string NormalizeProcessName(string processName)
            => processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? processName[..^4]
                : processName;
    }

    internal sealed record BrowserProviderDetection(ProviderId Provider, ProviderSource Source);
}
