using TaskbarQuota.Usage;

namespace TaskbarQuota.Tests;

public sealed class CredentialStorePersistenceTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"TaskbarQuota-CredentialStoreTests-{Guid.NewGuid():N}");

    [Fact]
    public void Save_PersistsCredentialsAcrossInstances()
    {
        var first = new CredentialStore(_directory);
        first.For(ProviderId.Copilot).ApiKey = "copilot-token";
        first.For(ProviderId.Cursor).CookieHeader = "WorkosCursorSessionToken=cursor-cookie";

        first.Save();
        var reloaded = new CredentialStore(_directory);

        Assert.Equal("copilot-token", reloaded.For(ProviderId.Copilot).ApiKey);
        Assert.Equal("WorkosCursorSessionToken=cursor-cookie", reloaded.For(ProviderId.Cursor).CookieHeader);
    }

    [Fact]
    public void Load_UsesBackupWhenPrimaryFileIsCorrupt()
    {
        var store = new CredentialStore(_directory);
        store.For(ProviderId.Copilot).ApiKey = "preserved-token";
        store.Save();

        // A second atomic save creates credentials.json.bak from the last known-good file.
        store.For(ProviderId.Copilot).ApiKey = "new-token";
        store.Save();
        File.WriteAllText(Path.Combine(_directory, "credentials.json"), "{broken json");

        var recovered = new CredentialStore(_directory);

        Assert.Equal("preserved-token", recovered.For(ProviderId.Copilot).ApiKey);
    }

    [Fact]
    public void Save_ReplacesAnExistingBackupOnLaterSaves()
    {
        var store = new CredentialStore(_directory);
        store.For(ProviderId.Copilot).ApiKey = "first";
        store.Save();
        store.For(ProviderId.Copilot).ApiKey = "second";
        store.Save();
        store.For(ProviderId.Copilot).ApiKey = "third";

        store.Save();

        var reloaded = new CredentialStore(_directory);
        Assert.Equal("third", reloaded.For(ProviderId.Copilot).ApiKey);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }
}
