using TaskbarQuota.Usage;

namespace TaskbarQuota;

internal static class ProviderSetupInfo
{
    public static string Hint(ProviderId id) => ProviderInstallDetector.NotInstalledMessage(id);

    public static string? SetupUrl(ProviderId id) => id switch
    {
        ProviderId.Grok => "https://x.ai/grok",
        ProviderId.Devin => "https://app.devin.ai",
        ProviderId.Codex => "https://chatgpt.com/codex",
        ProviderId.Claude => "https://claude.ai",
        ProviderId.Copilot => "https://github.com/settings/copilot",
        ProviderId.Cursor => "https://cursor.com",
        ProviderId.Antigravity => "https://antigravity.google",
        ProviderId.OpenCode or ProviderId.OpenCodeGo => "https://opencode.ai",
        ProviderId.Cline => "https://app.cline.bot/dashboard/credits",
        ProviderId.ClinePass => "https://app.cline.bot/dashboard/subscription",
        ProviderId.Zai => "https://z.ai",
        ProviderId.Kimi => "https://www.kimi.com/code/console",
        _ => null,
    };
}
