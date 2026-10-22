using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using TaskbarQuota.Diagnostics;

namespace TaskbarQuota.Services;

public enum GitHubStarResult
{
    /// <summary>Starred through the signed-in GitHub CLI account.</summary>
    Starred,
    /// <summary>No usable gh login (or the API call failed); the caller should open the repo page.</summary>
    NeedsBrowser,
}

/// <summary>
/// Stars the TaskbarQuota repo with the GitHub account the `gh` CLI is signed in to.
/// Only ever runs from an explicit user click; never in the background.
/// </summary>
internal static class GitHubStarService
{
    public const string RepoOwner = "zioder";
    public const string RepoName = "TaskbarQuota";
    public static readonly Uri RepoUri = new($"https://github.com/{RepoOwner}/{RepoName}");

    private const string StarredEndpoint = $"https://api.github.com/user/starred/{RepoOwner}/{RepoName}";
    private static readonly HttpClient SharedClient = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    })
    {
        Timeout = TimeSpan.FromSeconds(10),
    };

    // Wait a few days after first launch so the prompt never greets a brand-new user.
    private static readonly TimeSpan PromptDelay = TimeSpan.FromDays(3);

    private static string StatePath => Path.Combine(AppStorage.AppDataDirectory, "github-starred");
    private static string DismissedPath => Path.Combine(AppStorage.AppDataDirectory, "star-prompt-dismissed");
    private static string FirstSeenPath => Path.Combine(AppStorage.AppDataDirectory, "star-prompt-first-seen");

    /// <summary>Raised on the calling thread when the starred or dismissed state changes.</summary>
    public static event Action? Changed;

    /// <summary>Whether the flyout should show the star prompt (not starred, not dismissed, used a few days).</summary>
    public static bool ShouldPrompt()
    {
        if (IsMarkedStarred() || FileExists(DismissedPath))
            return false;

        try
        {
            return DateTimeOffset.UtcNow - GetFirstUse() >= PromptDelay;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// First use, pinned in a marker the first time it is asked for. The seed is the earliest of now,
    /// the app data folder's creation and its oldest file, so people who used the app long before
    /// this prompt shipped don't wait another few days. Once written, the marker alone decides, so
    /// later rewrites of settings files can't push the date forward.
    /// </summary>
    private static DateTimeOffset GetFirstUse()
    {
        if (File.Exists(FirstSeenPath)
            && DateTimeOffset.TryParse(File.ReadAllText(FirstSeenPath), out var firstSeen))
        {
            return firstSeen;
        }

        var earliest = DateTimeOffset.UtcNow;
        var dir = new DirectoryInfo(AppStorage.AppDataDirectory);
        if (dir.Exists)
        {
            earliest = Min(earliest, dir.CreationTimeUtc);
            foreach (var entry in dir.EnumerateFileSystemInfos())
            {
                if (!entry.Name.StartsWith("star-prompt", StringComparison.OrdinalIgnoreCase))
                    earliest = Min(earliest, entry.LastWriteTimeUtc);
            }
        }

        Directory.CreateDirectory(AppStorage.AppDataDirectory);
        File.WriteAllText(FirstSeenPath, earliest.ToString("O"));
        return earliest;
    }

    private static DateTimeOffset Min(DateTimeOffset current, DateTime utc)
    {
        var candidate = new DateTimeOffset(utc, TimeSpan.Zero);
        return candidate < current ? candidate : current;
    }

    public static void DismissPrompt()
    {
        WriteMarker(DismissedPath);
        Changed?.Invoke();
    }

    /// <summary>True once the user starred from inside the app (local marker, no network).</summary>
    public static bool IsMarkedStarred() => FileExists(StatePath);

    /// <summary>Checks GitHub for an existing star; null when gh is unavailable or the call fails.</summary>
    public static async Task<bool?> IsStarredAsync(CancellationToken cancellationToken = default)
    {
        var token = await TryGetGhTokenAsync(cancellationToken).ConfigureAwait(false);
        if (token is null)
            return null;

        try
        {
            using var request = CreateRequest(HttpMethod.Get, token);
            using var response = await SharedClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NoContent)
            {
                MarkStarred();
                return true;
            }
            return response.StatusCode == HttpStatusCode.NotFound ? false : null;
        }
        // HttpClient timeouts surface as OperationCanceledException; only caller cancellation propagates.
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            Log.Warning(ex, "GitHub star status check failed");
            return null;
        }
    }

    public static async Task<GitHubStarResult> StarAsync(CancellationToken cancellationToken = default)
    {
        var token = await TryGetGhTokenAsync(cancellationToken).ConfigureAwait(false);
        if (token is null)
            return GitHubStarResult.NeedsBrowser;

        try
        {
            using var request = CreateRequest(HttpMethod.Put, token);
            request.Content = new ByteArrayContent([]);
            using var response = await SharedClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NoContent)
            {
                MarkStarred();
                Log.Information("Starred GitHub repo via gh CLI account");
                return GitHubStarResult.Starred;
            }

            Log.Warning($"GitHub star request returned {(int)response.StatusCode}");
            return GitHubStarResult.NeedsBrowser;
        }
        // HttpClient timeouts surface as OperationCanceledException; only caller cancellation propagates.
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            Log.Warning(ex, "GitHub star request failed");
            return GitHubStarResult.NeedsBrowser;
        }
    }

    private static HttpRequestMessage CreateRequest(HttpMethod method, string token)
    {
        var request = new HttpRequestMessage(method, StarredEndpoint);
        request.Headers.UserAgent.ParseAdd("TaskbarQuota");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        return request;
    }

    private static void MarkStarred()
    {
        if (IsMarkedStarred())
            return;
        WriteMarker(StatePath);
        Changed?.Invoke();
    }

    private static bool FileExists(string path)
    {
        try { return File.Exists(path); }
        catch { return false; }
    }

    private static void WriteMarker(string path)
    {
        try
        {
            Directory.CreateDirectory(AppStorage.AppDataDirectory);
            File.WriteAllText(path, "1");
        }
        catch
        {
            // Best effort; showing the star prompt again is harmless.
        }
    }

    private static async Task<string?> TryGetGhTokenAsync(CancellationToken cancellationToken)
    {
        try
        {
            var psi = new ProcessStartInfo("gh", "auth token --hostname github.com")
            {
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var process = Process.Start(psi);
            if (process is null)
                return null;

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            var output = await process.StandardOutput.ReadToEndAsync(timeout.Token).ConfigureAwait(false);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            output = output.Trim();
            return process.ExitCode == 0 && output.Length > 0 ? output : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // gh missing, not signed in, or hung past the timeout.
            return null;
        }
    }
}
