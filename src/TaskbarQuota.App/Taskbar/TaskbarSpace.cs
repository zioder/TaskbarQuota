using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace TaskbarQuota.Taskbar;

/// <summary>
/// The free width each taskbar widget last measured for itself, shared so the pin budget can answer
/// "would this fit?" without owning a widget.
///
/// The measurement only exists in <see cref="TaskBarWidget"/> — it comes from the gap solver that already
/// knows where the shell's own elements are — while the decision to allow a pin is made in the dashboard,
/// which has no widget. Rather than a fixed guess at how much room a taskbar has, the real number is
/// published here as it changes.
/// </summary>
internal static class TaskbarSpace
{
    /// <summary>Assumed free width before any measurement has been taken.</summary>
    public const int UnknownWidth = 0;

    /// <summary>
    /// Compatibility fallback for callers that do not have a display identity. It is kept at the widest
    /// reported span so a secondary display can never overwrite a wider primary measurement.
    /// </summary>
    public static int AvailableLogicalWidth
    {
        get => Volatile.Read(ref compatibilityWidth);
        set => Volatile.Write(ref compatibilityWidth, Math.Max(UnknownWidth, value));
    }

    /// <summary>Display identities for which a widget has reported a measurement or an unknown width.</summary>
    public static IReadOnlyList<string> KnownDisplayKeys
        => AvailableByDisplay.Keys.OrderBy(key => key, StringComparer.OrdinalIgnoreCase).ToArray();

    /// <summary>The display identity marked primary by the most recent widget registration.</summary>
    public static string? PrimaryDisplayKey
        => Volatile.Read(ref primaryDisplayKey);

    /// <summary>Forgets taskbar geometry when no injected widget remains to keep it current.</summary>
    public static void ResetAvailableWidth()
    {
        AvailableByDisplay.Clear();
        Volatile.Write(ref primaryDisplayKey, null);
        Volatile.Write(ref compatibilityWidth, UnknownWidth);
    }

    /// <summary>
    /// Publishes the free logical width for one display. A zero width is retained as an identity marker but
    /// remains unknown to fit decisions, which keeps a transient geometry failure permissive.
    /// </summary>
    public static void ReportAvailableWidth(string? displayKey, int logicalWidth, bool isPrimary = false)
    {
        string key = displayKey?.Trim() ?? string.Empty;
        int width = Math.Max(UnknownWidth, logicalWidth);
        if (key.Length == 0)
        {
            if (width > UnknownWidth)
                PublishCompatibilityWidth(width);
            return;
        }

        AvailableByDisplay[key] = width;
        if (isPrimary)
            Volatile.Write(ref primaryDisplayKey, key);
        if (width > UnknownWidth)
            PublishCompatibilityWidth(width);
    }

    /// <summary>Gets a measured width for one display; false means that display is currently unknown.</summary>
    public static bool TryGetAvailableWidth(string? displayKey, out int logicalWidth)
    {
        string key = displayKey?.Trim() ?? string.Empty;
        if (key.Length > 0
            && AvailableByDisplay.TryGetValue(key, out logicalWidth)
            && logicalWidth > UnknownWidth)
        {
            return true;
        }

        logicalWidth = UnknownWidth;
        return false;
    }

    /// <summary>Removes a display after its taskbar widget is disposed.</summary>
    public static void ForgetDisplay(string? displayKey)
    {
        string key = displayKey?.Trim() ?? string.Empty;
        if (key.Length == 0)
            return;

        AvailableByDisplay.TryRemove(key, out _);
        if (string.Equals(Volatile.Read(ref primaryDisplayKey), key, StringComparison.OrdinalIgnoreCase))
            Volatile.Write(ref primaryDisplayKey, null);

        int widest = AvailableByDisplay.Values.DefaultIfEmpty(UnknownWidth).Max();
        Volatile.Write(ref compatibilityWidth, widest);
    }

    // Real rendered width per provider. The budget used to model this — an icon plus a column group per
    // two rows — which is close but not close enough: a Credits row or a long label puts a tile tens of
    // pixels over the model, and at that error a set that genuinely fits gets refused. The widget measures
    // every tile it lays out anyway, so the exact numbers are recorded here and used in preference.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Usage.ProviderId, int> Measured = new();
    private static readonly ConcurrentDictionary<string, int> AvailableByDisplay =
        new(StringComparer.OrdinalIgnoreCase);
    private static int compatibilityWidth = UnknownWidth;
    private static string? primaryDisplayKey;

    private static void PublishCompatibilityWidth(int width)
    {
        while (true)
        {
            int current = Volatile.Read(ref compatibilityWidth);
            if (current >= width
                || Interlocked.CompareExchange(ref compatibilityWidth, width, current) == current)
            {
                return;
            }
        }
    }

    public static void RecordTileWidth(Usage.ProviderId provider, int logicalWidth)
    {
        if (logicalWidth > 0)
            Measured[provider] = logicalWidth;
    }

    public static bool TryGetTileWidth(Usage.ProviderId provider, out int logicalWidth)
        => Measured.TryGetValue(provider, out logicalWidth);
}
