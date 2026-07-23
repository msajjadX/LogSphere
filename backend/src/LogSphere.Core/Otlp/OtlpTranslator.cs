using System.Globalization;
using System.Text.Json.Nodes;

namespace LogSphere.Core.Otlp;

/// <summary>
/// Translates OTLP/JSON (OpenTelemetry protocol, http/json encoding) into LogSphere event
/// envelopes. Pure functions — no I/O — so the full mapping is unit-testable. The output
/// JsonObjects use the exact camelCase keys <c>EventEnvelope</c> deserializes, and flow through
/// the normal IngestService pipeline (validation, sanitization, size caps, durable queue).
/// Identity (tenant/project/application/environment) always comes from the API key — resource
/// attributes never override it.
/// </summary>
public static class OtlpTranslator
{
    // ---------------------------------------------------------------- public API

    /// <summary>Translates an OTLP ExportLogsServiceRequest (resourceLogs[]) to envelopes.</summary>
    public static List<JsonObject> TranslateLogs(JsonNode root)
    {
        var result = new List<JsonObject>();
        if (root?["resourceLogs"] is not JsonArray resourceLogs) return result;

        foreach (var rl in resourceLogs)
        {
            var res = ExtractResource(rl?["resource"]);
            if (rl?["scopeLogs"] is not JsonArray scopeLogs) continue;
            foreach (var sl in scopeLogs)
            {
                var scopeName = sl?["scope"]?["name"]?.GetValue<string>();
                if (sl?["logRecords"] is not JsonArray records) continue;
                foreach (var r in records)
                {
                    if (r is JsonObject rec) result.Add(TranslateLogRecord(rec, res, scopeName));
                }
            }
        }
        return result;
    }

    /// <summary>Translates an OTLP ExportTraceServiceRequest (resourceSpans[]) to envelopes.</summary>
    public static List<JsonObject> TranslateTraces(JsonNode root)
    {
        var result = new List<JsonObject>();
        if (root?["resourceSpans"] is not JsonArray resourceSpans) return result;

        foreach (var rs in resourceSpans)
        {
            var res = ExtractResource(rs?["resource"]);
            if (rs?["scopeSpans"] is not JsonArray scopeSpans) continue;
            foreach (var ss in scopeSpans)
            {
                var scopeName = ss?["scope"]?["name"]?.GetValue<string>();
                if (ss?["spans"] is not JsonArray spans) continue;
                foreach (var s in spans)
                {
                    if (s is JsonObject span) result.Add(TranslateSpan(span, res, scopeName));
                }
            }
        }
        return result;
    }

    // ---------------------------------------------------------------- resource

    private sealed record ResourceInfo(
        string? ServiceName, string? ServiceVersion, string? HostName, JsonObject Extra);

    private static ResourceInfo ExtractResource(JsonNode? resource)
    {
        string? service = null, version = null, host = null;
        var extra = new JsonObject();
        foreach (var (key, value) in Attrs(resource?["attributes"]))
        {
            switch (key)
            {
                case "service.name": service = value as string; break;
                case "service.version": version = value as string; break;
                case "host.name": host = value as string; break;
                default: extra[key] = ToNode(value); break;
            }
        }
        return new ResourceInfo(service, version, host, extra);
    }

    // ---------------------------------------------------------------- log records

    private static JsonObject TranslateLogRecord(JsonObject rec, ResourceInfo res, string? scopeName)
    {
        var props = new JsonObject();
        string? userId = null, sessionId = null, component = null;
        string? exType = null, exMessage = null, exStack = null;

        foreach (var (key, value) in Attrs(rec["attributes"]))
        {
            switch (key)
            {
                case "exception.type": exType = value as string; break;
                case "exception.message": exMessage = value as string; break;
                case "exception.stacktrace": exStack = value as string; break;
                case "enduser.id": userId = value as string; break;
                case "session.id": sessionId = value as string; break;
                case "code.namespace": component = value as string; break;
                default: props[key] = ToNode(value); break;
            }
        }
        if (res.Extra.Count > 0) props["resource"] = res.Extra.DeepClone();

        var hasException = exType is not null || exMessage is not null || exStack is not null;
        var envelope = new JsonObject
        {
            ["eventType"] = hasException ? "Exception" : "Application",
            ["severity"] = MapSeverity(rec["severityNumber"], rec["severityText"]?.GetValue<string>()),
            ["eventTimestamp"] = UnixNanosToIso(rec["timeUnixNano"] ?? rec["observedTimeUnixNano"]),
            ["message"] = AnyValueToString(rec["body"]),
            ["module"] = res.ServiceName,
            ["component"] = component ?? scopeName,
            ["userId"] = userId,
            ["sessionId"] = sessionId,
            ["traceId"] = rec["traceId"]?.GetValue<string>(),
            ["spanId"] = rec["spanId"]?.GetValue<string>(),
            ["machineName"] = res.HostName,
            ["applicationVersion"] = res.ServiceVersion,
            ["properties"] = props.Count > 0 ? props : null,
        };
        if (hasException)
            envelope["exception"] = new JsonObject
            {
                ["type"] = exType, ["message"] = exMessage, ["stackTrace"] = exStack
            };
        return envelope;
    }

    // ---------------------------------------------------------------- spans

    private static JsonObject TranslateSpan(JsonObject span, ResourceInfo res, string? scopeName)
    {
        var props = new JsonObject();
        string? httpMethod = null, httpRoute = null, clientIp = null, userAgent = null;
        int? httpStatus = null;
        string? dbSystem = null, dbStatement = null;
        string? userId = null, sessionId = null;

        foreach (var (key, value) in Attrs(span["attributes"]))
        {
            switch (key)
            {
                // current + legacy HTTP semantic conventions
                case "http.request.method": case "http.method": httpMethod = value as string; break;
                case "http.route": case "url.path": case "http.target": httpRoute ??= value as string; break;
                case "http.response.status_code": case "http.status_code": httpStatus = ToInt(value); break;
                case "client.address": case "http.client_ip": clientIp = value as string; break;
                case "user_agent.original": case "http.user_agent": userAgent = value as string; break;
                case "db.system": case "db.system.name": dbSystem = value as string; break;
                case "db.statement": case "db.query.text": dbStatement = value as string; break;
                case "enduser.id": userId = value as string; break;
                case "session.id": sessionId = value as string; break;
                default: props[key] = ToNode(value); break;
            }
        }
        if (res.Extra.Count > 0) props["resource"] = res.Extra.DeepClone();

        var kind = MapKind(span["kind"]);
        var isError = IsErrorStatus(span["status"]);
        var startNs = ToLong(span["startTimeUnixNano"]);
        var endNs = ToLong(span["endTimeUnixNano"]);
        double? durationMs = startNs is not null && endNs is not null && endNs >= startNs
            ? (endNs.Value - startNs.Value) / 1_000_000.0
            : null;

        var eventType = kind switch
        {
            SpanKind.Server => "ApiRequest",
            SpanKind.Client when dbSystem is not null => "Performance",
            SpanKind.Client => "Integration",
            SpanKind.Producer or SpanKind.Consumer => "BackgroundJob",
            _ => "Application",
        };

        if (dbSystem is not null)
            props["db"] = new JsonObject { ["system"] = dbSystem, ["statement"] = dbStatement };

        // exception recorded as a span event (OTel convention)
        JsonObject? exception = null;
        if (span["events"] is JsonArray events)
        {
            foreach (var ev in events)
            {
                if (ev?["name"]?.GetValue<string>() != "exception") continue;
                string? t = null, m = null, st = null;
                foreach (var (key, value) in Attrs(ev["attributes"]))
                {
                    switch (key)
                    {
                        case "exception.type": t = value as string; break;
                        case "exception.message": m = value as string; break;
                        case "exception.stacktrace": st = value as string; break;
                    }
                }
                if (t is not null || m is not null || st is not null)
                    exception = new JsonObject { ["type"] = t, ["message"] = m, ["stackTrace"] = st };
                break;
            }
        }

        var envelope = new JsonObject
        {
            ["eventType"] = exception is not null ? "Exception" : eventType,
            ["severity"] = isError ? "Error" : "Information",
            ["status"] = isError ? "Failed" : "Completed",
            // SDK semantics: the event timestamp is the operation's END; start = end - duration
            ["eventTimestamp"] = UnixNanosToIso(span["endTimeUnixNano"] ?? span["startTimeUnixNano"]),
            ["message"] = span["name"]?.GetValue<string>(),
            ["actionName"] = span["name"]?.GetValue<string>(),
            ["module"] = res.ServiceName,
            ["component"] = scopeName,
            ["userId"] = userId,
            ["sessionId"] = sessionId,
            ["traceId"] = span["traceId"]?.GetValue<string>(),
            ["spanId"] = span["spanId"]?.GetValue<string>(),
            ["parentSpanId"] = span["parentSpanId"]?.GetValue<string>(),
            ["durationMs"] = durationMs,
            ["dbDurationMs"] = dbSystem is not null ? durationMs : null,
            ["machineName"] = res.HostName,
            ["applicationVersion"] = res.ServiceVersion,
            ["properties"] = props.Count > 0 ? props : null,
        };
        if (httpMethod is not null || httpRoute is not null || httpStatus is not null || clientIp is not null || userAgent is not null)
            envelope["http"] = new JsonObject
            {
                ["method"] = httpMethod,
                ["route"] = httpRoute,
                ["statusCode"] = httpStatus,
                ["clientIp"] = clientIp,
                ["userAgent"] = userAgent,
            };
        if (exception is not null) envelope["exception"] = exception;
        return envelope;
    }

    // ---------------------------------------------------------------- OTLP primitives

    /// <summary>Iterates an OTLP attribute list: [{ "key": ..., "value": { AnyValue } }].</summary>
    private static IEnumerable<(string Key, object? Value)> Attrs(JsonNode? attributes)
    {
        if (attributes is not JsonArray arr) yield break;
        foreach (var item in arr)
        {
            var key = item?["key"]?.GetValue<string>();
            if (string.IsNullOrEmpty(key)) continue;
            yield return (key, AnyValueToObject(item?["value"]));
        }
    }

    /// <summary>Converts an OTLP AnyValue ({stringValue}|{intValue}|{doubleValue}|{boolValue}|{arrayValue}|{kvlistValue}|{bytesValue}).</summary>
    private static object? AnyValueToObject(JsonNode? v)
    {
        if (v is not JsonObject o) return null;
        if (o["stringValue"] is { } s) return s.GetValue<string>();
        if (o["boolValue"] is { } b) return b.GetValue<bool>();
        if (o["intValue"] is { } i) // proto3 JSON encodes int64 as string
            return long.TryParse(i.GetValueKind() == System.Text.Json.JsonValueKind.String
                ? i.GetValue<string>() : i.ToJsonString(), out var l) ? l : null;
        if (o["doubleValue"] is { } d) return d.GetValue<double>();
        if (o["arrayValue"] is { } av)
        {
            var arr = new JsonArray();
            if (av["values"] is JsonArray values)
                foreach (var item in values) arr.Add(ToNode(AnyValueToObject(item)));
            return arr;
        }
        if (o["kvlistValue"] is { } kv)
        {
            var obj = new JsonObject();
            if (kv["values"] is JsonArray values)
                foreach (var item in values)
                {
                    var key = item?["key"]?.GetValue<string>();
                    if (!string.IsNullOrEmpty(key)) obj[key] = ToNode(AnyValueToObject(item?["value"]));
                }
            return obj;
        }
        if (o["bytesValue"] is { } by) return by.GetValue<string>(); // keep base64 as-is
        return null;
    }

    private static string? AnyValueToString(JsonNode? v)
    {
        var value = AnyValueToObject(v);
        return value switch
        {
            null => null,
            string s => s,
            JsonNode n => n.ToJsonString(),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture),
        };
    }

    private static JsonNode? ToNode(object? value) => value switch
    {
        null => null,
        string s => JsonValue.Create(s),
        bool b => JsonValue.Create(b),
        long l => JsonValue.Create(l),
        double d => JsonValue.Create(d),
        JsonNode n => n,
        _ => JsonValue.Create(value.ToString()),
    };

    /// <summary>OTel severityNumber 1–24 → LogSphere severity name; falls back to severityText.</summary>
    private static string MapSeverity(JsonNode? severityNumber, string? severityText)
    {
        var n = ToInt(ToLong(severityNumber));
        if (n is > 0 and <= 24)
            return n switch
            {
                <= 4 => "Trace",
                <= 8 => "Debug",
                <= 12 => "Information",
                <= 16 => "Warning",
                <= 20 => "Error",
                _ => "Critical",
            };
        return severityText?.Trim().ToUpperInvariant() switch
        {
            "TRACE" => "Trace",
            "DEBUG" => "Debug",
            "INFO" or "INFORMATION" => "Information",
            "WARN" or "WARNING" => "Warning",
            "ERROR" or "ERR" => "Error",
            "FATAL" or "CRITICAL" => "Critical",
            _ => "Information",
        };
    }

    private enum SpanKind { Unspecified, Internal, Server, Client, Producer, Consumer }

    /// <summary>Span kind arrives as an int (0–5) or a "SPAN_KIND_SERVER"-style enum name.</summary>
    private static SpanKind MapKind(JsonNode? kind)
    {
        if (kind is null) return SpanKind.Unspecified;
        if (ToLong(kind) is { } n && n is >= 0 and <= 5) return (SpanKind)n;
        return (kind.GetValueKind() == System.Text.Json.JsonValueKind.String ? kind.GetValue<string>() : "") switch
        {
            "SPAN_KIND_INTERNAL" => SpanKind.Internal,
            "SPAN_KIND_SERVER" => SpanKind.Server,
            "SPAN_KIND_CLIENT" => SpanKind.Client,
            "SPAN_KIND_PRODUCER" => SpanKind.Producer,
            "SPAN_KIND_CONSUMER" => SpanKind.Consumer,
            _ => SpanKind.Unspecified,
        };
    }

    /// <summary>Status.code 2 / "STATUS_CODE_ERROR" means the span failed.</summary>
    private static bool IsErrorStatus(JsonNode? status)
    {
        var code = status?["code"];
        if (code is null) return false;
        if (ToLong(code) is { } n) return n == 2;
        return code.GetValueKind() == System.Text.Json.JsonValueKind.String &&
               code.GetValue<string>() == "STATUS_CODE_ERROR";
    }

    /// <summary>proto3 JSON encodes uint64 nanos as a string; some SDKs emit numbers.</summary>
    private static long? ToLong(JsonNode? v)
    {
        if (v is null) return null;
        return v.GetValueKind() switch
        {
            System.Text.Json.JsonValueKind.String =>
                long.TryParse(v.GetValue<string>(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) ? s : null,
            // TryGetValue tolerates both parsed JSON numbers and in-memory JsonValue.Create(int/long/double)
            System.Text.Json.JsonValueKind.Number =>
                v.AsValue().TryGetValue<long>(out var l) ? l
                : v.AsValue().TryGetValue<int>(out var i) ? i
                : v.AsValue().TryGetValue<double>(out var d) ? (long)d
                : null,
            _ => null,
        };
    }

    private static int? ToInt(object? value) => value switch
    {
        long l => (int)l,
        int i => i,
        string s when int.TryParse(s, out var i) => i,
        _ => null,
    };

    private static string? UnixNanosToIso(JsonNode? nanos)
    {
        var n = ToLong(nanos);
        if (n is null or <= 0) return null;
        return DateTimeOffset.FromUnixTimeMilliseconds(n.Value / 1_000_000)
            .AddTicks(n.Value % 1_000_000 / 100)
            .UtcDateTime.ToString("o", CultureInfo.InvariantCulture);
    }
}
