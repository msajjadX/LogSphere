using System.Text.Json.Nodes;
using LogSphere.Core.Data;
using LogSphere.Core.Models;
using LogSphere.Core.Notifications;
using LogSphere.Core.Repositories;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace LogSphere.Core.Workers;

/// <summary>Evaluates alert rules on a fixed interval; deduplicated + cooldown-suppressed occurrences
/// are dispatched to the rule's notification channels.</summary>
public sealed class AlertWorker(
    Db db,
    AlertRepository alerts,
    QueueRepository queue,
    Notifier notifier,
    SystemRepository system,
    IConfiguration config,
    ILogger<AlertWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!config.GetValue("Workers:Alerts:Enabled", true)) return;
        var interval = TimeSpan.FromSeconds(config.GetValue("Workers:Alerts:IntervalSeconds", 30));
        logger.LogInformation("Alert worker started (interval {Interval})", interval);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await system.HeartbeatAsync("alerts", null, ct);
                var rules = await alerts.ListRulesAsync(ct, enabledOnly: true);
                var channels = await alerts.ListChannelsAsync(ct);
                foreach (var rule in rules)
                {
                    if (!IsActiveNow(rule, DateTimeOffset.UtcNow)) continue; // outside its schedule
                    try { await EvaluateAsync(rule, channels, ct); }
                    catch (Exception ex) when (!ct.IsCancellationRequested)
                    {
                        logger.LogWarning(ex, "Rule {Rule} evaluation failed", rule.Name);
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (Exception ex)
            {
                logger.LogError(ex, "Alert loop error");
            }
            try { await Task.Delay(interval, ct); } catch (OperationCanceledException) { }
        }
    }

    /// <summary>Whether the rule's optional schedule (active days + hours, in its time zone)
    /// allows evaluation right now. No schedule = always active. An unknown time zone falls back
    /// to UTC rather than silencing the rule. from &gt; to wraps past midnight (e.g. 22:00–06:00).</summary>
    public static bool IsActiveNow(AlertRule rule, DateTimeOffset utcNow)
    {
        var tz = TimeZoneInfo.Utc;
        if (!string.IsNullOrWhiteSpace(rule.TimeZone))
        {
            try { tz = TimeZoneInfo.FindSystemTimeZoneById(rule.TimeZone.Trim()); }
            catch { /* unknown id — evaluate in UTC instead of never */ }
        }
        var local = TimeZoneInfo.ConvertTime(utcNow, tz);

        if (rule.ActiveDays is { Length: > 0 } days && !days.Contains((short)local.DayOfWeek))
            return false;

        if (rule.ActiveFromMinute is { } from && rule.ActiveToMinute is { } to && from != to)
        {
            var minute = local.Hour * 60 + local.Minute;
            var inside = from < to
                ? minute >= from && minute < to
                : minute >= from || minute < to; // window wraps past midnight
            if (!inside) return false;
        }
        return true;
    }

    private async Task EvaluateAsync(AlertRule rule, List<NotificationChannel> allChannels, CancellationToken ct)
    {
        var from = DateTimeOffset.UtcNow.AddMinutes(-rule.WindowMinutes).UtcDateTime;

        string Scope(NpgsqlCommand cmd)
        {
            var clauses = new List<string> { "e.tenant_id = @tid" };
            cmd.Parameters.AddWithValue("tid", rule.TenantId);
            if (rule.ProjectId is not null) { clauses.Add("e.project_id = @pid"); cmd.Parameters.AddWithValue("pid", rule.ProjectId.Value); }
            if (rule.EnvironmentId is not null) { clauses.Add("e.environment_id = @eid"); cmd.Parameters.AddWithValue("eid", rule.EnvironmentId.Value); }
            clauses.Add("e.event_timestamp >= @from");
            cmd.Parameters.AddWithValue("from", from);
            return string.Join(" AND ", clauses);
        }

        async Task Trigger(string dedupSuffix, string summary, JsonObject details)
        {
            var occurrenceId = await alerts.TryCreateOccurrenceAsync(
                rule, $"{rule.Id}:{dedupSuffix}", summary, details, rule.ProjectId, ct);
            if (occurrenceId is null) return; // dedup/cooldown
            logger.LogInformation("Alert triggered: {Rule} - {Summary}", rule.Name, summary);
            var targets = allChannels.Where(c => rule.Channels.Contains(c.Id));
            await notifier.DispatchAsync(targets, $"[LogSphere] {rule.Name}", summary, ct);
        }

        await using var conn = await db.OpenAsync(ct);

        switch (rule.ConditionType)
        {
            case "ErrorCountThreshold":
            {
                await using var cmd = conn.CreateCommand();
                var scope = Scope(cmd);
                cmd.CommandText = $"SELECT count(*) FROM log_events e WHERE {scope} AND e.severity >= 4";
                var count = (long)(await cmd.ExecuteScalarAsync(ct) ?? 0L);
                if (count >= (long)rule.Threshold)
                    await Trigger("errors",
                        $"{count} errors in the last {rule.WindowMinutes} min (threshold {rule.Threshold}).",
                        new JsonObject { ["count"] = count, ["windowMinutes"] = rule.WindowMinutes });
                break;
            }
            case "ErrorRatePercent":
            {
                await using var cmd = conn.CreateCommand();
                var scope = Scope(cmd);
                cmd.CommandText = $"""
                    SELECT count(*), count(*) FILTER (WHERE e.severity >= 4) FROM log_events e WHERE {scope}
                    """;
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                if (await reader.ReadAsync(ct))
                {
                    var total = reader.GetInt64(0);
                    var errors = reader.GetInt64(1);
                    var rate = total > 0 ? 100.0 * errors / total : 0;
                    if (total >= 20 && rate >= (double)rule.Threshold)
                        await Trigger("errorrate",
                            $"Error rate {rate:F1}% over the last {rule.WindowMinutes} min (threshold {rule.Threshold}%).",
                            new JsonObject { ["rate"] = rate, ["total"] = total, ["errors"] = errors });
                }
                break;
            }
            case "SameFingerprintCount":
            {
                await using var cmd = conn.CreateCommand();
                var scope = Scope(cmd);
                cmd.CommandText = $"""
                    SELECT e.exception_fingerprint, count(*), max(e.exception_type)
                    FROM log_events e
                    WHERE {scope} AND e.exception_fingerprint IS NOT NULL
                    GROUP BY 1 HAVING count(*) >= @th
                    """;
                cmd.Parameters.AddWithValue("th", (long)rule.Threshold);
                var hits = new List<(string Fp, long Count, string? Type)>();
                await using (var reader = await cmd.ExecuteReaderAsync(ct))
                {
                    while (await reader.ReadAsync(ct))
                        hits.Add((reader.GetString(0), reader.GetInt64(1), reader.IsDBNull(2) ? null : reader.GetString(2)));
                }
                foreach (var (fp, count, type) in hits)
                    await Trigger($"fp:{fp}",
                        $"Exception {type ?? fp} occurred {count}x in the last {rule.WindowMinutes} min.",
                        new JsonObject { ["fingerprint"] = fp, ["count"] = count, ["exceptionType"] = type });
                break;
            }
            case "DurationP95Threshold":
            {
                await using var cmd = conn.CreateCommand();
                var scope = Scope(cmd);
                cmd.CommandText = $"""
                    SELECT percentile_cont(0.95) WITHIN GROUP (ORDER BY e.duration_ms)
                    FROM log_events e WHERE {scope} AND e.duration_ms IS NOT NULL
                    """;
                var result = await cmd.ExecuteScalarAsync(ct);
                if (result is double p95 && p95 >= (double)rule.Threshold)
                    await Trigger("p95",
                        $"P95 duration {p95:F0} ms over the last {rule.WindowMinutes} min (threshold {rule.Threshold} ms).",
                        new JsonObject { ["p95Ms"] = p95 });
                break;
            }
            case "SeverityDetected":
            {
                var minSeverity = rule.SeverityFilter ?? 5;
                await using var cmd = conn.CreateCommand();
                var scope = Scope(cmd);
                cmd.CommandText = $"SELECT count(*), max(e.message) FROM log_events e WHERE {scope} AND e.severity >= @sev";
                cmd.Parameters.AddWithValue("sev", minSeverity);
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                if (await reader.ReadAsync(ct) && reader.GetInt64(0) > 0)
                    await Trigger("severity",
                        $"{reader.GetInt64(0)} event(s) at severity >= {Severities.Name(minSeverity)} in the last {rule.WindowMinutes} min.",
                        new JsonObject { ["count"] = reader.GetInt64(0), ["sample"] = reader.IsDBNull(1) ? null : reader.GetString(1) });
                break;
            }
            case "AuthFailureCount":
            {
                await using var cmd = conn.CreateCommand();
                var scope = Scope(cmd);
                cmd.CommandText = $"SELECT count(*) FROM log_events e WHERE {scope} AND e.event_type = 'Security'";
                var count = (long)(await cmd.ExecuteScalarAsync(ct) ?? 0L);
                if (count >= (long)rule.Threshold)
                    await Trigger("authfail",
                        $"{count} security events in the last {rule.WindowMinutes} min (threshold {rule.Threshold}).",
                        new JsonObject { ["count"] = count });
                break;
            }
            case "IngestSilence":
            {
                await using var cmd = conn.CreateCommand();
                var scope = Scope(cmd);
                // optional dead-man-switch filters: watch one module / action instead of the whole scope
                if (!string.IsNullOrWhiteSpace(rule.ModuleFilter))
                {
                    scope += " AND e.module ILIKE @modf";
                    cmd.Parameters.AddWithValue("modf", "%" + rule.ModuleFilter.Trim() + "%");
                }
                if (!string.IsNullOrWhiteSpace(rule.ActionFilter))
                {
                    scope += " AND e.action_name ILIKE @actf";
                    cmd.Parameters.AddWithValue("actf", "%" + rule.ActionFilter.Trim() + "%");
                }
                cmd.CommandText = $"SELECT count(*) FROM log_events e WHERE {scope}";
                var count = (long)(await cmd.ExecuteScalarAsync(ct) ?? 0L);
                if (count <= rule.MinCount)
                {
                    var what = !string.IsNullOrWhiteSpace(rule.ActionFilter) ? $"'{rule.ActionFilter.Trim()}' events"
                        : !string.IsNullOrWhiteSpace(rule.ModuleFilter) ? $"events from module '{rule.ModuleFilter.Trim()}'"
                        : "events";
                    var summary = rule.MinCount > 0
                        ? $"Only {count} {what} in the last {rule.WindowMinutes} min (expected more than {rule.MinCount})."
                        : $"No {what} received in the last {rule.WindowMinutes} min - the source may have stopped logging.";
                    await Trigger("silence", summary, new JsonObject
                    {
                        ["count"] = count,
                        ["minCount"] = rule.MinCount,
                        ["windowMinutes"] = rule.WindowMinutes,
                        ["moduleFilter"] = rule.ModuleFilter,
                        ["actionFilter"] = rule.ActionFilter
                    });
                }
                break;
            }
            case "QueueDepth":
            {
                var depth = await queue.DepthAsync(ct);
                if (depth >= (long)rule.Threshold)
                    await Trigger("queuedepth",
                        $"Ingest queue depth is {depth} (threshold {rule.Threshold}).",
                        new JsonObject { ["depth"] = depth });
                break;
            }
        }
    }
}
