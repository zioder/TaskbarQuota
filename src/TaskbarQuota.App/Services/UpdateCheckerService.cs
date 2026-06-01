using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TaskbarQuota.Services;

public sealed class UpdateCheckerService
{
    private const string LatestReleaseUrl = "https://api.github.com/repos/zioder/TaskbarQuota/releases/latest";
    private const string HttpUserAgent = "TaskbarQuota";

    public async Task<UpdateCheckResult> CheckAsync(string currentVersion, CancellationToken cancellationToken = default)
    {
        using var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd(HttpUserAgent);
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

        using var response = await client.GetAsync(LatestReleaseUrl, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var release = await JsonSerializer.DeserializeAsync<GitHubRelease>(stream, cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException("Could not read the latest GitHub release.");

        var latest = VersionComparer.Normalize(release.TagName);
        var current = VersionComparer.Normalize(currentVersion);

        if (VersionComparer.Compare(latest, current) <= 0)
            return UpdateCheckResult.UpToDate;

        var archSlug = GetInstallerArchSlug();
        var installers = release.Assets?
            .Where(IsTaskbarQuotaSetupExe)
            .ToList() ?? [];

        var asset = installers.FirstOrDefault(a => MatchesArch(a.Name, archSlug))
            ?? installers.FirstOrDefault(a => MatchesArch(a.Name, "x64"))
            ?? installers.FirstOrDefault();

        return UpdateCheckResult.UpdateAvailable(
            latest,
            Uri.TryCreate(release.HtmlUrl, UriKind.Absolute, out var releaseUri) ? releaseUri : null,
            asset?.BrowserDownloadUrl is { } download && Uri.TryCreate(download, UriKind.Absolute, out var downloadUri)
                ? downloadUri
                : null);
    }

    public async Task<DownloadedUpdate> DownloadAsync(
        UpdateCheckResult result,
        IProgress<UpdateDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (result.Kind != UpdateCheckResultKind.UpdateAvailable
            || string.IsNullOrWhiteSpace(result.Version)
            || result.DownloadUrl is null)
        {
            throw new InvalidOperationException("The latest GitHub release does not include a downloadable Windows installer.");
        }

        using var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd(HttpUserAgent);

        using var response = await client.GetAsync(
            result.DownloadUrl,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();

        var directory = GetUpdateDownloadDirectory(result.Version);
        Directory.CreateDirectory(directory);

        var fileName = GetDownloadFileName(response, result.DownloadUrl);
        var destination = Path.Combine(directory, fileName);
        if (File.Exists(destination))
            File.Delete(destination);

        var totalBytes = response.Content.Headers.ContentLength;
        progress?.Report(new UpdateDownloadProgress(0, totalBytes, fileName));

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var target = File.Create(destination);

        var buffer = new byte[81_920];
        long received = 0;
        int read;
        while ((read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            received += read;
            progress?.Report(new UpdateDownloadProgress(received, totalBytes, fileName));
        }

        return new DownloadedUpdate(result.Version, destination);
    }

    private static bool IsTaskbarQuotaSetupExe(GitHubAsset asset) =>
        asset.Name.StartsWith("TaskbarQuotaSetup-", StringComparison.OrdinalIgnoreCase)
        && asset.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);

    private static bool MatchesArch(string fileName, string archSlug) =>
        fileName.Contains($"-{archSlug}.exe", StringComparison.OrdinalIgnoreCase);

    private static string GetInstallerArchSlug() =>
        RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm64 => "arm64",
            Architecture.X64 => "x64",
            Architecture.X86 => "x64",
            _ => "x64",
        };

    private static string GetUpdateDownloadDirectory(string version) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TaskbarQuota",
        "Updates",
        version);

    private static string GetDownloadFileName(HttpResponseMessage response, Uri fallbackUri)
    {
        var suggested = response.Content.Headers.ContentDisposition?.FileNameStar
            ?? response.Content.Headers.ContentDisposition?.FileName;
        suggested = suggested?.Trim('"');

        if (!string.IsNullOrWhiteSpace(suggested))
            return suggested;

        var fallback = Path.GetFileName(fallbackUri.LocalPath);
        if (!string.IsNullOrWhiteSpace(fallback) && IsTaskbarQuotaSetupExe(new GitHubAsset { Name = fallback }))
            return fallback;

        return $"TaskbarQuotaSetup-{GetInstallerArchSlug()}.exe";
    }

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")]
        public string TagName { get; set; } = string.Empty;

        [JsonPropertyName("html_url")]
        public string HtmlUrl { get; set; } = string.Empty;

        [JsonPropertyName("assets")]
        public List<GitHubAsset>? Assets { get; set; }
    }

    private sealed class GitHubAsset
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("browser_download_url")]
        public string BrowserDownloadUrl { get; set; } = string.Empty;
    }
}

public enum UpdateCheckResultKind
{
    UpToDate,
    UpdateAvailable,
}

public sealed record UpdateCheckResult(
    UpdateCheckResultKind Kind,
    string? Version = null,
    Uri? ReleaseUrl = null,
    Uri? DownloadUrl = null)
{
    public static UpdateCheckResult UpToDate { get; } = new(UpdateCheckResultKind.UpToDate);

    public static UpdateCheckResult UpdateAvailable(string version, Uri? releaseUrl, Uri? downloadUrl) =>
        new(UpdateCheckResultKind.UpdateAvailable, version, releaseUrl, downloadUrl);
}

public sealed record DownloadedUpdate(string Version, string FilePath);

public readonly record struct UpdateDownloadProgress(long BytesReceived, long? TotalBytes, string FileName)
{
    public double Percent => TotalBytes is > 0
        ? Math.Clamp(BytesReceived * 100.0 / TotalBytes.Value, 0, 100)
        : 0;
}

public static class VersionComparer
{
    public static string Normalize(string value)
    {
        var trimmed = value.Trim();
        return trimmed.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? trimmed[1..] : trimmed;
    }

    public static int Compare(string left, string right)
    {
        var leftParts = left.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var rightParts = right.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var count = Math.Max(leftParts.Length, rightParts.Length);

        for (var index = 0; index < count; index++)
        {
            var leftValue = index < leftParts.Length && int.TryParse(leftParts[index], out var parsedLeft) ? parsedLeft : 0;
            var rightValue = index < rightParts.Length && int.TryParse(rightParts[index], out var parsedRight) ? parsedRight : 0;
            var compare = leftValue.CompareTo(rightValue);
            if (compare != 0)
                return compare;
        }

        return 0;
    }
}
