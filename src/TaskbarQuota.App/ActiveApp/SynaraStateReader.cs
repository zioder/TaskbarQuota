using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TaskbarQuota.Usage;

namespace TaskbarQuota.ActiveApp
{
    /// <summary>
    /// Synara is a meta-app that wraps many coding agents (Codex, Claude, Cursor, Grok, OpenCode, ...)
    /// behind one window, with a per-thread provider/model selection. When Synara is the focused app we
    /// read its local state DB (<c>~/.synara/userdata/state.sqlite</c>) to resolve which provider the
    /// currently-active thread is using, then attribute usage to that provider — mirroring how OpenCode's
    /// model-state files drive <see cref="ActiveAppDetector.DetectOpenCodeProviderFromModelState"/>.
    ///
    /// Only providers Win-CodexBar already tracks are mapped (Codex, Claude, Cursor, Grok, OpenCode /
    /// OpenCode Go). Synara providers without a usage backend (Gemini, Kilo, Pi) resolve to null so the
    /// caller falls back to the last active provider instead of showing an unsupported one.
    /// </summary>
    public static class SynaraStateReader
    {
        /// <summary>Resolved Synara thread selection: the inner provider plus display context for tooltips.</summary>
        public sealed record SynaraSelection(
            ProviderId Provider,
            string ProviderLiteral,
            string? Model,
            string? ThreadTitle);

        // Short cache keyed off the DB's last-write time so rapid taskbar ticks don't reopen SQLite.
        private static readonly object Gate = new();
        private static string? _cachedDbPath;
        private static DateTime _cachedDbWriteUtc = DateTime.MinValue;
        private static SynaraSelection? _cachedSelection;
        // Thread titles change rarely; cache so the draft fast-path never has to open SQLite per tick.
        private static readonly Dictionary<string, string?> ThreadTitleCache = new(StringComparer.Ordinal);

        /// <summary>Invalidate cached state so the next resolve re-reads (called by the file watcher).</summary>
        public static void InvalidateDraftCache()
        {
            SynaraComposerDraftReader.Invalidate();
            lock (Gate)
            {
                _cachedDbPath = null;
                _cachedDbWriteUtc = DateTime.MinValue;
                _cachedSelection = null;
                _lockCtxThreadId = null;
                _lockCtxWriteUtc = DateTime.MinValue;
                _lockCtx = null;
            }
        }

        public static bool IsSynaraProcessName(string? processName) =>
            processName != null
            && (processName.Equals("synara", StringComparison.OrdinalIgnoreCase)
                || processName.Equals("synara (dev)", StringComparison.OrdinalIgnoreCase)
                || processName.Equals("synara-desktop", StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// Resolve the active thread's provider from Synara's state DB, or null when Synara is not
        /// installed, has no usable thread, or the active thread uses an unsupported provider.
        /// </summary>
        public static SynaraSelection? GetActiveSelection(
            bool includeThreadTitle = true,
            bool preferStickyComposerSelection = false,
            string? onScreenModel = null)
        {
            try
            {
                var dbPath = GetStateDbPath();
                var focusedThreadId = SynaraComposerDraftReader.GetFocusedThreadId();

                var draft = focusedThreadId is { Length: > 0 }
                    ? SynaraComposerDraftReader.TryGetDraft(focusedThreadId)
                    : null;
                var sticky = preferStickyComposerSelection
                    ? SynaraComposerDraftReader.TryGetStickySelectionFast()
                        ?? SynaraComposerDraftReader.GetStickySelection()
                    : null;

                // Candidate A — the focused thread, resolved with Synara's real lock precedence
                // (session/thread lock → draft → thread/project default), mirroring ChatView.tsx.
                ThreadLockContext? ctx = null;
                SynaraSelection? focusedSel = null;
                if (focusedThreadId is { Length: > 0 } && dbPath != null)
                {
                    ctx = GetThreadLockContext(dbPath, focusedThreadId);
                    if (ctx != null)
                        focusedSel = ResolveFocusedSelection(focusedThreadId, draft, ctx, includeThreadTitle);
                }

                // Candidate B — the global sticky composer selection, authoritative for a new/unsaved
                // composer that has no started focused thread yet.
                SynaraSelection? stickySel = null;
                if (sticky is { } s && MapProvider(s.ProviderLiteral, s.Model) is { } sp)
                    stickySel = new SynaraSelection(sp, s.ProviderLiteral, s.Model, null);

                // Continuously learn which providers each model belongs to from the per-provider maps.
                LearnCatalog(draft);
                LearnCatalog(sticky);

                LogResolution(focusedThreadId, focusedSel, stickySel, onScreenModel);

                // FAST PATH — provider by on-screen model, but ONLY when the model is unique to a single
                // provider in the learned catalog. Synara's active-provider write to localStorage can be
                // buffered by Chromium for a long time on picker-only changes, so the authoritative
                // selection below may be badly stale; the live UIA model is the only instant signal. A
                // model name shared across providers (Opus 4.8 under Claude+Cursor, GPT-5.5 under
                // Codex/Cursor/OpenCode) is ambiguous and intentionally NOT resolved here — it falls
                // through to the authoritative selection so we never confidently show the wrong provider.
                if (!string.IsNullOrEmpty(onScreenModel)
                    && ResolveFromCatalog(onScreenModel, focusedThreadId, ctx, includeThreadTitle) is { } fast)
                {
                    return fast;
                }

                // The on-screen model also disambiguates between the two authoritative candidates
                // (focused thread vs sticky composer) — which context the user is looking at.
                if (!string.IsNullOrEmpty(onScreenModel))
                {
                    if (focusedSel != null && ModelMatchesOnScreen(focusedSel.Model, onScreenModel))
                        return focusedSel;
                    if (stickySel != null && ModelMatchesOnScreen(stickySel.Model, onScreenModel))
                        return stickySel;
                }

                // Default precedence: the locked focused thread wins; sticky only when there is none.
                if (focusedSel != null)
                    return focusedSel;
                if (stickySel != null)
                    return stickySel;

                // Last resort: the most recently touched started thread (cached by SQLite write time).
                if (dbPath == null)
                    return null;
                var writeUtc = LatestWriteUtc(dbPath);
                lock (Gate)
                {
                    if (_cachedDbPath == dbPath && _cachedDbWriteUtc == writeUtc)
                        return _cachedSelection;
                }
                var newest = ReadNewestSelection(dbPath);
                lock (Gate)
                {
                    _cachedDbPath = dbPath;
                    _cachedDbWriteUtc = writeUtc;
                    _cachedSelection = newest;
                }
                return newest;
            }
            catch (Exception ex)
            {
                Diagnostics.Log.Debug($"Synara state read failed: {ex.Message}");
                return null;
            }
        }

        // Learned model→provider catalog: model core → (provider literal → exact model id last seen).
        // Accumulated from every per-provider map we read, so it survives even when Chromium stops
        // flushing localStorage. A core under >1 provider is "shared" and can't be resolved from the
        // model alone.
        private static readonly Dictionary<string, Dictionary<string, string>> _modelCatalog = new(StringComparer.Ordinal);
        private static bool _catalogLoaded;
        private static bool _catalogDirty;
        private static DateTime _catalogLastSaveUtc = DateTime.MinValue;
        private static readonly TimeSpan CatalogSaveThrottle = TimeSpan.FromSeconds(10);

        private static string CatalogPath() => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TaskbarQuota", "synara-model-catalog.json");

        private static void LearnCatalog(SynaraComposerDraftReader.DraftSelection? selection)
        {
            if (selection?.ModelByProvider is not { } map)
                return;
            lock (Gate)
            {
                EnsureCatalogLoaded();
                foreach (var (literal, modelId) in map)
                {
                    if (string.IsNullOrEmpty(literal) || string.IsNullOrEmpty(modelId))
                        continue;
                    var core = ModelCore(modelId);
                    if (core.Length == 0)
                        continue;
                    if (!_modelCatalog.TryGetValue(core, out var providers))
                        _modelCatalog[core] = providers = new Dictionary<string, string>(StringComparer.Ordinal);
                    if (!providers.TryGetValue(literal, out var existing) || existing != modelId)
                    {
                        providers[literal] = modelId;
                        _catalogDirty = true;
                    }
                }
                MaybeSaveCatalog();
            }
        }

        // The learned catalog persists across restarts so providers don't have to be re-learned (which is
        // what made the model picker briefly mis-resolve right after a relaunch). Caller holds Gate.
        private static void EnsureCatalogLoaded()
        {
            if (_catalogLoaded)
                return;
            _catalogLoaded = true;
            try
            {
                var path = CatalogPath();
                if (!File.Exists(path))
                    return;
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                foreach (var coreProp in doc.RootElement.EnumerateObject())
                {
                    if (coreProp.Value.ValueKind != JsonValueKind.Object)
                        continue;
                    var providers = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach (var p in coreProp.Value.EnumerateObject())
                        if (p.Value.ValueKind == JsonValueKind.String && p.Value.GetString() is { Length: > 0 } id)
                            providers[p.Name] = id;
                    if (providers.Count > 0)
                        _modelCatalog[coreProp.Name] = providers;
                }
            }
            catch (Exception ex) { Diagnostics.Log.Debug($"[synara] catalog load failed: {ex.Message}"); }
        }

        private static void MaybeSaveCatalog()
        {
            if (!_catalogDirty || DateTime.UtcNow - _catalogLastSaveUtc < CatalogSaveThrottle)
                return;
            _catalogDirty = false;
            _catalogLastSaveUtc = DateTime.UtcNow;
            try
            {
                var path = CatalogPath();
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using var stream = File.Create(path);
                using var writer = new Utf8JsonWriter(stream);
                writer.WriteStartObject();
                foreach (var (core, providers) in _modelCatalog)
                {
                    writer.WriteStartObject(core);
                    foreach (var (literal, modelId) in providers)
                        writer.WriteString(literal, modelId);
                    writer.WriteEndObject();
                }
                writer.WriteEndObject();
            }
            catch (Exception ex) { Diagnostics.Log.Debug($"[synara] catalog save failed: {ex.Message}"); }
        }

        /// <summary>
        /// Instant provider resolution from the on-screen model, using the learned catalog. Returns a
        /// selection only when the model's core maps to exactly one provider literal (unique) — otherwise
        /// null (shared/unknown), so the caller falls back to the authoritative active-provider selection.
        /// </summary>
        private static SynaraSelection? ResolveFromCatalog(
            string onScreenModel, string? focusedThreadId, ThreadLockContext? ctx, bool includeThreadTitle)
        {
            var core = ModelCore(onScreenModel);
            if (core.Length == 0)
                return null;

            string? literal = null, modelId = null;
            lock (Gate)
            {
                if (_modelCatalog.TryGetValue(core, out var providers) && providers.Count > 0)
                {
                    if (providers.Count == 1)
                    {
                        var only = providers.First();
                        literal = only.Key;
                        modelId = only.Value;
                    }
                    else
                    {
                        // Shared across providers. Tiebreaker: a STARTED focused thread is locked to its
                        // running provider (ChatView lock) — and a locked thread can't change provider, so
                        // whatever is on screen IS that provider. Use it only when it's one of the shared
                        // candidates, so we never invent a provider the model can't belong to.
                        var locked = ctx is { HasStarted: true } ? (ctx.SessionProvider ?? ctx.ThreadProvider) : null;
                        if (locked is { Length: > 0 } && providers.TryGetValue(locked, out var lockedModel))
                        {
                            literal = locked;
                            modelId = lockedModel;
                            LogByModel(onScreenModel, core, providers.Count, $"lock={locked}");
                        }
                        else
                        {
                            LogByModel(onScreenModel, core, providers.Count);
                            return null; // genuinely ambiguous — defer to authoritative selection.
                        }
                    }
                }
                else
                {
                    LogByModel(onScreenModel, core, 0);
                    return null;
                }
            }

            if (literal is null || modelId is null || MapProvider(literal, modelId) is not { } provider)
                return null;

            LogByModel(onScreenModel, core, 1, $"{provider}/{modelId}");
            var title = focusedThreadId is { Length: > 0 }
                ? (includeThreadTitle ? GetThreadTitle(focusedThreadId) : TryGetCachedThreadTitle(focusedThreadId))
                : null;
            return new SynaraSelection(provider, literal, modelId, title);
        }

        /// <summary>Bare alphanumeric core of a model id/name (after any "&lt;origin&gt;/" prefix).</summary>
        private static string ModelCore(string model)
        {
            var s = model;
            var slash = s.LastIndexOf('/');
            if (slash >= 0 && slash < s.Length - 1)
                s = s[(slash + 1)..];
            return AlphaNumCore(s);
        }

        private static string? _lastByModelLog;
        private static void LogByModel(string onScreen, string core, int providerCount, string? hit = null)
        {
            var line = $"onScreen='{onScreen}' core={core} providers={providerCount} {(hit ?? "fallback")}";
            if (line == _lastByModelLog) return;
            _lastByModelLog = line;
            Diagnostics.Log.Debug($"[synara] by-model {line}");
        }

        private static string? _lastResolutionLog;

        /// <summary>Throttled debug trace of the candidate resolution (only logs when it changes).</summary>
        private static void LogResolution(
            string? focusedThreadId, SynaraSelection? focused, SynaraSelection? sticky, string? onScreenModel)
        {
            static string D(SynaraSelection? s) => s is null ? "none" : $"{s.Provider}/{s.Model}";
            var line = $"focused={focusedThreadId?[..Math.Min(8, focusedThreadId.Length)] ?? "none"} "
                + $"focusedSel={D(focused)} stickySel={D(sticky)} onScreen={onScreenModel ?? "null"}";
            if (line == _lastResolutionLog)
                return;
            _lastResolutionLog = line;
            Diagnostics.Log.Debug($"[synara] resolve {line}");
        }

        /// <summary>Thread title for tooltips, cached per id (titles rarely change; avoids per-tick SQLite).</summary>
        private static string? GetThreadTitle(string threadId)
        {
            lock (Gate)
            {
                if (ThreadTitleCache.TryGetValue(threadId, out var cached))
                    return cached;
            }

            string? title = null;
            try
            {
                var dbPath = GetStateDbPath();
                if (dbPath != null)
                {
                    using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadWrite;Cache=Private");
                    conn.Open();
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = "SELECT title FROM projection_threads WHERE thread_id = $id LIMIT 1";
                    cmd.Parameters.AddWithValue("$id", threadId);
                    if (cmd.ExecuteScalar() is string t && t.Length > 0)
                        title = t;
                }
            }
            catch { /* title is cosmetic */ }

            lock (Gate)
                ThreadTitleCache[threadId] = title;
            return title;
        }

        private static string? TryGetCachedThreadTitle(string threadId)
        {
            lock (Gate)
                return ThreadTitleCache.TryGetValue(threadId, out var cached) ? cached : null;
        }

        /// <summary>
        /// Locate <c>state.sqlite</c>. Synara's base dir is <c>$SYNARA_HOME</c> with legacy
        /// <c>DPCODE_HOME</c> / <c>T3CODE_HOME</c> fallbacks; the desktop build writes to
        /// <c>userdata</c> and the dev build to <c>dev</c>.
        /// </summary>
        internal static string? GetStateDbPath()
        {
            foreach (var home in GetStateRoots())
            {
                foreach (var profile in new[] { "userdata", "dev" })
                {
                    var candidate = Path.Combine(home, profile, "state.sqlite");
                    if (File.Exists(candidate))
                        return candidate;
                }
            }

            return null;
        }

        private static IEnumerable<string> GetStateRoots()
        {
            var roots = new List<string>();
            foreach (var envName in new[] { "SYNARA_HOME", "DPCODE_HOME", "T3CODE_HOME" })
            {
                var value = Environment.GetEnvironmentVariable(envName)?.Trim();
                if (!string.IsNullOrEmpty(value))
                    roots.Add(value);
            }

            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            roots.Add(Path.Combine(userProfile, ".synara"));
            roots.Add(Path.Combine(userProfile, ".dpcode"));
            roots.Add(Path.Combine(userProfile, ".t3code"));

            return roots.Distinct(StringComparer.OrdinalIgnoreCase);
        }

        private static DateTime LatestWriteUtc(string dbPath)
        {
            var latest = File.GetLastWriteTimeUtc(dbPath);
            foreach (var suffix in new[] { "-wal", "-shm" })
            {
                var side = dbPath + suffix;
                if (File.Exists(side))
                {
                    var w = File.GetLastWriteTimeUtc(side);
                    if (w > latest) latest = w;
                }
            }
            return latest;
        }

        /// <summary>
        /// Synara's effective-provider inputs for one thread, read from the projection tables: the
        /// session provider (set once a session starts), the thread's own model selection, its project's
        /// default, and whether the thread has started (a started thread LOCKS the provider ahead of the
        /// composer draft — see ChatView.tsx).
        /// </summary>
        private sealed record ThreadLockContext(
            bool Exists,
            string? Title,
            string? SessionProvider,
            string? ThreadProvider,
            string? ThreadModel,
            string? ProjectProvider,
            string? ProjectModel,
            bool HasStarted);

        // Cache the per-thread SQLite read, keyed on the focused thread + the DB's newest write time.
        private static string? _lockCtxThreadId;
        private static DateTime _lockCtxWriteUtc = DateTime.MinValue;
        private static ThreadLockContext? _lockCtx;

        private static ThreadLockContext? GetThreadLockContext(string dbPath, string threadId)
        {
            var writeUtc = LatestWriteUtc(dbPath);
            lock (Gate)
            {
                if (_lockCtxThreadId == threadId && _lockCtxWriteUtc == writeUtc)
                    return _lockCtx;
            }

            var ctx = ReadThreadLockContext(dbPath, threadId);
            lock (Gate)
            {
                _lockCtxThreadId = threadId;
                _lockCtxWriteUtc = writeUtc;
                _lockCtx = ctx;
            }
            return ctx;
        }

        private static ThreadLockContext? ReadThreadLockContext(string dbPath, string threadId)
        {
            // ReadWrite (not ReadOnly): a read-only SQLite connection can't attach the WAL's shared-memory
            // index, so it would read stale pre-WAL data. We only ever SELECT, so no writes happen.
            using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadWrite;Cache=Private");
            conn.Open();
            using (var pragma = conn.CreateCommand())
            {
                pragma.CommandText = "PRAGMA query_only = ON;";
                pragma.ExecuteNonQuery();
            }

            string? title = null, threadModelJson = null, projectId = null;
            bool exists = false, hasLatestTurn = false;
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT title, project_id, latest_turn_id, model_selection_json FROM projection_threads " +
                    "WHERE thread_id = $id AND deleted_at IS NULL LIMIT 1";
                cmd.Parameters.AddWithValue("$id", threadId);
                using var reader = cmd.ExecuteReader();
                if (reader.Read())
                {
                    exists = true;
                    title = reader.IsDBNull(0) ? null : reader.GetString(0);
                    projectId = reader.IsDBNull(1) ? null : reader.GetString(1);
                    hasLatestTurn = !reader.IsDBNull(2);
                    threadModelJson = reader.IsDBNull(3) ? null : reader.GetString(3);
                }
            }

            string? sessionProvider = null;
            bool hasSession = false;
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT provider_name FROM projection_thread_sessions WHERE thread_id = $id LIMIT 1";
                cmd.Parameters.AddWithValue("$id", threadId);
                using var reader = cmd.ExecuteReader();
                if (reader.Read())
                {
                    hasSession = true;
                    sessionProvider = reader.IsDBNull(0) ? null : reader.GetString(0);
                }
            }

            bool hasMessages = false;
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT 1 FROM projection_thread_messages WHERE thread_id = $id LIMIT 1";
                cmd.Parameters.AddWithValue("$id", threadId);
                hasMessages = cmd.ExecuteScalar() != null;
            }

            string? projectModelJson = null;
            if (projectId is { Length: > 0 })
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT default_model_selection_json FROM projection_projects WHERE project_id = $pid LIMIT 1";
                cmd.Parameters.AddWithValue("$pid", projectId);
                if (cmd.ExecuteScalar() is string j && j.Length > 0)
                    projectModelJson = j;
            }

            var (threadProvider, threadModel) = ParseProviderModel(threadModelJson);
            var (projectProvider, projectModel) = ParseProviderModel(projectModelJson);
            var hasStarted = hasLatestTurn || hasSession || hasMessages;

            return new ThreadLockContext(
                exists, title, sessionProvider,
                threadProvider, threadModel, projectProvider, projectModel, hasStarted);
        }

        /// <summary>
        /// Resolve the focused thread's effective provider and model exactly as Synara's ChatView does:
        /// <c>lockedProvider = started ? (session ?? thread ?? project ?? draft) : null</c>;
        /// <c>effective = lockedProvider ?? draft ?? thread/project</c>. The model for that provider comes
        /// from the draft's per-provider map first, then the thread/project default if it names the same
        /// provider. Returns null when the effective provider isn't one we track.
        /// </summary>
        private static SynaraSelection? ResolveFocusedSelection(
            string threadId,
            SynaraComposerDraftReader.DraftSelection? draft,
            ThreadLockContext ctx,
            bool includeThreadTitle)
        {
            var draftActive = draft?.ProviderLiteral is { Length: > 0 } dp ? dp : null;
            var threadProvider = ctx.ThreadProvider ?? ctx.ProjectProvider;

            string? lockedProvider = ctx.HasStarted
                ? (ctx.SessionProvider ?? threadProvider ?? draftActive)
                : null;
            var effective = lockedProvider ?? draftActive ?? threadProvider;
            if (string.IsNullOrEmpty(effective))
                return null;

            var model = draft?.ModelFor(effective)
                ?? (string.Equals(ctx.ThreadProvider, effective, StringComparison.OrdinalIgnoreCase) ? ctx.ThreadModel : null)
                ?? (string.Equals(ctx.ProjectProvider, effective, StringComparison.OrdinalIgnoreCase) ? ctx.ProjectModel : null);

            if (MapProvider(effective, model) is not { } provider)
                return null;

            var title = includeThreadTitle ? (ctx.Title ?? GetThreadTitle(threadId)) : TryGetCachedThreadTitle(threadId);
            return new SynaraSelection(provider, effective, model, title);
        }

        /// <summary>Parse <c>{"provider":"...","model":"..."}</c> into (provider, model); nulls when absent.</summary>
        private static (string? Provider, string? Model) ParseProviderModel(string? json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return (null, null);
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                    return (null, null);
                string? provider = doc.RootElement.TryGetProperty("provider", out var p) && p.ValueKind == JsonValueKind.String
                    ? p.GetString() : null;
                string? model = doc.RootElement.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.String
                    ? m.GetString() : null;
                return (provider, model);
            }
            catch (JsonException)
            {
                return (null, null);
            }
        }

        private static SynaraSelection? ReadNewestSelection(string dbPath)
        {
            using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadWrite;Cache=Private");
            conn.Open();
            using (var pragma = conn.CreateCommand())
            {
                pragma.CommandText = "PRAGMA query_only = ON;";
                pragma.ExecuteNonQuery();
            }

            using var cmd = conn.CreateCommand();
            // Most recently touched visible thread. Switching the provider bumps updated_at (not
            // latest_user_message_at), so order by the MAX of both timestamps.
            cmd.CommandText =
                "SELECT thread_id, title, model_selection_json FROM projection_threads " +
                "WHERE deleted_at IS NULL AND archived_at IS NULL AND model_selection_json IS NOT NULL " +
                "ORDER BY MAX(COALESCE(latest_user_message_at, ''), COALESCE(updated_at, '')) DESC LIMIT 1";

            string? threadId, title, modelJson;
            using (var reader = cmd.ExecuteReader())
            {
                if (!reader.Read())
                    return null;
                threadId = reader.IsDBNull(0) ? null : reader.GetString(0);
                title = reader.IsDBNull(1) ? null : reader.GetString(1);
                modelJson = reader.IsDBNull(2) ? null : reader.GetString(2);
            }

            return ResolveSelection(threadId, modelJson, title, requireKnownThread: true);
        }

        /// <summary>
        /// Combine the persisted thread selection with the live composer draft. The dropdown's draft
        /// (<c>activeProvider</c>) is the realtime signal — it changes the instant the user switches
        /// providers, before any turn runs — so it takes precedence when it maps to a tracked provider.
        /// Falls back to the persisted <c>model_selection_json</c> when there's no usable draft.
        /// <paramref name="requireKnownThread"/> guards the fallback: when the thread isn't in the DB
        /// (draft-only), only the draft can produce a selection.
        /// </summary>
        internal static SynaraSelection? ResolveSelection(
            string? threadId,
            string? modelSelectionJson,
            string? threadTitle,
            bool requireKnownThread = true)
        {
            if (threadId is { Length: > 0 }
                && SynaraComposerDraftReader.TryGetDraft(threadId) is { } draft
                && MapProvider(draft.ProviderLiteral, draft.Model) is { } draftProvider)
            {
                return new SynaraSelection(draftProvider, draft.ProviderLiteral, draft.Model, threadTitle);
            }

            return requireKnownThread ? ParseSelection(modelSelectionJson, threadTitle) : null;
        }

        /// <summary>
        /// Parse Synara's <c>model_selection_json</c> (<c>{"provider":"opencode","model":"opencode-go/kimi"}</c>)
        /// into a Win-CodexBar provider. Pure for unit testing. Returns null for unsupported providers.
        /// </summary>
        internal static SynaraSelection? ParseSelection(string? modelSelectionJson, string? threadTitle)
        {
            if (string.IsNullOrWhiteSpace(modelSelectionJson))
                return null;

            string providerLiteral;
            string? model = null;
            try
            {
                using var doc = JsonDocument.Parse(modelSelectionJson);
                if (doc.RootElement.ValueKind != JsonValueKind.Object
                    || !doc.RootElement.TryGetProperty("provider", out var providerProp)
                    || providerProp.ValueKind != JsonValueKind.String)
                    return null;

                providerLiteral = providerProp.GetString() ?? string.Empty;
                if (doc.RootElement.TryGetProperty("model", out var modelProp) && modelProp.ValueKind == JsonValueKind.String)
                    model = modelProp.GetString();
            }
            catch (JsonException)
            {
                return null;
            }

            if (MapProvider(providerLiteral, model) is not { } provider)
                return null;

            return new SynaraSelection(provider, providerLiteral, model, threadTitle);
        }

        /// <summary>
        /// Map Synara's provider literal (plus model id, to split OpenCode vs OpenCode Go) to a tracked
        /// <see cref="ProviderId"/>. Gemini, Kilo, and Pi have no usage backend and return null.
        /// </summary>
        internal static ProviderId? MapProvider(string? providerLiteral, string? model)
        {
            switch (providerLiteral?.Trim().ToLowerInvariant())
            {
                case "codex":
                    return ProviderId.Codex;
                case "claudeagent":
                    return ProviderId.Claude;
                case "cursor":
                    return ProviderId.Cursor;
                case "grok":
                    return ProviderId.Grok;
                case "opencode":
                    // Synara prefixes the model with the underlying provider id; "opencode-go/..." is the
                    // OpenCode Go (subscription) backend, everything else is OpenCode (zen/BYOK).
                    return model != null && model.TrimStart().StartsWith("opencode-go/", StringComparison.OrdinalIgnoreCase)
                        ? ProviderId.OpenCodeGo
                        : ProviderId.OpenCode;
                default:
                    return null;
            }
        }

        /// <summary>
        /// True when a stored model id matches the model name shown live on Synara's composer button.
        /// Synara's display name ("Claude Sonnet 4.6", "Deepseek V4 Flash") and the stored id
        /// ("claude-sonnet-4-6", "opencode-go/deepseek-v4-flash", "openai/gpt-5") differ in separators
        /// and an optional provider prefix, so both are reduced to their bare alphanumeric core before
        /// comparing — robust to '-' vs '.' vs ' ' and the "&lt;origin&gt;/" prefix.
        /// </summary>
        internal static bool ModelMatchesOnScreen(string? storedModelId, string onScreenModel)
        {
            if (string.IsNullOrEmpty(storedModelId) || string.IsNullOrEmpty(onScreenModel))
                return false;

            var id = storedModelId;
            var slash = id.LastIndexOf('/');
            if (slash >= 0 && slash < id.Length - 1)
                id = id[(slash + 1)..];

            return string.Equals(AlphaNumCore(id), AlphaNumCore(onScreenModel), StringComparison.Ordinal);
        }

        private static string AlphaNumCore(string s)
        {
            var sb = new System.Text.StringBuilder(s.Length);
            foreach (var c in s)
                if (char.IsLetterOrDigit(c))
                    sb.Append(char.ToLowerInvariant(c));
            return sb.ToString();
        }

        internal static void ResetCacheForTesting()
        {
            lock (Gate)
            {
                _cachedDbPath = null;
                _cachedDbWriteUtc = DateTime.MinValue;
                _cachedSelection = null;
            }
        }
    }
}
