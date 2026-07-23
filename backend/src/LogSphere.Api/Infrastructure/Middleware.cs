using System.Text.RegularExpressions;
using LogSphere.Api.Infrastructure;

namespace LogSphere.Api.Infrastructure;

/// <summary>Blocks a user flagged <c>must_change_password</c> from every protected resource except
/// the auth endpoints, so a temporary password cannot be used indefinitely by bypassing the client
/// prompt. Applied to the query/admin/operational/system groups (not the auth group).</summary>
public sealed class MustChangePasswordFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var resolver = http.RequestServices.GetRequiredService<CurrentUserResolver>();
        var user = await resolver.GetAsync(http, http.RequestAborted);
        if (user is not null && user.MustChangePassword)
            return ApiEnvelope.Fail(http, StatusCodes.Status403Forbidden, "LOG-AUTH-003",
                "You must change your password before using this resource.");
        return await next(context);
    }
}

/// <summary>Accepts X-Correlation-ID (validated) or generates one; parses W3C traceparent;
/// always echoes the correlation id on the response.</summary>
public sealed partial class CorrelationMiddleware(RequestDelegate next)
{
    [GeneratedRegex(@"^[A-Za-z0-9\-_.]{1,64}$")]
    private static partial Regex SafeIdRegex();

    [GeneratedRegex(@"^00-([0-9a-f]{32})-([0-9a-f]{16})-[0-9a-f]{2}$")]
    private static partial Regex TraceParentRegex();

    public async Task InvokeAsync(HttpContext context)
    {
        var incoming = context.Request.Headers["X-Correlation-ID"].FirstOrDefault();
        var correlationId = incoming is not null && SafeIdRegex().IsMatch(incoming)
            ? incoming
            : Guid.NewGuid().ToString("N");
        context.Items["CorrelationId"] = correlationId;

        var traceparent = context.Request.Headers["traceparent"].FirstOrDefault();
        if (traceparent is not null && TraceParentRegex().Match(traceparent) is { Success: true } match)
        {
            context.Items["TraceId"] = match.Groups[1].Value;
            context.Items["SpanId"] = match.Groups[2].Value;
        }

        context.Response.OnStarting(() =>
        {
            context.Response.Headers["X-Correlation-ID"] = correlationId;
            return Task.CompletedTask;
        });

        await next(context);
    }
}

/// <summary>Global error handler: envelope response, no stack traces to clients.</summary>
public sealed class ErrorHandlingMiddleware(RequestDelegate next, ILogger<ErrorHandlingMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (Exception ex) when (context.RequestAborted.IsCancellationRequested)
        {
            // client disconnected mid-request (page navigation, refresh) — not a server error
            logger.LogDebug(ex, "Request aborted by client on {Path}", context.Request.Path);
        }
        catch (BadHttpRequestException ex)
        {
            logger.LogWarning(ex, "Bad request on {Path}", context.Request.Path);
            if (!context.Response.HasStarted)
            {
                var result = ApiEnvelope.Fail(context, StatusCodes.Status400BadRequest,
                    "LOG-VALIDATION-002", "The request payload is malformed or too large.");
                await result.ExecuteAsync(context);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unhandled error on {Path} (correlation {CorrelationId})",
                context.Request.Path, ApiEnvelope.Correlation(context));
            if (!context.Response.HasStarted)
            {
                var result = ApiEnvelope.Fail(context, StatusCodes.Status500InternalServerError,
                    "LOG-SERVER-001", "An internal error occurred. Reference the correlation id when reporting.");
                await result.ExecuteAsync(context);
            }
        }
    }
}
