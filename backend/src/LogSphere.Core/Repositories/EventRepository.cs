using System.Text.Json.Nodes;
using LogSphere.Core.Auth;
using LogSphere.Core.Data;
using LogSphere.Core.Models;
using Npgsql;
using NpgsqlTypes;

namespace LogSphere.Core.Repositories;

public sealed class EventRepository(Db db)
{
    private long _failedWrites;
    public long FailedWrites => Interlocked.Read(ref _failedWrites);

    // ---------------------------------------------------------------- writes

    /// <summary>Idempotent batch insert from queue payloads (ON CONFLICT DO NOTHING on event id).
    /// Returns the set of event ids that were actually inserted (i.e. not conflicts) so callers can
    /// avoid double-counting aggregates when the durable queue redelivers an already-persisted event.</summary>
    public async Task<HashSet<Guid>> InsertBatchAsync(IReadOnlyList<JsonObject> payloads, CancellationToken ct)
    {
        var inserted = new HashSet<Guid>();
        if (payloads.Count == 0) return inserted;
        try
        {
            await using var conn = await db.OpenAsync(ct);
            await using var batch = new NpgsqlBatch(conn);
            foreach (var p in payloads) batch.BatchCommands.Add(BuildInsert(p));
            await using var reader = await batch.ExecuteReaderAsync(ct);
            do
            {
                while (await reader.ReadAsync(ct)) inserted.Add(reader.GetGuid(0));
            } while (await reader.NextResultAsync(ct));
            return inserted;
        }
        catch
        {
            Interlocked.Add(ref _failedWrites, payloads.Count);
            throw;
        }
    }

    private static NpgsqlBatchCommand BuildInsert(JsonObject p)
    {
        var cmd = new NpgsqlBatchCommand("""
            INSERT INTO log_events (
                event_id, tenant_id, project_id, application_id, environment_id, module, component,
                event_type, severity, status, event_timestamp, received_timestamp,
                correlation_id, trace_id, span_id, parent_span_id, sequence,
                user_id, session_id, user_name, business_entity_type, business_entity_id,
                action_name, message, duration_ms, db_duration_ms, external_duration_ms,
                http_method, http_route, http_status_code, client_ip, user_agent, request_size, response_size,
                machine_name, application_version, deployment_version,
                exception_type, exception_message, exception_fingerprint, error_code,
                properties, request_data, response_data, exception_data,
                sanitization_applied, sanitized_field_count, truncated, schema_version)
            VALUES (
                @event_id,@tenant_id,@project_id,@application_id,@environment_id,@module,@component,
                @event_type,@severity,@status,@event_timestamp,@received_timestamp,
                @correlation_id,@trace_id,@span_id,@parent_span_id,@sequence,
                @user_id,@session_id,@user_name,@business_entity_type,@business_entity_id,
                @action_name,@message,@duration_ms,@db_duration_ms,@external_duration_ms,
                @http_method,@http_route,@http_status_code,@client_ip,@user_agent,@request_size,@response_size,
                @machine_name,@application_version,@deployment_version,
                @exception_type,@exception_message,@exception_fingerprint,@error_code,
                @properties,@request_data,@response_data,@exception_data,
                @sanitization_applied,@sanitized_field_count,@truncated,@schema_version)
            ON CONFLICT (event_id, event_timestamp) DO NOTHING
            RETURNING event_id
            """);

        void Add(string name, object? value, NpgsqlDbType? type = null)
        {
            var param = new NpgsqlParameter(name, value ?? DBNull.Value);
            if (type is not null) param.NpgsqlDbType = type.Value;
            cmd.Parameters.Add(param);
        }

        string? S(string key) => p[key]?.GetValue<string?>();
        double? D(string key) => p[key] is JsonValue v && v.TryGetValue<double>(out var d) ? d : null;
        int? I(string key) => p[key] is JsonValue v && v.TryGetValue<int>(out var i) ? i : null;
        object? J(string key) => p[key] is { } node ? node.ToJsonString() : null;

        Add("event_id", Guid.Parse(S("eventId")!));
        Add("tenant_id", Guid.Parse(S("tenantId")!));
        Add("project_id", Guid.Parse(S("projectId")!));
        Add("application_id", Guid.Parse(S("applicationId")!));
        Add("environment_id", (short)I("environmentId")!.Value);
        Add("module", S("module"));
        Add("component", S("component"));
        Add("event_type", S("eventType"));
        Add("severity", (short)I("severity")!.Value);
        Add("status", S("status"));
        Add("event_timestamp", DateTime.SpecifyKind(DateTime.Parse(S("eventTimestamp")!, null,
            System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal), DateTimeKind.Utc));
        Add("received_timestamp", DateTime.SpecifyKind(DateTime.Parse(S("receivedTimestamp")!, null,
            System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal), DateTimeKind.Utc));
        Add("correlation_id", S("correlationId"));
        Add("trace_id", S("traceId"));
        Add("span_id", S("spanId"));
        Add("parent_span_id", S("parentSpanId"));
        Add("sequence", I("sequence"));
        Add("user_id", S("userId"));
        Add("session_id", S("sessionId"));
        Add("user_name", S("userName"));
        Add("business_entity_type", S("businessEntityType"));
        Add("business_entity_id", S("businessEntityId"));
        Add("action_name", S("actionName"));
        Add("message", S("message"));
        Add("duration_ms", D("durationMs"));
        Add("db_duration_ms", D("dbDurationMs"));
        Add("external_duration_ms", D("externalDurationMs"));
        Add("http_method", S("httpMethod"));
        Add("http_route", S("httpRoute"));
        Add("http_status_code", I("httpStatusCode"));
        Add("client_ip", S("clientIp"));
        Add("user_agent", S("userAgent"));
        Add("request_size", I("requestSize"));
        Add("response_size", I("responseSize"));
        Add("machine_name", S("machineName"));
        Add("application_version", S("applicationVersion"));
        Add("deployment_version", S("deploymentVersion"));
        Add("exception_type", S("exceptionType"));
        Add("exception_message", S("exceptionMessage"));
        Add("exception_fingerprint", S("exceptionFingerprint"));
        Add("error_code", S("errorCode"));
        Add("properties", J("properties"), NpgsqlDbType.Jsonb);
        Add("request_data", J("requestData"), NpgsqlDbType.Jsonb);
        Add("response_data", J("responseData"), NpgsqlDbType.Jsonb);
        Add("exception_data", J("exceptionData"), NpgsqlDbType.Jsonb);
        Add("sanitization_applied", p["sanitizationApplied"]?.GetValue<bool>() ?? false);
        Add("sanitized_field_count", I("sanitizedFieldCount") ?? 0);
        Add("truncated", p["truncated"]?.GetValue<bool>() ?? false);
        Add("schema_version", S("schemaVersion") ?? "1.0");
        return cmd;
    }

    // ---------------------------------------------------------------- filter plumbing

    private const string SummaryColumns = """
        e.id, e.event_id, e.tenant_id, e.project_id, p.name AS project_name,
        e.application_id, a.name AS application_name, e.environment_id, env.name AS environment_name,
        e.module, e.component,
        e.event_type, e.severity, e.status, e.event_timestamp, e.correlation_id, e.trace_id,
        e.user_id, e.user_name, e.business_entity_type, e.business_entity_id, e.action_name,
        e.message, e.duration_ms, e.http_method, e.http_route, e.http_status_code,
        e.exception_type, e.exception_fingerprint, e.machine_name, e.application_version
        """;

    private const string FromClause = """
        FROM log_events e
        LEFT JOIN projects p ON p.id = e.project_id
        LEFT JOIN applications a ON a.id = e.application_id
        LEFT JOIN environments env ON env.id = e.environment_id
        """;

    private static string BuildFilterSql(NpgsqlCommand cmd, LogQueryFilter f, UserContext user)
    {
        var where = new List<string> { user.BuildEventPredicate(cmd) };
        var n = cmd.Parameters.Count;

        void Add(string clause, object value)
        {
            where.Add(clause.Replace("{p}", $"@f{n}"));
            cmd.Parameters.AddWithValue($"f{n}", value);
            n++;
        }

        Add("e.event_timestamp >= {p}", (f.From ?? DateTimeOffset.UtcNow.AddHours(-24)).UtcDateTime);
        Add("e.event_timestamp <= {p}", (f.To ?? DateTimeOffset.UtcNow.AddMinutes(5)).UtcDateTime);
        if (f.TenantId is not null) Add("e.tenant_id = {p}", f.TenantId.Value);
        if (f.ProjectId is not null) Add("e.project_id = {p}", f.ProjectId.Value);
        if (f.ApplicationId is not null) Add("e.application_id = {p}", f.ApplicationId.Value);
        if (f.EnvironmentId is not null) Add("e.environment_id = {p}", f.EnvironmentId.Value);
        if (f.ProjectIds is { Count: > 0 }) Add("e.project_id = ANY({p})", f.ProjectIds.ToArray());
        if (f.ApplicationIds is { Count: > 0 }) Add("e.application_id = ANY({p})", f.ApplicationIds.ToArray());
        if (f.EnvironmentIds is { Count: > 0 }) Add("e.environment_id = ANY({p})", f.EnvironmentIds.ToArray());
        // text filters are contains-matches (Excel-style column filters)
        if (!string.IsNullOrWhiteSpace(f.Module)) Add("e.module ILIKE {p}", "%" + f.Module + "%");
        if (!string.IsNullOrWhiteSpace(f.Component)) Add("e.component ILIKE {p}", "%" + f.Component + "%");
        if (f.EventTypes is { Count: > 0 }) Add("e.event_type = ANY({p})", f.EventTypes.ToArray());
        if (f.Severities is { Count: > 0 })
        {
            var ids = f.Severities.Select(Severities.Parse).Where(s => s is not null).Select(s => s!.Value).ToArray();
            if (ids.Length > 0) Add("e.severity = ANY({p})", ids);
        }
        if (f.Statuses is { Count: > 0 }) Add("e.status = ANY({p})", f.Statuses.ToArray());
        if (f.EventId is not null) Add("e.event_id = {p}", f.EventId.Value);
        if (!string.IsNullOrWhiteSpace(f.CorrelationId)) Add("e.correlation_id = {p}", f.CorrelationId);
        if (!string.IsNullOrWhiteSpace(f.TraceId)) Add("e.trace_id = {p}", f.TraceId);
        if (!string.IsNullOrWhiteSpace(f.UserId)) Add("(e.user_id ILIKE {p} OR e.user_name ILIKE {p})", "%" + f.UserId + "%");
        if (!string.IsNullOrWhiteSpace(f.BusinessEntityType)) Add("e.business_entity_type ILIKE {p}", "%" + f.BusinessEntityType + "%");
        if (!string.IsNullOrWhiteSpace(f.BusinessEntityId)) Add("e.business_entity_id = {p}", f.BusinessEntityId);
        if (!string.IsNullOrWhiteSpace(f.ActionName)) Add("e.action_name ILIKE {p}", "%" + f.ActionName + "%");
        if (!string.IsNullOrWhiteSpace(f.HttpRoute)) Add("e.http_route ILIKE {p}", "%" + f.HttpRoute + "%");
        if (f.HttpStatusCode is not null) Add("e.http_status_code = {p}", f.HttpStatusCode.Value);
        if (!string.IsNullOrWhiteSpace(f.ExceptionType)) Add("e.exception_type ILIKE {p}", "%" + f.ExceptionType + "%");
        if (!string.IsNullOrWhiteSpace(f.Fingerprint)) Add("e.exception_fingerprint = {p}", f.Fingerprint);
        if (!string.IsNullOrWhiteSpace(f.ApplicationVersion)) Add("e.application_version = {p}", f.ApplicationVersion);
        if (!string.IsNullOrWhiteSpace(f.MachineName)) Add("e.machine_name = {p}", f.MachineName);
        if (!string.IsNullOrWhiteSpace(f.Text)) Add("e.message ILIKE {p}", "%" + f.Text + "%");
        if (f.AfterId is not null) Add("e.id > {p}", f.AfterId.Value);

        return string.Join(" AND ", where);
    }

    private static LogEventSummary ReadSummary(NpgsqlDataReader r) => new()
    {
        Id = r.Get<long>("id"),
        EventId = r.Get<Guid>("event_id"),
        TenantId = r.Get<Guid>("tenant_id"),
        ProjectId = r.Get<Guid>("project_id"),
        ProjectName = r.Get<string>("project_name"),
        ApplicationId = r.Get<Guid>("application_id"),
        ApplicationName = r.Get<string>("application_name"),
        EnvironmentId = r.Get<short>("environment_id"),
        EnvironmentName = r.Get<string>("environment_name"),
        Module = r.Get<string>("module"),
        Component = r.Get<string>("component"),
        EventType = r.Get<string>("event_type")!,
        Severity = Severities.Name(r.Get<short>("severity")),
        Status = r.Get<string>("status"),
        EventTimestamp = r.GetFieldValue<DateTimeOffset>(r.GetOrdinal("event_timestamp")),
        CorrelationId = r.Get<string>("correlation_id"),
        TraceId = r.Get<string>("trace_id"),
        UserId = r.Get<string>("user_id"),
        UserName = r.Get<string>("user_name"),
        BusinessEntityType = r.Get<string>("business_entity_type"),
        BusinessEntityId = r.Get<string>("business_entity_id"),
        ActionName = r.Get<string>("action_name"),
        Message = r.Get<string>("message"),
        DurationMs = r.Get<double?>("duration_ms"),
        HttpMethod = r.Get<string>("http_method"),
        HttpRoute = r.Get<string>("http_route"),
        HttpStatusCode = r.Get<int?>("http_status_code"),
        ExceptionType = r.Get<string>("exception_type"),
        ExceptionFingerprint = r.Get<string>("exception_fingerprint"),
        MachineName = r.Get<string>("machine_name"),
        ApplicationVersion = r.Get<string>("application_version")
    };

    // ---------------------------------------------------------------- queries

    public async Task<LogPage> QueryAsync(LogQueryFilter filter, UserContext user, CancellationToken ct)
    {
        var limit = Math.Clamp(filter.Limit, 1, 1000);
        var order = filter.Order.Equals("asc", StringComparison.OrdinalIgnoreCase) ? "ASC" : "DESC";
        await using var conn = await db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        var where = BuildFilterSql(cmd, filter, user);
        cmd.CommandText = $"SELECT {SummaryColumns} {FromClause} WHERE {where} ORDER BY e.id {order} LIMIT {limit + 1}";

        var items = new List<LogEventSummary>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) items.Add(ReadSummary(reader));

        long? next = null;
        if (items.Count > limit)
        {
            items.RemoveAt(items.Count - 1);
            next = items[^1].Id;
        }
        return new LogPage(items, next);
    }

    public async Task<LogEventDetail?> GetDetailAsync(Guid eventId, UserContext user, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        var predicate = user.BuildEventPredicate(cmd);
        cmd.CommandText = $"""
            SELECT {SummaryColumns},
                   e.received_timestamp, e.span_id, e.parent_span_id, e.sequence, e.session_id,
                   e.db_duration_ms, e.external_duration_ms, e.client_ip, e.user_agent,
                   e.request_size, e.response_size, e.deployment_version, e.exception_message, e.error_code,
                   e.properties::text AS properties, e.request_data::text AS request_data,
                   e.response_data::text AS response_data, e.exception_data::text AS exception_data,
                   e.sanitization_applied, e.sanitized_field_count, e.truncated, e.schema_version
            {FromClause}
            WHERE e.event_id = @eid AND {predicate}
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("eid", eventId);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;

        var summary = ReadSummary(reader);
        var detail = new LogEventDetail
        {
            Id = summary.Id, EventId = summary.EventId, TenantId = summary.TenantId,
            ProjectId = summary.ProjectId, ProjectName = summary.ProjectName,
            ApplicationId = summary.ApplicationId, ApplicationName = summary.ApplicationName,
            EnvironmentId = summary.EnvironmentId, EnvironmentName = summary.EnvironmentName,
            Module = summary.Module, Component = summary.Component,
            EventType = summary.EventType, Severity = summary.Severity, Status = summary.Status,
            EventTimestamp = summary.EventTimestamp, CorrelationId = summary.CorrelationId,
            TraceId = summary.TraceId, UserId = summary.UserId, UserName = summary.UserName,
            BusinessEntityType = summary.BusinessEntityType, BusinessEntityId = summary.BusinessEntityId,
            ActionName = summary.ActionName, Message = summary.Message, DurationMs = summary.DurationMs,
            HttpMethod = summary.HttpMethod, HttpRoute = summary.HttpRoute, HttpStatusCode = summary.HttpStatusCode,
            ExceptionType = summary.ExceptionType, ExceptionFingerprint = summary.ExceptionFingerprint,
            MachineName = summary.MachineName, ApplicationVersion = summary.ApplicationVersion,
            ReceivedTimestamp = reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("received_timestamp")),
            SpanId = reader.Get<string>("span_id"),
            ParentSpanId = reader.Get<string>("parent_span_id"),
            Sequence = reader.Get<int?>("sequence"),
            SessionId = reader.Get<string>("session_id"),
            DbDurationMs = reader.Get<double?>("db_duration_ms"),
            ExternalDurationMs = reader.Get<double?>("external_duration_ms"),
            ClientIp = reader.Get<string>("client_ip"),
            UserAgent = reader.Get<string>("user_agent"),
            RequestSize = reader.Get<int?>("request_size"),
            ResponseSize = reader.Get<int?>("response_size"),
            DeploymentVersion = reader.Get<string>("deployment_version"),
            ExceptionMessage = reader.Get<string>("exception_message"),
            ErrorCode = reader.Get<string>("error_code"),
            SanitizationApplied = reader.Get<bool>("sanitization_applied"),
            SanitizedFieldCount = reader.Get<int>("sanitized_field_count"),
            Truncated = reader.Get<bool>("truncated"),
            SchemaVersion = reader.Get<string>("schema_version") ?? "1.0",
            Properties = Parse(reader.Get<string>("properties")),
            ExceptionData = Parse(reader.Get<string>("exception_data"))
        };

        // field-level security: bodies only for users with the CanViewBodies grant
        if (user.CanViewBodies(summary.TenantId, summary.ProjectId))
        {
            detail.RequestData = Parse(reader.Get<string>("request_data"));
            detail.ResponseData = Parse(reader.Get<string>("response_data"));
        }
        else
        {
            var withheld = new JsonObject { ["_withheld"] = "You do not have permission to view request/response bodies." };
            if (reader.Get<string>("request_data") is not null) detail.RequestData = withheld.DeepClone();
            if (reader.Get<string>("response_data") is not null) detail.ResponseData = withheld.DeepClone();
        }
        return detail;

        static JsonNode? Parse(string? json) => json is null ? null : JsonNode.Parse(json);
    }

    public async Task<TraceResponse> GetTraceAsync(string traceId, UserContext user, CancellationToken ct)
    {
        var filter = new LogQueryFilter
        {
            TraceId = traceId, Limit = 1000, Order = "asc",
            From = DateTimeOffset.UtcNow.AddDays(-30), To = DateTimeOffset.UtcNow
        };
        var page = await QueryAsync(filter, user, ct);
        var spans = page.Items
            .Where(e => e.TraceId == traceId)
            .Select(e => new TraceSpan
            {
                SpanId = e.EventId.ToString("N")[..16],
                EventId = e.EventId,
                Name = e.ActionName ?? e.HttpRoute ?? e.Message ?? e.EventType,
                Start = e.EventTimestamp,
                DurationMs = e.DurationMs,
                EventType = e.EventType,
                Severity = e.Severity,
                Status = e.Status
            }).ToList();

        // enrich span ids from detail columns
        await using var conn = await db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        var predicate = user.BuildEventPredicate(cmd);
        cmd.CommandText = $"SELECT e.event_id, e.span_id, e.parent_span_id FROM log_events e WHERE e.trace_id = @tid AND {predicate}";
        cmd.Parameters.AddWithValue("tid", traceId);
        var spanIds = new Dictionary<Guid, (string? Span, string? Parent)>();
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
                spanIds[reader.GetGuid(0)] = (reader.Get<string>("span_id"), reader.Get<string>("parent_span_id"));
        }
        foreach (var span in spans)
        {
            if (spanIds.TryGetValue(span.EventId, out var ids))
            {
                if (ids.Span is not null) span.SpanId = ids.Span;
                span.ParentSpanId = ids.Parent;
            }
        }
        return new TraceResponse(traceId, spans, page.Items);
    }

    public async Task<List<LogEventSummary>> GetCorrelationAsync(string correlationId, UserContext user, CancellationToken ct)
    {
        var page = await QueryAsync(new LogQueryFilter
        {
            CorrelationId = correlationId, Limit = 1000, Order = "asc",
            From = DateTimeOffset.UtcNow.AddDays(-30), To = DateTimeOffset.UtcNow
        }, user, ct);
        return page.Items;
    }

    // ---------------------------------------------------------------- stats

    public async Task<StatsOverview> GetOverviewAsync(StatsRequest req, UserContext user, CancellationToken ct)
    {
        var overview = new StatsOverview();
        var filter = new LogQueryFilter { From = req.From, To = req.To, ProjectId = req.ProjectId, EnvironmentId = req.EnvironmentId };
        var windowMinutes = Math.Max(1, ((req.To ?? DateTimeOffset.UtcNow) - (req.From ?? DateTimeOffset.UtcNow.AddHours(-24))).TotalMinutes);

        await using var conn = await db.OpenAsync(ct);

        await using (var cmd = conn.CreateCommand())
        {
            var where = BuildFilterSql(cmd, filter, user);
            cmd.CommandText = $"""
                SELECT count(*) AS total,
                       count(*) FILTER (WHERE severity >= 4) AS errors,
                       count(*) FILTER (WHERE severity = 3) AS warnings,
                       count(*) FILTER (WHERE severity = 0) AS s0,
                       count(*) FILTER (WHERE severity = 1) AS s1,
                       count(*) FILTER (WHERE severity = 2) AS s2,
                       count(*) FILTER (WHERE severity = 3) AS s3,
                       count(*) FILTER (WHERE severity = 4) AS s4,
                       count(*) FILTER (WHERE severity = 5) AS s5,
                       avg(duration_ms) AS avg_dur,
                       percentile_cont(0.95) WITHIN GROUP (ORDER BY duration_ms) AS p95,
                       percentile_cont(0.99) WITHIN GROUP (ORDER BY duration_ms) AS p99,
                       count(*) FILTER (WHERE duration_ms > 3000) AS slow,
                       count(*) FILTER (WHERE event_type = 'ApiRequest') AS requests
                FROM log_events e WHERE {where}
                """;
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                overview.TotalEvents = reader.Get<long>("total");
                overview.ErrorCount = reader.Get<long>("errors");
                overview.WarningCount = reader.Get<long>("warnings");
                overview.ErrorRate = overview.TotalEvents > 0 ? Math.Round(100.0 * overview.ErrorCount / overview.TotalEvents, 2) : 0;
                overview.WarningRate = overview.TotalEvents > 0 ? Math.Round(100.0 * overview.WarningCount / overview.TotalEvents, 2) : 0;
                overview.AvgDurationMs = reader.Get<double?>("avg_dur");
                overview.P95DurationMs = reader.Get<double?>("p95");
                overview.P99DurationMs = reader.Get<double?>("p99");
                overview.SlowRequests = reader.Get<long>("slow");
                overview.RequestsPerMinute = Math.Round(reader.Get<long>("requests") / windowMinutes, 2);
                for (short s = 0; s <= 5; s++)
                    overview.SeverityCounts[Severities.Name(s)] = reader.Get<long>($"s{s}");
            }
        }

        await using (var cmd = conn.CreateCommand())
        {
            var where = BuildFilterSql(cmd, filter, user);
            cmd.CommandText = $"""
                SELECT coalesce(p.name, e.project_id::text) AS name,
                       count(*) AS total,
                       count(*) FILTER (WHERE e.severity >= 4) AS errors
                FROM log_events e LEFT JOIN projects p ON p.id = e.project_id
                WHERE {where}
                GROUP BY 1 ORDER BY errors DESC, total DESC LIMIT 5
                """;
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                overview.TopProjects.Add(new NamedCount(reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2)));
        }

        await using (var cmd = conn.CreateCommand())
        {
            var where = BuildFilterSql(cmd, filter, user);
            cmd.CommandText = $"""
                SELECT e.module,
                       count(*) AS total,
                       count(*) FILTER (WHERE e.severity >= 4) AS errors
                FROM log_events e
                WHERE {where} AND e.module IS NOT NULL
                GROUP BY 1 HAVING count(*) FILTER (WHERE e.severity >= 4) > 0
                ORDER BY errors DESC LIMIT 5
                """;
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                overview.TopModules.Add(new NamedCount(reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2)));
        }

        await using (var cmd = conn.CreateCommand())
        {
            var where = BuildFilterSql(cmd, filter, user);
            cmd.CommandText = $"""
                SELECT e.http_route,
                       count(*) AS total,
                       count(*) FILTER (WHERE e.http_status_code >= 500) AS failures
                FROM log_events e
                WHERE {where} AND e.http_route IS NOT NULL
                GROUP BY 1 HAVING count(*) FILTER (WHERE e.http_status_code >= 500) > 0
                ORDER BY failures DESC LIMIT 5
                """;
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var total = reader.GetInt64(1);
                var failures = reader.GetInt64(2);
                overview.TopFailingRoutes.Add(new RouteFailure(reader.GetString(0), total, failures,
                    total > 0 ? Math.Round(100.0 * failures / total, 2) : 0));
            }
        }

        var criticalPage = await QueryAsync(new LogQueryFilter
        {
            From = req.From, To = req.To, ProjectId = req.ProjectId,
            Severities = new List<string> { "Error", "Critical" }, Limit = 10
        }, user, ct);
        overview.RecentCritical = criticalPage.Items;

        await using (var cmd = conn.CreateCommand())
        {
            var (tenantIds, projectIds) = user.VisibleScopes();
            var clauses = new List<string> { "status NOT IN ('Resolved','Ignored')", "last_seen >= @xfrom" };
            cmd.Parameters.AddWithValue("xfrom", (req.From ?? DateTimeOffset.UtcNow.AddHours(-24)).UtcDateTime);
            if (req.ProjectId is not null) { clauses.Add("project_id = @xp"); cmd.Parameters.AddWithValue("xp", req.ProjectId.Value); }
            // Scope non-super-admins unless they hold a genuinely platform-wide grant (both null).
            // A tenant-scoped grant leaves projectIds null but tenantIds set and must still be filtered.
            if (!user.IsSuperAdmin && !(tenantIds is null && projectIds is null))
            {
                var scopeParts = new List<string>();
                if (tenantIds is not null) { scopeParts.Add("tenant_id = ANY(@xts)"); cmd.Parameters.AddWithValue("xts", tenantIds); }
                if (projectIds is not null) { scopeParts.Add("project_id = ANY(@xps)"); cmd.Parameters.AddWithValue("xps", projectIds); }
                if (scopeParts.Count > 0) clauses.Add("(" + string.Join(" OR ", scopeParts) + ")");
            }
            cmd.CommandText = $"SELECT count(*) FROM exception_groups WHERE {string.Join(" AND ", clauses)}";
            overview.ActiveExceptionGroups = (long)(await cmd.ExecuteScalarAsync(ct) ?? 0L);
        }

        return overview;
    }

    public async Task<List<TimeseriesPoint>> GetTimeseriesAsync(StatsRequest req, UserContext user, CancellationToken ct)
    {
        var from = (req.From ?? DateTimeOffset.UtcNow.AddHours(-24)).UtcDateTime;
        var interval = Math.Clamp(req.IntervalMinutes, 1, 24 * 60);
        var filter = new LogQueryFilter
        {
            From = req.From, To = req.To, ProjectId = req.ProjectId,
            EnvironmentId = req.EnvironmentId, Fingerprint = req.Fingerprint
        };

        await using var conn = await db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        var where = BuildFilterSql(cmd, filter, user);
        cmd.Parameters.AddWithValue("origin", from);
        cmd.Parameters.AddWithValue("iv", TimeSpan.FromMinutes(interval));

        cmd.CommandText = req.Metric switch
        {
            "errors" => $"""
                SELECT date_bin(@iv, e.event_timestamp, @origin) AS bucket, 'errors' AS series, count(*)::float AS value
                FROM log_events e WHERE {where} AND e.severity >= 4 GROUP BY 1 ORDER BY 1
                """,
            "avgDuration" => $"""
                SELECT date_bin(@iv, e.event_timestamp, @origin) AS bucket, 'avgDuration' AS series,
                       coalesce(avg(e.duration_ms), 0) AS value
                FROM log_events e WHERE {where} AND e.duration_ms IS NOT NULL GROUP BY 1 ORDER BY 1
                """,
            _ => $"""
                SELECT date_bin(@iv, e.event_timestamp, @origin) AS bucket,
                       CASE WHEN e.severity >= 4 THEN 'Error' WHEN e.severity = 3 THEN 'Warning' ELSE 'Info' END AS series,
                       count(*)::float AS value
                FROM log_events e WHERE {where} GROUP BY 1, 2 ORDER BY 1
                """
        };

        var points = new List<TimeseriesPoint>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            points.Add(new TimeseriesPoint(
                reader.GetFieldValue<DateTimeOffset>(0), reader.GetString(1), reader.GetDouble(2)));
        return points;
    }

    // ---------------------------------------------------------------- audit

    public async Task<List<JsonObject>> QueryAuditAsync(AuditQueryFilter f, UserContext user, CancellationToken ct)
    {
        var filter = new LogQueryFilter
        {
            From = f.From, To = f.To, ProjectId = f.ProjectId,
            ProjectIds = f.ProjectIds, ApplicationIds = f.ApplicationIds, EnvironmentIds = f.EnvironmentIds,
            EventTypes = new List<string> { EventTypes.Audit },
            UserId = f.ActorUserId, ActionName = f.ActionName,
            BusinessEntityType = f.EntityType, BusinessEntityId = f.EntityId,
            Text = f.Text,
            AfterId = f.AfterId, Limit = Math.Clamp(f.Limit, 1, 500)
        };

        await using var conn = await db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        var where = BuildFilterSql(cmd, filter, user);
        cmd.CommandText = $"""
            SELECT e.id, e.event_id, e.event_timestamp, e.project_id, p.name AS project_name,
                   e.application_id, a.name AS application_name, e.environment_id, env.name AS environment_name,
                   e.user_id, e.user_name, e.action_name, e.business_entity_type, e.business_entity_id,
                   e.message, e.status, e.client_ip, e.correlation_id,
                   e.request_data::text AS old_values, e.response_data::text AS new_values,
                   e.properties::text AS props
            {FromClause}
            WHERE {where}
            ORDER BY e.id DESC LIMIT {filter.Limit}
            """;

        var results = new List<JsonObject>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var props = reader.Get<string>("props") is { } pj ? JsonNode.Parse(pj) : null;
            results.Add(new JsonObject
            {
                ["id"] = reader.Get<long>("id"),
                ["eventId"] = reader.Get<Guid>("event_id"),
                ["eventType"] = EventTypes.Audit,
                ["eventTimestamp"] = reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("event_timestamp")),
                ["projectId"] = reader.Get<Guid>("project_id"),
                ["projectName"] = reader.Get<string>("project_name"),
                ["applicationId"] = reader.Get<Guid>("application_id"),
                ["applicationName"] = reader.Get<string>("application_name"),
                ["environmentId"] = reader.Get<short>("environment_id"),
                ["environmentName"] = reader.Get<string>("environment_name"),
                ["userId"] = reader.Get<string>("user_id"),
                ["userName"] = reader.Get<string>("user_name"),
                ["actionName"] = reader.Get<string>("action_name"),
                ["businessEntityType"] = reader.Get<string>("business_entity_type"),
                ["businessEntityId"] = reader.Get<string>("business_entity_id"),
                ["message"] = reader.Get<string>("message"),
                ["status"] = reader.Get<string>("status"),
                ["sourceIp"] = reader.Get<string>("client_ip"),
                ["correlationId"] = reader.Get<string>("correlation_id"),
                ["oldValues"] = reader.Get<string>("old_values") is { } ov ? JsonNode.Parse(ov) : null,
                ["newValues"] = reader.Get<string>("new_values") is { } nv ? JsonNode.Parse(nv) : null,
                ["changedFields"] = props?["changedFields"]?.DeepClone(),
                ["reason"] = props?["reason"]?.DeepClone()
            });
        }
        return results;
    }

    /// <summary>Recent distinct traces (grouped), for the Trace Viewer landing page.
    /// Trace-level criteria (name, min events, errors, slowest step) are applied in
    /// HAVING so the LIMIT returns matching traces, not newest-then-filtered.</summary>
    public async Task<List<JsonObject>> GetRecentTracesAsync(TraceSearchRequest filter, UserContext user, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        var where = BuildFilterSql(cmd, filter, user);
        if (!string.IsNullOrWhiteSpace(filter.TraceIdContains))
        {
            where += " AND e.trace_id ILIKE @tidlike";
            cmd.Parameters.AddWithValue("tidlike", "%" + filter.TraceIdContains + "%");
        }

        var having = new List<string>();
        if (!string.IsNullOrWhiteSpace(filter.NameContains))
        {
            having.Add("max(coalesce(e.action_name, e.http_route)) ILIKE @namelike");
            cmd.Parameters.AddWithValue("namelike", "%" + filter.NameContains + "%");
        }
        if (filter.MinEvents is > 0)
        {
            having.Add("count(*) >= @minev");
            cmd.Parameters.AddWithValue("minev", (long)filter.MinEvents.Value);
        }
        if (filter.ErrorsOnly)
            having.Add("count(*) FILTER (WHERE e.severity >= 4) > 0");
        if (filter.MinDurationMs is > 0)
        {
            having.Add("max(e.duration_ms) >= @mindur");
            cmd.Parameters.AddWithValue("mindur", filter.MinDurationMs.Value);
        }

        var orderBy = filter.Sort switch
        {
            "slowest" => "max(e.duration_ms) DESC NULLS LAST",
            "errors" => "count(*) FILTER (WHERE e.severity >= 4) DESC, max(e.event_timestamp) DESC",
            _ => "max(e.event_timestamp) DESC",
        };

        cmd.CommandText = $"""
            SELECT e.trace_id,
                   count(*) AS events,
                   min(e.event_timestamp) AS started,
                   max(e.event_timestamp) AS ended,
                   count(*) FILTER (WHERE e.severity >= 4) AS errors,
                   max(p.name) AS project_name,
                   max(coalesce(e.action_name, e.http_route)) AS name,
                   max(e.duration_ms) AS max_duration_ms,
                   max(a.name) AS application_name,
                   max(env.name) AS environment_name
            FROM log_events e
            LEFT JOIN projects p ON p.id = e.project_id
            LEFT JOIN applications a ON a.id = e.application_id
            LEFT JOIN environments env ON env.id = e.environment_id
            WHERE {where} AND e.trace_id IS NOT NULL
            GROUP BY e.trace_id
            {(having.Count > 0 ? "HAVING " + string.Join(" AND ", having) : "")}
            ORDER BY {orderBy}
            LIMIT {Math.Clamp(filter.Limit, 1, 200)}
            """;
        var list = new List<JsonObject>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(new JsonObject
            {
                ["traceId"] = reader.GetString(0),
                ["events"] = reader.GetInt64(1),
                ["started"] = reader.GetFieldValue<DateTimeOffset>(2).UtcDateTime,
                ["ended"] = reader.GetFieldValue<DateTimeOffset>(3).UtcDateTime,
                ["errors"] = reader.GetInt64(4),
                ["projectName"] = reader.IsDBNull(5) ? null : reader.GetString(5),
                ["name"] = reader.IsDBNull(6) ? null : reader.GetString(6),
                ["maxDurationMs"] = reader.IsDBNull(7) ? null : (double?)reader.GetDouble(7),
                ["applicationName"] = reader.IsDBNull(8) ? null : reader.GetString(8),
                ["environmentName"] = reader.IsDBNull(9) ? null : reader.GetString(9)
            });
        }
        return list;
    }

    public async Task<List<PartitionInfo>> GetPartitionStatsAsync(CancellationToken ct)
    {
        await using var cmd = db.Cmd("""
            SELECT c.relname,
                   round(pg_total_relation_size(c.oid) / 1048576.0, 1) AS size_mb,
                   greatest(coalesce(c.reltuples, 0), 0)::bigint AS rows
            FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = 'public' AND c.relname LIKE 'log\_events\_%' AND c.relkind = 'r'
            ORDER BY c.relname
            """);
        var list = new List<PartitionInfo>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            list.Add(new PartitionInfo(reader.GetString(0), reader.GetDouble(1), reader.GetInt64(2)));
        return list;
    }
}
