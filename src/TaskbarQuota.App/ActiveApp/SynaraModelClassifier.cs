using System;
using TaskbarQuota.Usage;

namespace TaskbarQuota.ActiveApp
{
    /// <summary>
    /// Parses Synara's live composer model-button name into a provider (and the model display name),
    /// so a provider switch is observed the instant the user picks it — independent of Chromium's lazy
    /// localStorage flush to disk (which can lag ~5-6s and is what made the disk-only path feel slow).
    ///
    /// Labelled Synara builds expose the HOST provider in the button's accessible name as
    /// "{Host} · {Model}" (optionally " · {Status}" for the combined picker), e.g.
    /// "Cursor · GPT-5.5", "OpenCode Go · Deepseek V4 Flash", "Codex · GPT-5.5 · Medium". The host is
    /// unambiguous and instant for EVERY provider — including Cursor and OpenCode, which proxy the same
    /// model names as the native brands and can't be identified from the model alone. "OpenCode Go" is
    /// split from "OpenCode" by the host label, so the Go/Zen distinction is instant too.
    ///
    /// Older unlabelled builds expose only the model display string. That is not enough to identify the
    /// host provider because Cursor/OpenCode can wrap the same GPT/Claude/Grok model names as native
    /// providers. For those builds this parser intentionally returns null and lets the local state reader
    /// publish the provider from Synara's authoritative persisted selection.
    /// </summary>
    internal static class SynaraModelClassifier
    {
        // Separator used in the composed accessible name ("{Host} · {Model}[ · {Status}...]").
        private const char SegmentSeparator = '\u00B7';

        internal sealed record Classification(ProviderId Provider, string? ModelDisplayName);

        /// <summary>
        /// Parse the composer button name. Returns null when no provider can be identified (the caller
        /// defers to the authoritative localStorage reader).
        /// </summary>
        internal static Classification? Classify(string? buttonName)
        {
            if (string.IsNullOrWhiteSpace(buttonName))
                return null;

            // Labelled builds: "{Host} · {Model} ...". The host is the segment before the first "·";
            // the model is the segment after it. Anything past the second "·" is effort/status (ignored).
            var segments = buttonName.Split(SegmentSeparator);
            if (segments.Length >= 2 && TryMapHostLabel(segments[0].Trim()) is { } provider)
            {
                var model = segments[1].Trim();
                return new Classification(provider, model.Length == 0 ? null : model);
            }

            // Unlabelled build: the whole string is only the model display name. Never infer the
            // provider from it; the same label can belong to Codex, Cursor, or OpenCode.
            return null;
        }

        /// <summary>Map a host label from the composed name to a tracked provider. Null when unrecognized.</summary>
        private static ProviderId? TryMapHostLabel(string hostLabel)
        {
            if (hostLabel.Length == 0) return null;
            if (hostLabel.Equals("Codex", StringComparison.OrdinalIgnoreCase)) return ProviderId.Codex;
            if (hostLabel.Equals("Claude", StringComparison.OrdinalIgnoreCase)) return ProviderId.Claude;
            if (hostLabel.Equals("Cursor", StringComparison.OrdinalIgnoreCase)) return ProviderId.Cursor;
            if (hostLabel.Equals("Grok", StringComparison.OrdinalIgnoreCase)) return ProviderId.Grok;
            if (hostLabel.Equals("OpenCode Go", StringComparison.OrdinalIgnoreCase)) return ProviderId.OpenCodeGo;
            if (hostLabel.Equals("OpenCode", StringComparison.OrdinalIgnoreCase)) return ProviderId.OpenCode;
            return null;
        }
    }
}
