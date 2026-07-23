using System.Text.Json.Nodes;
using LogSphere.Api.Infrastructure;
using LogSphere.Core.Auth;
using LogSphere.Core.Models;
using LogSphere.Core.Services;

namespace LogSphere.Api.Endpoints;

public static class IngestEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/events");
        group.MapPost("", async (HttpContext ctx, ApiKeyService apiKeys, IngestService ingest, CancellationToken ct) =>
        {
            var (context, error) = await AuthenticateAsync(ctx, apiKeys, ct);
            if (context is null) return error!;

            var node = await ReadJsonAsync(ctx, ct);
            if (node is null) return ApiEnvelope.Validation(ctx, "Request body must be a JSON event envelope.");

            var result = await ingest.IngestAsync(new[] { (JsonNode?)node }, context, ApiEnvelope.Correlation(ctx), ct);
            return Respond(ctx, result);
        });

        group.MapPost("/batch", async (HttpContext ctx, ApiKeyService apiKeys, IngestService ingest, CancellationToken ct) =>
        {
            var (context, error) = await AuthenticateAsync(ctx, apiKeys, ct);
            if (context is null) return error!;

            var node = await ReadJsonAsync(ctx, ct);
            if (node is not JsonObject obj || obj["events"] is not JsonArray events)
                return ApiEnvelope.Validation(ctx, "Body must be { \"events\": [ ... ] }.");
            if (events.Count > IngestService.MaxBatchCount)
                return ApiEnvelope.Fail(ctx, StatusCodes.Status413PayloadTooLarge, "LOG-VALIDATION-003",
                    $"Batch exceeds {IngestService.MaxBatchCount} events.");

            var result = await ingest.IngestAsync(events.ToList(), context, ApiEnvelope.Correlation(ctx), ct);
            return Respond(ctx, result);
        });
    }

    private static IResult Respond(HttpContext ctx, IngestResult result) =>
        result.Accepted == 0 && result.Rejected.Count > 0
            ? ApiEnvelope.Fail(ctx, StatusCodes.Status400BadRequest, "LOG-VALIDATION-004",
                $"All events rejected: {result.Rejected[0].Reason}")
            : ApiEnvelope.Accepted(ctx, new { accepted = result.Accepted, rejected = result.Rejected },
                $"{result.Accepted} event(s) accepted for durable processing.");

    internal static async Task<(IngestContext?, IResult?)> AuthenticateAsync(HttpContext ctx, ApiKeyService apiKeys, CancellationToken ct)
    {
        var apiKey = ctx.Request.Headers["X-LogSphere-Key"].FirstOrDefault();
        var clientIp = ctx.Connection.RemoteIpAddress?.ToString();
        var context = await apiKeys.ValidateAsync(apiKey, clientIp, ct);
        if (context is null)
        {
            if (apiKey is not null && apiKeys.IsRateLimited(apiKey))
                return (null, ApiEnvelope.Fail(ctx, StatusCodes.Status429TooManyRequests, "LOG-RATELIMIT-001",
                    "Rate limit exceeded for this credential."));
            return (null, ApiEnvelope.Unauthorized(ctx, "Missing, invalid, expired or revoked API key."));
        }
        return (context, null);
    }

    internal static async Task<JsonNode?> ReadJsonAsync(HttpContext ctx, CancellationToken ct)
    {
        try
        {
            ctx.Request.EnableBuffering();
            return await JsonNode.ParseAsync(ctx.Request.Body, cancellationToken: ct);
        }
        catch
        {
            return null;
        }
    }
}
