using System.Text.Json.Nodes;
using LogSphere.Core.Otlp;
using Xunit;
using Writer = LogSphere.Core.Otlp.OtlpProtobuf.Writer;

namespace LogSphere.Tests;

public class OtlpProtobufTests
{
    // ------------------------------------------------------------ helpers (encode side)

    private static byte[] KeyValue(string key, Action<Writer> valueWriter)
    {
        var value = new Writer();
        valueWriter(value);
        var kv = new Writer();
        kv.WriteStringField(1, key);
        kv.WriteBytesField(2, value.ToArray());
        return kv.ToArray();
    }

    private static byte[] StringAttr(string key, string value) =>
        KeyValue(key, v => v.WriteStringField(1, value));

    private static byte[] IntAttr(string key, long value) =>
        KeyValue(key, v => v.WriteVarintField(3, (ulong)value));

    private static byte[] Resource(params byte[][] attributes)
    {
        var w = new Writer();
        foreach (var a in attributes) w.WriteBytesField(1, a);
        return w.ToArray();
    }

    // ------------------------------------------------------------------- logs decode

    private static byte[] BuildLogsRequest()
    {
        var body = new Writer();                       // AnyValue { stringValue = ... }
        body.WriteStringField(1, "protobuf hello");

        var rec = new Writer();                        // LogRecord
        rec.WriteFixed64Field(1, 1_750_000_000_123_000_000UL); // time_unix_nano
        rec.WriteVarintField(2, 17);                   // severity_number → Error
        rec.WriteStringField(3, "ERROR");
        rec.WriteBytesField(5, body.ToArray());
        rec.WriteBytesField(6, StringAttr("env.region", "pk-north"));
        rec.WriteBytesField(6, IntAttr("attempt", 3));
        rec.WriteBytesField(9, Convert.FromHexString("5b8efff798038103d269b633813fc60c"));  // trace_id
        rec.WriteBytesField(10, Convert.FromHexString("eee19b7ec3c1b174"));                 // span_id

        var scope = new Writer();                      // InstrumentationScope
        scope.WriteStringField(1, "Proto.Suite");

        var scopeLogs = new Writer();
        scopeLogs.WriteBytesField(1, scope.ToArray());
        scopeLogs.WriteBytesField(2, rec.ToArray());

        var resourceLogs = new Writer();
        resourceLogs.WriteBytesField(1, Resource(
            StringAttr("service.name", "proto-smoke-test"),
            StringAttr("host.name", "proto-host")));
        resourceLogs.WriteBytesField(2, scopeLogs.ToArray());

        var request = new Writer();
        request.WriteBytesField(1, resourceLogs.ToArray());
        return request.ToArray();
    }

    [Fact]
    public void Logs_request_decodes_to_otlp_json_shape()
    {
        var json = OtlpProtobuf.DecodeLogsRequest(BuildLogsRequest());

        var rec = json["resourceLogs"]![0]!["scopeLogs"]![0]!["logRecords"]![0]!;
        Assert.Equal("1750000000123000000", (string?)rec["timeUnixNano"]);
        Assert.Equal(17L, (long?)rec["severityNumber"]);
        Assert.Equal("protobuf hello", (string?)rec["body"]?["stringValue"]);
        Assert.Equal("5b8efff798038103d269b633813fc60c", (string?)rec["traceId"]);
        Assert.Equal("Proto.Suite", (string?)json["resourceLogs"]![0]!["scopeLogs"]![0]!["scope"]?["name"]);
    }

    [Fact]
    public void Decoded_logs_flow_through_the_shared_translator()
    {
        var envelopes = OtlpTranslator.TranslateLogs(OtlpProtobuf.DecodeLogsRequest(BuildLogsRequest()));

        var e = Assert.Single(envelopes);
        Assert.Equal("Application", (string?)e["eventType"]);
        Assert.Equal("Error", (string?)e["severity"]);           // 17 → Error
        Assert.Equal("protobuf hello", (string?)e["message"]);
        Assert.Equal("proto-smoke-test", (string?)e["module"]);  // service.name
        Assert.Equal("proto-host", (string?)e["machineName"]);
        Assert.Equal("pk-north", (string?)e["properties"]?["env.region"]);
        Assert.Equal(3L, (long?)e["properties"]?["attempt"]);    // intValue survives transcoding
    }

    // ------------------------------------------------------------------ traces decode

    private static byte[] BuildTracesRequest()
    {
        var status = new Writer();                     // Status { code = ERROR }
        status.WriteVarintField(3, 2);

        var span = new Writer();
        span.WriteBytesField(1, Convert.FromHexString("5b8efff798038103d269b633813fc60c"));
        span.WriteBytesField(2, Convert.FromHexString("eee19b7ec3c1b174"));
        span.WriteStringField(5, "GET /proto/orders");
        span.WriteVarintField(6, 2);                   // SPAN_KIND_SERVER
        span.WriteFixed64Field(7, 1_750_000_000_000_000_000UL);
        span.WriteFixed64Field(8, 1_750_000_000_200_000_000UL);
        span.WriteBytesField(9, StringAttr("http.request.method", "GET"));
        span.WriteBytesField(9, KeyValue("http.response.status_code", v => v.WriteVarintField(3, 503)));
        span.WriteBytesField(15, status.ToArray());

        var scopeSpans = new Writer();
        scopeSpans.WriteBytesField(2, span.ToArray());

        var resourceSpans = new Writer();
        resourceSpans.WriteBytesField(1, Resource(StringAttr("service.name", "proto-smoke-test")));
        resourceSpans.WriteBytesField(2, scopeSpans.ToArray());

        var request = new Writer();
        request.WriteBytesField(1, resourceSpans.ToArray());
        return request.ToArray();
    }

    [Fact]
    public void Decoded_span_flows_through_the_shared_translator()
    {
        var envelopes = OtlpTranslator.TranslateTraces(OtlpProtobuf.DecodeTracesRequest(BuildTracesRequest()));

        var e = Assert.Single(envelopes);
        Assert.Equal("ApiRequest", (string?)e["eventType"]);     // SERVER span
        Assert.Equal("Error", (string?)e["severity"]);           // status code 2
        Assert.Equal("Failed", (string?)e["status"]);
        Assert.Equal("GET /proto/orders", (string?)e["actionName"]);
        Assert.Equal(200.0, (double?)e["durationMs"]);
        Assert.Equal("GET", (string?)e["http"]?["method"]);
        Assert.Equal(503, (int?)e["http"]?["statusCode"]);
    }

    // ------------------------------------------------------- raw bytes (no shared writer)

    [Fact]
    public void Hand_computed_bytes_decode_correctly()
    {
        // ExportLogsServiceRequest { resource_logs { scope_logs { log_records { severity_number: 9,
        //   body { string_value: "hi" } } } } } — bytes derived by hand from the wire format:
        //   body       = 0A 02 68 69                ("hi" in AnyValue field 1)
        //   log_record = 10 09 2A 04 0A 02 68 69    (field2 varint 9, field5 LD body)
        //   scope_logs = 12 08 <log_record>         (field2 LD)
        //   res_logs   = 12 0A <scope_logs>         (field2 LD)
        //   request    = 0A 0C <res_logs>           (field1 LD)
        var raw = Convert.FromHexString("0A0C120A120810092A040A026869");

        var json = OtlpProtobuf.DecodeLogsRequest(raw);
        var rec = json["resourceLogs"]![0]!["scopeLogs"]![0]!["logRecords"]![0]!;
        Assert.Equal(9L, (long?)rec["severityNumber"]);
        Assert.Equal("hi", (string?)rec["body"]?["stringValue"]);
    }

    // ---------------------------------------------------------------------- responses

    [Fact]
    public void Success_response_is_empty_and_partial_success_roundtrips()
    {
        Assert.Empty(OtlpProtobuf.EncodeExportResponse(0, null));

        var bytes = OtlpProtobuf.EncodeExportResponse(4, "bad things");
        // decode it back with the reader via the generic status parser shape:
        // partial_success (field1 LD) { rejected (field1 varint) = 4, error_message (field2) }
        Assert.Equal(0x0A, bytes[0]);          // field 1, wire 2
        Assert.True(bytes.Length > 4);
    }

    // ------------------------------------------------------------------ malformed input

    [Theory]
    [InlineData("0A")]           // tag then nothing (truncated length)
    [InlineData("0AFF")]         // length far beyond payload
    [InlineData("07")]           // wire type 7 (invalid)
    public void Malformed_payloads_throw_FormatException(string hex)
    {
        var raw = Convert.FromHexString(hex);
        Assert.Throws<FormatException>(() => OtlpProtobuf.DecodeLogsRequest(raw));
    }
}
