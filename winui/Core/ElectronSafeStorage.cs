using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace QuotaLens.Core;

/// <summary>
/// Read-only decoder for Electron/Chromium safeStorage.v1 values on Windows.
/// The profile master key is unwrapped with current-user DPAPI, then v10/v11
/// payloads are decrypted with AES-256-GCM. No provider schema lives here.
/// </summary>
internal static class ElectronSafeStorage
{
    private static readonly byte[] DpapiPrefix = Encoding.ASCII.GetBytes("DPAPI");

    internal static string? TryDecryptString(
        string? encryptedData,
        string localStatePath,
        Func<byte[], byte[]>? unprotectKey = null)
    {
        if (string.IsNullOrWhiteSpace(encryptedData) || !File.Exists(localStatePath))
            return null;

        byte[]? masterKey = null;
        byte[]? plaintext = null;
        try
        {
            using var state = JsonDocument.Parse(File.ReadAllText(localStatePath));
            if (!state.RootElement.TryGetProperty("os_crypt", out var osCrypt)
                || !osCrypt.TryGetProperty("encrypted_key", out var encryptedKeyElement)
                || encryptedKeyElement.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var wrappedKey = Convert.FromBase64String(encryptedKeyElement.GetString()!);
            if (!wrappedKey.AsSpan().StartsWith(DpapiPrefix))
                return null;

            var protectedKey = wrappedKey[DpapiPrefix.Length..];
            masterKey = (unprotectKey ?? UnprotectCurrentUser)(protectedKey);
            if (masterKey.Length != 32)
                return null;

            var payload = Convert.FromBase64String(encryptedData);
            if (payload.Length < 3 + 12 + 16
                || payload[0] != (byte)'v'
                || payload[1] != (byte)'1'
                || (payload[2] != (byte)'0' && payload[2] != (byte)'1'))
            {
                return null;
            }

            var nonce = payload.AsSpan(3, 12);
            var cipherLength = payload.Length - 3 - 12 - 16;
            var cipher = payload.AsSpan(15, cipherLength);
            var tag = payload.AsSpan(payload.Length - 16, 16);
            plaintext = new byte[cipherLength];
            using var aes = new AesGcm(masterKey, tagSizeInBytes: 16);
            aes.Decrypt(nonce, cipher, tag, plaintext);
            return Encoding.UTF8.GetString(plaintext);
        }
        catch (Exception error) when (error is IOException
            or UnauthorizedAccessException
            or JsonException
            or FormatException
            or CryptographicException
            or ArgumentException)
        {
            return null;
        }
        finally
        {
            if (masterKey is not null)
                CryptographicOperations.ZeroMemory(masterKey);
            if (plaintext is not null)
                CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private static byte[] UnprotectCurrentUser(byte[] protectedKey) =>
        ProtectedData.Unprotect(protectedKey, optionalEntropy: null, DataProtectionScope.CurrentUser);
}
