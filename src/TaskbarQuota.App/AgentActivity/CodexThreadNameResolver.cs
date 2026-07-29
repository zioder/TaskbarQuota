using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace TaskbarQuota.AgentActivity;

/// <summary>Reads Codex's own persisted thread names from its append-only session index.</summary>
internal sealed class CodexThreadNameResolver
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(10);
    private readonly string _indexPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "session_index.jsonl");
    private readonly Dictionary<string, string> _names = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset _loadedAt = DateTimeOffset.MinValue;

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
        _names.Clear();
        if (!File.Exists(_indexPath))
            return;

        try
        {
            foreach (var line in File.ReadLines(_indexPath))
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;
                using var entry = JsonDocument.Parse(line);
                var root = entry.RootElement;
                if (!root.TryGetProperty("id", out var id) || !root.TryGetProperty("thread_name", out var name))
                    continue;
                var idValue = id.GetString();
                var nameValue = name.GetString()?.Trim();
                if (!string.IsNullOrWhiteSpace(idValue) && !string.IsNullOrWhiteSpace(nameValue))
                    _names[idValue] = nameValue;
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (JsonException) { }
    }
}
