using System.Text.Json.Nodes;

namespace LogSphere.Sdk;

/// <summary>Client-side sanitization (first defense layer; the server always re-sanitizes).</summary>
public sealed class ClientSanitizer
{
    private static readonly string[] DefaultKeys =
    {
        "password", "passcode", "pwd", "pin", "secret", "client_secret", "api_key", "apikey",
        "authorization", "access_token", "refresh_token", "bearer", "jwt", "cookie", "session_id",
        "sessionid", "private_key", "connection_string", "connectionstring", "cvv", "cvc",
        "card_number", "cardnumber", "account_number", "accountnumber", "iban", "biometric",
        "face_template", "fingerprint_template", "cnic", "national_id", "date_of_birth",
        "dateofbirth", "dob", "otp"
    };

    private readonly string[] _keys;

    public ClientSanitizer(IEnumerable<string>? extraKeys = null) =>
        _keys = DefaultKeys.Concat(extraKeys ?? Enumerable.Empty<string>())
            .Select(k => k.ToLowerInvariant()).Distinct().ToArray();

    public bool IsSensitive(string key)
    {
        var normalized = key.Replace("-", "_").Replace(" ", "_").ToLowerInvariant();
        return _keys.Any(k => normalized.Contains(k));
    }

    public JsonNode? Sanitize(JsonNode? node, int depth = 0)
    {
        if (node is null || depth > 16) return node;
        switch (node)
        {
            case JsonObject obj:
                foreach (var key in obj.Select(kv => kv.Key).ToList())
                {
                    if (IsSensitive(key)) obj[key] = "[REDACTED]";
                    else obj[key] = Sanitize(obj[key], depth + 1);
                }
                return obj;
            case JsonArray arr:
                for (var i = 0; i < arr.Count; i++) arr[i] = Sanitize(arr[i], depth + 1);
                return arr;
            default:
                return node;
        }
    }
}
