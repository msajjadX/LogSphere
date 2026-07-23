using System.Collections.Concurrent;
using LogSphere.Core.Data;
using LogSphere.Core.Models;

namespace LogSphere.Core.Sanitization;

/// <summary>Loads redaction rules (global + tenant + project) with a short cache.
/// Global rules always apply; project rules can only add, never remove protection.</summary>
public sealed class RulesProvider(Db db)
{
    private readonly ConcurrentDictionary<Guid, (RedactionRuleSet Rules, DateTimeOffset At)> _cache = new();
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(60);

    public async Task<RedactionRuleSet> GetForProjectAsync(Guid tenantId, Guid projectId, CancellationToken ct)
    {
        if (_cache.TryGetValue(projectId, out var cached) && DateTimeOffset.UtcNow - cached.At < Ttl)
            return cached.Rules;

        var rules = new List<RedactionRule>();
        await using var cmd = db.Cmd("""
            SELECT key_pattern, is_regex, strategy, applies_to
            FROM redaction_rules
            WHERE enabled
              AND (tenant_id IS NULL OR tenant_id = $1)
              AND (project_id IS NULL OR project_id = $2)
            """);
        cmd.Parameters.AddWithValue(tenantId);
        cmd.Parameters.AddWithValue(projectId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rules.Add(new RedactionRule
            {
                KeyPattern = reader.GetString(0),
                IsRegex = reader.GetBoolean(1),
                Strategy = reader.GetString(2),
                AppliesTo = reader.GetString(3),
                Enabled = true
            });
        }

        // defense in depth: defaults always active even if DB rules were emptied
        var set = new RedactionRuleSet(rules.Concat(RedactionRuleSet.DefaultRules()));
        _cache[projectId] = (set, DateTimeOffset.UtcNow);
        return set;
    }
}
