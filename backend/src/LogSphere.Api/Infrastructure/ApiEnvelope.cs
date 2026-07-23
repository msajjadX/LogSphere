using Microsoft.AspNetCore.Http;

namespace LogSphere.Api.Infrastructure;

/// <summary>Platform response envelope: { success, statusCode, statusDetails, data, correlationId }.</summary>
public static class ApiEnvelope
{
    public static IResult Ok(HttpContext ctx, object? data, string details = "OK", string code = "LOG-200") =>
        Results.Json(new
        {
            success = true,
            statusCode = code,
            statusDetails = details,
            data,
            correlationId = Correlation(ctx)
        });

    public static IResult Accepted(HttpContext ctx, object? data, string details = "Accepted") =>
        Results.Json(new
        {
            success = true,
            statusCode = "LOG-202",
            statusDetails = details,
            data,
            correlationId = Correlation(ctx)
        }, statusCode: StatusCodes.Status202Accepted);

    public static IResult Fail(HttpContext ctx, int httpStatus, string code, string details) =>
        Results.Json(new
        {
            success = false,
            statusCode = code,
            statusDetails = details,
            data = (object?)null,
            correlationId = Correlation(ctx)
        }, statusCode: httpStatus);

    public static IResult Validation(HttpContext ctx, string details) =>
        Fail(ctx, StatusCodes.Status400BadRequest, "LOG-VALIDATION-001", details);

    public static IResult Unauthorized(HttpContext ctx, string details = "Authentication required or invalid.") =>
        Fail(ctx, StatusCodes.Status401Unauthorized, "LOG-AUTH-001", details);

    public static IResult Forbidden(HttpContext ctx, string details = "You do not have permission for this resource.") =>
        Fail(ctx, StatusCodes.Status403Forbidden, "LOG-FORBIDDEN-001", details);

    public static IResult NotFound(HttpContext ctx, string details = "Resource not found.") =>
        Fail(ctx, StatusCodes.Status404NotFound, "LOG-NOTFOUND-001", details);

    public static string Correlation(HttpContext ctx) =>
        ctx.Items.TryGetValue("CorrelationId", out var id) ? id?.ToString() ?? "" : "";
}
