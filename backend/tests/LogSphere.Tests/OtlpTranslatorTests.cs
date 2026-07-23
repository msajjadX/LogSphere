using System.Text.Json;
using System.Text.Json.Nodes;
using LogSphere.Core.Models;
using LogSphere.Core.Otlp;
using Xunit;

namespace LogSphere.Tests;

public class OtlpTranslatorTests
{
    private static readonly JsonSerializerOptions EnvelopeOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static JsonNode Parse(string json) => JsonNode.Parse(json)!;

    // ------------------------------------------------------------------ logs

    private const string LogsRequest = """
    {
      "resourceLogs": [{
        "resource": { "attributes": [
          { "key": "service.name",    "value": { "stringValue": "billing-api" } },
          { "key": "service.version", "value": { "stringValue": "2.4.1" } },
          { "key": "host.name",       "value": { "stringValue": "prod-web-01" } },
          { "key": "os.type",         "value": { "stringValue": "linux" } }
        ]},
        "scopeLogs": [{
          "scope": { "name": "Billing.Invoices" },
          "logRecords": [{
            "timeUnixNano": "1750000000123456789",
            "severityNumber": 13,
            "severityText": "WARN",
            "body": { "stringValue": "Invoice total mismatch" },
            "attributes": [
              { "key": "invoice.id", "value": { "intValue": "991" } },
              { "key": "enduser.id", "value": { "stringValue": "u-1001" } }
            ],
            "traceId": "5b8efff798038103d269b633813fc60c",
            "spanId": "eee19b7ec3c1b174"
          }]
        }]
      }]
    }
    """;

    [Fact]
    public void Log_record_maps_core_fields()
    {
        var envelopes = OtlpTranslator.TranslateLogs(Parse(LogsRequest));

        var e = Assert.Single(envelopes);
        Assert.Equal("Application", (string?)e["eventType"]);
        Assert.Equal("Warning", (string?)e["severity"]); // severityNumber 13 wins over text
        Assert.Equal("Invoice total mismatch", (string?)e["message"]);
        Assert.Equal("billing-api", (string?)e["module"]);
        Assert.Equal("Billing.Invoices", (string?)e["component"]);
        Assert.Equal("prod-web-01", (string?)e["machineName"]);
        Assert.Equal("2.4.1", (string?)e["applicationVersion"]);
        Assert.Equal("u-1001", (string?)e["userId"]);
        Assert.Equal("5b8efff798038103d269b633813fc60c", (string?)e["traceId"]);
        Assert.Equal(991L, (long?)e["properties"]?["invoice.id"]);
        Assert.Equal("linux", (string?)e["properties"]?["resource"]?["os.type"]);
        // 1750000000.123456789s epoch → 2025-06-15T15:06:40.123Z (ms precision preserved)
        Assert.StartsWith("2025-06-15T15:06:40.123", (string?)e["eventTimestamp"]);
    }

    [Fact]
    public void Log_record_with_exception_semconv_becomes_Exception_event()
    {
        var request = Parse("""
        {
          "resourceLogs": [{
            "scopeLogs": [{
              "logRecords": [{
                "timeUnixNano": "1750000000000000000",
                "severityNumber": 17,
                "body": { "stringValue": "boom" },
                "attributes": [
                  { "key": "exception.type",       "value": { "stringValue": "System.NullReferenceException" } },
                  { "key": "exception.message",    "value": { "stringValue": "Object reference not set" } },
                  { "key": "exception.stacktrace", "value": { "stringValue": "at Billing.Charge()" } }
                ]
              }]
            }]
          }]
        }
        """);

        var e = Assert.Single(OtlpTranslator.TranslateLogs(request));
        Assert.Equal("Exception", (string?)e["eventType"]);
        Assert.Equal("Error", (string?)e["severity"]);
        Assert.Equal("System.NullReferenceException", (string?)e["exception"]?["type"]);
        Assert.Equal("at Billing.Charge()", (string?)e["exception"]?["stackTrace"]);
        // exception.* keys are lifted out of properties
        Assert.Null(e["properties"]?["exception.type"]);
    }

    [Theory]
    [InlineData(null, "WARN", "Warning")]
    [InlineData(null, "fatal", "Critical")]
    [InlineData(null, null, "Information")]
    [InlineData(3, "ERROR", "Trace")] // number wins over text
    [InlineData(24, null, "Critical")]
    public void Severity_mapping_covers_number_and_text(int? number, string? text, string expected)
    {
        var rec = new JsonObject { ["timeUnixNano"] = "1750000000000000000" };
        if (number is not null) rec["severityNumber"] = number.Value;
        if (text is not null) rec["severityText"] = text;
        var request = new JsonObject
        {
            ["resourceLogs"] = new JsonArray(new JsonObject
            {
                ["scopeLogs"] = new JsonArray(new JsonObject { ["logRecords"] = new JsonArray(rec) }),
            }),
        };

        var e = Assert.Single(OtlpTranslator.TranslateLogs(request));
        Assert.Equal(expected, (string?)e["severity"]);
    }

    // ------------------------------------------------------------------ spans

    private const string ServerSpanRequest = """
    {
      "resourceSpans": [{
        "resource": { "attributes": [
          { "key": "service.name", "value": { "stringValue": "billing-api" } }
        ]},
        "scopeSpans": [{
          "spans": [{
            "traceId": "5b8efff798038103d269b633813fc60c",
            "spanId": "eee19b7ec3c1b174",
            "parentSpanId": "aaa19b7ec3c1b174",
            "name": "GET /api/invoices/{id}",
            "kind": 2,
            "startTimeUnixNano": "1750000000000000000",
            "endTimeUnixNano":   "1750000000250000000",
            "attributes": [
              { "key": "http.request.method",        "value": { "stringValue": "GET" } },
              { "key": "http.route",                 "value": { "stringValue": "/api/invoices/{id}" } },
              { "key": "http.response.status_code",  "value": { "intValue": "500" } },
              { "key": "client.address",             "value": { "stringValue": "10.1.2.3" } },
              { "key": "user_agent.original",        "value": { "stringValue": "curl/8.0" } }
            ],
            "status": { "code": 2 }
          }]
        }]
      }]
    }
    """;

    [Fact]
    public void Server_span_maps_to_ApiRequest_with_http_and_error_status()
    {
        var e = Assert.Single(OtlpTranslator.TranslateTraces(Parse(ServerSpanRequest)));

        Assert.Equal("ApiRequest", (string?)e["eventType"]);
        Assert.Equal("Error", (string?)e["severity"]);
        Assert.Equal("Failed", (string?)e["status"]);
        Assert.Equal("GET /api/invoices/{id}", (string?)e["actionName"]);
        Assert.Equal(250.0, (double?)e["durationMs"]);
        Assert.Equal("aaa19b7ec3c1b174", (string?)e["parentSpanId"]);
        Assert.Equal("GET", (string?)e["http"]?["method"]);
        Assert.Equal(500, (int?)e["http"]?["statusCode"]);
        Assert.Equal("10.1.2.3", (string?)e["http"]?["clientIp"]);
        Assert.Equal("curl/8.0", (string?)e["http"]?["userAgent"]);
        // timestamp is the span END (SDK semantics: duration events are stamped at completion)
        Assert.StartsWith("2025-06-15T15:06:40.250", (string?)e["eventTimestamp"]);
    }

    [Fact]
    public void Client_db_span_maps_to_Performance_with_db_properties()
    {
        var request = Parse("""
        {
          "resourceSpans": [{
            "scopeSpans": [{
              "spans": [{
                "name": "SELECT invoices",
                "kind": "SPAN_KIND_CLIENT",
                "startTimeUnixNano": "1750000000000000000",
                "endTimeUnixNano":   "1750000000042000000",
                "attributes": [
                  { "key": "db.system",    "value": { "stringValue": "postgresql" } },
                  { "key": "db.statement", "value": { "stringValue": "SELECT * FROM invoices WHERE id = $1" } }
                ]
              }]
            }]
          }]
        }
        """);

        var e = Assert.Single(OtlpTranslator.TranslateTraces(request));
        Assert.Equal("Performance", (string?)e["eventType"]);
        Assert.Equal("Completed", (string?)e["status"]);
        Assert.Equal(42.0, (double?)e["durationMs"]);
        Assert.Equal(42.0, (double?)e["dbDurationMs"]);
        Assert.Equal("postgresql", (string?)e["properties"]?["db"]?["system"]);
    }

    [Fact]
    public void Span_exception_event_attaches_exception_and_switches_type()
    {
        var request = Parse("""
        {
          "resourceSpans": [{
            "scopeSpans": [{
              "spans": [{
                "name": "ProcessPayment",
                "kind": 1,
                "startTimeUnixNano": "1750000000000000000",
                "endTimeUnixNano":   "1750000000100000000",
                "status": { "code": "STATUS_CODE_ERROR" },
                "events": [{
                  "timeUnixNano": "1750000000090000000",
                  "name": "exception",
                  "attributes": [
                    { "key": "exception.type",    "value": { "stringValue": "TimeoutException" } },
                    { "key": "exception.message", "value": { "stringValue": "gateway timed out" } }
                  ]
                }]
              }]
            }]
          }]
        }
        """);

        var e = Assert.Single(OtlpTranslator.TranslateTraces(request));
        Assert.Equal("Exception", (string?)e["eventType"]);
        Assert.Equal("Error", (string?)e["severity"]);
        Assert.Equal("TimeoutException", (string?)e["exception"]?["type"]);
    }

    [Theory]
    [InlineData(2, null, "ApiRequest")]                 // SERVER
    [InlineData(3, null, "Integration")]                // CLIENT (non-db)
    [InlineData(3, "mysql", "Performance")]             // CLIENT + db.system
    [InlineData(4, null, "BackgroundJob")]              // PRODUCER
    [InlineData(5, null, "BackgroundJob")]              // CONSUMER
    [InlineData(1, null, "Application")]                // INTERNAL
    public void Span_kind_maps_to_event_type(int kind, string? dbSystem, string expected)
    {
        var attrs = new JsonArray();
        if (dbSystem is not null)
            attrs.Add(new JsonObject
            {
                ["key"] = "db.system",
                ["value"] = new JsonObject { ["stringValue"] = dbSystem },
            });
        var request = new JsonObject
        {
            ["resourceSpans"] = new JsonArray(new JsonObject
            {
                ["scopeSpans"] = new JsonArray(new JsonObject
                {
                    ["spans"] = new JsonArray(new JsonObject
                    {
                        ["name"] = "op",
                        ["kind"] = kind,
                        ["startTimeUnixNano"] = "1750000000000000000",
                        ["endTimeUnixNano"] = "1750000000001000000",
                        ["attributes"] = attrs,
                    }),
                }),
            }),
        };

        var e = Assert.Single(OtlpTranslator.TranslateTraces(request));
        Assert.Equal(expected, (string?)e["eventType"]);
    }

    // ------------------------------------------------------- pipeline round-trip

    [Fact]
    public void Translated_envelopes_deserialize_into_EventEnvelope_with_valid_enums()
    {
        // The keys the translator emits must be the keys EventEnvelope actually binds —
        // this is exactly the class IngestService deserializes before validating.
        var all = OtlpTranslator.TranslateLogs(Parse(LogsRequest))
            .Concat(OtlpTranslator.TranslateTraces(Parse(ServerSpanRequest)));

        foreach (var node in all)
        {
            var env = node.Deserialize<EventEnvelope>(EnvelopeOpts);
            Assert.NotNull(env);
            Assert.True(EventTypes.IsValid(env!.EventType), $"invalid eventType '{env.EventType}'");
            Assert.NotNull(Severities.Parse(env.Severity));
            if (env.Status is not null) Assert.True(OperationStatuses.IsValid(env.Status));
            Assert.NotNull(env.EventTimestamp);
        }

        var span = OtlpTranslator.TranslateTraces(Parse(ServerSpanRequest))[0]
            .Deserialize<EventEnvelope>(EnvelopeOpts)!;
        Assert.Equal("GET", span.Http?.Method);
        Assert.Equal(500, span.Http?.StatusCode);
        Assert.Equal(250.0, span.DurationMs);
    }
}
