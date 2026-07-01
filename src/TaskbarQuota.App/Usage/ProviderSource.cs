namespace TaskbarQuota.Usage
{
    public enum ProviderSourceKind
    {
        Unknown,
        Browser,
        DesktopApp,
        Cli,
        HostApp,
    }

    public sealed record ProviderSource(
        ProviderSourceKind Kind,
        string? Name = null,
        string? IconKey = null)
    {
        public static ProviderSource Unknown { get; } = new(ProviderSourceKind.Unknown);

        public bool IsKnown => Kind != ProviderSourceKind.Unknown;

        public string DisplayName => string.IsNullOrWhiteSpace(Name)
            ? Kind switch
            {
                ProviderSourceKind.Browser => "browser",
                ProviderSourceKind.DesktopApp => "desktop app",
                ProviderSourceKind.Cli => "terminal",
                ProviderSourceKind.HostApp => "host app",
                _ => string.Empty,
            }
            : Name!;

        public string SourceText => IsKnown ? $"Detected via {DisplayName}" : string.Empty;
        public string ShortViaText => IsKnown ? $"via {DisplayName}" : string.Empty;
    }
}
