using TaskbarQuota.Services;

namespace TaskbarQuota.Tests;

public class ClaudeOAuthTests
{
    [Fact]
    public void CreateLoginRequest_UsesClaudeCodeAuthorizeShape()
    {
        var request = ClaudeOAuth.CreateLoginRequest();
        var uri = new Uri(request.AuthorizeUrl);
        var query = ParseQuery(uri.Query);

        Assert.Equal("https", uri.Scheme);
        Assert.Equal("claude.com", uri.Host);
        Assert.Equal("/cai/oauth/authorize", uri.AbsolutePath);
        Assert.Equal("9d1c250a-e61b-44d9-88ed-5944d1962f5e", query["client_id"]);
        Assert.Equal("https://platform.claude.com/oauth/code/callback", query["redirect_uri"]);
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.Equal(43, request.CodeVerifier.Length);
        Assert.Equal(43, request.State.Length);
        Assert.Equal(request.CodeVerifier, request.State);
    }

    [Theory]
    [InlineData("abc123#state", "abc123")]
    [InlineData("https://platform.claude.com/oauth/code/callback?code=abc123&state=xyz", "abc123")]
    public void ExtractCode_AcceptsClaudePasteFormats(string input, string expected)
    {
        Assert.Equal(expected, ClaudeOAuth.ExtractCode(input));
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            var key = eq < 0 ? pair : pair[..eq];
            var value = eq < 0 ? "" : pair[(eq + 1)..];
            map[Uri.UnescapeDataString(key)] = Uri.UnescapeDataString(value);
        }

        return map;
    }
}
