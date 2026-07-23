using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using LogSphere.Core.Auth;
using LogSphere.Core.Models;
using LogSphere.Core.Repositories;
using Microsoft.IdentityModel.Tokens;

namespace LogSphere.Api.Infrastructure;

public sealed class JwtService(IConfiguration config)
{
    public const string DevFallbackSecret = "logsphere-dev-jwt-secret-change-me-0123456789abcdef";

    public string Secret => config["Jwt:Secret"] is { Length: > 0 } secret ? secret : DevFallbackSecret;

    public (string Token, DateTimeOffset ExpiresAt) Issue(UserAccount user)
    {
        var expires = DateTimeOffset.UtcNow.AddHours(config.GetValue("Jwt:LifetimeHours", 8));
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Secret));
        var token = new JwtSecurityToken(
            issuer: "logsphere",
            audience: "logsphere-dashboard",
            claims: new[]
            {
                new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
                new Claim(JwtRegisteredClaimNames.UniqueName, user.Username),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N"))
            },
            expires: expires.UtcDateTime,
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));
        return (new JwtSecurityTokenHandler().WriteToken(token), expires);
    }
}

/// <summary>Resolves the authenticated dashboard user's UserContext (grants loaded fresh, short per-request cache).</summary>
public sealed class CurrentUserResolver(AdminRepository admin)
{
    public async Task<UserContext?> GetAsync(HttpContext ctx, CancellationToken ct)
    {
        if (ctx.Items.TryGetValue("UserContext", out var cached) && cached is UserContext existing)
            return existing;

        var sub = ctx.User.FindFirstValue(JwtRegisteredClaimNames.Sub)
                  ?? ctx.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (sub is null || !Guid.TryParse(sub, out var userId)) return null;

        var account = await admin.GetUserAsync(userId, ct);
        if (account is null || !account.IsActive) return null;

        var userContext = new UserContext
        {
            UserId = account.Id, Username = account.Username, Grants = account.Grants,
            MustChangePassword = account.MustChangePassword
        };
        ctx.Items["UserContext"] = userContext;
        return userContext;
    }
}
