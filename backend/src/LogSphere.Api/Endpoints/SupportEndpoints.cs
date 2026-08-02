using System.Text.Json.Nodes;
using LogSphere.Api.Infrastructure;
using LogSphere.Core.Repositories;

namespace LogSphere.Api.Endpoints;

public static class SupportEndpoints
{
    // Only these client-supplied context fields are forwarded to SupportHub, where they are
    // stored, displayed to the support team, and logged verbatim.
    private static readonly string[] AllowedContextFields = ["module", "page", "recordRef", "requestId", "appVersion"];

    public static void Map(IEndpointRouteBuilder app)
    {
        // Deliberately NOT behind MustChangePasswordFilter: a user stuck on the forced
        // password-change screen is exactly the user who may need to reach support.
        var group = app.MapGroup("/api/v1/support").RequireAuthorization();

        group.MapGet("/config", (HttpContext ctx, SupportHubClient support) =>
            ApiEnvelope.Ok(ctx, new { enabled = support.IsConfigured, widgetUrl = support.WidgetUrl }));

        group.MapPost("/launch", async (HttpContext ctx, JsonObject? context, CurrentUserResolver resolver,
            AdminRepository admin, SupportHubClient support, CancellationToken ct) =>
        {
            var user = await resolver.GetAsync(ctx, ct);
            if (user is null) return ApiEnvelope.Unauthorized(ctx);

            if (!support.IsConfigured)
                return ApiEnvelope.Fail(ctx, StatusCodes.Status503ServiceUnavailable, "LOG-SUPPORT-001",
                    "Support is not configured on this server.");

            // The identity sent to SupportHub comes from the session, never from the request body —
            // if the browser could name the user, any user could impersonate any other.
            var account = await admin.GetUserAsync(user.UserId, ct);
            if (account is null) return ApiEnvelope.Unauthorized(ctx);

            if (string.IsNullOrWhiteSpace(account.Email))
                return ApiEnvelope.Fail(ctx, StatusCodes.Status400BadRequest, "LOG-SUPPORT-002",
                    "Your account has no email address, which support requires. Ask an administrator to add one under Admin → Users.");

            var sync = await support.SyncUserAsync(account, ct);
            if (!sync.Ok)
            {
                // "04" is an account-identity conflict (staff-owned email, or the external id already
                // mapped elsewhere) — the caller can't fix it by retrying, an admin must. Pass
                // SupportHub's own message through: it names the actual conflict.
                var detail = string.IsNullOrEmpty(sync.Message) ? "SupportHub rejected the user sync." : sync.Message;
                // Not 502: Cloudflare (and some other proxies) replace an origin 502/504 body with
                // their own error page, so the browser would show a generic failure instead of
                // SupportHub's actual reason (e.g. "that project is archived"). 422 carries the
                // envelope through untouched.
                return sync.Code == "04"
                    ? ApiEnvelope.Fail(ctx, StatusCodes.Status409Conflict, "LOG-SUPPORT-004",
                        $"Support account conflict: {detail} Ask a SupportHub administrator to resolve it.")
                    : ApiEnvelope.Fail(ctx, StatusCodes.Status422UnprocessableEntity, "LOG-SUPPORT-003",
                        $"Could not open support: {detail}");
            }

            var minted = await support.MintLaunchTokenAsync(account.Id, FilterContext(context, ctx), ct);
            if (!minted.Ok || minted.Data?["launchToken"]?.GetValue<string>() is not { Length: > 0 } launchToken)
                return ApiEnvelope.Fail(ctx, StatusCodes.Status422UnprocessableEntity, "LOG-SUPPORT-003",
                    $"Could not open support: {(string.IsNullOrEmpty(minted.Message) ? "SupportHub did not issue a launch token." : minted.Message)}");

            await admin.RecordAccessAsync(user.UserId, "SupportLaunch", null,
                ctx.Connection.RemoteIpAddress?.ToString(), ct);

            return ApiEnvelope.Ok(ctx, new { launchToken });
        });
    }

    /// <summary>Allow-listed, length-capped copy of the browser-supplied context, plus the
    /// request's correlation id so a ticket can be tied back to LogSphere's own logs.</summary>
    private static JsonObject FilterContext(JsonObject? context, HttpContext ctx)
    {
        var filtered = new JsonObject();
        foreach (var field in AllowedContextFields)
        {
            if (context?[field] is JsonValue value && value.TryGetValue<string>(out var text) &&
                !string.IsNullOrWhiteSpace(text))
            {
                filtered[field] = text.Length <= 200 ? text : text[..200];
            }
        }
        if (ApiEnvelope.Correlation(ctx) is { Length: > 0 } correlationId)
            filtered["correlationId"] = correlationId;
        return filtered;
    }
}
