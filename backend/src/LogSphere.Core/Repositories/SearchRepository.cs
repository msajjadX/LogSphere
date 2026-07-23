using System.Text.Json.Nodes;
using LogSphere.Core.Data;
using LogSphere.Core.Models;
using Npgsql;
using NpgsqlTypes;

namespace LogSphere.Core.Repositories;

public sealed class SearchRepository(Db db)
{
    public async Task<List<SavedSearch>> ListAsync(Guid userId, CancellationToken ct)
    {
        await using var cmd = db.Cmd("""
            SELECT id, user_id, name, filter::text, shared, pinned, created_at
            FROM saved_searches
            WHERE user_id = $1 OR shared
            ORDER BY pinned DESC, name
            """);
        cmd.Parameters.AddWithValue(userId);
        var list = new List<SavedSearch>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            list.Add(new SavedSearch
            {
                Id = reader.GetGuid(0), UserId = reader.GetGuid(1), Name = reader.GetString(2),
                Filter = JsonNode.Parse(reader.GetString(3)),
                Shared = reader.GetBoolean(4), Pinned = reader.GetBoolean(5),
                CreatedAt = reader.GetFieldValue<DateTimeOffset>(6)
            });
        return list;
    }

    public async Task<SavedSearch> UpsertAsync(SavedSearch s, CancellationToken ct)
    {
        if (s.Id == Guid.Empty) s.Id = Guid.NewGuid();
        await using var cmd = db.Cmd("""
            INSERT INTO saved_searches (id, user_id, name, filter, shared, pinned) VALUES ($1,$2,$3,$4,$5,$6)
            ON CONFLICT (id) DO UPDATE SET name=$3, filter=$4, shared=$5, pinned=$6
            WHERE saved_searches.user_id = $2
            """);
        cmd.Parameters.AddWithValue(s.Id);
        cmd.Parameters.AddWithValue(s.UserId);
        cmd.Parameters.AddWithValue(s.Name);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = s.Filter?.ToJsonString() ?? "{}" });
        cmd.Parameters.AddWithValue(s.Shared);
        cmd.Parameters.AddWithValue(s.Pinned);
        await cmd.ExecuteNonQueryAsync(ct);
        return s;
    }

    public async Task DeleteAsync(Guid id, Guid userId, CancellationToken ct)
    {
        await using var cmd = db.Cmd("DELETE FROM saved_searches WHERE id = $1 AND user_id = $2");
        cmd.Parameters.AddWithValue(id);
        cmd.Parameters.AddWithValue(userId);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
