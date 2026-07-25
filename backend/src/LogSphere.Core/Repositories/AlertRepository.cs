using System.Text.Json.Nodes;
using LogSphere.Core.Auth;
using LogSphere.Core.Data;
using LogSphere.Core.Models;
using Npgsql;
using NpgsqlTypes;

namespace LogSphere.Core.Repositories;

public sealed class AlertRepository(Db db)
{
    // ---------------------------------------------------------------- channels

    public async Task<List<NotificationChannel>> ListChannelsAsync(CancellationToken ct)
    {
        await using var cmd = db.Cmd("SELECT id, tenant_id, name, type, target, enabled FROM notification_channels ORDER BY name");
        var list = new List<NotificationChannel>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            list.Add(new NotificationChannel
            {
                Id = reader.GetGuid(0),
                TenantId = reader.IsDBNull(1) ? null : reader.GetGuid(1),
                Name = reader.GetString(2), Type = reader.GetString(3),
                Target = reader.GetString(4), Enabled = reader.GetBoolean(5)
            });
        return list;
    }

    public async Task<NotificationChannel> UpsertChannelAsync(NotificationChannel c, CancellationToken ct)
    {
        if (c.Id == Guid.Empty) c.Id = Guid.NewGuid();
        await using var cmd = db.Cmd("""
            INSERT INTO notification_channels (id, tenant_id, name, type, target, enabled) VALUES ($1,$2,$3,$4,$5,$6)
            ON CONFLICT (id) DO UPDATE SET tenant_id=$2, name=$3, type=$4, target=$5, enabled=$6
            """);
        cmd.Parameters.AddWithValue(c.Id);
        cmd.Parameters.AddWithValue((object?)c.TenantId ?? DBNull.Value);
        cmd.Parameters.AddWithValue(c.Name);
        cmd.Parameters.AddWithValue(c.Type);
        cmd.Parameters.AddWithValue(c.Target);
        cmd.Parameters.AddWithValue(c.Enabled);
        await cmd.ExecuteNonQueryAsync(ct);
        return c;
    }

    public async Task DeleteChannelAsync(Guid id, CancellationToken ct)
    {
        await using var cmd = db.Cmd("DELETE FROM notification_channels WHERE id = $1");
        cmd.Parameters.AddWithValue(id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ---------------------------------------------------------------- rules

    public async Task<List<AlertRule>> ListRulesAsync(CancellationToken ct, bool enabledOnly = false)
    {
        await using var cmd = db.Cmd($"""
            SELECT id, tenant_id, project_id, environment_id, name, enabled, condition_type, threshold,
                   window_minutes, cooldown_minutes, severity_filter, channels, created_at, last_triggered_at,
                   module_filter, action_filter, min_count, active_from, active_to, active_days, time_zone
            FROM alert_rules {(enabledOnly ? "WHERE enabled" : "")} ORDER BY name
            """);
        var list = new List<AlertRule>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            list.Add(new AlertRule
            {
                Id = reader.GetGuid(0), TenantId = reader.GetGuid(1),
                ProjectId = reader.IsDBNull(2) ? null : reader.GetGuid(2),
                EnvironmentId = reader.IsDBNull(3) ? null : reader.GetInt16(3),
                Name = reader.GetString(4), Enabled = reader.GetBoolean(5),
                ConditionType = reader.GetString(6), Threshold = reader.GetDecimal(7),
                WindowMinutes = reader.GetInt32(8), CooldownMinutes = reader.GetInt32(9),
                SeverityFilter = reader.IsDBNull(10) ? null : reader.GetInt16(10),
                Channels = reader.GetFieldValue<Guid[]>(11),
                CreatedAt = reader.GetFieldValue<DateTimeOffset>(12),
                LastTriggeredAt = reader.IsDBNull(13) ? null : reader.GetFieldValue<DateTimeOffset>(13),
                ModuleFilter = reader.IsDBNull(14) ? null : reader.GetString(14),
                ActionFilter = reader.IsDBNull(15) ? null : reader.GetString(15),
                MinCount = reader.GetInt32(16),
                ActiveFromMinute = reader.IsDBNull(17) ? null : reader.GetInt16(17),
                ActiveToMinute = reader.IsDBNull(18) ? null : reader.GetInt16(18),
                ActiveDays = reader.IsDBNull(19) ? null : reader.GetFieldValue<short[]>(19),
                TimeZone = reader.IsDBNull(20) ? null : reader.GetString(20)
            });
        return list;
    }

    public async Task<AlertRule> UpsertRuleAsync(AlertRule r, CancellationToken ct)
    {
        if (r.Id == Guid.Empty) r.Id = Guid.NewGuid();
        await using var cmd = db.Cmd("""
            INSERT INTO alert_rules (id, tenant_id, project_id, environment_id, name, enabled, condition_type,
                                     threshold, window_minutes, cooldown_minutes, severity_filter, channels,
                                     module_filter, action_filter, min_count,
                                     active_from, active_to, active_days, time_zone)
            VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14,$15,$16,$17,$18,$19)
            ON CONFLICT (id) DO UPDATE SET tenant_id=$2, project_id=$3, environment_id=$4, name=$5, enabled=$6,
                condition_type=$7, threshold=$8, window_minutes=$9, cooldown_minutes=$10,
                severity_filter=$11, channels=$12, module_filter=$13, action_filter=$14, min_count=$15,
                active_from=$16, active_to=$17, active_days=$18, time_zone=$19
            """);
        cmd.Parameters.AddWithValue(r.Id);
        cmd.Parameters.AddWithValue(r.TenantId);
        cmd.Parameters.AddWithValue((object?)r.ProjectId ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)r.EnvironmentId ?? DBNull.Value);
        cmd.Parameters.AddWithValue(r.Name);
        cmd.Parameters.AddWithValue(r.Enabled);
        cmd.Parameters.AddWithValue(r.ConditionType);
        cmd.Parameters.AddWithValue(r.Threshold);
        cmd.Parameters.AddWithValue(r.WindowMinutes);
        cmd.Parameters.AddWithValue(r.CooldownMinutes);
        cmd.Parameters.AddWithValue((object?)r.SeverityFilter ?? DBNull.Value);
        cmd.Parameters.AddWithValue(r.Channels);
        cmd.Parameters.AddWithValue(string.IsNullOrWhiteSpace(r.ModuleFilter) ? DBNull.Value : (object)r.ModuleFilter.Trim());
        cmd.Parameters.AddWithValue(string.IsNullOrWhiteSpace(r.ActionFilter) ? DBNull.Value : (object)r.ActionFilter.Trim());
        cmd.Parameters.AddWithValue(Math.Max(0, r.MinCount));
        cmd.Parameters.AddWithValue(r.ActiveFromMinute is { } af ? (object)(short)Math.Clamp(af, 0, 1439) : DBNull.Value);
        cmd.Parameters.AddWithValue(r.ActiveToMinute is { } at ? (object)(short)Math.Clamp(at, 0, 1439) : DBNull.Value);
        cmd.Parameters.AddWithValue(r.ActiveDays is { Length: > 0 } ? (object)r.ActiveDays : DBNull.Value);
        cmd.Parameters.AddWithValue(string.IsNullOrWhiteSpace(r.TimeZone) ? DBNull.Value : (object)r.TimeZone.Trim());
        await cmd.ExecuteNonQueryAsync(ct);
        return r;
    }

    public async Task DeleteRuleAsync(Guid id, CancellationToken ct)
    {
        await using var cmd = db.Cmd("DELETE FROM alert_rules WHERE id = $1");
        cmd.Parameters.AddWithValue(id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task TouchRuleTriggeredAsync(Guid ruleId, CancellationToken ct)
    {
        await using var cmd = db.Cmd("UPDATE alert_rules SET last_triggered_at = now() WHERE id = $1");
        cmd.Parameters.AddWithValue(ruleId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ---------------------------------------------------------------- occurrences

    /// <summary>Creates an occurrence unless one with the same dedup key is still open or in cooldown.</summary>
    public async Task<Guid?> TryCreateOccurrenceAsync(AlertRule rule, string dedupKey, string summary,
        JsonObject? details, Guid? projectId, CancellationToken ct)
    {
        await using var check = db.Cmd("""
            SELECT count(*) FROM alert_occurrences
            WHERE rule_id = $1 AND dedup_key = $2
              AND (state = 'Open' OR triggered_at > now() - make_interval(mins => $3))
            """);
        check.Parameters.AddWithValue(rule.Id);
        check.Parameters.AddWithValue(dedupKey);
        check.Parameters.AddWithValue(rule.CooldownMinutes);
        var existing = (long)(await check.ExecuteScalarAsync(ct) ?? 0L);
        if (existing > 0) return null;

        var id = Guid.NewGuid();
        await using var cmd = db.Cmd("""
            INSERT INTO alert_occurrences (id, rule_id, project_id, dedup_key, summary, details)
            VALUES ($1,$2,$3,$4,$5,$6)
            """);
        cmd.Parameters.AddWithValue(id);
        cmd.Parameters.AddWithValue(rule.Id);
        cmd.Parameters.AddWithValue((object?)projectId ?? DBNull.Value);
        cmd.Parameters.AddWithValue(dedupKey);
        cmd.Parameters.AddWithValue(summary);
        cmd.Parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlDbType.Jsonb,
            Value = details is null ? DBNull.Value : details.ToJsonString()
        });
        await cmd.ExecuteNonQueryAsync(ct);
        await TouchRuleTriggeredAsync(rule.Id, ct);
        return id;
    }

    public async Task<List<AlertOccurrence>> ListOccurrencesAsync(string? state, Guid? projectId, int limit, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        var clauses = new List<string> { "TRUE" };
        if (state == "open") clauses.Add("o.state IN ('Open','Acknowledged')");
        if (projectId is not null)
        {
            clauses.Add("o.project_id = @pid");
            cmd.Parameters.AddWithValue("pid", projectId.Value);
        }
        cmd.CommandText = $"""
            SELECT o.id, o.rule_id, r.name, o.project_id, o.triggered_at, o.state, o.summary, o.details::text,
                   o.acknowledged_by, o.acknowledged_at, o.resolved_by, o.resolved_at
            FROM alert_occurrences o JOIN alert_rules r ON r.id = o.rule_id
            WHERE {string.Join(" AND ", clauses)}
            ORDER BY o.triggered_at DESC LIMIT {Math.Clamp(limit, 1, 500)}
            """;
        var list = new List<AlertOccurrence>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            list.Add(new AlertOccurrence
            {
                Id = reader.GetGuid(0), RuleId = reader.GetGuid(1), RuleName = reader.GetString(2),
                ProjectId = reader.IsDBNull(3) ? null : reader.GetGuid(3),
                TriggeredAt = reader.GetFieldValue<DateTimeOffset>(4),
                State = reader.GetString(5), Summary = reader.GetString(6),
                Details = reader.IsDBNull(7) ? null : JsonNode.Parse(reader.GetString(7)),
                AcknowledgedBy = reader.IsDBNull(8) ? null : reader.GetGuid(8),
                AcknowledgedAt = reader.IsDBNull(9) ? null : reader.GetFieldValue<DateTimeOffset>(9),
                ResolvedBy = reader.IsDBNull(10) ? null : reader.GetGuid(10),
                ResolvedAt = reader.IsDBNull(11) ? null : reader.GetFieldValue<DateTimeOffset>(11)
            });
        return list;
    }

    /// <summary>Resolves the owning tenant (from the rule) and project of an occurrence, for scope checks.</summary>
    public async Task<(Guid TenantId, Guid? ProjectId)?> GetOccurrenceScopeAsync(Guid id, CancellationToken ct)
    {
        await using var cmd = db.Cmd("""
            SELECT r.tenant_id, o.project_id
            FROM alert_occurrences o JOIN alert_rules r ON r.id = o.rule_id
            WHERE o.id = $1
            """);
        cmd.Parameters.AddWithValue(id);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return (reader.GetGuid(0), reader.IsDBNull(1) ? null : reader.GetGuid(1));
    }

    public async Task<bool> SetOccurrenceStateAsync(Guid id, string state, Guid userId, CancellationToken ct)
    {
        var sql = state switch
        {
            "Acknowledged" => "UPDATE alert_occurrences SET state='Acknowledged', acknowledged_by=$2, acknowledged_at=now() WHERE id=$1 AND state='Open'",
            "Resolved" => "UPDATE alert_occurrences SET state='Resolved', resolved_by=$2, resolved_at=now() WHERE id=$1 AND state IN ('Open','Acknowledged')",
            _ => null
        };
        if (sql is null) return false;
        await using var cmd = db.Cmd(sql);
        cmd.Parameters.AddWithValue(id);
        cmd.Parameters.AddWithValue(userId);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }
}
