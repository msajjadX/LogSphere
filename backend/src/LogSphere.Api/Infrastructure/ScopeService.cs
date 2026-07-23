using LogSphere.Core.Auth;
using LogSphere.Core.Models;
using LogSphere.Core.Repositories;

namespace LogSphere.Api.Infrastructure;

/// <summary>Resolved administrative visibility for a dashboard user: which tenants and projects
/// their grants cover. Used to scope admin/alert listings server-side — non-super-admins must
/// never see (or manage) resources outside their scope.</summary>
public sealed record AdminScope(bool Unrestricted, HashSet<Guid> TenantIds, HashSet<Guid> ProjectIds)
{
    public static readonly AdminScope All = new(true, new HashSet<Guid>(), new HashSet<Guid>());

    public bool CoversTenant(Guid? tenantId) =>
        Unrestricted || (tenantId is not null && TenantIds.Contains(tenantId.Value));

    public bool CoversProject(Guid? projectId) =>
        Unrestricted || (projectId is not null && ProjectIds.Contains(projectId.Value));

    /// <summary>Global resources (tenantId and projectId both null) are visible to everyone
    /// but manageable only by super admins.</summary>
    public bool CoversResource(Guid? tenantId, Guid? projectId) =>
        Unrestricted || (tenantId is null && projectId is null) ||
        CoversTenant(tenantId) || CoversProject(projectId);
}

public sealed class ScopeService(AdminRepository admin)
{
    public async Task<AdminScope> GetAsync(UserContext user, CancellationToken ct)
    {
        if (user.IsSuperAdmin) return AdminScope.All;
        // a fully unscoped grant (no tenant, no project) sees platform-wide (minus super admins)
        if (user.Grants.Any(g => g.TenantId is null && g.ProjectId is null)) return AdminScope.All;

        var projects = await admin.ListProjectsAsync(user, ct); // already grant-filtered
        var tenantIds = user.Grants.Where(g => g.TenantId is not null).Select(g => g.TenantId!.Value)
            .Concat(projects.Select(p => p.TenantId))
            .ToHashSet();
        var projectIds = projects.Select(p => p.Id).ToHashSet();
        return new AdminScope(false, tenantIds, projectIds);
    }

    /// <summary>Users visible to the caller: super admins see everyone; others see themselves plus
    /// users whose grants fall inside the caller's tenants/projects — and never super admins.</summary>
    public async Task<List<UserAccount>> ListVisibleUsersAsync(UserContext user, CancellationToken ct)
    {
        var users = await admin.ListUsersAsync(ct);
        if (user.IsSuperAdmin) return users;
        var scope = await GetAsync(user, ct);
        return users.Where(u =>
                u.Id == user.UserId ||
                (!u.Grants.Any(g => g.Role == Roles.SuperAdmin) &&
                 (scope.Unrestricted ||
                  u.Grants.Any(g => scope.CoversTenant(g.TenantId) || scope.CoversProject(g.ProjectId)))))
            .ToList();
    }
}
