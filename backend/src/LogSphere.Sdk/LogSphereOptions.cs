namespace LogSphere.Sdk;

public sealed class LogSphereOptions
{
    /// <summary>Base URL of the LogSphere ingestion API, e.g. https://logs.example.com</summary>
    public string Endpoint { get; set; } = "";

    /// <summary>Application API key ("ls_&lt;keyId&gt;.&lt;secret&gt;"). Never commit this to source control.</summary>
    public string ProjectKey { get; set; } = "";

    public string ApplicationName { get; set; } = "";
    public string Environment { get; set; } = "Development";
    public string? ApplicationVersion { get; set; }
    public string? DeploymentVersion { get; set; }

    /// <summary>Max events buffered in memory before oldest are dropped (app is never blocked).</summary>
    public int BufferCapacity { get; set; } = 10_000;

    public int BatchSize { get; set; } = 200;
    public TimeSpan FlushInterval { get; set; } = TimeSpan.FromSeconds(2);
    public TimeSpan HttpTimeout { get; set; } = TimeSpan.FromSeconds(5);
    public int MaxRetries { get; set; } = 3;

    /// <summary>Consecutive failures before the circuit opens (events are buffered/dropped, not sent).</summary>
    public int CircuitBreakerThreshold { get; set; } = 5;
    public TimeSpan CircuitBreakerCooldown { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Optional directory for a disk-backed overflow/offline buffer (JSONL). Null disables it.</summary>
    public string? OfflineBufferDirectory { get; set; }
    public long OfflineBufferMaxBytes { get; set; } = 50 * 1024 * 1024;

    /// <summary>Additional sensitive key fragments sanitized client-side (defaults always apply).</summary>
    public List<string> ExtraSensitiveKeys { get; set; } = new();

    /// <summary>Routes for which request/response bodies are never captured.</summary>
    public List<string> DoNotLogBodyRoutes { get; set; } = new() { "/auth", "/login", "/token", "/payment" };

    /// <summary>Automatically log every HTTP request/response through the middleware.</summary>
    public bool AutoRequestLogging { get; set; } = true;
    public bool CaptureRequestBody { get; set; } = true;
    public bool CaptureResponseBody { get; set; } = false;
    public int MaxCapturedBodyBytes { get; set; } = 16 * 1024;

    /// <summary>Minimum severity forwarded from the ILogger bridge.</summary>
    public LogLevelThreshold LoggerBridgeMinimumLevel { get; set; } = LogLevelThreshold.Warning;

    /// <summary>Optional sink for the SDK's own diagnostics (send failures, circuit state).
    /// Deliberately NOT an ILogger: the transport must never log through the pipeline it feeds,
    /// and taking ILogger in the client would recreate the DI cycle with ILoggerFactory.
    /// Wire it to Console.Error or a file — never back into LogSphere.</summary>
    public Action<string, Exception?>? OnDiagnostic { get; set; }
}

public enum LogLevelThreshold { Trace, Debug, Information, Warning, Error, Critical }
