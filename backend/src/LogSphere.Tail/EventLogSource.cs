using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Runtime.Versioning;
using System.Text.Json.Nodes;

namespace LogSphere.Tail;

/// <summary>
/// Windows Event Log source: subscribes to a channel (Application / System / Security / custom)
/// through the Event Log APIs — these logs are binary databases, not tailable text files.
/// Selective by design: minLevel, providers[] and eventIds[] narrow capture to exactly the events
/// that matter (e.g. only 4625 failed logons from Security). Resume position is the channel's
/// EventRecordID, persisted in the same state file as file offsets and — like them — only
/// advanced after LogSphere accepts the batch (at-least-once). Windows-only; on other platforms
/// the source is skipped with a warning.
/// </summary>
internal static class EventLogSource
{
    private const int MaxEventsPerCycle = 500;
    private static readonly HashSet<string> WarnedSkipped = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Critical=1 … Verbose=5 (Event Log levels are inverted: lower = more severe).</summary>
    public static byte? ParseMinLevel(string? name) => name?.Trim().ToUpperInvariant() switch
    {
        null or "" => 4,
        "CRITICAL" => 1,
        "ERROR" => 2,
        "WARNING" => 3,
        "INFORMATION" or "INFO" => 4,
        "VERBOSE" or "TRACE" => 5,
        _ => null,
    };

    public static string MapSeverity(byte? level) => level switch
    {
        1 => "Critical",
        2 => "Error",
        3 => "Warning",
        5 => "Trace",
        _ => "Information", // 4, 0 (LogAlways), unknown
    };

    /// <summary>Builds the Event Log XPath for the source's filters + resume position.
    /// Pure function — unit-tested without Windows.</summary>
    public static string BuildQuery(TailSource source, long afterRecordId)
    {
        var clauses = new List<string> { $"EventRecordID > {afterRecordId}" };

        var minLevel = ParseMinLevel(source.MinLevel) ?? 4;
        if (minLevel < 5) // Verbose = capture everything, no level clause
            clauses.Add($"(Level >= 1 and Level <= {minLevel})");

        if (source.EventIds.Count > 0)
            clauses.Add("(" + string.Join(" or ", source.EventIds.Select(id => $"EventID={id}")) + ")");

        if (source.Providers.Count > 0)
            clauses.Add("Provider[" + string.Join(" or ",
                source.Providers.Select(p => $"@Name='{p.Replace("'", "&apos;")}'")) + "]");

        return $"*[System[{string.Join(" and ", clauses)}]]";
    }

    /// <summary>One poll cycle over an Event Log channel. Returns events shipped.</summary>
    public static async Task<int> TailChannelAsync(
        TailSource source, TailConfig config, StateStore state, BatchSender sender, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
        {
            if (WarnedSkipped.Add(source.EventLog!))
                Console.Error.WriteLine($"[tail] source '{source.EventLog}': Windows Event Log sources only work on Windows - skipping.");
            return 0;
        }
        return await Task.Run(() => TailWindows(source, config, state, sender, ct), ct);
    }

    [SupportedOSPlatform("windows")]
    private static int TailWindows(
        TailSource source, TailConfig config, StateStore state, BatchSender sender, CancellationToken ct)
    {
        var channel = source.EventLog!;
        var stateKey = $"eventlog:{channel}";

        var lastId = state.GetPosition(stateKey);
        if (lastId < 0)
        {
            // first sight: start at the channel's newest record (no history flood) unless asked
            lastId = source.FromBeginning ? 0 : LatestRecordId(channel);
            state.SetPosition(stateKey, lastId);
        }

        var query = new EventLogQuery(channel, PathType.LogName, BuildQuery(source, lastId));
        using var reader = new EventLogReader(query);

        var events = new List<JsonObject>();
        var maxSeen = lastId;
        for (var i = 0; i < MaxEventsPerCycle; i++)
        {
            ct.ThrowIfCancellationRequested();
            using var record = reader.ReadEvent();
            if (record is null) break;
            events.Add(ToEnvelope(record, source, channel));
            if (record.RecordId is { } rid && rid > maxSeen) maxSeen = (long)rid;
        }

        if (events.Count == 0) return 0;
        var accepted = sender.SendAsync(events, source.Key ?? config.Key, ct).GetAwaiter().GetResult();
        if (!accepted) return 0; // position NOT advanced → same records retried next cycle

        state.SetPosition(stateKey, maxSeen);
        return events.Count;
    }

    [SupportedOSPlatform("windows")]
    private static long LatestRecordId(string channel)
    {
        try
        {
            using var reader = new EventLogReader(
                new EventLogQuery(channel, PathType.LogName) { ReverseDirection = true });
            using var newest = reader.ReadEvent();
            return newest?.RecordId is { } rid ? (long)rid : 0;
        }
        catch
        {
            return 0;
        }
    }

    [SupportedOSPlatform("windows")]
    private static JsonObject ToEnvelope(EventRecord record, TailSource source, string channel)
    {
        string? message = null;
        try { message = record.FormatDescription(); }
        catch (EventLogException) { /* publisher metadata missing on this machine */ }
        message ??= $"Event {record.Id} from {record.ProviderName}";

        // Security channel events map to LogSphere's Security event type unless overridden
        var eventType = source.EventType == "Application" &&
                        channel.Equals("Security", StringComparison.OrdinalIgnoreCase)
            ? "Security"
            : source.EventType;

        return new JsonObject
        {
            ["eventType"] = eventType,
            ["severity"] = MapSeverity(record.Level),
            ["eventTimestamp"] = record.TimeCreated?.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture),
            ["message"] = message,
            ["module"] = source.Module ?? record.ProviderName,
            ["actionName"] = $"EventID {record.Id}",
            ["machineName"] = record.MachineName,
            ["properties"] = new JsonObject
            {
                ["channel"] = channel,
                ["provider"] = record.ProviderName,
                ["eventId"] = record.Id,
                ["recordId"] = record.RecordId,
                ["task"] = TryGet(() => record.TaskDisplayName),
                ["user"] = record.UserId?.Value,
            },
        };
    }

    private static string? TryGet(Func<string?> f)
    {
        try { return f(); } catch { return null; }
    }
}
