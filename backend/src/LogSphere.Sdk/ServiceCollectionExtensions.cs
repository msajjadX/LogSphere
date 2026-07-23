using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace LogSphere.Sdk;

public static class ServiceCollectionExtensions
{
    /// <summary>Registers LogSphere centralized logging:
    /// <code>
    /// services.AddCentralLogging(options =>
    /// {
    ///     options.Endpoint = "https://logs.example.com";
    ///     options.ProjectKey = configuration["LogSphere:Key"];
    ///     options.ApplicationName = "Billing API";
    ///     options.Environment = "Production";
    /// });
    /// </code>
    /// Then call <c>app.UseLogSphere()</c> and inject <see cref="AuditLogger"/> /
    /// <see cref="ActivityLogger"/> where needed.</summary>
    public static IServiceCollection AddCentralLogging(this IServiceCollection services,
        Action<LogSphereOptions> configure)
    {
        services.AddOptions<LogSphereOptions>().Configure(configure).Validate(o =>
            !string.IsNullOrWhiteSpace(o.Endpoint) && !string.IsNullOrWhiteSpace(o.ProjectKey),
            "LogSphere Endpoint and ProjectKey are required.");

        services.AddHttpClient("logsphere");
        services.TryAddSingleton<LogSphereClient>();
        services.TryAddSingleton<AuditLogger>();
        services.TryAddSingleton<ActivityLogger>();
        services.AddSingleton<ILoggerProvider, LogSphereLoggerProvider>();
        return services;
    }
}
