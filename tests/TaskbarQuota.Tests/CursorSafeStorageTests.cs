using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TaskbarQuota.Usage.Providers;
using Xunit;

namespace TaskbarQuota.Tests;

/// <summary>
/// Regression tests for Cursor 3.27+ <c>cursor-safe-storage:v1</c> token envelopes and plain JWT passthrough.
/// </summary>
public sealed class CursorSafeStorageTests
{
  /// <summary>Builds a minimal unsigned JWT for fixtures.</summary>
  private static string Jwt(string payloadJson)
  {
    static string Encode(string value) => Convert.ToBase64String(
        Encoding.UTF8.GetBytes(value))
      .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    return $"{Encode("{\"alg\":\"none\"}")}.{Encode(payloadJson)}.sig";
  }

  /// <summary>Writes a Chromium-style <c>Local State</c> whose <c>os_crypt.encrypted_key</c> wraps <paramref name="aesKey"/> via DPAPI.</summary>
  private static string WriteLocalStateWithKey(byte[] aesKey)
  {
    var protectedKey = ProtectedData.Protect(aesKey, null, DataProtectionScope.CurrentUser);
    var dpapiBlob = new byte[5 + protectedKey.Length];
    Encoding.ASCII.GetBytes("DPAPI").CopyTo(dpapiBlob, 0);
    protectedKey.CopyTo(dpapiBlob, 5);

    var json = JsonSerializer.Serialize(new
    {
      os_crypt = new { encrypted_key = Convert.ToBase64String(dpapiBlob) },
    });
    var path = Path.Combine(Path.GetTempPath(), $"tq-cursor-local-state-{Guid.NewGuid():N}.json");
    File.WriteAllText(path, json);
    return path;
  }

  /// <summary>Encrypts <paramref name="plaintext"/> as an Electron OSCrypt <c>v10</c> blob (prefix + nonce + ciphertext + tag).</summary>
  private static byte[] EncryptV10(byte[] aesKey, string plaintext)
  {
    var nonce = new byte[12];
    RandomNumberGenerator.Fill(nonce);
    var plain = Encoding.UTF8.GetBytes(plaintext);
    var ciphertext = new byte[plain.Length];
    var tag = new byte[16];
    using var gcm = new AesGcm(aesKey, 16);
    gcm.Encrypt(nonce, plain, ciphertext, tag);

    var blob = new byte[3 + nonce.Length + ciphertext.Length + tag.Length];
    Encoding.ASCII.GetBytes("v10").CopyTo(blob, 0);
    nonce.CopyTo(blob, 3);
    ciphertext.CopyTo(blob, 15);
    tag.CopyTo(blob, 15 + ciphertext.Length);
    return blob;
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

  /// <summary>
  /// Hermetic round-trip: synthetic <c>v10</c> blob + temporary <c>Local State</c> must yield the original JWT
  /// without an installed Cursor session or a plain token in the database.
  /// </summary>
  [Fact]
  public void DecodeStoredAuthSecret_DecryptsV10EnvelopeWithLocalState()
  {
    var jwt = Jwt("{\"sub\":\"auth0|user_hermetic\",\"exp\":9999999999}");
    var aesKey = new byte[32];
    RandomNumberGenerator.Fill(aesKey);
    var localState = WriteLocalStateWithKey(aesKey);
    try
    {
      var stored = "cursor-safe-storage:v1:" + Convert.ToBase64String(EncryptV10(aesKey, jwt));
      Assert.Equal(jwt, CursorProvider.DecodeStoredAuthSecret(stored, localState));
    }
    finally
    {
      File.Delete(localState);
    }
  }

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
