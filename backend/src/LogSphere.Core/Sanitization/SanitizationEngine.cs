using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using LogSphere.Core.Models;

namespace LogSphere.Core.Sanitization;

public sealed class SanitizationLimits
{
    public int MaxBodyBytes { get; set; } = 64 * 1024;
    public int MaxStackTraceBytes { get; set; } = 32 * 1024;
    public int MaxPropertiesBytes { get; set; } = 32 * 1024;
    public int MaxStringLength { get; set; } = 8 * 1024;
    public int MaxDepth { get; set; } = 16;
}

public sealed record SanitizationResult(int FieldsSanitized, bool Truncated)
{
    public bool Applied => FieldsSanitized > 0;
}

/// <summary>Compiled redaction rule set. Global rules always apply and cannot be disabled per project.</summary>
public sealed class RedactionRuleSet
{
    private readonly List<(Func<string, bool> Match, string Strategy, string AppliesTo)> _rules = new();

    public RedactionRuleSet(IEnumerable<RedactionRule> rules)
    {
        foreach (var r in rules.Where(r => r.Enabled))
        {
            if (r.IsRegex)
            {
                Regex rx;
                try { rx = new Regex(r.KeyPattern, RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromMilliseconds(50)); }
                catch (ArgumentException) { continue; }
                _rules.Add((key => SafeIsMatch(rx, key), r.Strategy, r.AppliesTo));
            }
            else
            {
                var pattern = r.KeyPattern;
                _rules.Add((key => key.Contains(pattern, StringComparison.OrdinalIgnoreCase), r.Strategy, r.AppliesTo));
            }
        }
    }

    private static bool SafeIsMatch(Regex rx, string key)
    {
        try { return rx.IsMatch(key); }
        catch (RegexMatchTimeoutException) { return true; } // fail closed: treat as sensitive
    }

    /// <summary>Returns the redaction strategy for a key, or null when the key is not sensitive.</summary>
    public string? Match(string key, string area)
    {
        var normalized = key.Replace("-", "_").Replace(" ", "_");
        foreach (var (match, strategy, appliesTo) in _rules)
        {
            if (appliesTo != "All" && !string.Equals(appliesTo, area, StringComparison.OrdinalIgnoreCase)) continue;
            if (match(normalized) || match(key)) return strategy;
        }
        return null;
    }

    public static readonly RedactionRuleSet Default = new(DefaultRules());

    public static IEnumerable<RedactionRule> DefaultRules()
    {
        string[] remove =
        {
            "password", "passcode", "pwd", "pin", "secret", "client_secret", "api_key", "apikey",
            "access_token", "refresh_token", "jwt", "private_key", "connection_string", "connectionstring",
            "cvv", "cvc", "biometric", "face_template", "fingerprint_template", "otp"
        };
        string[] redact = { "authorization", "bearer", "cookie", "set_cookie", "session_id", "sessionid", "date_of_birth", "dateofbirth", "dob" };
        string[] mask = { "card_number", "cardnumber", "account_number", "accountnumber", "iban", "cnic", "national_id", "nationalidentity", "phone", "mobile", "email" };

        foreach (var k in remove) yield return new RedactionRule { KeyPattern = k, Strategy = "Remove", AppliesTo = "All", Enabled = true };
        foreach (var k in redact) yield return new RedactionRule { KeyPattern = k, Strategy = "Redact", AppliesTo = "All", Enabled = true };
        foreach (var k in mask) yield return new RedactionRule { KeyPattern = k, Strategy = "MaskLast4", AppliesTo = "All", Enabled = true };
    }
}

/// <summary>Recursive JSON sanitizer. Mutates a JsonNode tree in place (works on a copy owned by the caller).</summary>
public sealed class SanitizationEngine
{
    private readonly byte[] _hashKey;

    public SanitizationEngine(string? hashKey = null)
    {
        _hashKey = Encoding.UTF8.GetBytes(hashKey ?? "logsphere-default-hash-key");
    }

    /// <summary>Sanitizes a JSON tree; returns the (possibly replaced) root plus stats.</summary>
    public (JsonNode? Node, SanitizationResult Result) Sanitize(JsonNode? node, RedactionRuleSet rules,
        SanitizationLimits limits, string area = "All")
    {
        if (node is null) return (null, new SanitizationResult(0, false));
        var state = new WalkState(rules, limits, area);
        var result = Walk(node, state, 0);
        return (result, new SanitizationResult(state.Sanitized, state.Truncated));
    }

    /// <summary>Sanitizes a raw query string like "a=1&password=x".</summary>
    public (string Sanitized, int Count) SanitizeQueryString(string? query, RedactionRuleSet rules)
    {
        if (string.IsNullOrEmpty(query)) return ("", 0);
        var count = 0;
        var parts = query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < parts.Length; i++)
        {
            var eq = parts[i].IndexOf('=');
            var key = eq < 0 ? parts[i] : parts[i][..eq];
            var strategy = rules.Match(Uri.UnescapeDataString(key), "All");
            if (strategy is not null && eq >= 0)
            {
                var value = parts[i][(eq + 1)..];
                parts[i] = key + "=" + ApplyToString(Uri.UnescapeDataString(value), strategy);
                count++;
            }
        }
        return (string.Join('&', parts), count);
    }

    private sealed class WalkState(RedactionRuleSet rules, SanitizationLimits limits, string area)
    {
        public readonly RedactionRuleSet Rules = rules;
        public readonly SanitizationLimits Limits = limits;
        public readonly string Area = area;
        public int Sanitized;
        public bool Truncated;
    }

    private JsonNode? Walk(JsonNode? node, WalkState s, int depth)
    {
        if (node is null) return null;
        if (depth > s.Limits.MaxDepth)
        {
            s.Truncated = true;
            return JsonValue.Create("[TRUNCATED:DEPTH]");
        }

        switch (node)
        {
            case JsonObject obj:
            {
                foreach (var key in obj.Select(kv => kv.Key).ToList())
                {
                    var strategy = s.Rules.Match(key, s.Area);
                    if (strategy is not null)
                    {
                        s.Sanitized++;
                        if (strategy == "Remove") { obj.Remove(key); continue; }
                        var raw = obj[key] is JsonValue jv && jv.TryGetValue<string>(out var sv)
                            ? sv
                            : obj[key]?.ToJsonString();
                        obj[key] = JsonValue.Create(ApplyToString(raw, strategy));
                    }
                    else
                    {
                        var child = obj[key];
                        var walked = Walk(child, s, depth + 1);
                        if (!ReferenceEquals(walked, child)) obj[key] = walked;
                    }
                }
                return obj;
            }
            case JsonArray arr:
            {
                for (var i = 0; i < arr.Count; i++)
                {
                    var child = arr[i];
                    var walked = Walk(child, s, depth + 1);
                    if (!ReferenceEquals(walked, child)) arr[i] = walked;
                }
                return arr;
            }
            case JsonValue value when value.TryGetValue<string>(out var str):
            {
                if (str.Length > s.Limits.MaxStringLength)
                {
                    s.Truncated = true;
                    return JsonValue.Create(str[..s.Limits.MaxStringLength] + "…[TRUNCATED]");
                }
                // strip control chars to prevent log forging / terminal injection
                if (str.Any(c => char.IsControl(c) && c != '\n' && c != '\r' && c != '\t'))
                    return JsonValue.Create(new string(str.Where(c => !char.IsControl(c) || c is '\n' or '\r' or '\t').ToArray()));
                return value;
            }
            default:
                return node;
        }
    }

    public string ApplyToString(string? value, string strategy) => strategy switch
    {
        "Remove" => "",
        "Redact" => "[REDACTED]",
        "MaskLast4" => value is { Length: > 4 } ? new string('*', Math.Min(value.Length - 4, 12)) + value[^4..] : "****",
        "Hash" => "hmac:" + Convert.ToHexString(HMACSHA256.HashData(_hashKey, Encoding.UTF8.GetBytes(value ?? ""))).ToLowerInvariant()[..24],
        _ => "[REDACTED]"
    };

    /// <summary>Enforces byte-size limits on a serialized JSON node; replaces with truncation marker when exceeded.</summary>
    public static (JsonNode? Node, bool Truncated) EnforceSize(JsonNode? node, int maxBytes)
    {
        if (node is null) return (null, false);
        var json = node.ToJsonString();
        if (Encoding.UTF8.GetByteCount(json) <= maxBytes) return (node, false);
        var obj = new JsonObject
        {
            ["_truncated"] = true,
            ["_originalBytes"] = Encoding.UTF8.GetByteCount(json),
            ["_preview"] = json[..Math.Min(json.Length, 2048)]
        };
        return (obj, true);
    }

    public static string TruncateString(string? value, int maxBytes, out bool truncated)
    {
        truncated = false;
        if (string.IsNullOrEmpty(value)) return value ?? "";
        if (Encoding.UTF8.GetByteCount(value) <= maxBytes) return value;
        truncated = true;
        var chars = Math.Min(value.Length, maxBytes); // conservative
        return value[..chars] + "…[TRUNCATED]";
    }
}
