using System;
using Interop.UIAutomationClient;

namespace TaskbarQuota.ActiveApp
{
    /// <summary>
    /// Reads Synara's <b>live</b> composer model selection straight from its UI Automation tree, so a
    /// provider switch is observed the instant the user picks it — independent of Chromium's lazy
    /// localStorage flush to disk (which can lag ~5-6s and is what made the disk-only path feel slow).
    ///
    /// Synara is a Chromium app: attaching a UIA client makes it expose the React DOM as an
    /// accessibility tree. The composer toolbar holds three "picker" buttons — access ("Full access"),
    /// model ("GPT-5.5"), and effort ("Medium" / "Change effort, context, and speed"). Current
    /// unlabelled builds expose only the model display string; labelled builds may expose
    /// "{Provider} · {Model}", which <see cref="SynaraModelClassifier"/> can parse. React generates
    /// the buttons' AutomationIds per render (not stable across
    /// restarts), so the model button is located structurally and the element handle is cached and
    /// re-read each poll; a stale handle (window/tree rebuilt) triggers a re-locate.
    ///
    /// All members are intended to be called off the UI thread (the coordinator's MTA poll thread).
    /// </summary>
    internal sealed class SynaraUiaReader
    {
        // Picker-trio class signature: the access/model/effort buttons are the only composer buttons
        // whose className carries "rounded-lg border". Send / Record / Composer-extras use different
        // classes, so this cheaply narrows the FindAll result to the three pickers.
        private const string PickerClassSignature = "rounded-lg border";

        private IUIAutomation? _automation;
        private IUIAutomationElement? _modelButton;
        private IntPtr _cachedHwnd;

        /// <summary>
        /// Current model display name from Synara's live composer (e.g. "GPT-5.5"), or null when the
        /// tree is not yet populated / the button can't be located. Never throws.
        /// </summary>
        internal string? TryReadActiveModelName(IntPtr synaraHwnd)
        {
            if (synaraHwnd == IntPtr.Zero)
                return null;

            try
            {
                _automation ??= new CUIAutomation();

                // Fast path: re-read the cached button directly (one cross-process call). A rebuilt tree
                // or closed window throws here, so we drop the cache and re-locate below.
                if (_modelButton is not null && _cachedHwnd == synaraHwnd)
                {
                    try
                    {
                        var name = _modelButton.CurrentName;
                        if (!string.IsNullOrEmpty(name))
                            return name;
                    }
                    catch
                    {
                        _modelButton = null;
                    }
                }

                _modelButton = LocateModelButton(synaraHwnd);
                _cachedHwnd = synaraHwnd;
                if (_modelButton is null)
                    return null;

                try
                {
                    var name = _modelButton.CurrentName;
                    return string.IsNullOrEmpty(name) ? null : name;
                }
                catch
                {
                    _modelButton = null;
                    return null;
                }
            }
            catch (Exception ex)
            {
                Diagnostics.Log.Debug($"[synara] uia read failed: {ex.Message}");
                _modelButton = null;
                return null;
            }
        }

        /// <summary>Drop cached COM handles (e.g. when Synara is no longer foreground).</summary>
        internal void Reset()
        {
            _modelButton = null;
            _cachedHwnd = IntPtr.Zero;
        }

        private IUIAutomationElement? LocateModelButton(IntPtr hwnd)
        {
            var automation = _automation;
            if (automation is null)
                return null;

            IUIAutomationElement root;
            try { root = automation.ElementFromHandle(hwnd); }
            catch { return null; }
            if (root is null)
                return null;

            // All Button descendants. Chromium populates this lazily after the first UIA touch, so an
            // early call can return an empty/short list — the caller polls, so a later tick succeeds.
            IUIAutomationElementArray buttons;
            try
            {
                var buttonCond = automation.CreatePropertyCondition(
                    UIA_PropertyIds.UIA_ControlTypePropertyId,
                    UIA_ControlTypeIds.UIA_ButtonControlTypeId);
                buttons = root.FindAll(TreeScope.TreeScope_Descendants, buttonCond);
            }
            catch { return null; }
            if (buttons is null)
                return null;

            IUIAutomationElement? candidate = null;
            int count = buttons.Length;
            for (int i = 0; i < count; i++)
            {
                IUIAutomationElement el;
                try { el = buttons.GetElement(i); }
                catch { continue; }

                string cls, name;
                try
                {
                    cls = el.CurrentClassName ?? string.Empty;
                    name = el.CurrentName ?? string.Empty;
                }
                catch { continue; }

                // Only the access/model/effort picker trio carries the signature class.
                if (cls.IndexOf(PickerClassSignature, StringComparison.Ordinal) < 0)
                    continue;

                // The access and effort buttons have fixed, recognizable labels; whatever remains in the
                // trio is the model button (its Name is the model display string).
                if (IsAccessOrEffortLabel(name))
                    continue;

                candidate = el;
                break;
            }

            return candidate;
        }

        /// <summary>
        /// True when the picker-button name is the access or effort control (or the combined picker's
        /// static "Change model and reasoning" label on unlabelled builds) rather than the model —
        /// matched loosely so wording variants ("Read only", "Low"/"High", localized effort) still
        /// exclude correctly. The model name (GPT-5.5, Kimi K2.6, Claude Opus, ...) never matches.
        /// </summary>
        private static bool IsAccessOrEffortLabel(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return true;

            var n = name.Trim().ToLowerInvariant();
            return n.Contains("access")          // "Full access", "Read only access"
                || n.Contains("read only")
                || n.Contains("read-only")
                || n == "ask"
                || n == "agent"
                || n.Contains("effort")           // "Change effort, context, and speed"
                || n == "low" || n == "medium" || n == "high" || n == "minimal"
                || n.Contains("change model");    // combined picker's static aria-label on unlabelled builds
        }
    }
}
