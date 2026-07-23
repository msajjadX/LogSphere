using System.Text.Json.Nodes;

namespace LogSphere.Core.Models;

public class LogQueryFilter
{
    public DateTimeOffset? From { get; set; }
    public DateTimeOffset? To { get; set; }
    public Guid? TenantId { get; set; }
    public Guid? ProjectId { get; set; }
    public Guid? ApplicationId { get; set; }
    public short? EnvironmentId { get; set; }
    /// <summary>Multi-select variants (column filters). Combined with the single-value
    /// fields above via AND, so either style can be used.</summary>
    public List<Guid>? ProjectIds { get; set; }
    public List<Guid>? ApplicationIds { get; set; }
    public List<short>? EnvironmentIds { get; set; }
    public string? Module { get; set; }
    public string? Component { get; set; }
    public List<string>? EventTypes { get; set; }
    public List<string>? Severities { get; set; }
    public List<string>? Statuses { get; set; }
    public Guid? EventId { get; set; }
    public string? CorrelationId { get; set; }
    public string? TraceId { get; set; }
    public string? UserId { get; set; }
    public string? BusinessEntityType { get; set; }
    public string? BusinessEntityId { get; set; }
    public string? ActionName { get; set; }
    public string? HttpRoute { get; set; }
    public int? HttpStatusCode { get; set; }
    public string? ExceptionType { get; set; }
    public string? Fingerprint { get; set; }
    public string? ApplicationVersion { get; set; }
    public string? MachineName { get; set; }
    public string? Text { get; set; }
    public long? AfterId { get; set; }
    public int Limit { get; set; } = 100;
    public string Order { get; set; } = "desc";
}

public class LogEventSummary
{
    public long Id { get; set; }
    public Guid EventId { get; set; }
    public Guid TenantId { get; set; }
    public Guid ProjectId { get; set; }
    public string? ProjectName { get; set; }
    public Guid ApplicationId { get; set; }
    public string? ApplicationName { get; set; }
    public short EnvironmentId { get; set; }
    public string? EnvironmentName { get; set; }
    public string? Module { get; set; }
    public string? Component { get; set; }
    public string EventType { get; set; } = "";
    public string Severity { get; set; } = "";
    public string? Status { get; set; }
    public DateTimeOffset EventTimestamp { get; set; }
    public string? CorrelationId { get; set; }
    public string? TraceId { get; set; }
    public string? UserId { get; set; }
    public string? UserName { get; set; }
    public string? BusinessEntityType { get; set; }
    public string? BusinessEntityId { get; set; }
    public string? ActionName { get; set; }
    public string? Message { get; set; }
    public double? DurationMs { get; set; }
    public string? HttpMethod { get; set; }
    public string? HttpRoute { get; set; }
    public int? HttpStatusCode { get; set; }
    public string? ExceptionType { get; set; }
    public string? ExceptionFingerprint { get; set; }
    public string? MachineName { get; set; }
    public string? ApplicationVersion { get; set; }
}

public sealed class LogEventDetail : LogEventSummary
{
    public DateTimeOffset ReceivedTimestamp { get; set; }
    public string? SpanId { get; set; }
    public string? ParentSpanId { get; set; }
    public int? Sequence { get; set; }
    public string? SessionId { get; set; }
    public double? DbDurationMs { get; set; }
    public double? ExternalDurationMs { get; set; }
    public string? ClientIp { get; set; }
    public string? UserAgent { get; set; }
    public int? RequestSize { get; set; }
    public int? ResponseSize { get; set; }
    public string? DeploymentVersion { get; set; }
    public string? ExceptionMessage { get; set; }
    public string? ErrorCode { get; set; }
    public JsonNode? Properties { get; set; }
    public JsonNode? RequestData { get; set; }
    public JsonNode? ResponseData { get; set; }
    public JsonNode? ExceptionData { get; set; }
    public bool SanitizationApplied { get; set; }
    public int SanitizedFieldCount { get; set; }
    public bool Truncated { get; set; }
    public string SchemaVersion { get; set; } = "1.0";
}

public sealed record LogPage(List<LogEventSummary> Items, long? NextCursor);

/// <summary>Search criteria for the Traces landing page. Inherits the row-level event filter;
/// adds trace-level (grouped) criteria applied via HAVING, so the database returns up to
/// `Limit` traces that MATCH — instead of the newest N being filtered client-side.</summary>
public sealed class TraceSearchRequest : LogQueryFilter
{
    /// <summary>Case-insensitive substring match on the trace's display name (action/route).</summary>
    public string? NameContains { get; set; }
    /// <summary>Case-insensitive substring match on the trace id.</summary>
    public string? TraceIdContains { get; set; }
    public int? MinEvents { get; set; }
    public bool ErrorsOnly { get; set; }
    /// <summary>Only traces whose longest step took at least this many milliseconds.</summary>
    public double? MinDurationMs { get; set; }
    /// <summary>newest (default) | slowest | errors</summary>
    public string? Sort { get; set; }
}

public sealed class StatsRequest
{
    public DateTimeOffset? From { get; set; }
    public DateTimeOffset? To { get; set; }
    public Guid? ProjectId { get; set; }
    public short? EnvironmentId { get; set; }
    public string? Fingerprint { get; set; }
    public int IntervalMinutes { get; set; } = 5;
    public string Metric { get; set; } = "count";
}

public sealed class StatsOverview
{
    public long TotalEvents { get; set; }
    public long ErrorCount { get; set; }
    public long WarningCount { get; set; }
    public double ErrorRate { get; set; }
    public double WarningRate { get; set; }
    public double RequestsPerMinute { get; set; }
    public double? AvgDurationMs { get; set; }
    public double? P95DurationMs { get; set; }
    public double? P99DurationMs { get; set; }
    public long SlowRequests { get; set; }
    public long ActiveExceptionGroups { get; set; }
    public Dictionary<string, long> SeverityCounts { get; set; } = new();
    public List<NamedCount> TopProjects { get; set; } = new();
    public List<NamedCount> TopModules { get; set; } = new();
    public List<RouteFailure> TopFailingRoutes { get; set; } = new();
    public List<LogEventSummary> RecentCritical { get; set; } = new();
    public long QueueDepth { get; set; }
    public bool IngestionHealthy { get; set; }
}

public sealed record NamedCount(string Name, long Count, long ErrorCount);
public sealed record RouteFailure(string Route, long Count, long ErrorCount, double ErrorRate);
public sealed record TimeseriesPoint(DateTimeOffset Bucket, string Severity, double Value);

public sealed class TraceSpan
{
    public string SpanId { get; set; } = "";
    public string? ParentSpanId { get; set; }
    public string? Name { get; set; }
    public DateTimeOffset Start { get; set; }
    public double? DurationMs { get; set; }
    public string EventType { get; set; } = "";
    public string Severity { get; set; } = "";
    public string? Status { get; set; }
    public Guid EventId { get; set; }
}

public sealed record TraceResponse(string TraceId, List<TraceSpan> Spans, List<LogEventSummary> Events);

public sealed class AuditQueryFilter
{
    public DateTimeOffset? From { get; set; }
    public DateTimeOffset? To { get; set; }
    public Guid? ProjectId { get; set; }
    public List<Guid>? ProjectIds { get; set; }
    public List<Guid>? ApplicationIds { get; set; }
    public List<short>? EnvironmentIds { get; set; }
    public string? ActorUserId { get; set; }
    public string? ActionName { get; set; }
    public string? EntityType { get; set; }
    public string? EntityId { get; set; }
    /// <summary>Case-insensitive substring match on the audit message.</summary>
    public string? Text { get; set; }
    public long? AfterId { get; set; }
    public int Limit { get; set; } = 100;
}

public sealed class ExportRequest : LogQueryFilter
{
    public string Format { get; set; } = "csv";
}

public sealed class ExceptionGroupSearch
{
    public DateTimeOffset? From { get; set; }
    public DateTimeOffset? To { get; set; }
    public Guid? ProjectId { get; set; }
    public List<Guid>? ProjectIds { get; set; }
    public string? Status { get; set; }
    public string? Text { get; set; }
    /// <summary>Case-insensitive substring match on the module alone (column filter).</summary>
    public string? Module { get; set; }
    public int Limit { get; set; } = 50;
}

public sealed class SystemMetrics
{
    public long QueueDepth { get; set; }
    public long DeadLetterCount { get; set; }
    public double EventsPerSecond { get; set; }
    public double AvgIngestLatencyMs { get; set; }
    public long FailedWrites { get; set; }
    public long SanitizationFailures { get; set; }
    public long AuthFailures { get; set; }
    public bool DbHealthy { get; set; }
    public List<WorkerStatus> Workers { get; set; } = new();
    public List<PartitionInfo> Storage { get; set; } = new();
    public DbStats? Db { get; set; }
}

public sealed record WorkerStatus(string Name, DateTimeOffset? LastHeartbeat, bool Healthy);
public sealed record PartitionInfo(string Partition, double SizeMb, long Rows);

/// <summary>Connection counts for one path into PostgreSQL (pooled via PgBouncer vs direct).</summary>
public sealed class ConnectionGroup
{
    public int Total { get; set; }
    public int Active { get; set; }
    public int Idle { get; set; }
    public int Waiting { get; set; }
}

/// <summary>PostgreSQL-side health snapshot (pg_stat_database / pg_stat_activity / pg_stat_user_tables).</summary>
public sealed class DbStats
{
    public double DatabaseSizeMb { get; set; }
    public int ConnectionsTotal { get; set; }
    public int ConnectionsActive { get; set; }
    public int ConnectionsIdle { get; set; }
    public int ConnectionsWaiting { get; set; }
    public int MaxConnections { get; set; }
    /// <summary>Backends whose client is the DB host itself / loopback — i.e. arriving through PgBouncer.</summary>
    public ConnectionGroup PooledConnections { get; set; } = new();
    /// <summary>Backends connecting straight to PostgreSQL from other hosts (port 5432 logins).</summary>
    public ConnectionGroup DirectConnections { get; set; } = new();
    /// <summary>Buffer cache hit ratio (percent) since stats reset.</summary>
    public double CacheHitRatio { get; set; }
    public long TransactionsCommitted { get; set; }
    public long TransactionsRolledBack { get; set; }
    public long Deadlocks { get; set; }
    public long TempFiles { get; set; }
    public double TempBytesMb { get; set; }
    /// <summary>Seconds the longest currently-active query has been running.</summary>
    public double LongestQuerySeconds { get; set; }
    public List<TableStat> TopTables { get; set; } = new();
}

public sealed record TableStat(
    string Table, double SizeMb, long LiveRows, long DeadRows, DateTimeOffset? LastAutovacuum, DateTimeOffset? LastAutoanalyze);
