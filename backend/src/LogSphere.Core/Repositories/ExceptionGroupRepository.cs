using System.Text.Json.Nodes;
using LogSphere.Core.Auth;
using LogSphere.Core.Data;
using LogSphere.Core.Models;
using Npgsql;

namespace LogSphere.Core.Repositories;

public sealed class ExceptionGroupRepository(Db db)
{
    /// <summary>Upserts group aggregates for a batch of persisted exception events.</summary>
    public async Task UpsertFromEventsAsync(IReadOnlyList<JsonObject> exceptionPayloads, CancellationToken ct)
    {
        foreach (var p in exceptionPayloads)
        {
            var fingerprint = p["exceptionFingerprint"]?.GetValue<string>();
            if (fingerprint is null) continue;
            await using var cmd = db.Cmd("""
                INSERT INTO exception_groups
                    (id, fingerprint, tenant_id, project_id, exception_type, message, module,
                     first_seen, last_seen, total_count, sample_event_id, affected_versions, status)
                VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $8, 1, $9,
                        CASE WHEN $10::text IS NULL THEN '{}'::text[] ELSE ARRAY[$10::text] END, 'New')
                ON CONFLICT (project_id, fingerprint) DO UPDATE SET
                    last_seen = GREATEST(exception_groups.last_seen, EXCLUDED.last_seen),
                    total_count = exception_groups.total_count + 1,
                    message = EXCLUDED.message,
                    sample_event_id = EXCLUDED.sample_event_id,
                    affected_versions = (SELECT array_agg(DISTINCT v) FROM unnest(
                        exception_groups.affected_versions || EXCLUDED.affected_versions) AS v),
                    status = CASE WHEN exception_groups.status = 'Resolved' THEN 'Recurring'
                                  ELSE exception_groups.status END
                """);
            cmd.Parameters.AddWithValue(Guid.NewGuid());
            cmd.Parameters.AddWithValue(fingerprint);
            cmd.Parameters.AddWithValue(Guid.Parse(p["tenantId"]!.GetValue<string>()));
            cmd.Parameters.AddWithValue(Guid.Parse(p["projectId"]!.GetValue<string>()));
            cmd.Parameters.AddWithValue((object?)p["exceptionType"]?.GetValue<string>() ?? DBNull.Value);
            cmd.Parameters.AddWithValue((object?)p["exceptionMessage"]?.GetValue<string>() ?? DBNull.Value);
            cmd.Parameters.AddWithValue((object?)p["module"]?.GetValue<string>() ?? DBNull.Value);
            cmd.Parameters.AddWithValue(DateTime.SpecifyKind(
                DateTime.Parse(p["eventTimestamp"]!.GetValue<string>(), null,
                    System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal),
                DateTimeKind.Utc));
            cmd.Parameters.AddWithValue(Guid.Parse(p["eventId"]!.GetValue<string>()));
            cmd.Parameters.AddWithValue((object?)p["applicationVersion"]?.GetValue<string>() ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    internal static string ScopeClause(NpgsqlCommand cmd, UserContext user, string alias)
    {
        if (user.IsSuperAdmin) return "TRUE";
        var (tenantIds, projectIds) = user.VisibleScopes();
        // Only a genuinely platform-wide grant (unrestricted at BOTH tenant and project level)
        // sees everything. A tenant-scoped grant has projectIds == null but tenantIds set, and
        // MUST still be restricted to its tenants — otherwise tenant isolation is broken.
        if (tenantIds is null && projectIds is null) return "TRUE";
        var parts = new List<string>();
        if (tenantIds is not null)
        {
            cmd.Parameters.AddWithValue("sc_t", tenantIds);
            parts.Add($"{alias}.tenant_id = ANY(@sc_t)");
        }
        if (projectIds is not null)
        {
            cmd.Parameters.AddWithValue("sc_p", projectIds);
            parts.Add($"{alias}.project_id = ANY(@sc_p)");
        }
        return parts.Count > 0 ? "(" + string.Join(" OR ", parts) + ")" : "TRUE";
    }

    public async Task<List<ExceptionGroup>> SearchAsync(ExceptionGroupSearch search, UserContext user, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        var clauses = new List<string> { ScopeClause(cmd, user, "g") };

        clauses.Add("g.last_seen >= @from");
        cmd.Parameters.AddWithValue("from", (search.From ?? DateTimeOffset.UtcNow.AddDays(-30)).UtcDateTime);
        if (search.To is not null)
        {
            clauses.Add("g.first_seen <= @to");
            cmd.Parameters.AddWithValue("to", search.To.Value.UtcDateTime);
        }
        if (search.ProjectId is not null)
        {
            clauses.Add("g.project_id = @pid");
            cmd.Parameters.AddWithValue("pid", search.ProjectId.Value);
        }
        if (search.ProjectIds is { Count: > 0 })
        {
            clauses.Add("g.project_id = ANY(@pids)");
            cmd.Parameters.AddWithValue("pids", search.ProjectIds.ToArray());
        }
        if (!string.IsNullOrWhiteSpace(search.Module))
        {
            clauses.Add("g.module ILIKE @mod");
            cmd.Parameters.AddWithValue("mod", "%" + search.Module + "%");
        }
        if (!string.IsNullOrWhiteSpace(search.Status))
        {
            clauses.Add("g.status = @st");
            cmd.Parameters.AddWithValue("st", search.Status);
        }
        if (!string.IsNullOrWhiteSpace(search.Text))
        {
            clauses.Add("(g.exception_type ILIKE @tx OR g.message ILIKE @tx OR g.module ILIKE @tx)");
            cmd.Parameters.AddWithValue("tx", "%" + search.Text + "%");
        }

        cmd.CommandText = $"""
            SELECT g.id, g.fingerprint, g.tenant_id, g.project_id, p.name AS project_name,
                   g.exception_type, g.message, g.module, g.first_seen, g.last_seen, g.total_count,
                   g.status, g.assigned_to, u.display_name AS assigned_name, g.notes, g.linked_issue_url,
                   g.sample_event_id, g.affected_versions,
                   (SELECT count(*) FROM log_events e
                     WHERE e.exception_fingerprint = g.fingerprint AND e.project_id = g.project_id
                       AND e.event_timestamp >= now() - interval '1 hour') AS last_hour,
                   (SELECT count(*) FROM log_events e
                     WHERE e.exception_fingerprint = g.fingerprint AND e.project_id = g.project_id
                       AND e.event_timestamp >= now() - interval '24 hours') AS last_24h,
                   ae.application_name, ae.environment_name
            FROM exception_groups g
            LEFT JOIN projects p ON p.id = g.project_id
            LEFT JOIN users u ON u.id = g.assigned_to
            LEFT JOIN LATERAL (
                SELECT string_agg(DISTINCT a.name, ', ' ORDER BY a.name) AS application_name,
                       string_agg(DISTINCT env.name, ', ' ORDER BY env.name) AS environment_name
                FROM log_events e
                LEFT JOIN applications a ON a.id = e.application_id
                LEFT JOIN environments env ON env.id = e.environment_id
                WHERE e.exception_fingerprint = g.fingerprint AND e.project_id = g.project_id
            ) ae ON true
            WHERE {string.Join(" AND ", clauses)}
            ORDER BY g.last_seen DESC
            LIMIT {Math.Clamp(search.Limit, 1, 500)}
            """;

        var list = new List<ExceptionGroup>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) list.Add(Read(reader));
        return list;
    }

    public async Task<ExceptionGroup?> GetAsync(Guid id, UserContext user, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        var scope = ScopeClause(cmd, user, "g");
        cmd.CommandText = $"""
            SELECT g.id, g.fingerprint, g.tenant_id, g.project_id, p.name AS project_name,
                   g.exception_type, g.message, g.module, g.first_seen, g.last_seen, g.total_count,
                   g.status, g.assigned_to, u.display_name AS assigned_name, g.notes, g.linked_issue_url,
                   g.sample_event_id, g.affected_versions,
                   (SELECT count(*) FROM log_events e
                     WHERE e.exception_fingerprint = g.fingerprint AND e.project_id = g.project_id
                       AND e.event_timestamp >= now() - interval '1 hour') AS last_hour,
                   (SELECT count(*) FROM log_events e
                     WHERE e.exception_fingerprint = g.fingerprint AND e.project_id = g.project_id
                       AND e.event_timestamp >= now() - interval '24 hours') AS last_24h,
                   ae.application_name, ae.environment_name
            FROM exception_groups g
            LEFT JOIN projects p ON p.id = g.project_id
            LEFT JOIN users u ON u.id = g.assigned_to
            LEFT JOIN LATERAL (
                SELECT string_agg(DISTINCT a.name, ', ' ORDER BY a.name) AS application_name,
                       string_agg(DISTINCT env.name, ', ' ORDER BY env.name) AS environment_name
                FROM log_events e
                LEFT JOIN applications a ON a.id = e.application_id
                LEFT JOIN environments env ON env.id = e.environment_id
                WHERE e.exception_fingerprint = g.fingerprint AND e.project_id = g.project_id
            ) ae ON true
            WHERE g.id = @id AND {scope}
            """;
        cmd.Parameters.AddWithValue("id", id);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Read(reader) : null;
    }

    public async Task<bool> PatchAsync(Guid id, PatchExceptionGroupRequest patch, UserContext user, CancellationToken ct)
    {
        var existing = await GetAsync(id, user, ct);
        if (existing is null) return false;

        await using var cmd = db.Cmd("""
            UPDATE exception_groups SET
                status = COALESCE($2, status),
                assigned_to = COALESCE($3, assigned_to),
                notes = COALESCE($4, notes),
                linked_issue_url = COALESCE($5, linked_issue_url)
            WHERE id = $1
            """);
        cmd.Parameters.AddWithValue(id);
        cmd.Parameters.AddWithValue((object?)patch.Status ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)patch.AssignedToUserId ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)patch.Notes ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)patch.LinkedIssueUrl ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);

        // triage trail — one entry per actual change
        if (patch.Status is not null && patch.Status != existing.Status)
            await AddActivityAsync(id, user, "StatusChanged",
                new JsonObject { ["from"] = existing.Status, ["to"] = patch.Status }, ct);
        if (patch.AssignedToUserId is not null && patch.AssignedToUserId != existing.AssignedTo)
            await AddActivityAsync(id, user, "Assigned",
                new JsonObject { ["fromUserId"] = existing.AssignedTo?.ToString(), ["toUserId"] = patch.AssignedToUserId.ToString() }, ct);
        if (patch.Notes is not null && patch.Notes != (existing.Notes ?? ""))
            await AddActivityAsync(id, user, "NotesUpdated", null, ct);
        if (patch.LinkedIssueUrl is not null && patch.LinkedIssueUrl != (existing.LinkedIssueUrl ?? ""))
            await AddActivityAsync(id, user, "IssueLinked",
                new JsonObject { ["url"] = patch.LinkedIssueUrl }, ct);
        return true;
    }

    private async Task AddActivityAsync(Guid groupId, UserContext user, string action, JsonObject? details, CancellationToken ct)
    {
        await using var cmd = db.Cmd("""
            INSERT INTO exception_group_activity (group_id, user_id, username, action, details)
            VALUES ($1, $2, $3, $4, $5)
            """);
        cmd.Parameters.AddWithValue(groupId);
        cmd.Parameters.AddWithValue(user.UserId);
        cmd.Parameters.AddWithValue(user.Username);
        cmd.Parameters.AddWithValue(action);
        cmd.Parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Jsonb,
            Value = details is null ? DBNull.Value : details.ToJsonString()
        });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<List<JsonObject>> ListActivityAsync(Guid groupId, CancellationToken ct)
    {
        await using var cmd = db.Cmd("""
            SELECT a.id, a.user_id, coalesce(u.display_name, a.username) AS actor,
                   a.action, a.details::text, a.occurred_at
            FROM exception_group_activity a
            LEFT JOIN users u ON u.id = a.user_id
            WHERE a.group_id = $1
            ORDER BY a.id DESC
            LIMIT 100
            """);
        cmd.Parameters.AddWithValue(groupId);
        var list = new List<JsonObject>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(new JsonObject
            {
                ["id"] = reader.GetInt64(0),
                ["userId"] = reader.IsDBNull(1) ? null : reader.GetGuid(1).ToString(),
                ["actor"] = reader.IsDBNull(2) ? null : reader.GetString(2),
                ["action"] = reader.GetString(3),
                ["details"] = reader.IsDBNull(4) ? null : JsonNode.Parse(reader.GetString(4)),
                ["occurredAt"] = reader.GetFieldValue<DateTimeOffset>(5).UtcDateTime
            });
        }
        return list;
    }

    private static ExceptionGroup Read(NpgsqlDataReader r) => new()
    {
        Id = r.GetGuid(0), Fingerprint = r.GetString(1), TenantId = r.GetGuid(2), ProjectId = r.GetGuid(3),
        ProjectName = r.IsDBNull(4) ? null : r.GetString(4),
        ExceptionType = r.IsDBNull(5) ? null : r.GetString(5),
        Message = r.IsDBNull(6) ? null : r.GetString(6),
        Module = r.IsDBNull(7) ? null : r.GetString(7),
        FirstSeen = r.GetFieldValue<DateTimeOffset>(8),
        LastSeen = r.GetFieldValue<DateTimeOffset>(9),
        TotalCount = r.GetInt64(10),
        Status = r.GetString(11),
        AssignedTo = r.IsDBNull(12) ? null : r.GetGuid(12),
        AssignedToName = r.IsDBNull(13) ? null : r.GetString(13),
        Notes = r.IsDBNull(14) ? null : r.GetString(14),
        LinkedIssueUrl = r.IsDBNull(15) ? null : r.GetString(15),
        SampleEventId = r.IsDBNull(16) ? null : r.GetGuid(16),
        AffectedVersions = r.IsDBNull(17) ? Array.Empty<string>() : r.GetFieldValue<string[]>(17),
        LastHourCount = r.GetInt64(18),
        Last24hCount = r.GetInt64(19),
        ApplicationName = r.IsDBNull(20) ? null : r.GetString(20),
        EnvironmentName = r.IsDBNull(21) ? null : r.GetString(21)
    };
}
