using System.Text.Json.Nodes;
using LogSphere.Core.Data;
using LogSphere.Core.Models;
using Npgsql;
using NpgsqlTypes;

namespace LogSphere.Core.Repositories;

public sealed record QueueItem(long Id, Guid EventId, JsonObject Payload, int Attempts);

/// <summary>PostgreSQL-backed durable queue (FOR UPDATE SKIP LOCKED claim protocol).</summary>
public sealed class QueueRepository(Db db)
{
    public const int MaxAttempts = 5;
    public static readonly TimeSpan VisibilityTimeout = TimeSpan.FromMinutes(5);

    public async Task EnqueueAsync(IReadOnlyList<(Guid EventId, string PayloadJson)> items, CancellationToken ct)
    {
        if (items.Count == 0) return;
        // single multi-row insert: one round-trip regardless of batch size
        await using var cmd = db.Cmd("""
            INSERT INTO ingest_queue (event_id, payload)
            SELECT * FROM unnest($1::uuid[], $2::jsonb[])
            """);
        cmd.Parameters.AddWithValue(items.Select(i => i.EventId).ToArray());
        cmd.Parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Jsonb,
            Value = items.Select(i => i.PayloadJson).ToArray()
        });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<List<QueueItem>> ClaimBatchAsync(string workerName, int batchSize, CancellationToken ct)
    {
        await using var cmd = db.Cmd("""
            UPDATE ingest_queue q
            SET claimed_at = now(), claimed_by = $1, attempts = q.attempts + 1
            WHERE q.id IN (
                SELECT id FROM ingest_queue
                WHERE claimed_at IS NULL OR claimed_at < now() - $2::interval
                ORDER BY id
                LIMIT $3
                FOR UPDATE SKIP LOCKED)
            RETURNING q.id, q.event_id, q.payload, q.attempts
            """);
        cmd.Parameters.AddWithValue(workerName);
        cmd.Parameters.AddWithValue(VisibilityTimeout);
        cmd.Parameters.AddWithValue(batchSize);

        var items = new List<QueueItem>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var payloadJson = reader.GetString(2);
            if (JsonNode.Parse(payloadJson) is JsonObject obj)
                items.Add(new QueueItem(reader.GetInt64(0), reader.GetGuid(1), obj, reader.GetInt32(3)));
        }
        return items;
    }

    public async Task CompleteAsync(IReadOnlyList<long> ids, CancellationToken ct)
    {
        if (ids.Count == 0) return;
        await using var cmd = db.Cmd("DELETE FROM ingest_queue WHERE id = ANY($1)");
        cmd.Parameters.AddWithValue(ids.ToArray());
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Releases failed items for retry, or moves them to the dead-letter store after MaxAttempts.</summary>
    public async Task FailAsync(IReadOnlyList<QueueItem> items, string reason, CancellationToken ct)
    {
        foreach (var item in items)
        {
            if (item.Attempts >= MaxAttempts)
            {
                await using var conn = await db.OpenAsync(ct);
                await using var tx = await conn.BeginTransactionAsync(ct);
                await using (var dl = new NpgsqlCommand(
                    "INSERT INTO dead_letter_events (event_id, payload, reason, attempts) VALUES ($1,$2,$3,$4)", conn, tx))
                {
                    dl.Parameters.AddWithValue(item.EventId);
                    dl.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = item.Payload.ToJsonString() });
                    dl.Parameters.AddWithValue(reason);
                    dl.Parameters.AddWithValue(item.Attempts);
                    await dl.ExecuteNonQueryAsync(ct);
                }
                await using (var del = new NpgsqlCommand("DELETE FROM ingest_queue WHERE id = $1", conn, tx))
                {
                    del.Parameters.AddWithValue(item.Id);
                    await del.ExecuteNonQueryAsync(ct);
                }
                await tx.CommitAsync(ct);
            }
            else
            {
                await using var cmd = db.Cmd("UPDATE ingest_queue SET claimed_at = NULL, claimed_by = NULL WHERE id = $1");
                cmd.Parameters.AddWithValue(item.Id);
                await cmd.ExecuteNonQueryAsync(ct);
            }
        }
    }

    public async Task<long> DepthAsync(CancellationToken ct)
    {
        await using var cmd = db.Cmd("SELECT count(*) FROM ingest_queue");
        return (long)(await cmd.ExecuteScalarAsync(ct) ?? 0L);
    }

    public async Task<long> DeadLetterCountAsync(CancellationToken ct)
    {
        await using var cmd = db.Cmd("SELECT count(*) FROM dead_letter_events");
        return (long)(await cmd.ExecuteScalarAsync(ct) ?? 0L);
    }

    public async Task<List<DeadLetterEntry>> ListDeadLettersAsync(int limit, CancellationToken ct)
    {
        await using var cmd = db.Cmd(
            "SELECT id, event_id, payload, reason, attempts, failed_at FROM dead_letter_events ORDER BY id DESC LIMIT $1");
        cmd.Parameters.AddWithValue(limit);
        var list = new List<DeadLetterEntry>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(new DeadLetterEntry
            {
                Id = reader.GetInt64(0),
                EventId = reader.IsDBNull(1) ? null : reader.GetGuid(1),
                Payload = reader.IsDBNull(2) ? null : JsonNode.Parse(reader.GetString(2)),
                Reason = reader.GetString(3),
                Attempts = reader.GetInt32(4),
                FailedAt = reader.GetFieldValue<DateTimeOffset>(5)
            });
        }
        return list;
    }
}
