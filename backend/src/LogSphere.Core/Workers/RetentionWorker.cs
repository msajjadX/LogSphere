using System.Globalization;
using LogSphere.Core.Data;
using LogSphere.Core.Repositories;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace LogSphere.Core.Workers;

/// <summary>Partition lifecycle: pre-creates upcoming partitions, applies row-level retention for
/// scoped policies, and drops whole partitions past the maximum retention window.
/// Legal-hold policies suppress destructive operations for their scope.</summary>
public sealed class RetentionWorker(
    Db db,
    AdminRepository admin,
    SystemRepository system,
    StatsRepository stats,
    IConfiguration config,
    ILogger<RetentionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!config.GetValue("Workers:Retention:Enabled", true)) return;
        var interval = TimeSpan.FromMinutes(config.GetValue("Workers:Retention:IntervalMinutes", 60));
        logger.LogInformation("Retention worker started (interval {Interval})", interval);
        var lastRun = DateTimeOffset.MinValue;

        // heartbeat every minute so health reporting stays fresh; heavy work runs on the interval
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await system.HeartbeatAsync("retention", new { lastRun }, ct);
                if (DateTimeOffset.UtcNow - lastRun >= interval)
                {
                    await EnsurePartitionsAsync(ct);
                    await ApplyRowRetentionAsync(ct);
                    await DropExpiredPartitionsAsync(ct);
                    await stats.PurgeAsync(config.GetValue("Workers:Retention:StatsRetentionDays", 90), ct);
                    lastRun = DateTimeOffset.UtcNow;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (Exception ex)
            {
                logger.LogError(ex, "Retention loop error");
            }
            try { await Task.Delay(TimeSpan.FromMinutes(1), ct); } catch (OperationCanceledException) { }
        }
    }

    private async Task EnsurePartitionsAsync(CancellationToken ct)
    {
        foreach (var offset in new[] { 0, 1 })
        {
            await using var cmd = db.Cmd("SELECT ensure_log_partition(($1)::date)");
            cmd.Parameters.AddWithValue(DateTime.UtcNow.AddMonths(offset).Date);
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    /// <summary>Deletes rows for policies whose retention is shorter than the partition-drop horizon.</summary>
    private async Task ApplyRowRetentionAsync(CancellationToken ct)
    {
        var policies = await admin.ListRetentionPoliciesAsync(ct);
        foreach (var p in policies.Where(p => !p.LegalHold))
        {
            var cutoff = DateTime.UtcNow.AddDays(-p.RetentionDays);
            for (var batch = 0; batch < 10; batch++)
            {
                await using var conn = await db.OpenAsync(ct);
                await using (var setGuc = new NpgsqlCommand("SELECT set_config('logsphere.retention', 'on', false)", conn))
                    await setGuc.ExecuteNonQueryAsync(ct);

                await using var cmd = conn.CreateCommand();
                var clauses = new List<string> { "event_timestamp < @cutoff" };
                cmd.Parameters.AddWithValue("cutoff", cutoff);
                if (p.TenantId is not null) { clauses.Add("tenant_id = @t"); cmd.Parameters.AddWithValue("t", p.TenantId.Value); }
                if (p.ProjectId is not null) { clauses.Add("project_id = @p"); cmd.Parameters.AddWithValue("p", p.ProjectId.Value); }
                if (p.EnvironmentId is not null) { clauses.Add("environment_id = @e"); cmd.Parameters.AddWithValue("e", p.EnvironmentId.Value); }
                if (p.EventType is not null) { clauses.Add("event_type = @et"); cmd.Parameters.AddWithValue("et", p.EventType); }
                if (p.Severity is not null) { clauses.Add("severity = @s"); cmd.Parameters.AddWithValue("s", p.Severity.Value); }

                // Longest-wins: each row's effective retention is the MAX window of every policy
                // that applies to it. So a row that also falls under any longer-retention (or
                // legal-hold) policy must be left alone here — that longer policy governs it and
                // will delete it at its own, later cutoff. Without this, a short policy (e.g.
                // Information = 30d) silently overrides a longer overlapping one (e.g. Performance
                // = 90d) for any row both match.
                var protectors = policies.Where(h => h != p && (h.LegalHold || h.RetentionDays > p.RetentionDays));
                foreach (var h in protectors)
                {
                    var scope = new List<string>();
                    if (h.TenantId is not null) { scope.Add($"tenant_id = @ht{h.Id:N}"); cmd.Parameters.AddWithValue($"ht{h.Id:N}", h.TenantId.Value); }
                    if (h.ProjectId is not null) { scope.Add($"project_id = @hp{h.Id:N}"); cmd.Parameters.AddWithValue($"hp{h.Id:N}", h.ProjectId.Value); }
                    if (h.EnvironmentId is not null) { scope.Add($"environment_id = @he{h.Id:N}"); cmd.Parameters.AddWithValue($"he{h.Id:N}", h.EnvironmentId.Value); }
                    if (h.EventType is not null) { scope.Add($"event_type = @het{h.Id:N}"); cmd.Parameters.AddWithValue($"het{h.Id:N}", h.EventType); }
                    if (h.Severity is not null) { scope.Add($"severity = @hs{h.Id:N}"); cmd.Parameters.AddWithValue($"hs{h.Id:N}", h.Severity.Value); }
                    // scope-less protector applies to every row -> this policy may delete nothing
                    clauses.Add(scope.Count > 0 ? $"NOT ({string.Join(" AND ", scope)})" : "false");
                }

                cmd.CommandText = $"""
                    DELETE FROM log_events WHERE id IN (
                        SELECT id FROM log_events WHERE {string.Join(" AND ", clauses)} LIMIT 5000)
                    """;
                var deleted = await cmd.ExecuteNonQueryAsync(ct);
                if (deleted > 0)
                    logger.LogInformation("Retention: deleted {Count} rows for policy {Policy} (cutoff {Cutoff:u})", deleted, p.Id, cutoff);
                if (deleted < 5000) break;
            }
        }
    }

    private async Task DropExpiredPartitionsAsync(CancellationToken ct)
    {
        var policies = await admin.ListRetentionPoliciesAsync(ct);
        if (policies.Any(p => p.LegalHold))
        {
            logger.LogInformation("Retention: legal hold active - partition drops suppressed");
            return;
        }
        var maxRetentionDays = policies.Count > 0 ? policies.Max(p => p.RetentionDays) : 2555;
        var horizon = DateTime.UtcNow.AddDays(-maxRetentionDays);

        await using var list = db.Cmd("""
            SELECT c.relname FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = 'public' AND c.relname ~ '^log_events_[0-9]{4}_[0-9]{2}$' AND c.relkind = 'r'
            """);
        var partitions = new List<string>();
        await using (var reader = await list.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct)) partitions.Add(reader.GetString(0));
        }

        foreach (var partition in partitions)
        {
            var monthPart = partition["log_events_".Length..];
            if (!DateTime.TryParseExact(monthPart, "yyyy_MM", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var monthStart)) continue;
            var monthEnd = monthStart.AddMonths(1);
            if (monthEnd >= horizon) continue;

            logger.LogWarning("Retention: dropping expired partition {Partition} (covers {Start:yyyy-MM})", partition, monthStart);
            await using var drop = db.Cmd($"DROP TABLE IF EXISTS \"{partition}\"");
            await drop.ExecuteNonQueryAsync(ct);
        }
    }
}
