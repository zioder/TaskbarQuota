using TaskbarQuota.Browser;

namespace TaskbarQuota.Tests;

public class CookieExtractorTests
{
    [Fact]
    public void CookieSource_HeaderPreservesSeparateCookiePairs()
    {
        var source = new CookieExtractor.CookieSource(
            "Firefox",
            "default-release",
            new[]
            {
                (Name: "auth", Value: "session-token"),
                (Name: "auth.0", Value: "chunk-a"),
                (Name: "auth.1", Value: "chunk-b"),
            });

        Assert.Equal("auth=session-token; auth.0=chunk-a; auth.1=chunk-b", source.Header);
    }
}
