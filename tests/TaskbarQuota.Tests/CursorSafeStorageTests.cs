using System;
using System.IO;
using Microsoft.Data.Sqlite;
using TaskbarQuota.Usage.Providers;
using Xunit;

namespace TaskbarQuota.Tests;

public sealed class CursorSafeStorageTests
{
  private static string Jwt(string payloadJson)
  {
    static string Encode(string value) => Convert.ToBase64String(
        System.Text.Encoding.UTF8.GetBytes(value))
      .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    return $"{Encode("{\"alg\":\"none\"}")}.{Encode(payloadJson)}.sig";
  }

  [Fact]
  public void DecodeStoredAuthSecret_PassThroughPlainJwt()
  {
    var token = Jwt("{\"sub\":\"auth0|user_01\",\"exp\":9999999999}");
    Assert.Equal(token, CursorProvider.DecodeStoredAuthSecret(token, "missing"));
  }

  [Fact]
  public void DecodeStoredAuthSecret_ReturnsNullForOpaqueEnvelopeWithoutLocalState()
    => Assert.Null(CursorProvider.DecodeStoredAuthSecret("cursor-safe-storage:v1:YQ==", "missing"));

  [Fact]
  public void Decode_live_cursor_access_token_when_installed()
  {
    var db = Path.Combine(
      Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
      "Cursor", "User", "globalStorage", "state.vscdb");
    if (!File.Exists(db))
      return;

    string? raw;
    using (var conn = new SqliteConnection($"Data Source={db};Mode=ReadOnly;Cache=Private"))
    {
      conn.Open();
      using var cmd = conn.CreateCommand();
      cmd.CommandText = "SELECT value FROM ItemTable WHERE key = 'cursorAuth/accessToken' LIMIT 1";
      raw = cmd.ExecuteScalar() as string;
    }

    if (string.IsNullOrWhiteSpace(raw))
      return;

    var localState = Path.Combine(
      Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
      "Cursor", "Local State");
    var jwt = CursorProvider.DecodeStoredAuthSecret(raw, localState);
    Assert.False(string.IsNullOrWhiteSpace(jwt));
    Assert.Contains('.', jwt);
    Assert.Equal("eyJ", jwt![..3], StringComparer.Ordinal);
  }
}
