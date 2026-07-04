using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using TaskbarQuota.Usage;

namespace TaskbarQuota;

/// <summary>
/// Detects whether a provider's CLI or desktop app is installed on this machine.
/// Used to distinguish "not set up" from "installed but waiting for the app to run".
/// </summary>
internal static class ProviderInstallDetector
{
    private static readonly string[] KnownClis =
        ["antigravity", "codex", "grok", "claude", "devin", "gh", "opencode", "cline"];

    private static readonly ConcurrentDictionary<string, bool> CliAvailability = new(StringComparer.OrdinalIgnoreCase);
    private static volatile bool _cliCacheReady;

    internal static Func<ProviderId, bool>? IsInstalledOverrideForTesting;

    /// <summary>Precomputes CLI availability once so startup does not spawn many where.exe processes.</summary>
    public static void WarmCliCache()
    {
        if (_cliCacheReady)
            return;

        foreach (var name in KnownClis)
            CliAvailability[name] = ProbeCli(name);

        _cliCacheReady = true;
    }

    internal static void ResetCliCacheForTesting()
    {
        CliAvailability.Clear();
        _cliCacheReady = false;
    }

    public static bool IsInstalled(ProviderId id)
    {
        if (IsInstalledOverrideForTesting is { } overrideFn)
            return overrideFn(id);

        return _cliCacheReady ? IsInstalledCore(id) : IsInstalledWithoutCliProbe(id);
    }

    private static bool IsInstalledCore(ProviderId id) => id switch
    {
        ProviderId.Antigravity => IsAntigravityInstalled(),
        ProviderId.Codex => IsCliAvailable("codex") || File.Exists(CodexAuthPath()),
        ProviderId.Grok => IsCliAvailable("grok") || File.Exists(GrokAuthPath()),
        ProviderId.Claude => IsCliAvailable("claude")
            || File.Exists(ClaudeCredentialsPath())
            || IsDesktopAppInstalled("Claude"),
        ProviderId.Devin => IsCliAvailable("devin")
            || File.Exists(DevinCredentialsPath())
            || IsDevinAppInstalled(),
        ProviderId.Copilot => IsCliAvailable("gh")
            || !string.IsNullOrWhiteSpace(CredentialStore.Instance.For(ProviderId.Copilot).ApiKey),
        ProviderId.Cursor => IsCursorInstalled(),
        ProviderId.OpenCode or ProviderId.OpenCodeGo => IsCliAvailable("opencode")
            || !string.IsNullOrWhiteSpace(CredentialStore.Instance.For(ProviderId.OpenCode).CookieHeader),
        ProviderId.Cline or ProviderId.ClinePass => IsCliAvailable("cline") || File.Exists(ClineProvidersPath()),
        _ => true,
    };

    /// <summary>Fast path for the UI thread before <see cref="WarmCliCache"/> finishes — never spawns processes.</summary>
    private static bool IsInstalledWithoutCliProbe(ProviderId id) => id switch
    {
        ProviderId.Antigravity => IsAntigravityInstalledWithoutCli(),
        ProviderId.Codex => File.Exists(CodexAuthPath()) || HasCachedCli("codex"),
        ProviderId.Grok => File.Exists(GrokAuthPath()) || HasCachedCli("grok"),
        ProviderId.Claude => File.Exists(ClaudeCredentialsPath())
            || IsDesktopAppInstalled("Claude")
            || HasCachedCli("claude"),
        ProviderId.Devin => File.Exists(DevinCredentialsPath())
            || IsDevinAppInstalled()
            || HasCachedCli("devin"),
        ProviderId.Copilot => !string.IsNullOrWhiteSpace(CredentialStore.Instance.For(ProviderId.Copilot).ApiKey)
            || HasCachedCli("gh"),
        ProviderId.Cursor => IsCursorInstalled(),
        ProviderId.OpenCode or ProviderId.OpenCodeGo =>
            !string.IsNullOrWhiteSpace(CredentialStore.Instance.For(ProviderId.OpenCode).CookieHeader)
            || HasCachedCli("opencode"),
        ProviderId.Cline or ProviderId.ClinePass => File.Exists(ClineProvidersPath()) || HasCachedCli("cline"),
        _ => true,
    };

    private static bool HasCachedCli(string name)
        => CliAvailability.TryGetValue(name, out bool available) && available;

    public static string WaitingMessage(ProviderId id) => id switch
    {
        ProviderId.Antigravity => "Waiting for Antigravity to be open.",
        ProviderId.Devin => "Waiting for the Devin app to be open.",
        ProviderId.Claude => "Waiting for Claude to be open.",
        ProviderId.Cursor => "Waiting for Cursor to be open.",
        _ => "Waiting for the app to be open.",
    };

    public static string NotInstalledMessage(ProviderId id) => id switch
    {
        ProviderId.Antigravity => "Install Antigravity to see usage here.",
        ProviderId.Grok => "Install the Grok CLI and run grok login.",
        ProviderId.Devin => "Install the Devin CLI or app, then sign in.",
        ProviderId.Codex => "Install the Codex CLI and run codex login.",
        ProviderId.Claude => "Open Claude in your browser, or install the Claude CLI/app and sign in.",
        ProviderId.Copilot => "Install the GitHub CLI or add a token in Settings.",
        ProviderId.Cursor => "Install Cursor and sign in.",
        ProviderId.OpenCode or ProviderId.OpenCodeGo => "Sign in at opencode.ai or paste cookies via Fix.",
        ProviderId.Cline or ProviderId.ClinePass => "Install the Cline CLI (npm i -g cline) and sign in.",
        _ => "Set up this provider to see usage here.",
    };

    internal static string ClineProvidersPath()
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".cline", "data", "settings", "providers.json");

    internal static string CodexAuthPath()
    {
        var codexHome = Environment.GetEnvironmentVariable("CODEX_HOME")?.Trim();
        if (!string.IsNullOrEmpty(codexHome))
            return Path.Combine(codexHome, "auth.json");
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "auth.json");
    }

    internal static string GrokAuthPath()
    {
        var grokHome = Environment.GetEnvironmentVariable("GROK_HOME")?.Trim();
        if (!string.IsNullOrEmpty(grokHome))
            return Path.Combine(grokHome, "auth.json");
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".grok", "auth.json");
    }

    internal static string ClaudeCredentialsPath()
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", ".credentials.json");

    internal static string DevinCredentialsPath()
    {
        string? fallback = null;
        foreach (var path in DevinCredentialsPaths())
        {
            fallback ??= path;
            if (File.Exists(path)) return path;
        }
        return fallback!;
    }

    private static IEnumerable<string> DevinCredentialsPaths()
    {
        // Windows: the CLI and app share credentials.toml under %APPDATA%\Devin.
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!string.IsNullOrEmpty(appData))
        {
            yield return Path.Combine(appData, "Devin", "credentials.toml");
            yield return Path.Combine(appData, "Devin - Next", "credentials.toml");
        }

        // XDG / `~/.local/share/devin` (Linux, macOS).
        var dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME")?.Trim();
        var root = !string.IsNullOrEmpty(dataHome)
            ? dataHome
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        yield return Path.Combine(root, "devin", "credentials.toml");
    }

    internal static bool IsCliAvailable(string name)
    {
        if (CliAvailability.TryGetValue(name, out bool cached))
            return cached;

        bool available = ProbeCli(name);
        CliAvailability[name] = available;
        return available;
    }

    private static bool ProbeCli(string name)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "where.exe",
                Arguments = name,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var process = Process.Start(psi);
            if (process == null)
                return false;

            string output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(500))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return false;
            }
            return process.ExitCode == 0 && !string.IsNullOrWhiteSpace(output);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsAntigravityInstalled()
    {
        if (IsCliAvailable("antigravity"))
            return true;

        return IsAntigravityInstalledWithoutCli();
    }

    private static bool IsAntigravityInstalledWithoutCli()
    {
        foreach (var root in AntigravityInstallRoots())
        {
            if (!Directory.Exists(root))
                continue;

            if (File.Exists(Path.Combine(root, "Antigravity.exe"))
                || File.Exists(Path.Combine(root, "bin", "antigravity.exe")))
                return true;

            foreach (var subdir in new[] { root, Path.Combine(root, "bin"), Path.Combine(root, "app") })
            {
                if (!Directory.Exists(subdir))
                    continue;

                try
                {
                    if (Directory.EnumerateFiles(subdir, "language_server*.exe", SearchOption.TopDirectoryOnly).Any())
                        return true;
                }
                catch
                {
                    // Ignore unreadable install directories.
                }
            }
        }

        return false;
    }

    private static IEnumerable<string> AntigravityInstallRoots()
    {
        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs",
            "Antigravity");
        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Antigravity");
        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "Antigravity");
    }

    private static bool IsDevinAppInstalled()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Directory.Exists(Path.Combine(appData, "Devin"))
            || Directory.Exists(Path.Combine(appData, "Devin - Next"));
    }

    private static bool IsCursorInstalled()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roots = new[]
        {
            Path.Combine(localAppData, "Programs", "cursor"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Cursor"),
        };

        foreach (var root in roots)
        {
            if (!Directory.Exists(root))
                continue;

            if (File.Exists(Path.Combine(root, "Cursor.exe")))
                return true;
        }

        return false;
    }

    private static bool IsDesktopAppInstalled(string appFolderName)
    {
        var localPrograms = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs",
            appFolderName);
        return Directory.Exists(localPrograms);
    }
}
