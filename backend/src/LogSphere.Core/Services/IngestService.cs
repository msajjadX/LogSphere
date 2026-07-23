using System.Text.Json;
using System.Text.Json.Nodes;
using LogSphere.Core.Fingerprinting;
using LogSphere.Core.Models;
using LogSphere.Core.Repositories;
using LogSphere.Core.Sanitization;
using Microsoft.Extensions.Logging;

namespace LogSphere.Core.Services;

/// <summary>Validates and server-side sanitizes incoming envelopes, then enqueues them durably.
/// Tenant/project/application/environment always come from the API key, never the payload.</summary>
public sealed class IngestService(
    QueueRepository queue,
    RulesProvider rulesProvider,
    SanitizationEngine sanitizer,
    EnrichmentService enrichment,
    ILogger<IngestService> logger)
{
    public const int MaxEventBytes = 256 * 1024;
    public const int MaxBatchCount = 500;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString
    };

    private long _sanitizationFailures;
    private long _processedTotal;
    private readonly object _rateLock = new();
    private readonly Queue<(DateTimeOffset At, int Count)> _recent = new();

    public long SanitizationFailures => Interlocked.Read(ref _sanitizationFailures);

    public double EventsPerSecond()
    {
        lock (_rateLock)
        {
            var cutoff = DateTimeOffset.UtcNow.AddSeconds(-60);
            while (_recent.Count > 0 && _recent.Peek().At < cutoff) _recent.Dequeue();
            return _recent.Sum(x => x.Count) / 60.0;
        }
    }

    public async Task<IngestResult> IngestAsync(IReadOnlyList<JsonNode?> rawEvents, IngestContext ctx,
        string? fallbackCorrelationId, CancellationToken ct)
    {
        var rejected = new List<RejectedEvent>();
        var toEnqueue = new List<(Guid, string)>();
        var rules = await rulesProvider.GetForProjectAsync(ctx.TenantId, ctx.ProjectId, ct);
        var limits = new SanitizationLimits();

        for (var i = 0; i < rawEvents.Count; i++)
        {
            try
            {
                var raw = rawEvents[i];
                if (raw is null) { rejected.Add(new RejectedEvent(i, "empty event")); continue; }

                var rawJson = raw.ToJsonString();
                if (rawJson.Length > MaxEventBytes)
                { rejected.Add(new RejectedEvent(i, $"event exceeds {MaxEventBytes} bytes")); continue; }

                var envelope = raw.Deserialize<EventEnvelope>(JsonOpts);
                if (envelope is null) { rejected.Add(new RejectedEvent(i, "unparseable envelope")); continue; }

                if (!EventTypes.IsValid(envelope.EventType))
                { rejected.Add(new RejectedEvent(i, $"unknown eventType '{envelope.EventType}'")); continue; }

                var severity = Severities.Parse(envelope.Severity);
                if (severity is null)
                { rejected.Add(new RejectedEvent(i, $"unknown severity '{envelope.Severity}'")); continue; }

                if (envelope.Status is not null && !OperationStatuses.IsValid(envelope.Status))
                { rejected.Add(new RejectedEvent(i, $"unknown status '{envelope.Status}'")); continue; }

                if (!envelope.SchemaVersion.StartsWith("1."))
                { rejected.Add(new RejectedEvent(i, $"unsupported schemaVersion '{envelope.SchemaVersion}'")); continue; }

                var payload = BuildStoragePayload(envelope, severity.Value, ctx, rules, limits, fallbackCorrelationId);
                toEnqueue.Add((payload.EventId, payload.Json));
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _sanitizationFailures);
                logger.LogWarning(ex, "Failed to process ingest event index {Index}", i);
                rejected.Add(new RejectedEvent(i, "processing failed"));
            }
        }

        if (toEnqueue.Count > 0)
        {
            await queue.EnqueueAsync(toEnqueue, ct);
            Interlocked.Add(ref _processedTotal, toEnqueue.Count);
            lock (_rateLock) _recent.Enqueue((DateTimeOffset.UtcNow, toEnqueue.Count));
        }
        return new IngestResult(toEnqueue.Count, rejected);
    }

    private (Guid EventId, string Json) BuildStoragePayload(EventEnvelope e, short severity, IngestContext ctx,
        RedactionRuleSet rules, SanitizationLimits limits, string? fallbackCorrelationId)
    {
        var eventId = e.EventId ?? Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var eventTs = e.EventTimestamp ?? now;
        // clamp absurd client clocks into a sane window so partition routing stays predictable
        if (eventTs > now.AddHours(1) || eventTs < now.AddDays(-30)) eventTs = now;

        var totalSanitized = 0;
        var truncated = false;

        JsonNode? SanitizeSection(JsonNode? node, string area, int maxBytes)
        {
            if (node is null) return null;
            var (sanitized, result) = sanitizer.Sanitize(node, rules, limits, area);
            totalSanitized += result.FieldsSanitized;
            truncated |= result.Truncated;
            var (sized, wasTruncated) = SanitizationEngine.EnforceSize(sanitized, maxBytes);
            truncated |= wasTruncated;
            return sized;
        }

        var requestData = SanitizeSection(e.RequestData, "Request", limits.MaxBodyBytes);
        var responseData = SanitizeSection(e.ResponseData, "Response", limits.MaxBodyBytes);
        var properties = SanitizeSection(e.Properties, "Properties", limits.MaxPropertiesBytes);

        // offline enrichment (GeoIP + user-agent) — best-effort, runs AFTER sanitization and
        // must never fail ingestion. Lands in properties.geo / properties.ua.
        try
        {
            if (properties is null or JsonObject)
            {
                var geo = enrichment.GeoLookup(e.Http?.ClientIp);
                var ua = enrichment.ParseUserAgent(e.Http?.UserAgent);
                if (geo is not null || ua is not null)
                {
                    var propsObj = properties as JsonObject ?? new JsonObject();
                    if (geo is not null) propsObj["geo"] = geo;
                    if (ua is not null) propsObj["ua"] = ua;
                    properties = propsObj;
                }
            }
        }
        catch { /* enrichment is optional by design */ }

        string? fingerprint = null;
        JsonNode? exceptionData = null;
        if (e.Exception is not null)
        {
            fingerprint = ExceptionFingerprinter.Compute(e.Exception, e.Module);
            e.Exception.StackTrace = SanitizationEngine.TruncateString(e.Exception.StackTrace, limits.MaxStackTraceBytes, out var st);
            truncated |= st;
            exceptionData = SanitizeSection(JsonSerializer.SerializeToNode(e.Exception, JsonOpts), "All", limits.MaxBodyBytes);
        }

        // route may embed a query string — sanitize it
        var route = e.Http?.Route;
        if (route is not null && route.Contains('?'))
        {
            var qIdx = route.IndexOf('?');
            var (cleanQuery, count) = sanitizer.SanitizeQueryString(route[(qIdx + 1)..], rules);
            totalSanitized += count;
            route = route[..qIdx] + (cleanQuery.Length > 0 ? "?" + cleanQuery : "");
        }

        string? Clean(string? value, int max = 512) =>
            value is null ? null : SanitizationEngine.TruncateString(
                new string(value.Where(c => !char.IsControl(c)).ToArray()), max, out _);

        var payload = new JsonObject
        {
            ["eventId"] = eventId,
            ["tenantId"] = ctx.TenantId,
            ["projectId"] = ctx.ProjectId,
            ["applicationId"] = ctx.ApplicationId,
            ["environmentId"] = ctx.EnvironmentId,
            ["eventType"] = e.EventType,
            ["severity"] = severity,
            ["status"] = e.Status,
            ["eventTimestamp"] = eventTs.UtcDateTime,
            ["receivedTimestamp"] = now.UtcDateTime,
            ["correlationId"] = Clean(e.CorrelationId ?? fallbackCorrelationId, 128),
            ["traceId"] = Clean(e.TraceId, 128),
            ["spanId"] = Clean(e.SpanId, 64),
            ["parentSpanId"] = Clean(e.ParentSpanId, 64),
            ["sequence"] = e.Sequence,
            ["module"] = Clean(e.Module, 256),
            ["component"] = Clean(e.Component, 256),
            ["message"] = Clean(e.Message, 4000),
            ["actionName"] = Clean(e.ActionName, 256),
            ["userId"] = Clean(e.UserId, 256),
            ["sessionId"] = Clean(e.SessionId, 256),
            ["userName"] = Clean(e.UserName, 256),
            ["businessEntityType"] = Clean(e.BusinessEntityType, 128),
            ["businessEntityId"] = Clean(e.BusinessEntityId, 256),
            ["durationMs"] = e.DurationMs,
            ["dbDurationMs"] = e.DbDurationMs,
            ["externalDurationMs"] = e.ExternalDurationMs,
            ["httpMethod"] = Clean(e.Http?.Method, 16),
            ["httpRoute"] = Clean(route, 1024),
            ["httpStatusCode"] = e.Http?.StatusCode,
            ["clientIp"] = Clean(e.Http?.ClientIp, 64),
            ["userAgent"] = Clean(e.Http?.UserAgent, 512),
            ["requestSize"] = e.Http?.RequestSize,
            ["responseSize"] = e.Http?.ResponseSize,
            ["machineName"] = Clean(e.MachineName, 256),
            ["applicationVersion"] = Clean(e.ApplicationVersion, 64),
            ["deploymentVersion"] = Clean(e.DeploymentVersion, 64),
            ["exceptionType"] = Clean(e.Exception?.Type, 512),
            ["exceptionMessage"] = Clean(e.Exception?.Message, 2000),
            ["exceptionFingerprint"] = fingerprint,
            ["errorCode"] = Clean(e.Exception?.ErrorCode, 64),
            ["properties"] = properties,
            ["requestData"] = requestData,
            ["responseData"] = responseData,
            ["exceptionData"] = exceptionData,
            ["sanitizationApplied"] = totalSanitized > 0,
            ["sanitizedFieldCount"] = totalSanitized,
            ["truncated"] = truncated,
            ["schemaVersion"] = e.SchemaVersion
        };
        return (eventId, payload.ToJsonString());
    }
}
