using System;
using System.Collections.Generic;

namespace TaskbarQuota.Cost
{
    /// <summary>
    /// Maps a provider-internal model id (as written to a local log) onto a canonical pricing key
    /// present in <see cref="PricingCatalog"/>. Resolution is deliberately conservative: an id that
    /// can't be confidently matched returns <c>null</c> so its tokens land in the "unpriced" bucket
    /// instead of being silently mispriced. This keeps the headline API-equivalent figure honest.
    /// </summary>
    public static class ModelResolver
    {
        /// <summary>Returns the canonical pricing key for a raw model id, or null when unpriceable.</summary>
        public static string? Resolve(string? rawModel)
        {
            if (string.IsNullOrWhiteSpace(rawModel))
                return null;

            string m = rawModel.Trim().ToLowerInvariant();

            // Synthetic / placeholder rows some CLIs emit (e.g. Claude Code "<synthetic>").
            if (m.StartsWith("<") || m.Contains("placeholder") || m.Contains("synthetic") || m == "auto")
                return null;

            // Anthropic — fable-5, opus-4-8, sonnet-5, haiku-4-5, … (family fallback; exact ids are
            // matched directly by PricingCatalog before we get here).
            if (m.Contains("claude") || m.StartsWith("anthropic"))
            {
                if (m.Contains("fable")) return "claude-fable-5";
                if (m.Contains("opus")) return "claude-opus-4-8";
                if (m.Contains("haiku")) return "claude-haiku-4-5";
                if (m.Contains("sonnet")) return "claude-sonnet-5";
                return "claude-sonnet-5"; // unknown Claude tier → sonnet (mid) rather than unpriced
            }

            // OpenAI / Codex — gpt-5.5, gpt-5.3-codex, gpt-5.4-mini, …
            if (m.StartsWith("gpt-5") || m.StartsWith("gpt5") || m.Contains("codex"))
            {
                if (m.Contains("mini")) return "gpt-5.4-mini";
                if (m.Contains("codex")) return "gpt-5.3-codex";
                return "gpt-5.5";
            }

            // xAI Grok — grok-4, grok-code-fast-1, grok-4-fast, …
            if (m.Contains("grok"))
                return m.Contains("code") || m.Contains("fast") ? "grok-code-fast-1" : "grok-4";

            // Z.ai GLM coding plan — GLM-5.2, GLM-4.6, glm-4.5-air, …
            if (m.StartsWith("glm") || m.Contains("zai") || m.Contains("glm"))
                return m.Contains("5.2") || m.Contains("5-2") ? "glm-5.2" : "glm-4.6";

            // Google Gemini — gemini-3-pro, gemini-3-flash, gemini-2.5-flash, …
            if (m.Contains("gemini"))
                return m.Contains("flash") || m.Contains("low") ? "gemini-3-flash" : "gemini-3-pro";

            // Moonshot Kimi — kimi-k2.7-code, kimi-k2.6, …
            if (m.Contains("kimi") || m.Contains("moonshot")) return "kimi-k2.7-code";

            return null;
        }

        /// <summary>Diagnostic set of raw ids seen this process that resolved to null.</summary>
        public static IReadOnlyCollection<string> UnresolvedModels => _unresolved;
        private static readonly HashSet<string> _unresolved = new(StringComparer.OrdinalIgnoreCase);

        internal static void NoteUnresolved(string raw)
        {
            lock (_unresolved) { _unresolved.Add(raw); }
        }
    }
}
