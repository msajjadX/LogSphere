using LogSphere.Api.Infrastructure;
using LogSphere.Core.Auth;
using LogSphere.Core.Data;
using LogSphere.Core.Models;
using LogSphere.Core.Repositories;
using LogSphere.Core.Services;

namespace LogSphere.Api.Endpoints;

public static class SystemEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/health", () => Results.Json(new { status = "alive", service = "logsphere" }));

        app.MapGet("/health/ready", async (Db db, CancellationToken ct) =>
        {
            var healthy = await db.IsHealthyAsync(ct);
            return Results.Json(new { status = healthy ? "ready" : "degraded", database = healthy },
                statusCode: healthy ? 200 : 503);
        });

        var system = app.MapGroup("/api/v1/system").RequireAuthorization()
            .AddEndpointFilter<MustChangePasswordFilter>();

        system.MapGet("/metrics", async (HttpContext ctx, CurrentUserResolver resolver, Db db,
            QueueRepository queue, EventRepository events, IngestService ingest,
            ApiKeyService apiKeys, SystemRepository systemRepo, CancellationToken ct) =>
        {
            var user = await resolver.GetAsync(ctx, ct);
            if (user is null) return ApiEnvelope.Unauthorized(ctx);
            if (!user.IsAnyAdmin) return ApiEnvelope.Forbidden(ctx);

            var metrics = new SystemMetrics
            {
                QueueDepth = await queue.DepthAsync(ct),
                DeadLetterCount = await queue.DeadLetterCountAsync(ct),
                EventsPerSecond = Math.Round(ingest.EventsPerSecond(), 2),
                AvgIngestLatencyMs = await systemRepo.AvgIngestLatencyMsAsync(ct),
                FailedWrites = events.FailedWrites,
                SanitizationFailures = ingest.SanitizationFailures,
                AuthFailures = apiKeys.AuthFailures,
                DbHealthy = await db.IsHealthyAsync(ct),
                Workers = await systemRepo.GetWorkerStatusesAsync(ct),
                Storage = await events.GetPartitionStatsAsync(ct)
            };
            try
            {
                metrics.Db = await systemRepo.GetDbStatsAsync(ct);
            }
            catch
            {
                // catalog stats are best-effort — a permissions issue must not break the page
            }
            return ApiEnvelope.Ok(ctx, metrics);
        });

        system.MapGet("/dead-letters", async (HttpContext ctx, int? limit,
            CurrentUserResolver resolver, QueueRepository queue, CancellationToken ct) =>
        {
            var user = await resolver.GetAsync(ctx, ct);
            if (user is null) return ApiEnvelope.Unauthorized(ctx);
            // dead-letter payloads are a global store with no tenant scoping — super admins only
            if (!user.IsSuperAdmin) return ApiEnvelope.Forbidden(ctx);
            return ApiEnvelope.Ok(ctx, new { items = await queue.ListDeadLettersAsync(Math.Clamp(limit ?? 50, 1, 500), ct) });
        });
    }
}
