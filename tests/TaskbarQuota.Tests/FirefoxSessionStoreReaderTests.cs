using System.Text;
using TaskbarQuota.ActiveApp;

namespace TaskbarQuota.Tests;

public class FirefoxSessionStoreReaderTests
{
    [Fact]
    public void TryExtractSelectedTabUrl_ReadsSelectedWindowAndTab()
    {
        var json = """
            {
              "selectedWindow": 2,
              "windows": [
                {
                  "selected": 1,
                  "tabs": [
                    { "index": 1, "entries": [ { "url": "https://example.com/" } ] }
                  ]
                },
                {
                  "selected": 2,
                  "tabs": [
                    { "index": 1, "entries": [ { "url": "https://claude.ai/new" } ] },
                    { "index": 2, "entries": [
                      { "url": "https://example.org/old" },
                      { "url": "https://chatgpt.com/c/abc" }
                    ] }
                  ]
                }
              ]
            }
            """;

        Assert.Equal("https://chatgpt.com/c/abc", FirefoxSessionStoreReader.TryExtractSelectedTabUrl(json));
    }

    [Fact]
    public void DecompressLz4Block_DecodesLiteralOnlyBlock()
    {
        var text = "hello";
        var compressed = new byte[] { 0x50 }.Concat(Encoding.UTF8.GetBytes(text)).ToArray();

        var decoded = FirefoxSessionStoreReader.DecompressLz4Block(compressed);

        Assert.Equal(text, Encoding.UTF8.GetString(decoded));
    }

    [Fact]
    public void DecompressLz4Block_DecodesBackReference()
    {
        // Literals "abc", then copy 3 bytes from offset 3 with match length 6: "abcabcabc".
        var compressed = new byte[] { 0x32, (byte)'a', (byte)'b', (byte)'c', 0x03, 0x00 };

        var decoded = FirefoxSessionStoreReader.DecompressLz4Block(compressed);

        Assert.Equal("abcabcabc", Encoding.UTF8.GetString(decoded));
    }
}
