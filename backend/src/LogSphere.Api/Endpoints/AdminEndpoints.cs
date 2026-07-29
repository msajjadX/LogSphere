using LogSphere.Api.Infrastructure;
using LogSphere.Core.Auth;
using LogSphere.Core.Models;
using LogSphere.Core.Repositories;

namespace LogSphere.Api.Endpoints;

public static class AdminEndpoints
{
    public sealed record ResetPasswordRequest(string? NewPassword);

    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/admin").RequireAuthorization()
            .AddEndpointFilter<MustChangePasswordFilter>();

        // -------------------------------------------------------- tenants
        group.MapGet("/tenants", async (HttpContext ctx, CurrentUserResolver resolver, AdminRepository admin, CancellationToken ct) =>
        {
            var user = await resolver.GetAsync(ctx, ct);
            if (user is null) return ApiEnvelope.Unauthorized(ctx);
            return ApiEnvelope.Ok(ctx, new { items = await admin.ListTenantsAsync(user, ct) });
        });

        group.MapPost("/tenants", async (HttpContext ctx, Tenant tenant,
            CurrentUserResolver resolver, AdminRepository admin, CancellationToken ct) =>
        {
            var user = await resolver.GetAsync(ctx, ct);
            if (user is null) return ApiEnvelope.Unauthorized(ctx);
            if (!user.IsSuperAdmin) return ApiEnvelope.Forbidden(ctx, "Only super administrators can manage tenants.");
            tenant.Id = Guid.Empty;
            var saved = await admin.UpsertTenantAsync(tenant, ct);
            await Audit(admin, user, ctx, "TenantCreated", new { saved.Id, saved.Name }, ct);
            return ApiEnvelope.Ok(ctx, saved, "Tenant created.");
        });

        group.MapPut("/tenants/{id:guid}", async (HttpContext ctx, Guid id, Tenant tenant,
            CurrentUserResolver resolver, AdminRepository admin, CancellationToken ct) =>
        {
            var user = await resolver.GetAsync(ctx, ct);
            if (user is null) return ApiEnvelope.Unauthorized(ctx);
            if (!user.IsTenantAdmin(id)) return ApiEnvelope.Forbidden(ctx);
            tenant.Id = id;
            var saved = await admin.UpsertTenantAsync(tenant, ct);
            await Audit(admin, user, ctx, "TenantUpdated", new { id }, ct);
            return ApiEnvelope.Ok(ctx, saved, "Tenant updated.");
        });

        // -------------------------------------------------------- projects
        group.MapGet("/projects", async (HttpContext ctx, CurrentUserResolver resolver, AdminRepository admin, CancellationToken ct) =>
        {
            var user = await resolver.GetAsync(ctx, ct);
            if (user is null) return ApiEnvelope.Unauthorized(ctx);
            return ApiEnvelope.Ok(ctx, new { items = await admin.ListProjectsAsync(user, ct) });
        });

        group.MapPost("/projects", async (HttpContext ctx, Project project,
            CurrentUserResolver resolver, AdminRepository admin, CancellationToken ct) =>
        {
            var user = await resolver.GetAsync(ctx, ct);
            if (user is null) return ApiEnvelope.Unauthorized(ctx);
            if (!user.IsTenantAdmin(project.TenantId)) return ApiEnvelope.Forbidden(ctx);
            project.Id = Guid.Empty;
            var saved = await admin.UpsertProjectAsync(project, ct);
            await Audit(admin, user, ctx, "ProjectCreated", new { saved.Id, saved.Name }, ct);
            return ApiEnvelope.Ok(ctx, saved, "Project created.");
        });

        group.MapPut("/projects/{id:guid}", async (HttpContext ctx, Guid id, Project project,
            CurrentUserResolver resolver, AdminRepository admin, CancellationToken ct) =>
        {
            var user = await resolver.GetAsync(ctx, ct);
            if (user is null) return ApiEnvelope.Unauthorized(ctx);
            var existing = await admin.GetProjectAsync(id, ct);
            if (existing is null) return ApiEnvelope.NotFound(ctx);
            if (!user.CanManageProject(existing.TenantId, id)) return ApiEnvelope.Forbidden(ctx);
            project.Id = id;
            project.TenantId = existing.TenantId;
            var saved = await admin.UpsertProjectAsync(project, ct);
            await Audit(admin, user, ctx, "ProjectUpdated", new { id }, ct);
            return ApiEnvelope.Ok(ctx, saved, "Project updated.");
        });

        // -------------------------------------------------------- applications & credentials
        group.MapGet("/applications", async (HttpContext ctx, Guid? projectId,
            CurrentUserResolver resolver, AdminRepository admin, CancellationToken ct) =>
        {
            var user = await resolver.GetAsync(ctx, ct);
            if (user is null) return ApiEnvelope.Unauthorized(ctx);
            return ApiEnvelope.Ok(ctx, new { items = await admin.ListApplicationsAsync(user, projectId, ct) });
        });

        group.MapPost("/applications", async (HttpContext ctx, Application application,
            CurrentUserResolver resolver, AdminRepository admin, CancellationToken ct) =>
        {
            var user = await resolver.GetAsync(ctx, ct);
            if (user is null) return ApiEnvelope.Unauthorized(ctx);
            var project = await admin.GetProjectAsync(application.ProjectId, ct);
            if (project is null) return ApiEnvelope.Validation(ctx, "Project does not exist.");
            if (!user.CanManageProject(project.TenantId, project.Id)) return ApiEnvelope.Forbidden(ctx);
            application.Id = Guid.Empty;
            var saved = await admin.UpsertApplicationAsync(application, ct);
            await Audit(admin, user, ctx, "ApplicationCreated", new { saved.Id, saved.Name }, ct);
            return ApiEnvelope.Ok(ctx, saved, "Application created.");
        });

        group.MapPut("/applications/{id:guid}", async (HttpContext ctx, Guid id, Application application,
            CurrentUserResolver resolver, AdminRepository admin, CancellationToken ct) =>
        {
            var user = await resolver.GetAsync(ctx, ct);
            if (user is null) return ApiEnvelope.Unauthorized(ctx);
            var existing = await admin.GetApplicationAsync(id, ct);
            if (existing is null) return ApiEnvelope.NotFound(ctx);
            var project = await admin.GetProjectAsync(existing.ProjectId, ct);
            if (project is null || !user.CanManageProject(project.TenantId, project.Id)) return ApiEnvelope.Forbidden(ctx);
            application.Id = id;
            application.ProjectId = existing.ProjectId;
            var saved = await admin.UpsertApplicationAsync(application, ct);
            return ApiEnvelope.Ok(ctx, saved, "Application updated.");
        });

        group.MapGet("/applications/{appId:guid}/credentials", async (HttpContext ctx, Guid appId,
            CurrentUserResolver resolver, AdminRepository admin, CancellationToken ct) =>
        {
            var user = await resolver.GetAsync(ctx, ct);
            if (user is null) return ApiEnvelope.Unauthorized(ctx);
            if (!await CanManageApplication(admin, user, appId, ct)) return ApiEnvelope.Forbidden(ctx);
            return ApiEnvelope.Ok(ctx, new { items = await admin.ListCredentialsAsync(appId, ct) });
        });

        group.MapPost("/applications/{appId:guid}/credentials", async (HttpContext ctx, Guid appId,
            CreateCredentialRequest request, CurrentUserResolver resolver, AdminRepository admin,
            ApiKeyService apiKeys, CancellationToken ct) =>
        {
            var user = await resolver.GetAsync(ctx, ct);
            if (user is null) return ApiEnvelope.Unauthorized(ctx);
            if (!await CanManageApplication(admin, user, appId, ct)) return ApiEnvelope.Forbidden(ctx);
            var (id, plaintextKey) = await apiKeys.CreateAsync(appId, request, ct);
            await Audit(admin, user, ctx, "CredentialCreated", new { appId, credentialId = id }, ct);
            return ApiEnvelope.Ok(ctx, new { id, apiKey = plaintextKey },
                "Credential created. Store the key now — it will not be shown again.");
        });

        group.MapPost("/credentials/{id:guid}/revoke", async (HttpContext ctx, Guid id,
            CurrentUserResolver resolver, AdminRepository admin, ApiKeyService apiKeys, CancellationToken ct) =>
        {
            var user = await resolver.GetAsync(ctx, ct);
            if (user is null) return ApiEnvelope.Unauthorized(ctx);
            var appId = await admin.GetCredentialApplicationAsync(id, ct);
            if (appId is null) return ApiEnvelope.NotFound(ctx);
            if (!await CanManageApplication(admin, user, appId.Value, ct)) return ApiEnvelope.Forbidden(ctx);
            await apiKeys.RevokeAsync(id, ct);
            await Audit(admin, user, ctx, "CredentialRevoked", new { credentialId = id }, ct);
            return ApiEnvelope.Ok(ctx, null, "Credential revoked.");
        });

        // -------------------------------------------------------- environments
        group.MapGet("/environments", async (HttpContext ctx, CurrentUserResolver resolver, AdminRepository admin, CancellationToken ct) =>
        {
            var user = await resolver.GetAsync(ctx, ct);
            if (user is null) return ApiEnvelope.Unauthorized(ctx);
            var environments = await admin.ListEnvironmentsAsync(ct);
            return ApiEnvelope.Ok(ctx, new { items = environments.Select(e => new { id = e.Id, name = e.Name }) });
        });

        // -------------------------------------------------------- users
        group.MapGet("/users", async (HttpContext ctx, CurrentUserResolver resolver, ScopeService scopes, CancellationToken ct) =>
        {
            var user = await resolver.GetAsync(ctx, ct);
            if (user is null) return ApiEnvelope.Unauthorized(ctx);
            if (!user.IsAnyAdmin) return ApiEnvelope.Forbidden(ctx);
            return ApiEnvelope.Ok(ctx, new { items = await scopes.ListVisibleUsersAsync(user, ct) });
        });

        group.MapPost("/users", async (HttpContext ctx, UpsertUserRequest request,
            CurrentUserResolver resolver, AdminRepository admin, SupportHubClient support, CancellationToken ct) =>
        {
            var user = await resolver.GetAsync(ctx, ct);
            if (user is null) return ApiEnvelope.Unauthorized(ctx);
            if (!user.IsSuperAdmin) return ApiEnvelope.Forbidden(ctx, "Only super administrators can create users.");
            if (string.IsNullOrWhiteSpace(request.Username)) return ApiEnvelope.Validation(ctx, "Username is required.");
            if (request.Grants.Any(g => !Roles.All.Contains(g.Role)))
                return ApiEnvelope.Validation(ctx, "One or more grants use an unknown role.");
            var id = await admin.UpsertUserAsync(null, request, ct);
            await Audit(admin, user, ctx, "UserCreated", new { id, request.Username }, ct);
            await SyncToSupportHub(admin, support, id);
            return ApiEnvelope.Ok(ctx, new { id }, "User created.");
        });

        group.MapPut("/users/{id:guid}", async (HttpContext ctx, Guid id, UpsertUserRequest request,
            CurrentUserResolver resolver, AdminRepository admin, SupportHubClient support, CancellationToken ct) =>
        {
            var user = await resolver.GetAsync(ctx, ct);
            if (user is null) return ApiEnvelope.Unauthorized(ctx);
            if (!user.IsSuperAdmin) return ApiEnvelope.Forbidden(ctx);
            if (request.Grants.Any(g => !Roles.All.Contains(g.Role)))
                return ApiEnvelope.Validation(ctx, "One or more grants use an unknown role.");
            await admin.UpsertUserAsync(id, request, ct);
            await Audit(admin, user, ctx, "UserUpdated", new { id }, ct);
            await SyncToSupportHub(admin, support, id);
            return ApiEnvelope.Ok(ctx, null, "User updated.");
        });

        group.MapPost("/users/{id:guid}/reset-password", async (HttpContext ctx, Guid id,
            ResetPasswordRequest? body, CurrentUserResolver resolver, AdminRepository admin, CancellationToken ct) =>
        {
            var user = await resolver.GetAsync(ctx, ct);
            if (user is null) return ApiEnvelope.Unauthorized(ctx);
            if (!user.IsSuperAdmin) return ApiEnvelope.Forbidden(ctx);
            // A password the administrator actually typed is either used or refused — never
            // silently replaced. Substituting a generated one for anything under 10 characters
            // reported success while setting a secret the caller never saw, which left the
            // account unusable and looked exactly like "reset password doesn't work".
            var supplied = body?.NewPassword;
            if (!string.IsNullOrWhiteSpace(supplied) && supplied.Trim().Length < 10)
                return ApiEnvelope.Validation(ctx, "New password must be at least 10 characters.");

            var generated = string.IsNullOrWhiteSpace(supplied);
            var temp = generated ? "Tmp#" + Guid.NewGuid().ToString("N")[..12] : supplied!;
            await admin.SetPasswordAsync(id, temp, mustChange: true, ct);
            await Audit(admin, user, ctx, "PasswordReset", new { id, generated }, ct);
            return ApiEnvelope.Ok(ctx, new { temporaryPassword = temp, generated },
                generated
                    ? "Temporary password generated. The user must change it at next login."
                    : "Password set. The user must change it at next login.");
        });

        // -------------------------------------------------------- redaction rules
        group.MapGet("/redaction-rules", async (HttpContext ctx, CurrentUserResolver resolver,
            AdminRepository admin, ScopeService scopes, CancellationToken ct) =>
        {
            var user = await resolver.GetAsync(ctx, ct);
            if (user is null) return ApiEnvelope.Unauthorized(ctx);
            if (!user.IsAnyAdmin) return ApiEnvelope.Forbidden(ctx);
            var scope = await scopes.GetAsync(user, ct);
            var rules = (await admin.ListRedactionRulesAsync(ct))
                .Where(r => scope.CoversResource(r.TenantId, r.ProjectId)).ToList();
            return ApiEnvelope.Ok(ctx, new { items = rules });
        });

        group.MapPost("/redaction-rules", async (HttpContext ctx, RedactionRule rule,
            CurrentUserResolver resolver, AdminRepository admin, ScopeService scopes, CancellationToken ct) =>
        {
            var user = await resolver.GetAsync(ctx, ct);
            if (user is null) return ApiEnvelope.Unauthorized(ctx);
            if (!user.IsAnyAdmin) return ApiEnvelope.Forbidden(ctx);
            if (!user.IsSuperAdmin)
            {
                var scope = await scopes.GetAsync(user, ct);
                if (rule.TenantId is null && rule.ProjectId is null)
                    return ApiEnvelope.Forbidden(ctx, "Only super administrators can manage global redaction rules.");
                if (!scope.CoversTenant(rule.TenantId) && !scope.CoversProject(rule.ProjectId))
                    return ApiEnvelope.Forbidden(ctx, "The rule's scope is outside your administration scope.");
            }
            rule.Id = Guid.Empty;
            await Audit(admin, user, ctx, "RedactionRuleCreated", new { rule.KeyPattern }, ct);
            return ApiEnvelope.Ok(ctx, await admin.UpsertRedactionRuleAsync(rule, ct), "Redaction rule created.");
        });

        group.MapPut("/redaction-rules/{id:guid}", async (HttpContext ctx, Guid id, RedactionRule rule,
            CurrentUserResolver resolver, AdminRepository admin, ScopeService scopes, CancellationToken ct) =>
        {
            var user = await resolver.GetAsync(ctx, ct);
            if (user is null) return ApiEnvelope.Unauthorized(ctx);
            if (!user.IsAnyAdmin) return ApiEnvelope.Forbidden(ctx);
            if (!user.IsSuperAdmin)
            {
                var scope = await scopes.GetAsync(user, ct);
                if (rule.TenantId is null && rule.ProjectId is null)
                    return ApiEnvelope.Forbidden(ctx, "Only super administrators can manage global redaction rules.");
                if (!scope.CoversTenant(rule.TenantId) && !scope.CoversProject(rule.ProjectId))
                    return ApiEnvelope.Forbidden(ctx, "The rule's scope is outside your administration scope.");
            }
            rule.Id = id;
            return ApiEnvelope.Ok(ctx, await admin.UpsertRedactionRuleAsync(rule, ct), "Redaction rule updated.");
        });

        group.MapDelete("/redaction-rules/{id:guid}", async (HttpContext ctx, Guid id,
            CurrentUserResolver resolver, AdminRepository admin, ScopeService scopes, CancellationToken ct) =>
        {
            var user = await resolver.GetAsync(ctx, ct);
            if (user is null) return ApiEnvelope.Unauthorized(ctx);
            if (!user.IsAnyAdmin) return ApiEnvelope.Forbidden(ctx);
            var rule = (await admin.ListRedactionRulesAsync(ct)).FirstOrDefault(r => r.Id == id);
            if (rule is null) return ApiEnvelope.NotFound(ctx);
            if (!user.IsSuperAdmin)
            {
                if (rule.TenantId is null && rule.ProjectId is null)
                    return ApiEnvelope.Forbidden(ctx, "Only super administrators can delete global redaction rules.");
                var scope = await scopes.GetAsync(user, ct);
                if (!scope.CoversTenant(rule.TenantId) && !scope.CoversProject(rule.ProjectId))
                    return ApiEnvelope.Forbidden(ctx, "The rule's scope is outside your administration scope.");
            }
            await admin.DeleteRedactionRuleAsync(id, ct);
            await Audit(admin, user, ctx, "RedactionRuleDeleted", new { id, rule.KeyPattern }, ct);
            return ApiEnvelope.Ok(ctx, null, "Redaction rule deleted.");
        });

        // -------------------------------------------------------- retention policies
        group.MapGet("/retention-policies", async (HttpContext ctx, CurrentUserResolver resolver,
            AdminRepository admin, ScopeService scopes, CancellationToken ct) =>
        {
            var user = await resolver.GetAsync(ctx, ct);
            if (user is null) return ApiEnvelope.Unauthorized(ctx);
            if (!user.IsAnyAdmin) return ApiEnvelope.Forbidden(ctx);
            var scope = await scopes.GetAsync(user, ct);
            var policies = (await admin.ListRetentionPoliciesAsync(ct))
                .Where(p => scope.CoversResource(p.TenantId, p.ProjectId)).ToList();
            return ApiEnvelope.Ok(ctx, new { items = policies });
        });

        group.MapPost("/retention-policies", async (HttpContext ctx, RetentionPolicy policy,
            CurrentUserResolver resolver, AdminRepository admin, ScopeService scopes, CancellationToken ct) =>
        {
            var user = await resolver.GetAsync(ctx, ct);
            if (user is null) return ApiEnvelope.Unauthorized(ctx);
            if (!user.IsAnyAdmin) return ApiEnvelope.Forbidden(ctx);
            if (!user.IsSuperAdmin)
            {
                var scope = await scopes.GetAsync(user, ct);
                if (policy.TenantId is null && policy.ProjectId is null)
                    return ApiEnvelope.Forbidden(ctx, "Only super administrators can manage global retention policies.");
                if (!scope.CoversTenant(policy.TenantId) && !scope.CoversProject(policy.ProjectId))
                    return ApiEnvelope.Forbidden(ctx, "The policy's scope is outside your administration scope.");
            }
            policy.Id = Guid.Empty;
            await Audit(admin, user, ctx, "RetentionPolicyCreated", new { policy.RetentionDays, policy.EventType }, ct);
            return ApiEnvelope.Ok(ctx, await admin.UpsertRetentionPolicyAsync(policy, ct), "Retention policy created.");
        });

        group.MapPut("/retention-policies/{id:guid}", async (HttpContext ctx, Guid id, RetentionPolicy policy,
            CurrentUserResolver resolver, AdminRepository admin, ScopeService scopes, CancellationToken ct) =>
        {
            var user = await resolver.GetAsync(ctx, ct);
            if (user is null) return ApiEnvelope.Unauthorized(ctx);
            if (!user.IsAnyAdmin) return ApiEnvelope.Forbidden(ctx);
            if (!user.IsSuperAdmin)
            {
                var scope = await scopes.GetAsync(user, ct);
                if (policy.TenantId is null && policy.ProjectId is null)
                    return ApiEnvelope.Forbidden(ctx, "Only super administrators can manage global retention policies.");
                if (!scope.CoversTenant(policy.TenantId) && !scope.CoversProject(policy.ProjectId))
                    return ApiEnvelope.Forbidden(ctx, "The policy's scope is outside your administration scope.");
            }
            policy.Id = id;
            return ApiEnvelope.Ok(ctx, await admin.UpsertRetentionPolicyAsync(policy, ct), "Retention policy updated.");
        });

        group.MapDelete("/retention-policies/{id:guid}", async (HttpContext ctx, Guid id,
            CurrentUserResolver resolver, AdminRepository admin, ScopeService scopes, CancellationToken ct) =>
        {
            var user = await resolver.GetAsync(ctx, ct);
            if (user is null) return ApiEnvelope.Unauthorized(ctx);
            if (!user.IsAnyAdmin) return ApiEnvelope.Forbidden(ctx);
            var policy = (await admin.ListRetentionPoliciesAsync(ct)).FirstOrDefault(p => p.Id == id);
            if (policy is null) return ApiEnvelope.NotFound(ctx);
            if (!user.IsSuperAdmin)
            {
                if (policy.TenantId is null && policy.ProjectId is null)
                    return ApiEnvelope.Forbidden(ctx, "Only super administrators can delete global retention policies.");
                var scope = await scopes.GetAsync(user, ct);
                if (!scope.CoversTenant(policy.TenantId) && !scope.CoversProject(policy.ProjectId))
                    return ApiEnvelope.Forbidden(ctx, "The policy's scope is outside your administration scope.");
            }
            await admin.DeleteRetentionPolicyAsync(id, ct);
            await Audit(admin, user, ctx, "RetentionPolicyDeleted", new { id }, ct);
            return ApiEnvelope.Ok(ctx, null, "Retention policy deleted.");
        });

        // -------------------------------------------------------- access audit
        group.MapGet("/audit-access", async (HttpContext ctx, int? limit,
            CurrentUserResolver resolver, AdminRepository admin, ScopeService scopes, CancellationToken ct) =>
        {
            var user = await resolver.GetAsync(ctx, ct);
            if (user is null) return ApiEnvelope.Unauthorized(ctx);
            if (!user.IsAnyAdmin) return ApiEnvelope.Forbidden(ctx);
            var items = await admin.ListAccessAuditAsync(Math.Clamp(limit ?? 200, 1, 1000), ct);
            if (!user.IsSuperAdmin)
            {
                // non-super admins see only activity of users within their own scope
                var visibleUserIds = (await scopes.ListVisibleUsersAsync(user, ct)).Select(u => u.Id).ToHashSet();
                items = items.Where(a => a.UserId is not null && visibleUserIds.Contains(a.UserId.Value)).ToList();
            }
            return ApiEnvelope.Ok(ctx, new { items });
        });
    }

    private static async Task<bool> CanManageApplication(AdminRepository admin, UserContext user, Guid appId, CancellationToken ct)
    {
        var app = await admin.GetApplicationAsync(appId, ct);
        if (app is null) return false;
        var project = await admin.GetProjectAsync(app.ProjectId, ct);
        return project is not null && user.CanManageProject(project.TenantId, project.Id);
    }

    private static Task Audit(AdminRepository admin, UserContext user, HttpContext ctx, string action, object details, CancellationToken ct) =>
        admin.RecordAccessAsync(user.UserId, action, details, ctx.Connection.RemoteIpAddress?.ToString(), ct);

    /// <summary>Keeps SupportHub's copy of a user in step after create/update — most importantly
    /// isActive:false, which revokes that person's support access along with everything else.
    /// The sync itself is fire-and-forget: SupportHub being slow or down must never fail or
    /// delay LogSphere's own admin operation.</summary>
    private static async Task SyncToSupportHub(AdminRepository admin, SupportHubClient support, Guid userId)
    {
        if (!support.IsConfigured) return;
        var account = await admin.GetUserAsync(userId, CancellationToken.None);
        if (account is not null) _ = support.TrySyncUserAsync(account, CancellationToken.None);
    }
}
