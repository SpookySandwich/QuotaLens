using System.Globalization;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using QuotaLens.Core;

namespace QuotaLens.Providers;

/// <summary>
/// Alibaba Cloud balance provider. Ports src-tauri/src/providers/alibaba.rs faithfully:
/// signs an RPC-style (v1.0 / HMAC-SHA1) request to the `QueryAccountBalance` action and
/// renders the account balance as the primary "Account Balance" window + a BalanceInfo.
///
/// The Rust source performs a single signed GET against business.aliyuncs.com; the signing
/// is reproduced byte-for-byte (sorted params, RFC3986 percent-encoding, the
/// "GET&%2F&{encoded-canonical}" StringToSign, the "{secret}&" HMAC key, standard base64).
/// </summary>
public sealed class AlibabaProvider : IProvider
{
    public string Type => "alibabacloud";
    public string Name => "Alibaba Cloud";
    public string SourceLabel => "Alibaba Billing API";
    public Confidence Confidence => Confidence.Official;

    /// <summary>
    /// Top-level response from the Alibaba `QueryAccountBalance` action. Alibaba returns
    /// top-level `Code`/`Message` on error and a `Data` object on success.
    /// </summary>
    private sealed class BalanceResponse
    {
        [JsonPropertyName("Code")] public string? Code { get; set; }
        [JsonPropertyName("Message")] public string? Message { get; set; }
        [JsonPropertyName("Data")] public BalanceData? Data { get; set; }
    }

    private sealed class BalanceData
    {
        [JsonPropertyName("AvailableAmount")] public string? AvailableAmount { get; set; }
        [JsonPropertyName("AvailableCashAmount")] public string? AvailableCashAmount { get; set; }
        [JsonPropertyName("Currency")] public string? Currency { get; set; }
    }

    // Alphanumeric set for the 32-char SignatureNonce (matches rand::distributions::Alphanumeric).
    private const string NonceAlphabet =
        "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";

    private static (string Id, string Secret)? GetCreds(string instanceId, IConfig config)
    {
        // Env fallback is disabled for explicit blank scoped keys on a newly added card.
        var id = ProviderConfig.Resolve(instanceId, config, "alibabacloud", "alibabacloud_key_id");
        var secret = ProviderConfig.Resolve(instanceId, config, "alibabacloud", "alibabacloud_key_secret");

        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(secret))
            return null;
        return (id, secret);
    }

    public async Task<ProviderSnapshot> FetchAsync(string instanceId, IConfig config, CancellationToken ct)
    {
        var creds = GetCreds(instanceId, config)
            ?? throw new ProviderException("Not configured: Alibaba Cloud credentials not set. Add them in Settings.");

        // Base params live in a sorted map (Rust BTreeMap); SignRequest adds the common params.
        var parameters = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["Action"] = "QueryAccountBalance",
            ["Version"] = "2017-12-14",
        };
        var url = SignRequest(creds.Id, creds.Secret, parameters);

        HttpResponseMessage resp;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("Accept", "application/json");
            resp = await Http.Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new ProviderException($"Network error: {e.Message}", e);
        }

        string body;
        try
        {
            body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new ProviderException($"Parse error: {e.Message}", e);
        }

        return ParseBalance(body);
    }

    /// <summary>
    /// Reproduces the Rust `sign_request`: builds the full param map, percent-encodes each
    /// key/value, joins into the canonical query string, builds the StringToSign, signs with
    /// HMAC-SHA1 under the "{secret}&" key, base64-encodes, and returns the final signed URL.
    /// </summary>
    private static string SignRequest(string akId, string akSecret, SortedDictionary<string, string> parameters)
    {
        var nonce = RandomNonce(32);
        var timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

        // Copy the base params then add the common params; SortedDictionary keeps them sorted by key.
        var sp = new SortedDictionary<string, string>(parameters, StringComparer.Ordinal)
        {
            ["AccessKeyId"] = akId,
            ["Format"] = "JSON",
            ["SignatureMethod"] = "HMAC-SHA1",
            ["SignatureVersion"] = "1.0",
            ["SignatureNonce"] = nonce,
            ["Timestamp"] = timestamp,
        };

        var canonical = string.Join("&", sp.Select(kv => $"{UrlEncode(kv.Key)}={UrlEncode(kv.Value)}"));
        var stringToSign = $"GET&{UrlEncode("/")}&{UrlEncode(canonical)}";

        var key = $"{akSecret}&";
        using var hmac = new HMACSHA1(Encoding.UTF8.GetBytes(key));
        var digest = hmac.ComputeHash(Encoding.UTF8.GetBytes(stringToSign));
        var sig = Convert.ToBase64String(digest);

        return $"https://business.aliyuncs.com/?{canonical}&Signature={UrlEncode(sig)}";
    }

    /// <summary>
    /// Parse the `QueryAccountBalance` JSON body into a snapshot. Returns "Not available" for a
    /// non-"200" API `Code`, mirroring the Rust behavior.
    /// </summary>
    private ProviderSnapshot ParseBalance(string body)
    {
        BalanceResponse? resp;
        try
        {
            resp = JsonSerializer.Deserialize<BalanceResponse>(body);
        }
        catch (Exception e)
        {
            throw new ProviderException($"Parse error: Invalid response: {e.Message}", e);
        }
        if (resp is null)
            throw new ProviderException("Parse error: Invalid response: empty response");

        if (resp.Code is { } code && code != "200")
        {
            var msg = resp.Message ?? code;
            throw new ProviderException($"Not available: API error: {msg}");
        }

        var data = resp.Data ?? new BalanceData();
        var cash = ParseAmount(data.AvailableCashAmount);
        var available = ParseAmount(data.AvailableAmount);
        var currency = string.IsNullOrEmpty(data.Currency) ? "CNY" : data.Currency!;
        var symbol = currency == "CNY" ? "¥" : "$";
        var desc = $"{symbol}{available.ToString("F2", CultureInfo.InvariantCulture)} available "
                 + $"(cash: {symbol}{cash.ToString("F2", CultureInfo.InvariantCulture)})";

        return new ProviderSnapshot
        {
            ProviderId = Type,
            Name = Name,
            Primary = new RateWindow
            {
                Label = "Account Balance",
                UsedPercent = 0.0,
                ResetsAt = null,
                ResetDescription = desc,
                WindowMinutes = null,
            },
            Secondary = null,
            Tertiary = null,
            Balance = new BalanceInfo
            {
                Currency = currency,
                Total = available,
                Paid = cash,
                Granted = 0.0,
            },
            SourceLabel = SourceLabel,
            Confidence = Confidence,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
    }

    // Rust: s.and_then(|v| v.parse::<f64>().ok()).unwrap_or(0.0).
    private static double ParseAmount(string? s) =>
        s is not null && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0.0;

    /// <summary>
    /// RFC3986 percent-encoding matching the Rust `url_encode`: keep A-Z a-z 0-9 - _ . ~,
    /// everything else (per UTF-8 byte) becomes %XX with uppercase hex. (Alibaba's RPC v1.0
    /// canonicalization is exactly this set, so + → %2B, space → %20, / → %2F, * → %2A,
    /// and ~ is left literal.)
    /// </summary>
    private static string UrlEncode(string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        var sb = new StringBuilder(bytes.Length * 3);
        foreach (var b in bytes)
        {
            if ((b >= (byte)'A' && b <= (byte)'Z')
                || (b >= (byte)'a' && b <= (byte)'z')
                || (b >= (byte)'0' && b <= (byte)'9')
                || b == (byte)'-' || b == (byte)'_' || b == (byte)'.' || b == (byte)'~')
            {
                sb.Append((char)b);
            }
            else
            {
                sb.Append('%');
                sb.Append(((int)b).ToString("X2", CultureInfo.InvariantCulture));
            }
        }
        return sb.ToString();
    }

    private static string RandomNonce(int length)
    {
        var chars = new char[length];
        for (var i = 0; i < length; i++)
            chars[i] = NonceAlphabet[RandomNumberGenerator.GetInt32(NonceAlphabet.Length)];
        return new string(chars);
    }
}
