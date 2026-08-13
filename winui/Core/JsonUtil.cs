using System.Globalization;
using System.Text.Json;

namespace QuotaLens.Core;

/// <summary>
/// Canonical, defensive JSON scalar extraction for provider response parsers.
/// Every provider used to carry its own copy of a TryGetProperty + value-kind switch;
/// that drift meant a numeric field in one parser and a string in another would behave
/// inconsistently. These helpers are deliberately lenient and uniform: numbers may
/// arrive as strings or booleans, strings may arrive as numbers or booleans, and
/// epochs may be seconds or milliseconds.
/// </summary>
public static class JsonUtil
{
    public static double? ElementDouble(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Number when value.TryGetDouble(out var number) => number,
        JsonValueKind.String when double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) => number,
        JsonValueKind.True => 1,
        JsonValueKind.False => 0,
        _ => null,
    };

    public static double? OptionalDouble(JsonElement? obj, string key) =>
        obj is { ValueKind: JsonValueKind.Object } element && element.TryGetProperty(key, out var value)
            ? ElementDouble(value)
            : null;

    public static long? OptionalLong(JsonElement obj, string key)
    {
        if (!obj.TryGetProperty(key, out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt64(out var number) => number,
            JsonValueKind.Number when value.TryGetDouble(out var number) => (long)number,
            JsonValueKind.String when long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) => number,
            JsonValueKind.String when double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) => (long)number,
            _ => null,
        };
    }

    public static bool? OptionalBool(JsonElement obj, string key)
    {
        if (!obj.TryGetProperty(key, out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number when value.TryGetDouble(out var number) => Math.Abs(number) > 0.001,
            JsonValueKind.String when bool.TryParse(value.GetString(), out var parsed) => parsed,
            JsonValueKind.String when double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) => Math.Abs(number) > 0.001,
            _ => null,
        };
    }

    public static string? OptionalString(JsonElement? obj, string key)
    {
        if (obj is not { ValueKind: JsonValueKind.Object } element || !element.TryGetProperty(key, out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.String => TextUtil.Clean(value.GetString()),
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,
        };
    }

    public static JsonElement? ObjectProperty(JsonElement? parent, string key) =>
        parent is { ValueKind: JsonValueKind.Object } obj
        && obj.TryGetProperty(key, out var value)
        && value.ValueKind == JsonValueKind.Object
            ? value
            : null;

    public static JsonElement? ArrayProperty(JsonElement? parent, string key) =>
        parent is { ValueKind: JsonValueKind.Object } obj
        && obj.TryGetProperty(key, out var value)
        && value.ValueKind == JsonValueKind.Array
            ? value
            : null;

    public static IEnumerable<JsonElement> ArrayItems(JsonElement? element)
    {
        if (element is not { ValueKind: JsonValueKind.Array } array)
            yield break;

        foreach (var item in array.EnumerateArray())
            yield return item;
    }

    public static double RequiredDouble(JsonElement obj, string key) =>
        OptionalDouble(obj, key)
        ?? throw new ProviderException($"Parse error: Missing numeric field {key}");

    public static double? FirstDouble(JsonElement obj, params string[] keys)
    {
        foreach (var key in keys)
            if (OptionalDouble(obj, key) is { } value)
                return value;
        return null;
    }

    public static string? FirstString(JsonElement obj, params string[] keys)
    {
        foreach (var key in keys)
            if (OptionalString(obj, key) is { } value)
                return value;
        return null;
    }

    public static double? FirstDouble(IEnumerable<JsonElement> contexts, params string[] keys)
    {
        foreach (var context in contexts)
            foreach (var key in keys)
                if (OptionalDouble(context, key) is { } value)
                    return value;
        return null;
    }

    public static string? FirstString(IEnumerable<JsonElement> contexts, params string[] keys)
    {
        foreach (var context in contexts)
            foreach (var key in keys)
                if (OptionalString(context, key) is { } value)
                    return value;
        return null;
    }

    public static string? FirstDateIso(JsonElement obj, params string[] keys)
    {
        foreach (var key in keys)
            if (OptionalDateIso(obj, key) is { } value)
                return value;
        return null;
    }

    public static string? FirstDateIso(IEnumerable<JsonElement> contexts, params string[] keys)
    {
        foreach (var context in contexts)
            foreach (var key in keys)
                if (OptionalDateIso(context, key) is { } value)
                    return value;
        return null;
    }

    public static string? OptionalDateIso(JsonElement obj, string key)
    {
        if (!obj.TryGetProperty(key, out var value))
            return null;

        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number))
            return EpochToIso(number);
        if (value.ValueKind != JsonValueKind.String)
            return null;

        var text = TextUtil.Clean(value.GetString());
        if (string.IsNullOrWhiteSpace(text))
            return null;
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var numeric))
            return EpochToIso(numeric);
        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed.ToString("O", CultureInfo.InvariantCulture)
            : null;
    }

    public static string? UnixSecondsToIso(long? seconds) =>
        seconds is > 0
            ? DateTimeOffset.FromUnixTimeSeconds(seconds.Value).ToString("O", CultureInfo.InvariantCulture)
            : null;

    public static string? UnixMillisecondsToIso(long? milliseconds) =>
        milliseconds is > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds(milliseconds.Value).ToString("O", CultureInfo.InvariantCulture)
            : null;

    /// <summary>Epoch seconds or milliseconds (auto-detected) into an ISO-8601 string.</summary>
    public static string? EpochToIso(double value)
    {
        if (value <= 0 || !double.IsFinite(value))
            return null;

        var seconds = Math.Abs(value) > 10_000_000_000 ? value / 1000 : value;
        return DateTimeOffset.FromUnixTimeSeconds((long)Math.Round(seconds)).ToString("O", CultureInfo.InvariantCulture);
    }
}
