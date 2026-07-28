using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LogSphere.Sdk;

/// <summary>High-level typed logging services on top of the pipeline client.</summary>
public sealed class AuditLogger(LogSphereClient client)
{
    /// <summary>Records an immutable audit event with before/after values.</summary>
    public Task LogAsync(string action, string entityType, string entityId,
        object? oldValues = null, object? newValues = null, string? reason = null,
        string[]? changedFields = null, string? message = null)
    {
        client.Submit(new JsonObject
        {
            ["eventType"] = "Audit",
            ["severity"] = "Information",
            ["status"] = "Completed",
            ["actionName"] = action,
            ["businessEntityType"] = entityType,
            ["businessEntityId"] = entityId,
            ["message"] = message ?? $"{action} on {entityType} {entityId}",
            ["requestData"] = ToNode(oldValues),
            ["responseData"] = ToNode(newValues),
            ["properties"] = new JsonObject
            {
                ["changedFields"] = changedFields is null ? null : new JsonArray(changedFields.Select(f => (JsonNode)f).ToArray()),
                ["reason"] = reason
            }
        });
        return Task.CompletedTask;
    }

    private static JsonNode? ToNode(object? value) =>
        value is null ? null : JsonSerializer.SerializeToNode(value);
}

public sealed class ActivityLogger(LogSphereClient client, IOptions<LogSphereOptions> options)
{
    /// <summary>Submits a custom structured domain event.</summary>
    public void LogEvent(string eventName, object? properties = null, string severity = "Information",
        string? module = null, string? entityType = null, string? entityId = null, string? status = null)
    {
        client.Submit(new JsonObject
        {
            ["eventType"] = "Custom",
            ["severity"] = severity,
            ["status"] = status,
            ["actionName"] = eventName,
            ["message"] = eventName,
            ["module"] = module ?? options.Value.ApplicationName,
            ["businessEntityType"] = entityType,
            ["businessEntityId"] = entityId,
            ["properties"] = properties is null ? null : JsonSerializer.SerializeToNode(properties)
        });
    }

    public void LogException(Exception ex, string? module = null, string severity = "Error") =>
        client.Submit(LogSphereExceptionMiddleware.BuildExceptionEvent(
            ex, module ?? options.Value.ApplicationName, null));

    public void LogPerformance(string operationName, double durationMs, double? dbDurationMs = null,
        double? externalDurationMs = null, object? properties = null)
    {
        client.Submit(new JsonObject
        {
            ["eventType"] = "Performance",
            ["severity"] = "Information",
            ["actionName"] = operationName,
            ["message"] = $"{operationName} took {durationMs:F1} ms",
            ["module"] = options.Value.ApplicationName,
            ["durationMs"] = durationMs,
            ["dbDurationMs"] = dbDurationMs,
            ["externalDurationMs"] = externalDurationMs,
            ["properties"] = properties is null ? null : JsonSerializer.SerializeToNode(properties)
        });
    }
}

/// <summary>Bridges ILogger to LogSphere: warnings and above (configurable) are forwarded as events.</summary>
public sealed class LogSphereLoggerProvider(LogSphereClient client, IOptions<LogSphereOptions> options)
    : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new BridgeLogger(client, options.Value, categoryName);

    public void Dispose() { }

    private sealed class BridgeLogger(LogSphereClient client, LogSphereOptions options, string category) : ILogger
    {
        // Re-entrancy guard: if anything reached from Submit() ever logs (directly or via a
        // future change), that log call must not re-enter the bridge and feed the pipeline
        // it is reporting on. The category filter in IsEnabled covers LogSphere.* only.
        [ThreadStatic] private static bool _inBridge;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) =>
            logLevel != LogLevel.None && (int)logLevel >= (int)options.LoggerBridgeMinimumLevel &&
            !category.StartsWith("LogSphere.", StringComparison.Ordinal);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (_inBridge || !IsEnabled(logLevel)) return;
            var envelope = exception is not null
                ? LogSphereExceptionMiddleware.BuildExceptionEvent(exception, category, formatter(state, exception))
                : new JsonObject
                {
                    ["eventType"] = "Application",
                    ["message"] = formatter(state, exception),
                    ["module"] = category
                };
            envelope["severity"] = logLevel switch
            {
                LogLevel.Trace => "Trace",
                LogLevel.Debug => "Debug",
                LogLevel.Information => "Information",
                LogLevel.Warning => "Warning",
                LogLevel.Error => "Error",
                _ => "Critical"
            };
            _inBridge = true;
            try { client.Submit(envelope); }
            finally { _inBridge = false; }
        }
    }
}
