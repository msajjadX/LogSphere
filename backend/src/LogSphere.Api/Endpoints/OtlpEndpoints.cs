using System.Text.Json.Nodes;
using LogSphere.Core.Auth;
using LogSphere.Core.Models;
using LogSphere.Core.Otlp;
using LogSphere.Core.Services;

namespace LogSphere.Api.Endpoints;

/// <summary>
/// OTLP/HTTP receiver (OpenTelemetry) supporting both encodings. Any OTel-instrumented
/// application can point its OTLP exporter here — no LogSphere SDK required:
///
///   OTEL_EXPORTER_OTLP_ENDPOINT = https://host/api/v1/otlp
///   OTEL_EXPORTER_OTLP_PROTOCOL = http/protobuf   (exporter default)  or  http/json
///   OTEL_EXPORTER_OTLP_HEADERS  = X-LogSphere-Key=ls_xxx.yyy
///
/// Exporters append the standard /v1/logs and /v1/traces paths. Protobuf payloads are transcoded
/// to the OTLP/JSON shape (OtlpProtobuf) so both encodings share one translator, then flow through
/// the normal IngestService pipeline (validation, sanitization, size caps, durable queue).
/// Responses follow the OTLP spec in the request's encoding: HTTP 200 with an
/// Export*ServiceResponse, using partialSuccess when some records were rejected; failures carry a
/// google.rpc.Status body.
/// </summary>
public static class OtlpEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/otlp");
        group.MapPost("/v1/logs", (HttpContext ctx, ApiKeyService apiKeys, IngestService ingest, CancellationToken ct) =>
            HandleAsync(ctx, apiKeys, ingest,
                OtlpTranslator.TranslateLogs, OtlpProtobuf.DecodeLogsRequest, "rejectedLogRecords", ct));
        group.MapPost("/v1/traces", (HttpContext ctx, ApiKeyService apiKeys, IngestService ingest, CancellationToken ct) =>
            HandleAsync(ctx, apiKeys, ingest,
                OtlpTranslator.TranslateTraces, OtlpProtobuf.DecodeTracesRequest, "rejectedSpans", ct));
    }

    private delegate JsonObject ProtobufDecoder(ReadOnlySpan<byte> payload);

    private static async Task<IResult> HandleAsync(
        HttpContext ctx,
        ApiKeyService apiKeys,
        IngestService ingest,
        Func<JsonNode, List<JsonObject>> translate,
        ProtobufDecoder decodeProtobuf,
        string rejectedField,
        CancellationToken ct)
    {
        var isProtobuf = ctx.Request.ContentType?.Contains("protobuf", StringComparison.OrdinalIgnoreCase) == true;

        var (context, error) = await IngestEndpoints.AuthenticateAsync(ctx, apiKeys, ct);
        if (context is null) return error!;

        JsonNode? node;
        if (isProtobuf)
        {
            if (ctx.Request.ContentLength is > OtlpProtobuf.MaxPayloadBytes)
                return OtlpFailure(isProtobuf: true, StatusCodes.Status413PayloadTooLarge,
                    $"Payload exceeds {OtlpProtobuf.MaxPayloadBytes} bytes.");
            using var ms = new MemoryStream();
            await ctx.Request.Body.CopyToAsync(ms, ct);
            if (ms.Length > OtlpProtobuf.MaxPayloadBytes)
                return OtlpFailure(isProtobuf: true, StatusCodes.Status413PayloadTooLarge,
                    $"Payload exceeds {OtlpProtobuf.MaxPayloadBytes} bytes.");
            try
            {
                node = decodeProtobuf(ms.GetBuffer().AsSpan(0, (int)ms.Length));
            }
            catch (FormatException ex)
            {
                return OtlpFailure(isProtobuf: true, StatusCodes.Status400BadRequest,
                    $"Malformed OTLP protobuf payload: {ex.Message}");
            }
        }
        else
        {
            node = await IngestEndpoints.ReadJsonAsync(ctx, ct);
            if (node is null)
                return OtlpFailure(isProtobuf: false, StatusCodes.Status400BadRequest,
                    "Body must be an OTLP/JSON export request.");
        }

        var envelopes = translate(node);
        if (envelopes.Count == 0) return OtlpSuccess(isProtobuf, 0, null, rejectedField);

        // chunk to the ingest batch cap; aggregate accept/reject across chunks
        var rejected = new List<RejectedEvent>();
        var correlation = Infrastructure.ApiEnvelope.Correlation(ctx);
        for (var i = 0; i < envelopes.Count; i += IngestService.MaxBatchCount)
        {
            var chunk = envelopes.Skip(i).Take(IngestService.MaxBatchCount).Cast<JsonNode?>().ToList();
            var result = await ingest.IngestAsync(chunk, context, correlation, ct);
            rejected.AddRange(result.Rejected.Select(r => new RejectedEvent(i + r.Index, r.Reason)));
        }

        var message = rejected.Count > 0
            ? string.Join("; ", rejected.Take(5).Select(r => $"#{r.Index}: {r.Reason}"))
            : null;
        return OtlpSuccess(isProtobuf, rejected.Count, message, rejectedField);
    }

    private static IResult OtlpSuccess(bool isProtobuf, int rejectedCount, string? message, string rejectedField)
    {
        if (isProtobuf)
            return Results.Bytes(OtlpProtobuf.EncodeExportResponse(rejectedCount, message), "application/x-protobuf");

        var response = new JsonObject();
        if (rejectedCount > 0)
            response["partialSuccess"] = new JsonObject
            {
                [rejectedField] = (long)rejectedCount,
                ["errorMessage"] = message,
            };
        return Results.Json(response);
    }

    private static IResult OtlpFailure(bool isProtobuf, int httpStatus, string message) =>
        isProtobuf
            // google.rpc.Status, encoded to match the request's content type per the OTLP/HTTP spec
            ? new ProtobufResult(OtlpProtobuf.EncodeStatus(3, message), httpStatus)
            : Results.Json(new JsonObject { ["code"] = 3, ["message"] = message }, statusCode: httpStatus);

    /// <summary>Raw protobuf body with an explicit status code (Results.Bytes is 200-only).</summary>
    private sealed class ProtobufResult(byte[] payload, int statusCode) : IResult
    {
        public async Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.StatusCode = statusCode;
            httpContext.Response.ContentType = "application/x-protobuf";
            httpContext.Response.ContentLength = payload.Length;
            await httpContext.Response.Body.WriteAsync(payload);
        }
    }
}
