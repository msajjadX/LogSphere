using LogSphere.Api.Infrastructure;
using LogSphere.Core.Auth;
using LogSphere.Core.Repositories;

namespace LogSphere.Api.Endpoints;

public static class AuthEndpoints
{
    public sealed record LoginRequest(string Username, string Password);
    public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);

    // A fixed valid PBKDF2 hash (of a random secret) used to equalize login timing for unknown
    // users, so response latency cannot distinguish existing usernames from non-existent ones.
    private static readonly string DummyHash = PasswordHasher.Hash(Guid.NewGuid().ToString("N"));

    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/auth");

        group.MapPost("/login", async (HttpContext ctx, LoginRequest req, AdminRepository admin,
            JwtService jwt, LoginThrottle throttle, CancellationToken ct) =>
        {
            var ip = ctx.Connection.RemoteIpAddress?.ToString();
            if (string.IsNullOrWhiteSpace(req.Username) || string.IsNullOrWhiteSpace(req.Password))
                return ApiEnvelope.Validation(ctx, "Username and password are required.");

            if (throttle.RetryAfter(ip, req.Username) is { } retryAfter)
            {
                await admin.RecordAccessAsync(null, "LoginThrottled", new { username = req.Username }, ip, ct);
                return ApiEnvelope.Fail(ctx, StatusCodes.Status429TooManyRequests, "LOG-RATELIMIT-002",
                    $"Too many failed attempts. Try again in {Math.Ceiling(retryAfter.TotalSeconds)} seconds.");
            }

            var lookup = await admin.GetUserForAuthAsync(req.Username, ct);
            // always run a verify (dummy hash when the user is missing/inactive) for constant-time behavior
            var hashToCheck = lookup is { Account.IsActive: true } ? lookup.Value.PasswordHash : DummyHash;
            var passwordOk = PasswordHasher.Verify(req.Password, hashToCheck);

            if (lookup is not { Account.IsActive: true } || !passwordOk)
            {
                throttle.RecordFailure(ip, req.Username);
                await admin.RecordAccessAsync(lookup?.Account.Id, "LoginFailed", new { username = req.Username }, ip, ct);
                return ApiEnvelope.Unauthorized(ctx, "Invalid username or password.");
            }

            var user = lookup.Value.Account;
            throttle.RecordSuccess(ip, req.Username);
            await admin.TouchLoginAsync(user.Id, ct);
            await admin.RecordAccessAsync(user.Id, "Login", null, ip, ct);
            var (token, expiresAt) = jwt.Issue(user);
            return ApiEnvelope.Ok(ctx, new
            {
                token,
                expiresAt,
                user = new
                {
                    id = user.Id, username = user.Username, displayName = user.DisplayName,
                    mustChangePassword = user.MustChangePassword,
                    roles = user.Grants.Select(g => g.Role).Distinct().ToArray(),
                    grants = user.Grants
                }
            }, "Login successful.");
        });

        group.MapGet("/me", async (HttpContext ctx, CurrentUserResolver resolver, AdminRepository admin, CancellationToken ct) =>
        {
            var user = await resolver.GetAsync(ctx, ct);
            if (user is null) return ApiEnvelope.Unauthorized(ctx);
            var account = await admin.GetUserAsync(user.UserId, ct);
            return ApiEnvelope.Ok(ctx, new
            {
                id = account!.Id, username = account.Username, displayName = account.DisplayName,
                mustChangePassword = account.MustChangePassword,
                roles = account.Grants.Select(g => g.Role).Distinct().ToArray(),
                grants = account.Grants
            });
        }).RequireAuthorization();

        group.MapPost("/change-password", async (HttpContext ctx, ChangePasswordRequest req,
            CurrentUserResolver resolver, AdminRepository admin, CancellationToken ct) =>
        {
            var user = await resolver.GetAsync(ctx, ct);
            if (user is null) return ApiEnvelope.Unauthorized(ctx);
            if (string.IsNullOrWhiteSpace(req.NewPassword) || req.NewPassword.Length < 10)
                return ApiEnvelope.Validation(ctx, "New password must be at least 10 characters.");

            var account = await admin.GetUserForAuthAsync(user.Username, ct);
            // 400, not 401: the caller's session is perfectly valid — it is the submitted field
            // that is wrong. Returning 401/LOG-AUTH-001 here is indistinguishable from an expired
            // token, and the dashboard client logs the user out on any such 401, so one mistyped
            // character threw the user back to the login screen instead of showing the error.
            if (account is null || !PasswordHasher.Verify(req.CurrentPassword, account.Value.PasswordHash))
                return ApiEnvelope.Fail(ctx, StatusCodes.Status400BadRequest, "LOG-AUTH-004",
                    "Current password is incorrect.");

            await admin.SetPasswordAsync(user.UserId, req.NewPassword, mustChange: false, ct);
            await admin.RecordAccessAsync(user.UserId, "PasswordChanged", null,
                ctx.Connection.RemoteIpAddress?.ToString(), ct);
            return ApiEnvelope.Ok(ctx, null, "Password changed.");
        }).RequireAuthorization();
    }
}
