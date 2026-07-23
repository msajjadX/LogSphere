using System.Globalization;
using System.Text.Json.Nodes;

namespace LogSphere.Tail;

/// <summary>
/// Turns raw log lines into LogSphere event envelopes according to a source's parser config.
/// Stateless except for the per-file W3C #Fields header, which the caller passes back in.
/// Multi-line stitching (stack traces) happens in <see cref="MultilineAssembler"/>.
/// </summary>
internal static class LineParser
{
    private static readonly string[] SeverityNames = { "Trace", "Debug", "Information", "Warning", "Error", "Critical" };

    /// <summary>Built-in fallbacks so common level strings work without a severityMap.</summary>
    private static string? NormalizeSeverity(string? raw, TailSource src)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        raw = raw.Trim();
        if (src.SeverityMap.TryGetValue(raw, out var mapped)) raw = mapped;
        var exact = SeverityNames.FirstOrDefault(n => n.Equals(raw, StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return exact;
        return raw.ToUpperInvariant() switch
        {
            "TRC" or "VERBOSE" => "Trace",
            "DBG" => "Debug",
            "INF" or "INFO" or "NOTICE" => "Information",
            "WRN" or "WARN" => "Warning",
            "ERR" or "SEVERE" => "Error",
            "FTL" or "FATAL" or "CRIT" => "Critical",
            _ => null,
        };
    }

    private static string? ParseTimestamp(string? raw, TailSource src)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        raw = raw.Trim();
        DateTimeOffset ts;
        var styles = DateTimeStyles.AssumeLocal; // file logs are local time unless the format says otherwise
        var ok = src.TimestampFormat is not null
            ? DateTimeOffset.TryParseExact(raw, src.TimestampFormat, CultureInfo.InvariantCulture, styles, out ts)
            : DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, styles, out ts);
        return ok ? ts.UtcDateTime.ToString("o") : null;
    }

    private static JsonObject NewEnvelope(TailSource src, string? severity = null) => new()
    {
        ["eventType"] = src.EventType,
        ["severity"] = severity ?? src.Severity,
        ["module"] = src.Module,
    };

    /// <summary>Parses one line. Returns null for lines that produce no event (blank, W3C directives).</summary>
    public static JsonObject? Parse(string line, TailSource src, W3CFields w3c)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        return src.Parser switch
        {
            "json" => ParseJson(line, src),
            "regex" => ParseRegex(line, src),
            "w3c" => ParseW3C(line, src, w3c),
            _ => ParsePlain(line, src),
        };
    }

    private static JsonObject ParsePlain(string line, TailSource src)
    {
        var e = NewEnvelope(src);
        e["message"] = line;
        return e;
    }

    private static JsonObject ParseRegex(string line, TailSource src)
    {
        var m = src.CompiledPattern!.Match(line);
        if (!m.Success) return ParsePlain(line, src); // unparseable lines still get captured

        var e = NewEnvelope(src, NormalizeSeverity(m.Groups["sev"].Success ? m.Groups["sev"].Value : null, src));
        if (m.Groups["ts"].Success && ParseTimestamp(m.Groups["ts"].Value, src) is { } iso)
            e["eventTimestamp"] = iso;
        e["message"] = m.Groups["msg"].Success ? m.Groups["msg"].Value : line;

        JsonObject? props = null;
        foreach (var name in src.CompiledPattern!.GetGroupNames())
        {
            if (name is "0" or "ts" or "sev" or "msg" || int.TryParse(name, out _)) continue;
            if (!m.Groups[name].Success) continue;
            (props ??= new JsonObject())[name] = m.Groups[name].Value;
        }
        if (props is not null) e["properties"] = props;
        return e;
    }

    private static JsonObject ParseJson(string line, TailSource src)
    {
        JsonObject? obj;
        try { obj = JsonNode.Parse(line) as JsonObject; }
        catch { obj = null; }
        if (obj is null) return ParsePlain(line, src);

        // a line that already looks like a LogSphere envelope passes through (defaults filled)
        if (obj["eventType"] is not null)
        {
            obj["module"] ??= src.Module;
            obj["severity"] ??= src.Severity;
            return obj;
        }

        var e = NewEnvelope(src);
        JsonObject props = new();
        foreach (var (key, value) in obj.ToList())
        {
            switch (key.ToLowerInvariant())
            {
                // common structured-logging shapes: Serilog compact (@t/@m/@l), generic json
                case "message" or "msg" or "@m" or "@mt":
                    e["message"] = value?.GetValue<string>(); break;
                case "level" or "severity" or "@l":
                    if (NormalizeSeverity(value?.ToString(), src) is { } sev) e["severity"] = sev;
                    break;
                case "timestamp" or "time" or "@t" or "ts":
                    if (ParseTimestamp(value?.ToString(), src) is { } iso) e["eventTimestamp"] = iso;
                    break;
                case "exception" or "@x":
                    e["exception"] = new JsonObject { ["message"] = value?.ToString(), ["stackTrace"] = value?.ToString() };
                    e["eventType"] = "Exception";
                    if ((string?)e["severity"] is "Information") e["severity"] = "Error";
                    break;
                default:
                    props[key] = value?.DeepClone(); break;
            }
        }
        e["message"] ??= line;
        if (props.Count > 0) e["properties"] = props;
        return e;
    }

    private static JsonObject? ParseW3C(string line, TailSource src, W3CFields w3c)
    {
        if (line.StartsWith('#'))
        {
            if (line.StartsWith("#Fields:", StringComparison.OrdinalIgnoreCase))
                w3c.Names = line["#Fields:".Length..].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return null; // directives produce no event
        }
        if (w3c.Names is null) return ParsePlain(line, src);

        var values = line.Split(' ');
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < w3c.Names.Length && i < values.Length; i++)
            if (values[i] != "-") fields[w3c.Names[i]] = values[i];

        var e = NewEnvelope(src);
        e["eventType"] = src.EventType is "Application" ? "ApiRequest" : src.EventType;

        if (fields.TryGetValue("date", out var d) && fields.TryGetValue("time", out var t) &&
            DateTimeOffset.TryParse($"{d} {t}", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var ts))
            e["eventTimestamp"] = ts.UtcDateTime.ToString("o"); // W3C logs are UTC by spec

        var method = fields.GetValueOrDefault("cs-method");
        var uri = fields.GetValueOrDefault("cs-uri-stem");
        var status = fields.TryGetValue("sc-status", out var sc) && int.TryParse(sc, out var scv) ? scv : (int?)null;
        e["message"] = $"{method ?? "?"} {uri ?? "?"} -> {status?.ToString() ?? "?"}";
        if (status is >= 500) e["severity"] = "Error";
        else if (status is >= 400) e["severity"] = "Warning";

        var http = new JsonObject
        {
            ["method"] = method,
            ["route"] = uri + (fields.TryGetValue("cs-uri-query", out var q) ? "?" + q : ""),
            ["statusCode"] = status,
            ["clientIp"] = fields.GetValueOrDefault("c-ip"),
            ["userAgent"] = fields.GetValueOrDefault("cs(User-Agent)")?.Replace('+', ' '),
        };
        e["http"] = http;
        if (fields.TryGetValue("time-taken", out var tt) && double.TryParse(tt, out var ms))
            e["durationMs"] = ms;

        var props = new JsonObject();
        foreach (var (k, v) in fields)
            if (k is not ("date" or "time" or "cs-method" or "cs-uri-stem" or "cs-uri-query" or "sc-status" or "c-ip" or "cs(User-Agent)" or "time-taken"))
                props[k] = v;
        if (props.Count > 0) e["properties"] = props;
        return e;
    }
}

/// <summary>Per-file W3C parser state: the active #Fields column list.</summary>
internal sealed class W3CFields
{
    public string[]? Names;
}

/// <summary>Glues continuation lines (stack traces) onto the previous event when the source has a
/// multilineStart pattern. Completed events come out; the trailing event stays pending until the
/// next start-line or a flush.</summary>
internal sealed class MultilineAssembler(TailSource src)
{
    private const int MaxMessageChars = 4000;
    private JsonObject? _pending;

    /// <summary>Feeds one raw line; returns a completed event or null.</summary>
    public JsonObject? Push(string line, W3CFields w3c)
    {
        var startPattern = src.CompiledMultilineStart;
        if (startPattern is not null && _pending is not null && !startPattern.IsMatch(line))
        {
            // continuation → append to the pending event's message
            var msg = (string?)_pending["message"] ?? "";
            if (msg.Length < MaxMessageChars)
                _pending["message"] = (msg + "\n" + line)[..Math.Min(msg.Length + 1 + line.Length, MaxMessageChars)];
            return null;
        }

        var parsed = LineParser.Parse(line, src, w3c);
        if (startPattern is null) return parsed; // no multiline mode: every line stands alone

        var completed = _pending;
        _pending = parsed;
        return completed;
    }

    /// <summary>Emits the trailing pending event (end of a read cycle).</summary>
    public JsonObject? Flush()
    {
        var completed = _pending;
        _pending = null;
        return completed;
    }
}
