using System.Text.Json.Nodes;
using LogSphere.Core.Models;

namespace LogSphere.Api.Infrastructure;

/// <summary>
/// Server-to-server client for the SupportHub integration API. The integration secret lives only
/// here — it must never reach the browser, a bundle, or a URL: a holder can mint a launch token
/// naming any user of this project.
/// </summary>
public sealed class SupportHubClient(IHttpClientFactory httpFactory, IConfiguration config, ILogger<SupportHubClient> logger)
{
    public sealed record Result(bool Ok, string Code, string Message, JsonNode? Data);

    private string? BaseUrl => config["SupportHub:BaseUrl"] is { Length: > 0 } url ? url.TrimEnd('/') : null;
    private string? Secret => config["SupportHub:IntegrationSecret"] is { Length: > 0 } secret ? secret : null;

    /// <summary>Where the embed SDK and widget iframe are served from; falls back to the gateway origin.</summary>
    public string? WidgetUrl => config["SupportHub:WidgetUrl"] is { Length: > 0 } url ? url.TrimEnd('/') : BaseUrl;

    public bool IsConfigured => BaseUrl is not null && Secret is not null;

    public Task<Result> SyncUserAsync(UserAccount account, CancellationToken ct) =>
        PostAsync("/integration/v1/users/sync", new
        {
            externalUserId = account.Id.ToString(),
            email = account.Email,
            displayName = string.IsNullOrWhiteSpace(account.DisplayName) ? account.Username : account.DisplayName,
            isActive = account.IsActive,
        }, ct);

    public Task<Result> MintLaunchTokenAsync(Guid userId, JsonObject context, CancellationToken ct) =>
        PostAsync("/integration/v1/launch-tokens", new
        {
            externalUserId = userId.ToString(),
            context,
        }, ct);

    /// <summary>
    /// Best-effort sync for user-lifecycle hooks (create/rename/deactivate): SupportHub being
    /// down or unconfigured must never fail LogSphere's own admin operation. Deactivation matters
    /// most — isActive:false revokes that person's support access immediately.
    /// </summary>
    public async Task TrySyncUserAsync(UserAccount account, CancellationToken ct)
    {
        try
        {
            if (!IsConfigured || string.IsNullOrWhiteSpace(account.Email)) return;
            var result = await SyncUserAsync(account, ct);
            if (!result.Ok)
                logger.LogWarning("SupportHub user sync failed for {UserId}: {Code} {Message}",
                    account.Id, result.Code, result.Message);
        }
        catch (Exception ex)
        {
            // Runs fire-and-forget from admin endpoints — nothing may propagate.
            logger.LogWarning(ex, "SupportHub user sync failed for {UserId}", account.Id);
        }
    }

    private async Task<Result> PostAsync(string path, object body, CancellationToken ct)
    {
        if (!IsConfigured)
            return new Result(false, "LOG-SUPPORT-000", "SupportHub is not configured.", null);

        try
        {
            var client = httpFactory.CreateClient("supporthub");
            client.Timeout = TimeSpan.FromSeconds(10);

            using var request = new HttpRequestMessage(HttpMethod.Post, BaseUrl + path)
            {
                Content = JsonContent.Create(body),
            };
            request.Headers.Add("Authorization", $"Bearer {Secret}");
            request.Headers.Add("X-SupportHub-Timestamp", DateTime.UtcNow.ToString("O"));
            // Per request, not per session or retry: SupportHub rejects a reused nonce with 409.
            request.Headers.Add("X-SupportHub-Nonce", Guid.NewGuid().ToString("N"));

            using var response = await client.SendAsync(request, ct);
            var raw = await response.Content.ReadAsStringAsync(ct);

            JsonNode? envelope = null;
            try { envelope = JsonNode.Parse(raw); }
            catch { /* non-JSON body; fall through to the transport-level result */ }

            // Business outcomes ride HTTP 200 with { ok, code, message, data }; transport-level
            // refusals (401/409/429) carry { error }. Both must be read — a 200 can still be a "no".
            if (envelope is JsonObject obj && obj.ContainsKey("ok"))
            {
                return new Result(
                    obj["ok"]?.GetValue<bool>() ?? false,
                    obj["code"]?.GetValue<string>() ?? ((int)response.StatusCode).ToString(),
                    obj["message"]?.GetValue<string>() ?? "",
                    obj["data"]);
            }

            var error = (envelope?["error"]?.GetValue<string>())
                        ?? $"SupportHub returned HTTP {(int)response.StatusCode}.";
            return new Result(false, ((int)response.StatusCode).ToString(), error, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(ex, "SupportHub request to {Path} failed", path);
            return new Result(false, "LOG-SUPPORT-NET", "Could not reach SupportHub.", null);
        }
    }
}
