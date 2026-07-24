using System.Text.RegularExpressions;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace LogSphere.Tail;

/// <summary>Agent configuration (tail.yaml). ${ENV_VAR} placeholders are substituted from the
/// environment so the API key never lives in the file.</summary>
public sealed class TailConfig
{
    /// <summary>LogSphere base URL, e.g. http://localhost:8080 — /api/v1/events/batch is appended.</summary>
    public string Endpoint { get; set; } = "";
    /// <summary>Ingestion API key (ls_keyId.secret). Use ${LOGSPHERE_KEY} and set it in the environment.</summary>
    public string Key { get; set; } = "";
    /// <summary>Offset state file; created if missing. Defaults next to the config file.</summary>
    public string StateFile { get; set; } = "tail-state.json";
    public int PollSeconds { get; set; } = 2;
    public int BatchSize { get; set; } = 200;
    /// <summary>Bytes read per file per cycle (bounds memory on huge backlogs).</summary>
    public int MaxReadBytesPerCycle { get; set; } = 4 * 1024 * 1024;
    public List<TailSource> Sources { get; set; } = new();

    public static TailConfig Load(string path)
    {
        var raw = File.ReadAllText(path);
        // ${VAR} → environment value (empty when unset, so validation catches it)
        raw = Regex.Replace(raw, @"\$\{(\w+)\}", m => Environment.GetEnvironmentVariable(m.Groups[1].Value) ?? "");
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();
        var config = deserializer.Deserialize<TailConfig>(raw)
                     ?? throw new InvalidOperationException("Config file is empty.");
        config.Validate(Path.GetDirectoryName(Path.GetFullPath(path))!);
        return config;
    }

    private void Validate(string configDir)
    {
        if (string.IsNullOrWhiteSpace(Endpoint)) throw new InvalidOperationException("endpoint is required.");
        if (Sources.Count == 0) throw new InvalidOperationException("at least one source is required.");
        var allSourcesKeyed = Sources.All(s => !string.IsNullOrWhiteSpace(s.Key));
        if (!allSourcesKeyed && (string.IsNullOrWhiteSpace(Key) || !Key.StartsWith("ls_")))
            throw new InvalidOperationException(
                "key is required (ls_… — use ${LOGSPHERE_KEY} and set the env var) unless every source has its own key.");
        Endpoint = Endpoint.TrimEnd('/');
        if (!Path.IsPathRooted(StateFile)) StateFile = Path.Combine(configDir, StateFile);
        foreach (var s in Sources) s.Validate();
    }
}

public sealed class TailSource
{
    /// <summary>File glob, e.g. "C:/apps/legacy/logs/*.log" (directory must exist; * and ? in file
    /// name). A source is EITHER file-based (path) or Event-Log-based (eventLog) — set one.</summary>
    public string Path { get; set; } = "";
    /// <summary>Windows Event Log channel to subscribe to instead of files: "Application",
    /// "System", "Security", or any custom channel. Windows only; ignored with a warning on Linux.</summary>
    public string? EventLog { get; set; }
    /// <summary>Event Log only: minimum level to capture — Verbose | Information | Warning |
    /// Error | Critical. Default Information (skips Verbose noise).</summary>
    public string? MinLevel { get; set; }
    /// <summary>Event Log only: capture only these provider/source names (e.g. "MSSQLSERVER",
    /// ".NET Runtime"). Empty = all providers.</summary>
    public List<string> Providers { get; set; } = new();
    /// <summary>Event Log only: capture only these event IDs (e.g. [4625, 4740] for failed
    /// logons / lockouts). Empty = all IDs.</summary>
    public List<int> EventIds { get; set; } = new();
    /// <summary>Optional per-source API key. Each LogSphere key identifies one application, so give
    /// each legacy app its own key here and one agent ships every app under its own identity.
    /// Falls back to the global key when omitted.</summary>
    public string? Key { get; set; }
    /// <summary>json | regex | plain | w3c</summary>
    public string Parser { get; set; } = "plain";
    /// <summary>regex parser: pattern with named groups — (?&lt;ts&gt;…) (?&lt;sev&gt;…) (?&lt;msg&gt;…); other named groups become properties.</summary>
    public string? Pattern { get; set; }
    /// <summary>Exact timestamp format for the ts group, e.g. "yyyy-MM-dd HH:mm:ss".</summary>
    public string? TimestampFormat { get; set; }
    /// <summary>Lines NOT matching this regex are appended to the previous event (stack traces).</summary>
    public string? MultilineStart { get; set; }
    /// <summary>Raw severity text → LogSphere severity (Trace/Debug/Information/Warning/Error/Critical).</summary>
    public Dictionary<string, string> SeverityMap { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string EventType { get; set; } = "Application";
    public string? Module { get; set; }
    /// <summary>Default severity when a line carries none.</summary>
    public string Severity { get; set; } = "Information";
    /// <summary>Read pre-existing content of newly discovered files (default: start at end).</summary>
    public bool FromBeginning { get; set; }

    [YamlIgnore] public Regex? CompiledPattern { get; private set; }
    [YamlIgnore] public Regex? CompiledMultilineStart { get; private set; }

    internal void Validate()
    {
        var isEventLog = !string.IsNullOrWhiteSpace(EventLog);
        if (isEventLog == !string.IsNullOrWhiteSpace(Path))
            throw new InvalidOperationException("each source needs exactly one of: path (files) or eventLog (Windows Event Log channel).");
        if (Key is not null && !Key.StartsWith("ls_"))
            throw new InvalidOperationException($"source '{Path}{EventLog}': key must be an ls_… API key (use ${{ENV_VAR}}).");
        if (isEventLog)
        {
            if (MinLevel is not null && EventLogSource.ParseMinLevel(MinLevel) is null)
                throw new InvalidOperationException(
                    $"source '{EventLog}': unknown minLevel '{MinLevel}' (Verbose | Information | Warning | Error | Critical).");
            return; // file-parser options below don't apply
        }
        Parser = Parser.ToLowerInvariant();
        if (Parser is not ("json" or "regex" or "plain" or "w3c"))
            throw new InvalidOperationException($"unknown parser '{Parser}' (json | regex | plain | w3c).");
        if (Parser == "regex")
        {
            if (string.IsNullOrWhiteSpace(Pattern))
                throw new InvalidOperationException($"source '{Path}': regex parser requires pattern.");
            CompiledPattern = new Regex(Pattern, RegexOptions.Compiled, TimeSpan.FromSeconds(1));
        }
        if (!string.IsNullOrWhiteSpace(MultilineStart))
            CompiledMultilineStart = new Regex(MultilineStart, RegexOptions.Compiled, TimeSpan.FromSeconds(1));
    }
}
