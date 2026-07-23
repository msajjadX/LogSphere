using LogSphere.Core.Models;
using Npgsql;

namespace LogSphere.Core.Auth;

/// <summary>The authenticated dashboard user's effective permissions, materialized from grants.
/// Every event query MUST pass through BuildEventPredicate — authorization lives in the data layer.</summary>
public sealed class UserContext
{
    public required Guid UserId { get; init; }
    public required string Username { get; init; }
    public required List<AccessGrant> Grants { get; init; }

    /// <summary>True when the account is flagged to rotate its (temporary) password before it may
    /// use anything other than the auth endpoints. Enforced server-side by MustChangePasswordFilter.</summary>
    public bool MustChangePassword { get; init; }

    public bool IsSuperAdmin => Grants.Any(g => g.Role == Roles.SuperAdmin);

    public bool IsTenantAdmin(Guid tenantId) =>
        IsSuperAdmin || Grants.Any(g => g.Role == Roles.TenantAdmin && (g.TenantId is null || g.TenantId == tenantId));

    public bool IsAnyAdmin => IsSuperAdmin || Grants.Any(g => g.Role is Roles.TenantAdmin or Roles.ProjectAdmin);

    /// <summary>True when any grant filters at row level beyond tenant/project/environment scope
    /// (event-type categories, minimum severity, or hidden Security events). The pre-aggregated
    /// stats rollups cannot honor those filters, so such users take the raw-scan stats path.</summary>
    public bool HasRowLevelFilters =>
        !IsSuperAdmin &&
        Grants.Any(g => g.Categories is { Length: > 0 } || g.MinSeverity is not null || !g.CanViewSecurity);

    public bool CanManageProject(Guid tenantId, Guid? projectId) =>
        IsSuperAdmin ||
        Grants.Any(g => (g.Role is Roles.TenantAdmin && (g.TenantId is null || g.TenantId == tenantId)) ||
                        (g.Role is Roles.ProjectAdmin && (g.ProjectId is null || g.ProjectId == projectId) &&
                         (g.TenantId is null || g.TenantId == tenantId)));

    private bool GrantCovers(AccessGrant g, Guid? tenantId, Guid? projectId) =>
        (g.TenantId is null || tenantId is null || g.TenantId == tenantId) &&
        (g.ProjectId is null || projectId is null || g.ProjectId == projectId);

    public bool CanViewBodies(Guid? tenantId, Guid? projectId) =>
        IsSuperAdmin || Grants.Any(g => g.CanViewBodies && GrantCovers(g, tenantId, projectId));

    public bool CanExport(Guid? tenantId, Guid? projectId) =>
        IsSuperAdmin || Grants.Any(g => g.CanExport && GrantCovers(g, tenantId, projectId));

    public bool CanViewSecurity(Guid? tenantId, Guid? projectId) =>
        IsSuperAdmin || Grants.Any(g => g.CanViewSecurity && GrantCovers(g, tenantId, projectId));

    /// <summary>Builds a SQL predicate over the log_events alias restricting rows to what this user may see.
    /// Adds parameters to <paramref name="cmd"/> using unique names. Returns "1=1" only for super admins.</summary>
    public string BuildEventPredicate(NpgsqlCommand cmd, string alias = "e")
    {
        if (IsSuperAdmin) return "TRUE";
        if (Grants.Count == 0) return "FALSE";

        var clauses = new List<string>();
        var i = cmd.Parameters.Count;
        foreach (var g in Grants)
        {
            var parts = new List<string>();
            if (g.TenantId is not null)
            {
                parts.Add($"{alias}.tenant_id = @ac{i}");
                cmd.Parameters.AddWithValue($"ac{i++}", g.TenantId.Value);
            }
            if (g.ProjectId is not null)
            {
                parts.Add($"{alias}.project_id = @ac{i}");
                cmd.Parameters.AddWithValue($"ac{i++}", g.ProjectId.Value);
            }
            if (g.EnvironmentId is not null)
            {
                parts.Add($"{alias}.environment_id = @ac{i}");
                cmd.Parameters.AddWithValue($"ac{i++}", g.EnvironmentId.Value);
            }
            if (g.Categories is { Length: > 0 })
            {
                parts.Add($"{alias}.event_type = ANY(@ac{i})");
                cmd.Parameters.AddWithValue($"ac{i++}", g.Categories);
            }
            if (g.MinSeverity is not null)
            {
                parts.Add($"{alias}.severity >= @ac{i}");
                cmd.Parameters.AddWithValue($"ac{i++}", g.MinSeverity.Value);
            }
            if (!g.CanViewSecurity)
                parts.Add($"{alias}.event_type <> 'Security'");

            clauses.Add(parts.Count == 0 ? "TRUE" : "(" + string.Join(" AND ", parts) + ")");
        }
        return "(" + string.Join(" OR ", clauses) + ")";
    }

    /// <summary>Tenant/project id pairs this user can see (null = unrestricted for super admin).</summary>
    public (Guid[]? TenantIds, Guid[]? ProjectIds) VisibleScopes()
    {
        if (IsSuperAdmin) return (null, null);
        var tenants = Grants.Where(g => g.TenantId is not null).Select(g => g.TenantId!.Value).Distinct().ToArray();
        var projects = Grants.Where(g => g.ProjectId is not null).Select(g => g.ProjectId!.Value).Distinct().ToArray();
        var hasUnscopedTenant = Grants.Any(g => g.TenantId is null);
        var hasUnscopedProject = Grants.Any(g => g.ProjectId is null);
        return (hasUnscopedTenant ? null : tenants, hasUnscopedProject ? null : projects);
    }
}
