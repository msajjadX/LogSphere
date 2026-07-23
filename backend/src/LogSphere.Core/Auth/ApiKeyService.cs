using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using LogSphere.Core.Data;
using LogSphere.Core.Models;
using Microsoft.Extensions.Logging;

namespace LogSphere.Core.Auth;

/// <summary>Ingestion API-key management. Plaintext form: ls_&lt;keyId&gt;.&lt;secret&gt;.
/// Only HMAC-SHA256(secret, serverPepper) is stored. Includes a short-TTL validation cache
/// and per-key sliding-window rate limiting.</summary>
public sealed class ApiKeyService(Db db, ILogger<ApiKeyService> logger)
{
    private readonly byte[] _pepper = Encoding.UTF8.GetBytes(
        Environment.GetEnvironmentVariable("LOGSPHERE_KEY_PEPPER") ?? "logsphere-dev-pepper-change-in-prod");

    private readonly ConcurrentDictionary<string, (IngestContext Ctx, string Hash, DateTimeOffset CachedAt)> _cache = new();
    private readonly ConcurrentDictionary<string, (long WindowStart, int Count)> _rate = new();
    private long _authFailures;

    public long AuthFailures => Interlocked.Read(ref _authFailures);

    public string HashSecret(string secret) =>
        Convert.ToHexString(HMACSHA256.HashData(_pepper, Encoding.UTF8.GetBytes(secret))).ToLowerInvariant();

    /// <summary>Creates a credential; returns (credentialId, plaintextKey). Plaintext is shown once.</summary>
    public async Task<(Guid Id, string PlaintextKey)> CreateAsync(Guid applicationId, CreateCredentialRequest request, CancellationToken ct)
    {
        var keyId = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
        var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        var id = Guid.NewGuid();
        await using var cmd = db.Cmd("""
            INSERT INTO application_credentials
                (id, application_id, environment_id, key_id, secret_hash, ip_allowlist, rate_limit_per_minute, expires_at)
            VALUES ($1,$2,$3,$4,$5,$6,$7,$8)
            """);
        cmd.Parameters.AddWithValue(id);
        cmd.Parameters.AddWithValue(applicationId);
        cmd.Parameters.AddWithValue(request.EnvironmentId);
        cmd.Parameters.AddWithValue(keyId);
        cmd.Parameters.AddWithValue(HashSecret(secret));
        cmd.Parameters.AddWithValue((object?)request.IpAllowlist ?? DBNull.Value);
        cmd.Parameters.AddWithValue(request.RateLimitPerMinute);
        cmd.Parameters.AddWithValue((object?)request.ExpiresAt ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
        return (id, $"ls_{keyId}.{secret}");
    }

    public async Task RevokeAsync(Guid credentialId, CancellationToken ct)
    {
        await using var cmd = db.Cmd("UPDATE application_credentials SET revoked_at = now() WHERE id = $1");
        cmd.Parameters.AddWithValue(credentialId);
        await cmd.ExecuteNonQueryAsync(ct);
        _cache.Clear();
    }

    /// <summary>Validates "ls_keyId.secret" and resolves the full ingestion context, or null.</summary>
    public async Task<IngestContext?> ValidateAsync(string? apiKey, string? clientIp, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(apiKey) || !apiKey.StartsWith("ls_", StringComparison.Ordinal))
        { Interlocked.Increment(ref _authFailures); return null; }

        var body = apiKey[3..];
        var dot = body.IndexOf('.');
        if (dot <= 0) { Interlocked.Increment(ref _authFailures); return null; }
        var keyId = body[..dot];
        var secret = body[(dot + 1)..];
        var secretHash = HashSecret(secret);

        if (_cache.TryGetValue(keyId, out var cached) && DateTimeOffset.UtcNow - cached.CachedAt < TimeSpan.FromMinutes(2))
        {
            if (!CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(cached.Hash), Encoding.UTF8.GetBytes(secretHash)))
            { Interlocked.Increment(ref _authFailures); return null; }
            if (!CheckIpAndRate(cached.Ctx, keyId, clientIp)) return null;
            return cached.Ctx;
        }

        await using var cmd = db.Cmd("""
            SELECT c.id, c.secret_hash, c.environment_id, c.ip_allowlist, c.rate_limit_per_minute,
                   a.id AS app_id, a.code AS app_code, a.is_active AS app_active,
                   p.id AS project_id, p.code AS project_code, p.is_active AS project_active,
                   t.id AS tenant_id, t.is_active AS tenant_active
            FROM application_credentials c
            JOIN applications a ON a.id = c.application_id
            JOIN projects p ON p.id = a.project_id
            JOIN tenants t ON t.id = p.tenant_id
            WHERE c.key_id = $1
              AND c.revoked_at IS NULL
              AND (c.expires_at IS NULL OR c.expires_at > now())
            """);
        cmd.Parameters.AddWithValue(keyId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) { Interlocked.Increment(ref _authFailures); return null; }

        var storedHash = reader.Get<string>("secret_hash")!;
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(storedHash), Encoding.UTF8.GetBytes(secretHash)))
        { Interlocked.Increment(ref _authFailures); return null; }

        if (!reader.Get<bool>("app_active") || !reader.Get<bool>("project_active") || !reader.Get<bool>("tenant_active"))
        { Interlocked.Increment(ref _authFailures); return null; }

        var ctx = new IngestContext(
            reader.Get<Guid>("id"),
            reader.Get<Guid>("tenant_id"),
            reader.Get<Guid>("project_id"),
            reader.Get<Guid>("app_id"),
            reader.Get<short>("environment_id"),
            reader.Get<string>("project_code")!,
            reader.Get<string>("app_code")!,
            reader.Get<int>("rate_limit_per_minute"),
            reader.Get<string[]>("ip_allowlist"));
        await reader.DisposeAsync();

        _cache[keyId] = (ctx, storedHash, DateTimeOffset.UtcNow);
        _ = TouchLastUsedAsync(ctx.CredentialId);

        if (!CheckIpAndRate(ctx, keyId, clientIp)) return null;
        return ctx;
    }

    private bool CheckIpAndRate(IngestContext ctx, string keyId, string? clientIp)
    {
        if (ctx.IpAllowlist is { Length: > 0 } allowlist &&
            (clientIp is null || !allowlist.Contains(clientIp)))
        {
            logger.LogWarning("API key {KeyId} used from disallowed IP {Ip}", keyId, clientIp);
            Interlocked.Increment(ref _authFailures);
            return false;
        }

        var minute = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 60;
        var entry = _rate.AddOrUpdate(keyId,
            _ => (minute, 1),
            (_, current) => current.WindowStart == minute ? (minute, current.Count + 1) : (minute, 1));
        return entry.Count <= ctx.RateLimitPerMinute;
    }

    /// <summary>True when the last rate-limit check for this key exceeded the cap (checked after CheckIpAndRate).</summary>
    public bool IsRateLimited(string apiKey)
    {
        if (!apiKey.StartsWith("ls_")) return false;
        var keyId = apiKey[3..].Split('.')[0];
        if (!_rate.TryGetValue(keyId, out var entry)) return false;
        if (!_cache.TryGetValue(keyId, out var cached)) return false;
        return entry.Count > cached.Ctx.RateLimitPerMinute;
    }

    private async Task TouchLastUsedAsync(Guid credentialId)
    {
        try
        {
            await using var cmd = db.Cmd("UPDATE application_credentials SET last_used_at = now() WHERE id = $1");
            cmd.Parameters.AddWithValue(credentialId);
            await cmd.ExecuteNonQueryAsync();
        }
        catch (Exception ex) { logger.LogDebug(ex, "last_used_at update failed"); }
    }
}
