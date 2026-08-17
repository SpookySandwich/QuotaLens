using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using QuotaLens.Core;

namespace QuotaLens.Providers;

/// <summary>
/// Read-only access to the ZCode credential store (~/.zcode/v2/credentials.json).
///
/// Values are sealed in an "enc:v1:{iv}.{tag}.{ciphertext}" envelope (base64url
/// parts, AES-256-GCM) keyed by SHA256(secret), where the secret is the
/// ZCODE_CREDENTIAL_SECRET env var or the deterministic fallback
/// "zcode-credential-fallback:{node os.platform()}:{homedir}:{username}"
/// (verified against ZCode's app bundle, and confirmed to decrypt the local store).
///
/// READ-ONLY BY POLICY: the store belongs to ZCode; QuotaLens never writes to it
/// and never refreshes its tokens. An unreadable store (future enc:v2, changed
/// scheme, signed out) simply means no local session — the provider falls back to
/// the API-key source and the user renews by opening ZCode.
/// Enforced by ReadOnlyProviderSafetyTests.
/// </summary>
internal static class ZcodeCredentials
{
    private const string EnvelopePrefix = "enc:v1:";
    private const string SecretEnvKey = "ZCODE_CREDENTIAL_SECRET";
    private const string SessionTokenField = "zcodejwttoken";

    internal static string StorePath() => StorePath(null, "");

    /// <summary>
    /// The ZCode credential file, honouring a user-configured data folder so a
    /// non-default install is still readable. Falls back to ~/.zcode.
    /// </summary>
    internal static string StorePath(IConfig? config, string instanceId)
    {
        var configured = config?.GetScoped(instanceId, "zai_home") ?? "";
        var home = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".zcode")
            : Environment.ExpandEnvironmentVariables(configured.Trim().Trim('"'));

        return Path.Combine(home, "v2", "credentials.json");
    }

    /// <summary>True when a locally signed-in ZCode session can be read.</summary>
    internal static bool HasSession() => TryReadSessionToken() is not null;

    /// <summary>The ZCode session JWT, or null when signed out / unreadable.</summary>
    internal static string? TryReadSessionToken() =>
        TryReadSessionToken(StorePath(), secret: null);

    internal static string? TryReadSessionToken(string? filePath, string? secret)
    {
        try
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
                return null;

            using var doc = JsonDocument.Parse(File.ReadAllText(filePath));
            if (!doc.RootElement.TryGetProperty(SessionTokenField, out var field)
                || field.ValueKind != JsonValueKind.String)
                return null;

            return Decrypt(field.GetString()!, secret ?? Secret());
        }
        catch
        {
            return null; // unreadable, new envelope version, or wrong key: no session
        }
    }

    private static string Secret() =>
        Environment.GetEnvironmentVariable(SecretEnvKey) is { Length: > 0 } provided
            ? provided
            : $"zcode-credential-fallback:{NodePlatform()}:" +
              $"{Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)}:{Environment.UserName}";

    // Node's os.platform() spelling — the fallback secret must match ZCode byte for byte.
    private static string NodePlatform() =>
        OperatingSystem.IsWindows() ? "win32"
        : OperatingSystem.IsMacOS() ? "darwin"
        : "linux";

    private static string? Decrypt(string envelope, string secret)
    {
        if (!envelope.StartsWith(EnvelopePrefix, StringComparison.Ordinal))
            return null;

        var parts = envelope[EnvelopePrefix.Length..].Split('.');
        if (parts.Length != 3)
            return null;

        try
        {
            var key = SHA256.HashData(Encoding.UTF8.GetBytes(secret));
            var iv = FromB64Url(parts[0]);
            var tag = FromB64Url(parts[1]);
            var cipher = FromB64Url(parts[2]);
            var plain = new byte[cipher.Length];
            using var aes = new AesGcm(key, tagSizeInBytes: 16);
            aes.Decrypt(iv, cipher, tag, plain, null);
            return Encoding.UTF8.GetString(plain);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Test-only inverse of the envelope, so round-trips use the real scheme.</summary>
    internal static string Encrypt(string plaintext, string secret)
    {
        var key = SHA256.HashData(Encoding.UTF8.GetBytes(secret));
        var iv = RandomNumberGenerator.GetBytes(12);
        var plain = Encoding.UTF8.GetBytes(plaintext);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(key, tagSizeInBytes: 16);
        aes.Encrypt(iv, plain, cipher, tag, null);
        return $"{EnvelopePrefix}{ToB64Url(iv)}.{ToB64Url(tag)}.{ToB64Url(cipher)}";
    }

    private static byte[] FromB64Url(string value)
    {
        var b64 = value.Replace('-', '+').Replace('_', '/');
        while (b64.Length % 4 != 0)
            b64 += "=";
        return Convert.FromBase64String(b64);
    }

    private static string ToB64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
