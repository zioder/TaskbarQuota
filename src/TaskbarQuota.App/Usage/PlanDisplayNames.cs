using System;

namespace TaskbarQuota.Usage
{
    /// <summary>Short plan labels for UI next to provider titles (avoids repeating the app name).</summary>
    public static class PlanDisplayNames
    {
        public static string Shorten(ProviderId id, string? plan)
        {
            if (string.IsNullOrWhiteSpace(plan))
                return string.Empty;

            var text = plan.Trim();
            foreach (var prefix in PrefixesFor(id))
            {
                if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    text = text[prefix.Length..].Trim();
                    break;
                }
            }

            if (text.StartsWith('(') && text.EndsWith(')'))
                text = text[1..^1].Trim();

            return text;
        }

        /// <summary>Plan text for the dashboard title; empty when the display name already includes the tier.</summary>
        public static string ForTitle(ProviderId id, string displayName, string? plan)
        {
            var shortened = Shorten(id, plan);
            return IsRedundantWithDisplayName(displayName, shortened) ? string.Empty : shortened;
        }

        /// <summary>Dashboard page header: OpenCode shows a fixed prefix with Go/Zen as the accent; others use display name + plan.</summary>
        public static (string Primary, string Accent) ForPageHeader(ProviderId id, string displayName, string? plan)
        {
            if (id is ProviderId.OpenCode or ProviderId.OpenCodeGo)
                return ("OpenCode", id == ProviderId.OpenCodeGo ? "Go" : "Zen");

            if (id == ProviderId.Copilot)
                return ("GitHub Copilot", ForTitle(id, displayName, plan));

            return (displayName, ForTitle(id, displayName, plan));
        }

        public static bool IsRedundantWithDisplayName(string displayName, string planLabel)
        {
            if (string.IsNullOrWhiteSpace(planLabel))
                return true;

            var name = displayName.Trim();
            var plan = planLabel.Trim();
            return name.EndsWith(plan, StringComparison.OrdinalIgnoreCase)
                || name.Contains($" {plan}", StringComparison.OrdinalIgnoreCase);
        }

        private static string[] PrefixesFor(ProviderId id) => id switch
        {
            ProviderId.Codex => ["ChatGPT ", "Codex ", "OpenAI "],
            ProviderId.Claude => ["Claude "],
            ProviderId.Cursor => ["Cursor "],
            ProviderId.Copilot => ["GitHub Copilot ", "Copilot "],
            ProviderId.OpenCode => ["OpenCode "],
            ProviderId.OpenCodeGo => ["OpenCode "],
            ProviderId.Antigravity => ["Google AI ", "Antigravity ", "Google "],
            _ => Array.Empty<string>(),
        };
    }
}
