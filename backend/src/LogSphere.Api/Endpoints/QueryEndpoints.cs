using System.Text;
using LogSphere.Api.Infrastructure;
using LogSphere.Core.Models;
using LogSphere.Core.Repositories;
using LogSphere.Core.Services;

namespace LogSphere.Api.Endpoints;

public static class QueryEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1").RequireAuthorization()
            .AddEndpointFilter<MustChangePasswordFilter>();

        group.MapPost("/query/logs", async (HttpContext ctx, LogQueryFilter filter,
            CurrentUserResolver resolver, EventRepository events, CancellationToken ct) =>
        {
            var user = await resolver.GetAsync(ctx, ct);
            if (user is null) return ApiEnvelope.Unauthorized(ctx);
            var page = await events.QueryAsync(filter, user, ct);
            return ApiEnvelope.Ok(ctx, new { items = page.Items, nextCursor = page.NextCursor }, "Logs retrieved successfully.");
        });

        group.MapPost("/query/logs/tail", async (HttpContext ctx, LogQueryFilter filter,
            CurrentUserResolver resolver, EventRepository events, CancellationToken ct) =>
        {
            var user = await resolver.GetAsync(ctx, ct);
            if (user is null) return ApiEnvelope.Unauthorized(ctx);
            filter.Order = "asc";
            filter.Limit = Math.Clamp(filter.Limit, 1, 500);
            filter.To = DateTimeOffset.UtcNow.AddMinutes(5);
            var page = await events.QueryAsync(filter, user, ct);
            return ApiEnvelope.Ok(ctx, new { items = page.Items, nextCursor = page.Items.Count > 0 ? page.Items[^1].Id : filter.AfterId });
        });

        group.MapGet("/query/logs/{eventId:guid}", async (HttpContext ctx, Guid eventId,
            CurrentUserResolver resolver, EventRepository events, AdminRepository admin, CancellationToken ct) =>
        {
            var user = await resolver.GetAsync(ctx, ct);
            if (user is null) return ApiEnvelope.Unauthorized(ctx);
            var detail = await events.GetDetailAsync(eventId, user, ct);
            if (detail is null) return ApiEnvelope.NotFound(ctx, "Event not found or not accessible.");
            await admin.RecordAccessAsync(user.UserId, "ViewEvent", new { eventId },
                ctx.Connection.RemoteIpAddress?.ToString(), ct);
            return ApiEnvelope.Ok(ctx, detail);
        });

        group.MapPost("/query/traces/search", async (HttpContext ctx, TraceSearchRequest filter,
            CurrentUserResolver resolver, EventRepository events, CancellationToken ct) =>
        {
            var user = await resolver.GetAsync(ctx, ct);
            if (user is null) return ApiEnvelope.Unauthorized(ctx);
            filter.Limit = filter.Limit is > 0 and <= 200 ? filter.Limit : 50;
            var items = await events.GetRecentTracesAsync(filter, user, ct);
            return ApiEnvelope.Ok(ctx, new { items });
        });

        group.MapGet("/query/traces/{traceId}", async (HttpContext ctx, string traceId,
            CurrentUserResolver resolver, EventRepository events, CancellationToken ct) =>
        {
            var user = await resolver.GetAsync(ctx, ct);
            if (user is null) return ApiEnvelope.Unauthorized(ctx);
            var trace = await events.GetTraceAsync(traceId, user, ct);
            return ApiEnvelope.Ok(ctx, trace);
        });

        group.MapGet("/query/correlations/{correlationId}", async (HttpContext ctx, string correlationId,
            CurrentUserResolver resolver, EventRepository events, CancellationToken ct) =>
        {
            var user = await resolver.GetAsync(ctx, ct);
            if (user is null) return ApiEnvelope.Unauthorized(ctx);
            var items = await events.GetCorrelationAsync(correlationId, user, ct);
            return ApiEnvelope.Ok(ctx, new { items });
        });

        group.MapPost("/query/stats/overview", async (HttpContext ctx, StatsRequest req,
            CurrentUserResolver resolver, StatsRepository stats, QueueRepository queue,
            IngestService ingest, CancellationToken ct) =>
        {
            var user = await resolver.GetAsync(ctx, ct);
            if (user is null) return ApiEnvelope.Unauthorized(ctx);
            // reads pre-aggregated rollups (falls back to raw scans for row-filtered users)
            var overview = await stats.GetOverviewAsync(req, user, ct);
            overview.QueueDepth = await queue.DepthAsync(ct);
            overview.IngestionHealthy = overview.QueueDepth < 50_000;
            return ApiEnvelope.Ok(ctx, overview);
        });

        group.MapPost("/query/stats/timeseries", async (HttpContext ctx, StatsRequest req,
            CurrentUserResolver resolver, StatsRepository stats, CancellationToken ct) =>
        {
            var user = await resolver.GetAsync(ctx, ct);
            if (user is null) return ApiEnvelope.Unauthorized(ctx);
            var points = await stats.GetTimeseriesAsync(req, user, ct);
            return ApiEnvelope.Ok(ctx, points);
        });

        group.MapPost("/query/audit", async (HttpContext ctx, AuditQueryFilter filter,
            CurrentUserResolver resolver, EventRepository events, CancellationToken ct) =>
        {
            var user = await resolver.GetAsync(ctx, ct);
            if (user is null) return ApiEnvelope.Unauthorized(ctx);
            var items = await events.QueryAuditAsync(filter, user, ct);
            return ApiEnvelope.Ok(ctx, new { items });
        });

        group.MapPost("/export/logs", async (HttpContext ctx, ExportRequest req,
            CurrentUserResolver resolver, EventRepository events, AdminRepository admin, CancellationToken ct) =>
        {
            var user = await resolver.GetAsync(ctx, ct);
            if (user is null) return ApiEnvelope.Unauthorized(ctx);
            if (!user.CanExport(req.TenantId, req.ProjectId))
                return ApiEnvelope.Forbidden(ctx, "You do not have export permission for this scope.");

            req.Limit = Math.Clamp(req.Limit is > 0 and <= 50_000 ? req.Limit : 10_000, 1, 50_000);
            var page = await events.QueryAsync(req, user, ct);

            await admin.RecordAccessAsync(user.UserId, "Export",
                new { format = req.Format, count = page.Items.Count, filter = new { req.From, req.To, req.ProjectId } },
                ctx.Connection.RemoteIpAddress?.ToString(), ct);

            if (req.Format.Equals("json", StringComparison.OrdinalIgnoreCase))
            {
                var json = System.Text.Json.JsonSerializer.Serialize(page.Items,
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                return Results.File(Encoding.UTF8.GetBytes(json), "application/json",
                    $"logsphere-export-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json");
            }

            var sb = new StringBuilder();
            sb.AppendLine("EventTimestamp,Severity,EventType,Project,Application,Module,Message,ActionName,HttpMethod,HttpRoute,HttpStatusCode,DurationMs,CorrelationId,TraceId,UserId,BusinessEntityType,BusinessEntityId,ExceptionType,EventId");
            foreach (var e in page.Items)
            {
                sb.AppendLine(string.Join(',',
                    Csv(e.EventTimestamp.ToString("O")), Csv(e.Severity), Csv(e.EventType), Csv(e.ProjectName),
                    Csv(e.ApplicationName), Csv(e.Module), Csv(e.Message), Csv(e.ActionName), Csv(e.HttpMethod),
                    Csv(e.HttpRoute), Csv(e.HttpStatusCode?.ToString()), Csv(e.DurationMs?.ToString("F1")),
                    Csv(e.CorrelationId), Csv(e.TraceId), Csv(e.UserId), Csv(e.BusinessEntityType),
                    Csv(e.BusinessEntityId), Csv(e.ExceptionType), Csv(e.EventId.ToString())));
            }
            return Results.File(Encoding.UTF8.GetBytes(sb.ToString()), "text/csv",
                $"logsphere-export-{DateTime.UtcNow:yyyyMMdd-HHmmss}.csv");
        });
    }

    /// <summary>CSV escaping incl. formula-injection guard for spreadsheet consumers.</summary>
    private static string Csv(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        if (value.StartsWith('=') || value.StartsWith('+') || value.StartsWith('-') || value.StartsWith('@'))
            value = "'" + value;
        return value.Contains(',') || value.Contains('"') || value.Contains('\n')
            ? "\"" + value.Replace("\"", "\"\"") + "\""
            : value;
    }
}
