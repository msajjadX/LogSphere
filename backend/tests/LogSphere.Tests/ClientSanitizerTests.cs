using System.Text.Json.Nodes;
using LogSphere.Sdk;
using Microsoft.Extensions.Options;

namespace LogSphere.Tests;

// Regression for SDK <= 1.1.2: Sanitize re-assigned each array element into its own slot, and
// JsonArray.SetItem throws "node already has a parent" for the same instance (JsonObject's
// indexer tolerates it — that asymmetry is why object-only payloads always worked). Submit
// swallowed the throw, so every event containing a JSON array was silently dropped.
public class ClientSanitizerTests
{
    private readonly ClientSanitizer _sanitizer = new();

    [Fact]
    public void Array_of_scalars_survives_sanitization()
    {
        var node = JsonNode.Parse("""{ "attachmentIds": ["a1", "a2"], "count": 2 }""")!;
        var result = _sanitizer.Sanitize(node);
        Assert.Equal("a1", result!["attachmentIds"]![0]!.GetValue<string>());
        Assert.Equal("a2", result["attachmentIds"]![1]!.GetValue<string>());
    }

    [Fact]
    public void Array_of_objects_is_sanitized_in_place()
    {
        var node = JsonNode.Parse("""
            { "items": [ { "name": "ok", "password": "hunter2" }, { "tags": ["x", "y"] } ] }
            """)!;
        var json = _sanitizer.Sanitize(node)!.ToJsonString();
        Assert.DoesNotContain("hunter2", json);
        Assert.Contains("[REDACTED]", json);
        Assert.Contains("\"x\"", json);
    }

    [Fact]
    public void Nested_arrays_survive_sanitization()
    {
        var node = JsonNode.Parse("""{ "matrix": [[1, 2], [3, 4]], "filters": ["open", "high"] }""")!;
        var result = _sanitizer.Sanitize(node);
        Assert.Equal(3, result!["matrix"]![1]![0]!.GetValue<int>());
    }

    [Fact]
    public async Task Submit_with_array_payload_is_not_dropped()
    {
        // The unit tests above pass on any sanitizer that doesn't throw; this one guards the
        // consequence that mattered — Submit catching the throw and losing the event. Any
        // sanitize failure surfaces through OnDiagnostic, and a drop shows in DroppedEvents.
        var diagnostics = new List<string>();
        var options = Options.Create(new LogSphereOptions
        {
            Endpoint = "http://localhost:9",
            ProjectKey = "ls_test.sanitizer",
            ApplicationName = "LogSphere.Tests",
            FlushInterval = TimeSpan.FromMinutes(5), // keep the pump idle for the test's lifetime
            OnDiagnostic = (m, _) => { lock (diagnostics) diagnostics.Add(m); }
        });
        await using var client = new LogSphereClient(options, new StubHttpFactory());

        client.Submit(new JsonObject
        {
            ["eventType"] = "ApiRequest",
            ["message"] = "TICKET.CREATE",
            ["requestData"] = JsonNode.Parse(
                """{ "subject": "s", "attachmentIds": ["a1", "a2"], "tags": ["vip"] }"""),
            ["properties"] = JsonNode.Parse("""{ "filters": { "status": ["open", "closed"] } }""")
        });

        Assert.Empty(diagnostics);
        Assert.Equal(0, client.DroppedEvents);
    }

    private sealed class StubHttpFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
