namespace TaskbarQuota.Taskbar;

/// <summary>
/// The free width the taskbar widget last measured for itself, shared so the pin budget can answer
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

    /// <summary>Widest free span on the taskbar, in logical pixels. Zero until first measured.</summary>
    public static int AvailableLogicalWidth { get; set; } = UnknownWidth;

    // Real rendered width per provider. The budget used to model this — an icon plus a column group per
    // two rows — which is close but not close enough: a Credits row or a long label puts a tile tens of
    // pixels over the model, and at that error a set that genuinely fits gets refused. The widget measures
    // every tile it lays out anyway, so the exact numbers are recorded here and used in preference.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Usage.ProviderId, int> Measured = new();

    public static void RecordTileWidth(Usage.ProviderId provider, int logicalWidth)
    {
        if (logicalWidth > 0)
            Measured[provider] = logicalWidth;
    }

    public static bool TryGetTileWidth(Usage.ProviderId provider, out int logicalWidth)
        => Measured.TryGetValue(provider, out logicalWidth);
}
