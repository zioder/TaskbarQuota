using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace TaskbarQuota.AgentActivity;

/// <summary>Reads Codex's own persisted thread names from its append-only session index.</summary>
internal sealed class CodexThreadNameResolver
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(10);
    private readonly string _indexPath;
    private readonly Dictionary<string, string> _names = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset _loadedAt = DateTimeOffset.MinValue;

    public CodexThreadNameResolver(string? indexPath = null)
    {
        _indexPath = indexPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "session_index.jsonl");
    }

    public string? GetName(string? threadId)
    {
        if (string.IsNullOrWhiteSpace(threadId))
            return null;

        RefreshIfNeeded();
        return _names.TryGetValue(threadId, out var name) ? name : null;
    }

    private void RefreshIfNeeded()
    {
        if (DateTimeOffset.Now - _loadedAt < CacheLifetime)
            return;

        _loadedAt = DateTimeOffset.Now;
        if (!File.Exists(_indexPath))
        {
            _names.Clear();
            return;
        }

        try
        {
            var refreshed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in File.ReadLines(_indexPath))
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;
                try
                {
                    using var entry = JsonDocument.Parse(line);
                    var root = entry.RootElement;
                    if (!root.TryGetProperty("id", out var id)
                        || id.ValueKind != JsonValueKind.String
                        || !root.TryGetProperty("thread_name", out var name)
                        || name.ValueKind != JsonValueKind.String)
                        continue;
                    var idValue = id.GetString();
                    var nameValue = name.GetString()?.Trim();
                    if (!string.IsNullOrWhiteSpace(idValue) && !string.IsNullOrWhiteSpace(nameValue))
                        refreshed[idValue] = nameValue;
                }
                catch (JsonException)
                {
                    // session_index.jsonl is append-only; the last line may be incomplete while Codex writes it.
                }
            }

            _names.Clear();
            foreach (var (id, name) in refreshed)
                _names[id] = name;
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
