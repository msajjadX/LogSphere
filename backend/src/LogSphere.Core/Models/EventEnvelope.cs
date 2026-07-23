using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace LogSphere.Core.Models;

/// <summary>Incoming ingestion envelope (schema v1.0). Tenant/project/application/environment
/// are always resolved server-side from the API key, never trusted from the client.</summary>
public sealed class EventEnvelope
{
    public string SchemaVersion { get; set; } = "1.0";
    public Guid? EventId { get; set; }
    public string EventType { get; set; } = "";
    public DateTimeOffset? EventTimestamp { get; set; }
    public string Severity { get; set; } = "Information";
    public string? Status { get; set; }

    public string? CorrelationId { get; set; }
    public string? TraceId { get; set; }
    public string? SpanId { get; set; }
    public string? ParentSpanId { get; set; }
    public int? Sequence { get; set; }

    public string? Module { get; set; }
    public string? Component { get; set; }
    public string? Message { get; set; }
    public string? ActionName { get; set; }

    public string? UserId { get; set; }
    public string? SessionId { get; set; }
    public string? UserName { get; set; }

    public string? BusinessEntityType { get; set; }
    public string? BusinessEntityId { get; set; }

    public HttpInfo? Http { get; set; }

    public double? DurationMs { get; set; }
    public double? DbDurationMs { get; set; }
    public double? ExternalDurationMs { get; set; }

    public string? MachineName { get; set; }
    public string? ApplicationVersion { get; set; }
    public string? DeploymentVersion { get; set; }

    public JsonNode? RequestData { get; set; }
    public JsonNode? ResponseData { get; set; }
    public ExceptionInfo? Exception { get; set; }
    public JsonNode? Properties { get; set; }
}

public sealed class HttpInfo
{
    public string? Method { get; set; }
    public string? Route { get; set; }
    public int? StatusCode { get; set; }
    public string? ClientIp { get; set; }
    public string? UserAgent { get; set; }
    public int? RequestSize { get; set; }
    public int? ResponseSize { get; set; }
}

public sealed class ExceptionInfo
{
    public string? Type { get; set; }
    public string? Message { get; set; }
    public string? StackTrace { get; set; }
    public string? ErrorCode { get; set; }
    public string? ClassName { get; set; }
    public string? MethodName { get; set; }
    public string? FileName { get; set; }
    public int? LineNumber { get; set; }
    public List<ExceptionInfo>? InnerExceptions { get; set; }
}

public sealed class BatchRequest
{
    public List<JsonNode?> Events { get; set; } = new();
}

/// <summary>Ingestion context resolved from a validated API key.</summary>
public sealed record IngestContext(
    Guid CredentialId, Guid TenantId, Guid ProjectId, Guid ApplicationId, short EnvironmentId,
    string ProjectCode, string ApplicationCode, int RateLimitPerMinute, string[]? IpAllowlist);

public sealed record RejectedEvent(int Index, string Reason);

public sealed record IngestResult(int Accepted, List<RejectedEvent> Rejected);
