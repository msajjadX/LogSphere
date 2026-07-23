using LogSphere.Core.Auth;
using LogSphere.Core.Data;
using LogSphere.Core.Models;
using Npgsql;

namespace LogSphere.Core.Repositories;

public sealed class AdminRepository(Db db)
{
    // ---------------------------------------------------------------- tenants

    public async Task<List<Tenant>> ListTenantsAsync(UserContext user, CancellationToken ct)
    {
        var (tenantIds, _) = user.VisibleScopes();
        await using var conn = await db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, name, code, is_active, created_at FROM tenants";
        if (tenantIds is not null)
        {
            cmd.CommandText += " WHERE id = ANY(@ids)";
            cmd.Parameters.AddWithValue("ids", tenantIds);
        }
        cmd.CommandText += " ORDER BY name";
        var list = new List<Tenant>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            list.Add(new Tenant
            {
                Id = reader.GetGuid(0), Name = reader.GetString(1), Code = reader.GetString(2),
                IsActive = reader.GetBoolean(3), CreatedAt = reader.GetFieldValue<DateTimeOffset>(4)
            });
        return list;
    }

    public async Task<Tenant> UpsertTenantAsync(Tenant t, CancellationToken ct)
    {
        if (t.Id == Guid.Empty) t.Id = Guid.NewGuid();
        await using var cmd = db.Cmd("""
            INSERT INTO tenants (id, name, code, is_active) VALUES ($1,$2,$3,$4)
            ON CONFLICT (id) DO UPDATE SET name = $2, code = $3, is_active = $4
            """);
        cmd.Parameters.AddWithValue(t.Id);
        cmd.Parameters.AddWithValue(t.Name);
        cmd.Parameters.AddWithValue(t.Code);
        cmd.Parameters.AddWithValue(t.IsActive);
        await cmd.ExecuteNonQueryAsync(ct);
        return t;
    }

    // ---------------------------------------------------------------- projects

    public async Task<List<Project>> ListProjectsAsync(UserContext user, CancellationToken ct)
    {
        var (tenantIds, projectIds) = user.VisibleScopes();
        await using var conn = await db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, tenant_id, name, code, description, is_active, created_at FROM projects";
        if (!user.IsSuperAdmin && (tenantIds is not null || projectIds is not null))
        {
            cmd.CommandText += " WHERE tenant_id = ANY(@tids) OR id = ANY(@pids)";
            cmd.Parameters.AddWithValue("tids", tenantIds ?? Array.Empty<Guid>());
            cmd.Parameters.AddWithValue("pids", projectIds ?? Array.Empty<Guid>());
        }
        cmd.CommandText += " ORDER BY name";
        var list = new List<Project>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            list.Add(new Project
            {
                Id = reader.GetGuid(0), TenantId = reader.GetGuid(1), Name = reader.GetString(2),
                Code = reader.GetString(3), Description = reader.IsDBNull(4) ? null : reader.GetString(4),
                IsActive = reader.GetBoolean(5), CreatedAt = reader.GetFieldValue<DateTimeOffset>(6)
            });
        return list;
    }

    public async Task<Project> UpsertProjectAsync(Project p, CancellationToken ct)
    {
        if (p.Id == Guid.Empty) p.Id = Guid.NewGuid();
        await using var cmd = db.Cmd("""
            INSERT INTO projects (id, tenant_id, name, code, description, is_active) VALUES ($1,$2,$3,$4,$5,$6)
            ON CONFLICT (id) DO UPDATE SET name = $3, code = $4, description = $5, is_active = $6
            """);
        cmd.Parameters.AddWithValue(p.Id);
        cmd.Parameters.AddWithValue(p.TenantId);
        cmd.Parameters.AddWithValue(p.Name);
        cmd.Parameters.AddWithValue(p.Code);
        cmd.Parameters.AddWithValue((object?)p.Description ?? DBNull.Value);
        cmd.Parameters.AddWithValue(p.IsActive);
        await cmd.ExecuteNonQueryAsync(ct);
        return p;
    }

    public async Task<Project?> GetProjectAsync(Guid id, CancellationToken ct)
    {
        await using var cmd = db.Cmd("SELECT id, tenant_id, name, code, description, is_active, created_at FROM projects WHERE id = $1");
        cmd.Parameters.AddWithValue(id);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new Project
        {
            Id = reader.GetGuid(0), TenantId = reader.GetGuid(1), Name = reader.GetString(2),
            Code = reader.GetString(3), Description = reader.IsDBNull(4) ? null : reader.GetString(4),
            IsActive = reader.GetBoolean(5), CreatedAt = reader.GetFieldValue<DateTimeOffset>(6)
        };
    }

    // ---------------------------------------------------------------- applications

    public async Task<List<Application>> ListApplicationsAsync(UserContext user, Guid? projectId, CancellationToken ct)
    {
        var (tenantIds, projectIds) = user.VisibleScopes();
        await using var conn = await db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        var clauses = new List<string>();
        if (projectId is not null) { clauses.Add("a.project_id = @pid"); cmd.Parameters.AddWithValue("pid", projectId.Value); }
        if (!user.IsSuperAdmin && (tenantIds is not null || projectIds is not null))
        {
            clauses.Add("(p.tenant_id = ANY(@tids) OR a.project_id = ANY(@pids))");
            cmd.Parameters.AddWithValue("tids", tenantIds ?? Array.Empty<Guid>());
            cmd.Parameters.AddWithValue("pids", projectIds ?? Array.Empty<Guid>());
        }
        cmd.CommandText = "SELECT a.id, a.project_id, a.name, a.code, a.is_active, a.created_at, p.name AS project_name FROM applications a JOIN projects p ON p.id = a.project_id"
            + (clauses.Count > 0 ? " WHERE " + string.Join(" AND ", clauses) : "") + " ORDER BY a.name";
        var list = new List<Application>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            list.Add(new Application
            {
                Id = reader.GetGuid(0), ProjectId = reader.GetGuid(1), Name = reader.GetString(2),
                Code = reader.GetString(3), IsActive = reader.GetBoolean(4),
                CreatedAt = reader.GetFieldValue<DateTimeOffset>(5),
                ProjectName = reader.GetString(6)
            });
        return list;
    }

    public async Task<Application> UpsertApplicationAsync(Application a, CancellationToken ct)
    {
        if (a.Id == Guid.Empty) a.Id = Guid.NewGuid();
        await using var cmd = db.Cmd("""
            INSERT INTO applications (id, project_id, name, code, is_active) VALUES ($1,$2,$3,$4,$5)
            ON CONFLICT (id) DO UPDATE SET name = $3, code = $4, is_active = $5
            """);
        cmd.Parameters.AddWithValue(a.Id);
        cmd.Parameters.AddWithValue(a.ProjectId);
        cmd.Parameters.AddWithValue(a.Name);
        cmd.Parameters.AddWithValue(a.Code);
        cmd.Parameters.AddWithValue(a.IsActive);
        await cmd.ExecuteNonQueryAsync(ct);
        return a;
    }

    public async Task<Application?> GetApplicationAsync(Guid id, CancellationToken ct)
    {
        await using var cmd = db.Cmd("SELECT id, project_id, name, code, is_active, created_at FROM applications WHERE id = $1");
        cmd.Parameters.AddWithValue(id);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new Application
        {
            Id = reader.GetGuid(0), ProjectId = reader.GetGuid(1), Name = reader.GetString(2),
            Code = reader.GetString(3), IsActive = reader.GetBoolean(4),
            CreatedAt = reader.GetFieldValue<DateTimeOffset>(5)
        };
    }

    public async Task<List<CredentialInfo>> ListCredentialsAsync(Guid applicationId, CancellationToken ct)
    {
        await using var cmd = db.Cmd("""
            SELECT id, application_id, environment_id, key_id, ip_allowlist, rate_limit_per_minute,
                   expires_at, revoked_at, created_at, last_used_at
            FROM application_credentials WHERE application_id = $1 ORDER BY created_at DESC
            """);
        cmd.Parameters.AddWithValue(applicationId);
        var list = new List<CredentialInfo>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            list.Add(new CredentialInfo
            {
                Id = reader.GetGuid(0), ApplicationId = reader.GetGuid(1), EnvironmentId = reader.GetInt16(2),
                KeyId = reader.GetString(3),
                IpAllowlist = reader.IsDBNull(4) ? null : reader.GetFieldValue<string[]>(4),
                RateLimitPerMinute = reader.GetInt32(5),
                ExpiresAt = reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTimeOffset>(6),
                RevokedAt = reader.IsDBNull(7) ? null : reader.GetFieldValue<DateTimeOffset>(7),
                CreatedAt = reader.GetFieldValue<DateTimeOffset>(8),
                LastUsedAt = reader.IsDBNull(9) ? null : reader.GetFieldValue<DateTimeOffset>(9)
            });
        return list;
    }

    public async Task<Guid?> GetCredentialApplicationAsync(Guid credentialId, CancellationToken ct)
    {
        await using var cmd = db.Cmd("SELECT application_id FROM application_credentials WHERE id = $1");
        cmd.Parameters.AddWithValue(credentialId);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is Guid g ? g : null;
    }

    // ---------------------------------------------------------------- users

    public async Task<UserAccount?> GetUserByUsernameAsync(string username, CancellationToken ct) =>
        (await GetUserForAuthAsync(username, ct))?.Account;

    /// <summary>Loads a user together with its stored password hash for the authentication flow.
    /// The hash is returned to the caller — never held on this (singleton) repository — so
    /// concurrent logins can never verify one account's password against another's hash.</summary>
    public async Task<(UserAccount Account, string PasswordHash)?> GetUserForAuthAsync(string username, CancellationToken ct)
    {
        await using var cmd = db.Cmd("""
            SELECT id, username, display_name, email, is_active, must_change_password, created_at, last_login_at, password_hash
            FROM users WHERE lower(username) = lower($1)
            """);
        cmd.Parameters.AddWithValue(username);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        var user = ReadUser(reader);
        var passwordHash = reader.GetString(8);
        await reader.DisposeAsync();
        user.Grants = await GetGrantsAsync(user.Id, ct);
        return (user, passwordHash);
    }

    public async Task<UserAccount?> GetUserAsync(Guid id, CancellationToken ct)
    {
        await using var cmd = db.Cmd("""
            SELECT id, username, display_name, email, is_active, must_change_password, created_at, last_login_at
            FROM users WHERE id = $1
            """);
        cmd.Parameters.AddWithValue(id);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        var user = ReadUser(reader);
        await reader.DisposeAsync();
        user.Grants = await GetGrantsAsync(id, ct);
        return user;
    }

    private static UserAccount ReadUser(NpgsqlDataReader reader) => new()
    {
        Id = reader.GetGuid(0), Username = reader.GetString(1), DisplayName = reader.GetString(2),
        Email = reader.IsDBNull(3) ? null : reader.GetString(3),
        IsActive = reader.GetBoolean(4), MustChangePassword = reader.GetBoolean(5),
        CreatedAt = reader.GetFieldValue<DateTimeOffset>(6),
        LastLoginAt = reader.IsDBNull(7) ? null : reader.GetFieldValue<DateTimeOffset>(7)
    };

    public async Task<List<AccessGrant>> GetGrantsAsync(Guid userId, CancellationToken ct)
    {
        await using var cmd = db.Cmd("""
            SELECT id, role, tenant_id, project_id, environment_id, categories, min_severity,
                   can_view_bodies, can_export, can_view_security
            FROM user_access_grants WHERE user_id = $1
            """);
        cmd.Parameters.AddWithValue(userId);
        var grants = new List<AccessGrant>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            grants.Add(new AccessGrant
            {
                Id = reader.GetGuid(0), Role = reader.GetString(1),
                TenantId = reader.IsDBNull(2) ? null : reader.GetGuid(2),
                ProjectId = reader.IsDBNull(3) ? null : reader.GetGuid(3),
                EnvironmentId = reader.IsDBNull(4) ? null : reader.GetInt16(4),
                Categories = reader.IsDBNull(5) ? null : reader.GetFieldValue<string[]>(5),
                MinSeverity = reader.IsDBNull(6) ? null : reader.GetInt16(6),
                CanViewBodies = reader.GetBoolean(7),
                CanExport = reader.GetBoolean(8),
                CanViewSecurity = reader.GetBoolean(9)
            });
        return grants;
    }

    public async Task<List<UserAccount>> ListUsersAsync(CancellationToken ct)
    {
        await using var cmd = db.Cmd("""
            SELECT id, username, display_name, email, is_active, must_change_password, created_at, last_login_at
            FROM users ORDER BY username
            """);
        var users = new List<UserAccount>();
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct)) users.Add(ReadUser(reader));
        }
        foreach (var u in users) u.Grants = await GetGrantsAsync(u.Id, ct);
        return users;
    }

    public async Task<Guid> UpsertUserAsync(Guid? id, UpsertUserRequest req, CancellationToken ct)
    {
        var userId = id ?? Guid.NewGuid();
        await using var conn = await db.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        if (id is null)
        {
            await using var cmd = new NpgsqlCommand("""
                INSERT INTO users (id, username, display_name, email, password_hash, is_active, must_change_password)
                VALUES ($1,$2,$3,$4,$5,$6,true)
                """, conn, tx);
            cmd.Parameters.AddWithValue(userId);
            cmd.Parameters.AddWithValue(req.Username);
            cmd.Parameters.AddWithValue(req.DisplayName);
            cmd.Parameters.AddWithValue((object?)req.Email ?? DBNull.Value);
            cmd.Parameters.AddWithValue(PasswordHasher.Hash(req.Password ?? Guid.NewGuid().ToString("N")));
            cmd.Parameters.AddWithValue(req.IsActive);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        else
        {
            await using var cmd = new NpgsqlCommand(
                "UPDATE users SET display_name = $2, email = $3, is_active = $4 WHERE id = $1", conn, tx);
            cmd.Parameters.AddWithValue(userId);
            cmd.Parameters.AddWithValue(req.DisplayName);
            cmd.Parameters.AddWithValue((object?)req.Email ?? DBNull.Value);
            cmd.Parameters.AddWithValue(req.IsActive);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await using (var del = new NpgsqlCommand("DELETE FROM user_access_grants WHERE user_id = $1", conn, tx))
        {
            del.Parameters.AddWithValue(userId);
            await del.ExecuteNonQueryAsync(ct);
        }
        foreach (var g in req.Grants)
        {
            await using var ins = new NpgsqlCommand("""
                INSERT INTO user_access_grants
                    (id, user_id, role, tenant_id, project_id, environment_id, categories, min_severity,
                     can_view_bodies, can_export, can_view_security)
                VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11)
                """, conn, tx);
            ins.Parameters.AddWithValue(Guid.NewGuid());
            ins.Parameters.AddWithValue(userId);
            ins.Parameters.AddWithValue(g.Role);
            ins.Parameters.AddWithValue((object?)g.TenantId ?? DBNull.Value);
            ins.Parameters.AddWithValue((object?)g.ProjectId ?? DBNull.Value);
            ins.Parameters.AddWithValue((object?)g.EnvironmentId ?? DBNull.Value);
            ins.Parameters.AddWithValue((object?)g.Categories ?? DBNull.Value);
            ins.Parameters.AddWithValue((object?)g.MinSeverity ?? DBNull.Value);
            ins.Parameters.AddWithValue(g.CanViewBodies);
            ins.Parameters.AddWithValue(g.CanExport);
            ins.Parameters.AddWithValue(g.CanViewSecurity);
            await ins.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        return userId;
    }

    public async Task SetPasswordAsync(Guid userId, string password, bool mustChange, CancellationToken ct)
    {
        await using var cmd = db.Cmd("UPDATE users SET password_hash = $2, must_change_password = $3 WHERE id = $1");
        cmd.Parameters.AddWithValue(userId);
        cmd.Parameters.AddWithValue(PasswordHasher.Hash(password));
        cmd.Parameters.AddWithValue(mustChange);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task TouchLoginAsync(Guid userId, CancellationToken ct)
    {
        await using var cmd = db.Cmd("UPDATE users SET last_login_at = now() WHERE id = $1");
        cmd.Parameters.AddWithValue(userId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ---------------------------------------------------------------- redaction rules

    public async Task<List<RedactionRule>> ListRedactionRulesAsync(CancellationToken ct)
    {
        await using var cmd = db.Cmd("""
            SELECT id, tenant_id, project_id, key_pattern, is_regex, strategy, applies_to, enabled
            FROM redaction_rules ORDER BY key_pattern
            """);
        var list = new List<RedactionRule>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            list.Add(new RedactionRule
            {
                Id = reader.GetGuid(0),
                TenantId = reader.IsDBNull(1) ? null : reader.GetGuid(1),
                ProjectId = reader.IsDBNull(2) ? null : reader.GetGuid(2),
                KeyPattern = reader.GetString(3), IsRegex = reader.GetBoolean(4),
                Strategy = reader.GetString(5), AppliesTo = reader.GetString(6), Enabled = reader.GetBoolean(7)
            });
        return list;
    }

    public async Task<RedactionRule> UpsertRedactionRuleAsync(RedactionRule r, CancellationToken ct)
    {
        if (r.Id == Guid.Empty) r.Id = Guid.NewGuid();
        await using var cmd = db.Cmd("""
            INSERT INTO redaction_rules (id, tenant_id, project_id, key_pattern, is_regex, strategy, applies_to, enabled)
            VALUES ($1,$2,$3,$4,$5,$6,$7,$8)
            ON CONFLICT (id) DO UPDATE SET tenant_id=$2, project_id=$3, key_pattern=$4, is_regex=$5,
                strategy=$6, applies_to=$7, enabled=$8
            """);
        cmd.Parameters.AddWithValue(r.Id);
        cmd.Parameters.AddWithValue((object?)r.TenantId ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)r.ProjectId ?? DBNull.Value);
        cmd.Parameters.AddWithValue(r.KeyPattern);
        cmd.Parameters.AddWithValue(r.IsRegex);
        cmd.Parameters.AddWithValue(r.Strategy);
        cmd.Parameters.AddWithValue(r.AppliesTo);
        cmd.Parameters.AddWithValue(r.Enabled);
        await cmd.ExecuteNonQueryAsync(ct);
        return r;
    }

    public async Task DeleteRedactionRuleAsync(Guid id, CancellationToken ct)
    {
        await using var cmd = db.Cmd("DELETE FROM redaction_rules WHERE id = $1");
        cmd.Parameters.AddWithValue(id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ---------------------------------------------------------------- retention policies

    public async Task<List<RetentionPolicy>> ListRetentionPoliciesAsync(CancellationToken ct)
    {
        await using var cmd = db.Cmd("""
            SELECT id, tenant_id, project_id, environment_id, event_type, severity,
                   retention_days, archive_before_drop, legal_hold
            FROM retention_policies ORDER BY retention_days
            """);
        var list = new List<RetentionPolicy>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            list.Add(new RetentionPolicy
            {
                Id = reader.GetGuid(0),
                TenantId = reader.IsDBNull(1) ? null : reader.GetGuid(1),
                ProjectId = reader.IsDBNull(2) ? null : reader.GetGuid(2),
                EnvironmentId = reader.IsDBNull(3) ? null : reader.GetInt16(3),
                EventType = reader.IsDBNull(4) ? null : reader.GetString(4),
                Severity = reader.IsDBNull(5) ? null : reader.GetInt16(5),
                RetentionDays = reader.GetInt32(6),
                ArchiveBeforeDrop = reader.GetBoolean(7),
                LegalHold = reader.GetBoolean(8)
            });
        return list;
    }

    public async Task<RetentionPolicy> UpsertRetentionPolicyAsync(RetentionPolicy p, CancellationToken ct)
    {
        if (p.Id == Guid.Empty) p.Id = Guid.NewGuid();
        await using var cmd = db.Cmd("""
            INSERT INTO retention_policies (id, tenant_id, project_id, environment_id, event_type, severity,
                                            retention_days, archive_before_drop, legal_hold)
            VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9)
            ON CONFLICT (id) DO UPDATE SET tenant_id=$2, project_id=$3, environment_id=$4, event_type=$5,
                severity=$6, retention_days=$7, archive_before_drop=$8, legal_hold=$9
            """);
        cmd.Parameters.AddWithValue(p.Id);
        cmd.Parameters.AddWithValue((object?)p.TenantId ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)p.ProjectId ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)p.EnvironmentId ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)p.EventType ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)p.Severity ?? DBNull.Value);
        cmd.Parameters.AddWithValue(p.RetentionDays);
        cmd.Parameters.AddWithValue(p.ArchiveBeforeDrop);
        cmd.Parameters.AddWithValue(p.LegalHold);
        await cmd.ExecuteNonQueryAsync(ct);
        return p;
    }

    public async Task DeleteRetentionPolicyAsync(Guid id, CancellationToken ct)
    {
        await using var cmd = db.Cmd("DELETE FROM retention_policies WHERE id = $1");
        cmd.Parameters.AddWithValue(id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ---------------------------------------------------------------- access audit

    public async Task RecordAccessAsync(Guid? userId, string action, object? details, string? ip, CancellationToken ct)
    {
        await using var cmd = db.Cmd("INSERT INTO access_audit (user_id, action, details, ip) VALUES ($1,$2,$3,$4)");
        cmd.Parameters.AddWithValue((object?)userId ?? DBNull.Value);
        cmd.Parameters.AddWithValue(action);
        cmd.Parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Jsonb,
            Value = details is null ? DBNull.Value : System.Text.Json.JsonSerializer.Serialize(details)
        });
        cmd.Parameters.AddWithValue((object?)ip ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<List<AccessAuditEntry>> ListAccessAuditAsync(int limit, CancellationToken ct)
    {
        await using var cmd = db.Cmd("""
            SELECT a.id, a.user_id, u.username, a.action, a.details::text, a.ip, a.occurred_at
            FROM access_audit a LEFT JOIN users u ON u.id = a.user_id
            ORDER BY a.id DESC LIMIT $1
            """);
        cmd.Parameters.AddWithValue(limit);
        var list = new List<AccessAuditEntry>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            list.Add(new AccessAuditEntry
            {
                Id = reader.GetInt64(0),
                UserId = reader.IsDBNull(1) ? null : reader.GetGuid(1),
                Username = reader.IsDBNull(2) ? null : reader.GetString(2),
                Action = reader.GetString(3),
                Details = reader.IsDBNull(4) ? null : System.Text.Json.Nodes.JsonNode.Parse(reader.GetString(4)),
                Ip = reader.IsDBNull(5) ? null : reader.GetString(5),
                OccurredAt = reader.GetFieldValue<DateTimeOffset>(6)
            });
        return list;
    }

    // ---------------------------------------------------------------- environments

    public async Task<List<(short Id, string Name)>> ListEnvironmentsAsync(CancellationToken ct)
    {
        await using var cmd = db.Cmd("SELECT id, name FROM environments ORDER BY id");
        var list = new List<(short, string)>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) list.Add((reader.GetInt16(0), reader.GetString(1)));
        return list;
    }
}
