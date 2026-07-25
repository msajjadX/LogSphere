using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace LogSphere.Sdk;

/// <summary>Assigns/propagates X-Correlation-ID and W3C trace context for every request, and
/// captures the caller's IP + user agent so every event of the request — audits included —
/// carries them automatically.</summary>
public sealed class LogSphereCorrelationMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var incoming = context.Request.Headers["X-Correlation-ID"].FirstOrDefault();
        var correlationId = !string.IsNullOrWhiteSpace(incoming) && incoming.Length <= 64
            ? incoming
            : Guid.NewGuid().ToString("N");

        CorrelationContext.CorrelationId = correlationId;
        var activity = Activity.Current;
        CorrelationContext.TraceId = activity?.TraceId.ToString();
        CorrelationContext.SpanId = activity?.SpanId.ToString();
        CorrelationContext.UserId = context.User?.FindFirst("sub")?.Value
                                    ?? context.User?.Identity?.Name;
        CorrelationContext.ClientIp = ResolveClientIp(context);
        CorrelationContext.UserAgent = context.Request.Headers.UserAgent.FirstOrDefault();

        context.Response.OnStarting(() =>
        {
            context.Response.Headers["X-Correlation-ID"] = correlationId;
            return Task.CompletedTask;
        });
        await next(context);
    }

    /// <summary>The real caller IP: CF-Connecting-IP when behind Cloudflare (authoritative — set
    /// by Cloudflare itself and it survives an HAProxy TCP/SSL passthrough untouched), then the
    /// first hop of X-Forwarded-For (nginx/IIS ARR), then X-Real-IP, then the socket address. On a
    /// directly internet-exposed app with no proxy in front, forwarded headers are client-controlled.</summary>
    internal static string? ResolveClientIp(HttpContext context)
    {
        var cf = context.Request.Headers["CF-Connecting-IP"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(cf) && cf.Length <= 64) return cf.Trim();

        var forwarded = context.Request.Headers["X-Forwarded-For"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(forwarded))
        {
            var first = forwarded.Split(',')[0].Trim();
            if (first.Length is > 0 and <= 64) return first;
        }
        var realIp = context.Request.Headers["X-Real-IP"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(realIp) && realIp.Length <= 64) return realIp.Trim();
        return context.Connection.RemoteIpAddress?.ToString();
    }
}

/// <summary>Automatic API request/response logging with client-side body capture limits.</summary>
public sealed class LogSphereRequestLoggingMiddleware(RequestDelegate next, LogSphereClient client,
    IOptions<LogSphereOptions> options)
{
    private readonly LogSphereOptions _options = options.Value;

    public async Task InvokeAsync(HttpContext context)
    {
        if (!_options.AutoRequestLogging)
        {
            await next(context);
            return;
        }

        var path = context.Request.Path.Value ?? "/";
        var skipBody = _options.DoNotLogBodyRoutes.Any(r => path.Contains(r, StringComparison.OrdinalIgnoreCase));
        var stopwatch = Stopwatch.StartNew();

        JsonNode? requestBody = null;
        if (_options.CaptureRequestBody && !skipBody &&
            context.Request.ContentType?.Contains("application/json") == true &&
            context.Request.ContentLength is > 0 and var length && length <= _options.MaxCapturedBodyBytes)
        {
            context.Request.EnableBuffering();
            using var reader = new StreamReader(context.Request.Body, Encoding.UTF8, leaveOpen: true);
            var raw = await reader.ReadToEndAsync();
            context.Request.Body.Position = 0;
            try { requestBody = JsonNode.Parse(raw); } catch { /* not valid JSON */ }
        }

        client.Submit(new JsonObject
        {
            ["eventType"] = "ApiRequest",
            ["severity"] = "Information",
            ["message"] = $"HTTP {context.Request.Method} {path}",
            ["module"] = _options.ApplicationName,
            ["http"] = new JsonObject
            {
                ["method"] = context.Request.Method,
                ["route"] = path + context.Request.QueryString.Value,
                ["clientIp"] = CorrelationContext.ClientIp ?? LogSphereCorrelationMiddleware.ResolveClientIp(context),
                ["userAgent"] = context.Request.Headers.UserAgent.FirstOrDefault(),
                ["requestSize"] = (int?)context.Request.ContentLength
            },
            ["requestData"] = requestBody
        });

        try
        {
            await next(context);
        }
        finally
        {
            stopwatch.Stop();
            var status = context.Response.StatusCode;
            client.Submit(new JsonObject
            {
                ["eventType"] = "ApiResponse",
                ["severity"] = status >= 500 ? "Error" : status >= 400 ? "Warning" : "Information",
                ["status"] = status >= 400 ? "Failed" : "Completed",
                ["message"] = $"HTTP {status} {context.Request.Method} {path} in {stopwatch.Elapsed.TotalMilliseconds:F1} ms",
                ["module"] = _options.ApplicationName,
                ["durationMs"] = stopwatch.Elapsed.TotalMilliseconds,
                ["http"] = new JsonObject
                {
                    ["method"] = context.Request.Method,
                    ["route"] = path,
                    ["statusCode"] = status,
                    ["responseSize"] = (int?)context.Response.ContentLength
                }
            });
        }
    }
}

/// <summary>Captures unhandled exceptions as structured Exception events, then rethrows.</summary>
public sealed class LogSphereExceptionMiddleware(RequestDelegate next, LogSphereClient client,
    IOptions<LogSphereOptions> options)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (Exception ex)
        {
            client.Submit(BuildExceptionEvent(ex, options.Value.ApplicationName,
                context.Request.Method + " " + context.Request.Path));
            throw;
        }
    }

    internal static JsonObject BuildExceptionEvent(Exception ex, string module, string? contextMessage)
    {
        static JsonObject Describe(Exception e, int depth)
        {
            var frame = new StackTrace(e, true).GetFrame(0);
            var obj = new JsonObject
            {
                ["type"] = e.GetType().FullName,
                ["message"] = e.Message,
                ["stackTrace"] = e.StackTrace,
                ["className"] = e.TargetSite?.DeclaringType?.FullName,
                ["methodName"] = e.TargetSite?.Name,
                ["fileName"] = frame?.GetFileName(),
                ["lineNumber"] = frame?.GetFileLineNumber()
            };
            if (e.InnerException is not null && depth < 5)
                obj["innerExceptions"] = new JsonArray(Describe(e.InnerException, depth + 1));
            return obj;
        }

        return new JsonObject
        {
            ["eventType"] = "Exception",
            ["severity"] = "Error",
            ["status"] = "Failed",
            ["module"] = module,
            ["message"] = contextMessage is null ? ex.Message : $"{ex.Message} ({contextMessage})",
            ["exception"] = Describe(ex, 0)
        };
    }
}

public static class LogSphereApplicationBuilderExtensions
{
    /// <summary>Adds correlation, request/response logging and exception capture middleware.
    /// Call early in the pipeline (before UseRouting).</summary>
    public static IApplicationBuilder UseLogSphere(this IApplicationBuilder app) =>
        app.UseMiddleware<LogSphereCorrelationMiddleware>()
           .UseMiddleware<LogSphereExceptionMiddleware>()
           .UseMiddleware<LogSphereRequestLoggingMiddleware>();
}
