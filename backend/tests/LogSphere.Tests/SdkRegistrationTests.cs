using LogSphere.Sdk;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LogSphere.Tests;

public class SdkRegistrationTests
{
    // Regression for the DI cycle shipped in SDK <= 1.1.1: LogSphereClient took
    // ILogger<LogSphereClient>, so building ILoggerFactory required the bridge provider,
    // which required the client, which required ILoggerFactory. ValidateOnBuild walks the
    // full call-site graph, so any reintroduced cycle fails this test at build time.
    [Fact]
    public async Task AddCentralLogging_container_builds_and_bridge_logger_resolves()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCentralLogging(o =>
        {
            o.Endpoint = "http://localhost:9";
            o.ProjectKey = "ls_test.regression";
            o.ApplicationName = "LogSphere.Tests";
        });

        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });

        // Constructing ILoggerFactory instantiates every registered ILoggerProvider —
        // the exact path that deadlocked/cycled with 1.1.1.
        var factory = provider.GetRequiredService<ILoggerFactory>();
        var logger = factory.CreateLogger("LogSphere.Tests.SdkRegistrationTests");
        Assert.NotNull(logger);

        // The bridge must also be usable end-to-end (enqueue only; nothing is sent here).
        logger.LogWarning("regression probe");
    }

    [Fact]
    public void Client_diagnostics_go_to_OnDiagnostic_not_ILogger()
    {
        // The transport must never log through the pipeline it feeds. Guard the contract:
        // LogSphereClient exposes no ILogger dependency in any public constructor.
        var ctorParams = typeof(LogSphereClient).GetConstructors()
            .SelectMany(c => c.GetParameters())
            .Select(p => p.ParameterType);
        Assert.DoesNotContain(ctorParams, t =>
            t == typeof(ILogger) ||
            (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(ILogger<>)) ||
            t == typeof(ILoggerFactory));
    }
}
