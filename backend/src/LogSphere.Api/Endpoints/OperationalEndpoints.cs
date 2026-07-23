using LogSphere.Api.Infrastructure;
using LogSphere.Core.Auth;
using LogSphere.Core.Models;
using LogSphere.Core.Repositories;

namespace LogSphere.Api.Endpoints;

/// <summary>Exception groups, alerts, notification channels and saved searches.</summary>
public static class OperationalEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        MapExceptions(app);
        MapAlerts(app);
        MapSearches(app);
    }

    private static void MapExceptions(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/exceptions").RequireAuthorization()
            .AddEndpointFilter<MustChangePasswordFilter>();

        group.MapPost("/groups/search", async (HttpContext ctx, ExceptionGroupSearch search,
            CurrentUserResolver resolver, ExceptionGroupRepository repo, CancellationToken ct) =>
        {
            var user = await resolver.GetAsync(ctx, ct);
            if (user is null) return ApiEnvelope.Unauthorized(ctx);
            var groups = await repo.SearchAsync(search, user, ct);
            return ApiEnvelope.Ok(ctx, new { items = groups });
        });

        group.MapGet("/groups/{id:guid}", async (HttpContext ctx, Guid id,
            CurrentUserResolver resolver, ExceptionGroupRepository repo, EventRepository events, CancellationToken ct) =>
        {
            var user = await resolver.GetAsync(ctx, ct);
            if (user is null) return ApiEnvelope.Unauthorized(ctx);
            var groupDetail = await repo.GetAsync(id, user, ct);
            if (groupDetail is null) return ApiEnvelope.NotFound(ctx);

            var occurrences = await events.QueryAsync(new LogQueryFilter
            {
                Fingerprint = groupDetail.Fingerprint,
                ProjectId = groupDetail.ProjectId,
                From = DateTimeOffset.UtcNow.AddDays(-30),
                Limit = 20
            }, user, ct);

            var trend = await events.GetTimeseriesAsync(new StatsRequest
            {
                From = DateTimeOffset.UtcNow.AddHours(-48),
                To = DateTimeOffset.UtcNow,
                ProjectId = groupDetail.ProjectId,
                Fingerprint = groupDetail.Fingerprint,
                IntervalMinutes = 60,
                Metric = "count"
            }, user, ct);

            LogEventDetail? sample = null;
            if (groupDetail.SampleEventId is not null)
                sample = await events.GetDetailAsync(groupDetail.SampleEventId.Value, user, ct);

            var activity = await repo.ListActivityAsync(id, ct);

            return ApiEnvelope.Ok(ctx, new { group = groupDetail, occurrences = occurrences.Items, trend, sample, activity });
        });

        group.MapPatch("/groups/{id:guid}", async (HttpContext ctx, Guid id, PatchExceptionGroupRequest patch,
            CurrentUserResolver resolver, ExceptionGroupRepository repo, CancellationToken ct) =>
        {
            var user = await resolver.GetAsync(ctx, ct);
            if (user is null) return ApiEnvelope.Unauthorized(ctx);
            if (patch.Status is not null && !ExceptionGroupStatuses.IsValid(patch.Status))
                return ApiEnvelope.Validation(ctx, $"Invalid status '{patch.Status}'.");
            var updated = await repo.PatchAsync(id, patch, user, ct);
            return updated ? ApiEnvelope.Ok(ctx, null, "Exception group updated.") : ApiEnvelope.NotFound(ctx);
        });
    }

    private static void MapAlerts(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/alerts").RequireAuthorization()
            .AddEndpointFilter<MustChangePasswordFilter>();

        group.MapGet("/rules", async (HttpContext ctx, CurrentUserResolver resolver, AlertRepository repo,
            ScopeService scopes, CancellationToken ct) =>
        {
            var user = await resolver.GetAsync(ctx, ct);
            if (user is null) return ApiEnvelope.Unauthorized(ctx);
            var scope = await scopes.GetAsync(user, ct);
            var rules = (await repo.ListRulesAsync(ct))
                .Where(r => scope.CoversTenant(r.TenantId) || scope.CoversProject(r.ProjectId)).ToList();
            return ApiEnvelope.Ok(ctx, new { items = rules });
        });

        group.MapPost("/rules", async (HttpContext ctx, AlertRule rule,
            CurrentUserResolver resolver, AlertRepository repo, LogSphere.Core.Repositories.AdminRepository admin,
            CancellationToken ct) =>
        {
            var user = await resolver.GetAsync(ctx, ct);
            if (user is null) return ApiEnvelope.Unauthorized(ctx);
            var tenantError = await ResolveRuleTenantAsync(rule, admin, user, ctx, ct);
            if (tenantError is not null) return tenantError;
            if (!user.CanManageProject(rule.TenantId, rule.ProjectId))
                return ApiEnvelope.Forbidden(ctx, "Only project/tenant administrators can manage alert rules.");
            rule.Id = Guid.Empty;
            return ApiEnvelope.Ok(ctx, await repo.UpsertRuleAsync(rule, ct), "Alert rule created.");
        });

        group.MapPut("/rules/{id:guid}", async (HttpContext ctx, Guid id, AlertRule rule,
            CurrentUserResolver resolver, AlertRepository repo, LogSphere.Core.Repositories.AdminRepository admin,
            CancellationToken ct) =>
        {
            var user = await resolver.GetAsync(ctx, ct);
            if (user is null) return ApiEnvelope.Unauthorized(ctx);
            var tenantError = await ResolveRuleTenantAsync(rule, admin, user, ctx, ct);
            if (tenantError is not null) return tenantError;
            if (!user.CanManageProject(rule.TenantId, rule.ProjectId))
                return ApiEnvelope.Forbidden(ctx, "Only project/tenant administrators can manage alert rules.");
            rule.Id = id;
            return ApiEnvelope.Ok(ctx, await repo.UpsertRuleAsync(rule, ct), "Alert rule updated.");
        });

        group.MapDelete("/rules/{id:guid}", async (HttpContext ctx, Guid id,
            CurrentUserResolver resolver, AlertRepository repo, CancellationToken ct) =>
        {
            var user = await resolver.GetAsync(ctx, ct);
            if (user is null) return ApiEnvelope.Unauthorized(ctx);
            var rule = (await repo.ListRulesAsync(ct)).FirstOrDefault(r => r.Id == id);
            if (rule is null) return ApiEnvelope.NotFound(ctx);
            if (!user.CanManageProject(rule.TenantId, rule.ProjectId))
                return ApiEnvelope.Forbidden(ctx, "Only project/tenant administrators can manage alert rules.");
            await repo.DeleteRuleAsync(id, ct);
            return ApiEnvelope.Ok(ctx, null, "Alert rule deleted.");
        });

        group.MapGet("/occurrences", async (HttpContext ctx, string? state, Guid? projectId,
            CurrentUserResolver resolver, AlertRepository repo, ScopeService scopes, CancellationToken ct) =>
        {
            var user = await resolver.GetAsync(ctx, ct);
            if (user is null) return ApiEnvelope.Unauthorized(ctx);
            var scope = await scopes.GetAsync(user, ct);
            var items = (await repo.ListOccurrencesAsync(state, projectId, 200, ct))
                .Where(o => scope.Unrestricted || o.ProjectId is null || scope.CoversProject(o.ProjectId)).ToList();
            return ApiEnvelope.Ok(ctx, new { items });
        });

        group.MapPost("/occurrences/{id:guid}/ack", async (HttpContext ctx, Guid id,
            CurrentUserResolver resolver, AlertRepository repo, ScopeService scopes, CancellationToken ct) =>
        {
            var user = await resolver.GetAsync(ctx, ct);
            if (user is null) return ApiEnvelope.Unauthorized(ctx);
            if (!await CanAccessOccurrence(repo, scopes, user, id, ct)) return ApiEnvelope.Forbidden(ctx);
            var updated = await repo.SetOccurrenceStateAsync(id, "Acknowledged", user.UserId, ct);
            return updated ? ApiEnvelope.Ok(ctx, null, "Acknowledged.") : ApiEnvelope.NotFound(ctx);
        });

        group.MapPost("/occurrences/{id:guid}/resolve", async (HttpContext ctx, Guid id,
            CurrentUserResolver resolver, AlertRepository repo, ScopeService scopes, CancellationToken ct) =>
        {
            var user = await resolver.GetAsync(ctx, ct);
            if (user is null) return ApiEnvelope.Unauthorized(ctx);
            if (!await CanAccessOccurrence(repo, scopes, user, id, ct)) return ApiEnvelope.Forbidden(ctx);
            var updated = await repo.SetOccurrenceStateAsync(id, "Resolved", user.UserId, ct);
            return updated ? ApiEnvelope.Ok(ctx, null, "Resolved.") : ApiEnvelope.NotFound(ctx);
        });

        group.MapGet("/channels", async (HttpContext ctx, CurrentUserResolver resolver, AlertRepository repo,
            ScopeService scopes, CancellationToken ct) =>
        {
            var user = await resolver.GetAsync(ctx, ct);
            if (user is null) return ApiEnvelope.Unauthorized(ctx);
            var scope = await scopes.GetAsync(user, ct);
            var items = (await repo.ListChannelsAsync(ct))
                .Where(c => c.TenantId is null || scope.CoversTenant(c.TenantId)).ToList();
            return ApiEnvelope.Ok(ctx, new { items });
        });

        group.MapPost("/channels", async (HttpContext ctx, NotificationChannel channel,
            CurrentUserResolver resolver, AlertRepository repo, ScopeService scopes, CancellationToken ct) =>
        {
            var user = await resolver.GetAsync(ctx, ct);
            if (user is null) return ApiEnvelope.Unauthorized(ctx);
            if (!user.IsAnyAdmin) return ApiEnvelope.Forbidden(ctx);
            if (await ChannelScopeError(ctx, scopes, user, channel.TenantId, ct) is { } err) return err;
            channel.Id = Guid.Empty;
            return ApiEnvelope.Ok(ctx, await repo.UpsertChannelAsync(channel, ct), "Channel created.");
        });

        group.MapPut("/channels/{id:guid}", async (HttpContext ctx, Guid id, NotificationChannel channel,
            CurrentUserResolver resolver, AlertRepository repo, ScopeService scopes, CancellationToken ct) =>
        {
            var user = await resolver.GetAsync(ctx, ct);
            if (user is null) return ApiEnvelope.Unauthorized(ctx);
            if (!user.IsAnyAdmin) return ApiEnvelope.Forbidden(ctx);
            var existing = (await repo.ListChannelsAsync(ct)).FirstOrDefault(c => c.Id == id);
            if (existing is null) return ApiEnvelope.NotFound(ctx);
            // must be able to manage both the existing scope and the (possibly changed) target scope
            if (await ChannelScopeError(ctx, scopes, user, existing.TenantId, ct) is { } err1) return err1;
            if (await ChannelScopeError(ctx, scopes, user, channel.TenantId, ct) is { } err2) return err2;
            channel.Id = id;
            return ApiEnvelope.Ok(ctx, await repo.UpsertChannelAsync(channel, ct), "Channel updated.");
        });

        group.MapDelete("/channels/{id:guid}", async (HttpContext ctx, Guid id,
            CurrentUserResolver resolver, AlertRepository repo, ScopeService scopes, CancellationToken ct) =>
        {
            var user = await resolver.GetAsync(ctx, ct);
            if (user is null) return ApiEnvelope.Unauthorized(ctx);
            if (!user.IsAnyAdmin) return ApiEnvelope.Forbidden(ctx);
            var existing = (await repo.ListChannelsAsync(ct)).FirstOrDefault(c => c.Id == id);
            if (existing is null) return ApiEnvelope.NotFound(ctx);
            if (await ChannelScopeError(ctx, scopes, user, existing.TenantId, ct) is { } err) return err;
            await repo.DeleteChannelAsync(id, ct);
            return ApiEnvelope.Ok(ctx, null, "Channel deleted.");
        });
    }

    /// <summary>Null when the caller may manage a channel in the given tenant scope, else a Forbidden result.
    /// Global channels (tenantId null) are super-admin only; others must fall within the caller's scope.</summary>
    private static async Task<IResult?> ChannelScopeError(HttpContext ctx, ScopeService scopes,
        UserContext user, Guid? tenantId, CancellationToken ct)
    {
        if (user.IsSuperAdmin) return null;
        if (tenantId is null)
            return ApiEnvelope.Forbidden(ctx, "Only super administrators can manage global notification channels.");
        var scope = await scopes.GetAsync(user, ct);
        return scope.CoversTenant(tenantId)
            ? null
            : ApiEnvelope.Forbidden(ctx, "The channel's tenant is outside your administration scope.");
    }

    private static async Task<bool> CanAccessOccurrence(AlertRepository repo, ScopeService scopes,
        UserContext user, Guid occurrenceId, CancellationToken ct)
    {
        var occ = await repo.GetOccurrenceScopeAsync(occurrenceId, ct);
        if (occ is null) return false;
        var scope = await scopes.GetAsync(user, ct);
        return scope.CoversResource(occ.Value.TenantId, occ.Value.ProjectId);
    }

    /// <summary>Rules may arrive without a tenant (the UI scopes by project). Derive it from the
    /// project, or from the caller's tenant when unambiguous; otherwise ask for a project.</summary>
    private static async Task<IResult?> ResolveRuleTenantAsync(AlertRule rule,
        LogSphere.Core.Repositories.AdminRepository admin, LogSphere.Core.Auth.UserContext user,
        HttpContext ctx, CancellationToken ct)
    {
        if (rule.TenantId != Guid.Empty) return null;
        if (rule.ProjectId is not null)
        {
            var project = await admin.GetProjectAsync(rule.ProjectId.Value, ct);
            if (project is null) return ApiEnvelope.Validation(ctx, "The selected project does not exist.");
            rule.TenantId = project.TenantId;
            return null;
        }
        var tenants = await admin.ListTenantsAsync(user, ct);
        if (tenants.Count == 1)
        {
            rule.TenantId = tenants[0].Id;
            return null;
        }
        return ApiEnvelope.Validation(ctx, "Select a project for this rule (multiple tenants are visible to you).");
    }

    private static void MapSearches(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/searches").RequireAuthorization()
            .AddEndpointFilter<MustChangePasswordFilter>();

        group.MapGet("", async (HttpContext ctx, CurrentUserResolver resolver, SearchRepository repo, CancellationToken ct) =>
        {
            var user = await resolver.GetAsync(ctx, ct);
            if (user is null) return ApiEnvelope.Unauthorized(ctx);
            return ApiEnvelope.Ok(ctx, new { items = await repo.ListAsync(user.UserId, ct) });
        });

        group.MapPost("", async (HttpContext ctx, SavedSearch search,
            CurrentUserResolver resolver, SearchRepository repo, CancellationToken ct) =>
        {
            var user = await resolver.GetAsync(ctx, ct);
            if (user is null) return ApiEnvelope.Unauthorized(ctx);
            search.Id = Guid.Empty;
            search.UserId = user.UserId;
            return ApiEnvelope.Ok(ctx, await repo.UpsertAsync(search, ct), "Search saved.");
        });

        group.MapPut("/{id:guid}", async (HttpContext ctx, Guid id, SavedSearch search,
            CurrentUserResolver resolver, SearchRepository repo, CancellationToken ct) =>
        {
            var user = await resolver.GetAsync(ctx, ct);
            if (user is null) return ApiEnvelope.Unauthorized(ctx);
            search.Id = id;
            search.UserId = user.UserId;
            return ApiEnvelope.Ok(ctx, await repo.UpsertAsync(search, ct), "Search updated.");
        });

        group.MapDelete("/{id:guid}", async (HttpContext ctx, Guid id,
            CurrentUserResolver resolver, SearchRepository repo, CancellationToken ct) =>
        {
            var user = await resolver.GetAsync(ctx, ct);
            if (user is null) return ApiEnvelope.Unauthorized(ctx);
            await repo.DeleteAsync(id, user.UserId, ct);
            return ApiEnvelope.Ok(ctx, null, "Search deleted.");
        });
    }
}
