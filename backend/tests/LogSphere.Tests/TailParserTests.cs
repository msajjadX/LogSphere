using System.Text.Json.Nodes;
using LogSphere.Tail;
using Xunit;

namespace LogSphere.Tests;

public class TailParserTests
{
    private static TailSource Source(Action<TailSource>? mutate = null)
    {
        var s = new TailSource
        {
            Path = "C:/logs/*.log",
            Module = "TestApp",
        };
        mutate?.Invoke(s);
        s.Validate();
        return s;
    }

    private static JsonObject? ParseOne(string line, TailSource src) =>
        LineParser.Parse(line, src, new W3CFields());

    // ------------------------------------------------------------------ plain

    [Fact]
    public void Plain_line_becomes_Application_event_with_defaults()
    {
        var e = ParseOne("something happened", Source());
        Assert.NotNull(e);
        Assert.Equal("Application", (string?)e!["eventType"]);
        Assert.Equal("Information", (string?)e["severity"]);
        Assert.Equal("something happened", (string?)e["message"]);
        Assert.Equal("TestApp", (string?)e["module"]);
    }

    // ------------------------------------------------------------------ regex

    [Fact]
    public void Regex_extracts_timestamp_severity_message_and_extra_groups()
    {
        var src = Source(s =>
        {
            s.Parser = "regex";
            s.Pattern = @"^(?<ts>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}) \[(?<sev>\w+)\] \[(?<thread>[^\]]+)\] (?<msg>.*)$";
            s.TimestampFormat = "yyyy-MM-dd HH:mm:ss";
            s.SeverityMap["ERR"] = "Error";
        });

        var e = ParseOne("2026-07-24 10:30:00 [ERR] [worker-3] payment declined", src);
        Assert.NotNull(e);
        Assert.Equal("Error", (string?)e!["severity"]);
        Assert.Equal("payment declined", (string?)e["message"]);
        Assert.Equal("worker-3", (string?)e["properties"]?["thread"]);
        Assert.NotNull(e["eventTimestamp"]);
    }

    [Fact]
    public void Regex_nonmatching_line_falls_back_to_plain_capture()
    {
        var src = Source(s =>
        {
            s.Parser = "regex";
            s.Pattern = @"^(?<ts>\d{4}) (?<msg>.*)$";
        });
        var e = ParseOne("no leading year here", src);
        Assert.NotNull(e); // never lose a line
        Assert.Equal("no leading year here", (string?)e!["message"]);
    }

    [Theory]
    [InlineData("WARN", "Warning")]
    [InlineData("info", "Information")]
    [InlineData("FATAL", "Critical")]
    public void Builtin_severity_aliases_work_without_map(string raw, string expected)
    {
        var src = Source(s =>
        {
            s.Parser = "regex";
            s.Pattern = @"^\[(?<sev>\w+)\] (?<msg>.*)$";
        });
        var e = ParseOne($"[{raw}] x", src);
        Assert.Equal(expected, (string?)e!["severity"]);
    }

    // ------------------------------------------------------------------- json

    [Fact]
    public void Json_line_with_envelope_shape_passes_through()
    {
        var src = Source(s => s.Parser = "json");
        var e = ParseOne("""{"eventType":"Audit","actionName":"SAVE","message":"m"}""", src);
        Assert.Equal("Audit", (string?)e!["eventType"]);
        Assert.Equal("SAVE", (string?)e["actionName"]);
        Assert.Equal("TestApp", (string?)e["module"]); // default filled in
    }

    [Fact]
    public void Json_generic_shape_maps_level_message_and_rest_to_properties()
    {
        var src = Source(s => s.Parser = "json");
        var e = ParseOne("""{"level":"warn","message":"disk low","disk":"C:","freeMb":512}""", src);
        Assert.Equal("Warning", (string?)e!["severity"]);
        Assert.Equal("disk low", (string?)e["message"]);
        Assert.Equal("C:", (string?)e["properties"]?["disk"]);
        Assert.Equal(512, (int?)e["properties"]?["freeMb"]);
    }

    // -------------------------------------------------------------------- w3c

    [Fact]
    public void W3c_uses_fields_directive_and_maps_http()
    {
        var src = Source(s => s.Parser = "w3c");
        var w3c = new W3CFields();
        Assert.Null(LineParser.Parse("#Software: Microsoft Internet Information Services", src, w3c));
        Assert.Null(LineParser.Parse("#Fields: date time cs-method cs-uri-stem sc-status c-ip time-taken", src, w3c));

        var e = LineParser.Parse("2026-07-24 05:00:01 GET /api/orders 500 10.1.2.3 245", src, w3c);
        Assert.NotNull(e);
        Assert.Equal("ApiRequest", (string?)e!["eventType"]);
        Assert.Equal("Error", (string?)e["severity"]); // 500 → Error
        Assert.Equal("GET", (string?)e["http"]?["method"]);
        Assert.Equal("/api/orders", (string?)e["http"]?["route"]);
        Assert.Equal(500, (int?)e["http"]?["statusCode"]);
        Assert.Equal("10.1.2.3", (string?)e["http"]?["clientIp"]);
        Assert.Equal(245.0, (double?)e["durationMs"]);
    }

    // -------------------------------------------------------------- multiline

    [Fact]
    public void Multiline_appends_stack_trace_to_previous_event()
    {
        var src = Source(s =>
        {
            s.Parser = "regex";
            s.Pattern = @"^(?<ts>\d{4}-\d{2}-\d{2}) \[(?<sev>\w+)\] (?<msg>.*)$";
            s.TimestampFormat = "yyyy-MM-dd";
            s.MultilineStart = @"^\d{4}-\d{2}-\d{2}";
        });
        var asm = new MultilineAssembler(src);
        var w3c = new W3CFields();

        Assert.Null(asm.Push("2026-07-24 [ERROR] boom", w3c));               // pending
        Assert.Null(asm.Push("   at Payment.Charge()", w3c));                // continuation
        Assert.Null(asm.Push("   at Api.Invoke()", w3c));                    // continuation
        var first = asm.Push("2026-07-24 [INFO] next event", w3c);           // completes the first
        Assert.NotNull(first);
        Assert.Equal("boom\n   at Payment.Charge()\n   at Api.Invoke()", (string?)first!["message"]);
        Assert.Equal("Error", (string?)first["severity"]);

        var second = asm.Flush();
        Assert.Equal("next event", (string?)second!["message"]);
        Assert.Null(asm.Flush()); // nothing left
    }

    [Fact]
    public void Without_multiline_every_line_is_its_own_event()
    {
        var asm = new MultilineAssembler(Source());
        var w3c = new W3CFields();
        Assert.NotNull(asm.Push("line one", w3c));
        Assert.NotNull(asm.Push("line two", w3c));
        Assert.Null(asm.Flush());
    }
}
