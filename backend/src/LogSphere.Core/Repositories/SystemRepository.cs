using LogSphere.Core.Data;
using LogSphere.Core.Models;

namespace LogSphere.Core.Repositories;

public sealed class SystemRepository(Db db)
{
    public async Task HeartbeatAsync(string workerName, object? details, CancellationToken ct)
    {
        await using var cmd = db.Cmd("""
            INSERT INTO worker_heartbeats (name, last_heartbeat, details) VALUES ($1, now(), $2)
            ON CONFLICT (name) DO UPDATE SET last_heartbeat = now(), details = $2
            """);
        cmd.Parameters.AddWithValue(workerName);
        cmd.Parameters.Add(new Npgsql.NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Jsonb,
            Value = details is null ? DBNull.Value : System.Text.Json.JsonSerializer.Serialize(details)
        });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<List<WorkerStatus>> GetWorkerStatusesAsync(CancellationToken ct)
    {
        await using var cmd = db.Cmd("SELECT name, last_heartbeat FROM worker_heartbeats ORDER BY name");
        var list = new List<WorkerStatus>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var last = reader.GetFieldValue<DateTimeOffset>(1);
            list.Add(new WorkerStatus(reader.GetString(0), last, DateTimeOffset.UtcNow - last < TimeSpan.FromMinutes(2)));
        }
        return list;
    }

    /// <summary>Average (received - event) latency over the last 5 minutes, in ms.</summary>
    public async Task<double> AvgIngestLatencyMsAsync(CancellationToken ct)
    {
        await using var cmd = db.Cmd("""
            SELECT coalesce(avg(extract(epoch FROM (received_timestamp - event_timestamp)) * 1000), 0)
            FROM log_events
            WHERE received_timestamp >= now() - interval '5 minutes'
              AND received_timestamp >= event_timestamp
            """);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is double d ? Math.Round(d, 1) : 0;
    }

    /// <summary>Snapshot of what's going on inside PostgreSQL: size, connections, cache hit
    /// ratio, transactions, deadlocks, temp spill, longest running query, and the biggest
    /// tables with their bloat/vacuum state. Read-only catalog queries — cheap to run.</summary>
    public async Task<DbStats> GetDbStatsAsync(CancellationToken ct)
    {
        var stats = new DbStats();
        await using var conn = await db.OpenAsync(ct);

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT pg_database_size(current_database()) / 1048576.0 AS size_mb,
                       (SELECT setting::int FROM pg_settings WHERE name = 'max_connections') AS max_conn,
                       d.xact_commit, d.xact_rollback, d.deadlocks, d.temp_files,
                       d.temp_bytes / 1048576.0 AS temp_mb,
                       CASE WHEN d.blks_hit + d.blks_read > 0
                            THEN round(100.0 * d.blks_hit / (d.blks_hit + d.blks_read), 2)
                            ELSE 100 END AS cache_hit
                FROM pg_stat_database d WHERE d.datname = current_database()
                """;
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                stats.DatabaseSizeMb = Math.Round(reader.GetDouble(0), 1);
                stats.MaxConnections = reader.GetInt32(1);
                stats.TransactionsCommitted = reader.GetInt64(2);
                stats.TransactionsRolledBack = reader.GetInt64(3);
                stats.Deadlocks = reader.GetInt64(4);
                stats.TempFiles = reader.GetInt64(5);
                stats.TempBytesMb = Math.Round(reader.GetDouble(6), 1);
                stats.CacheHitRatio = (double)reader.GetDecimal(7);
            }
        }

        await using (var cmd = conn.CreateCommand())
        {
            // Split by path into the server. PgBouncer runs on the DB host, so its server
            // connections arrive from loopback / the server's own address (or a unix socket,
            // client_addr NULL); anything else is a direct port-5432 login. PgBouncer's own
            // admin console (SHOW POOLS) only speaks the simple query protocol, which Npgsql
            // does not — so this server-side view is how we tell the two apart.
            cmd.CommandText = """
                SELECT CASE WHEN client_addr IS NULL
                             OR client_addr::text IN ('127.0.0.1', '::1')
                             OR client_addr = inet_server_addr()
                        THEN 'pooled' ELSE 'direct' END AS via,
                       count(*) AS total,
                       count(*) FILTER (WHERE state = 'active') AS active,
                       count(*) FILTER (WHERE state LIKE 'idle%') AS idle,
                       count(*) FILTER (WHERE wait_event IS NOT NULL AND state = 'active') AS waiting,
                       coalesce(max(extract(epoch FROM (now() - query_start)))
                           FILTER (WHERE state = 'active' AND pid <> pg_backend_pid()), 0) AS longest
                FROM pg_stat_activity WHERE datname = current_database()
                GROUP BY 1
                """;
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var group = reader.GetString(0) == "pooled" ? stats.PooledConnections : stats.DirectConnections;
                group.Total = (int)reader.GetInt64(1);
                group.Active = (int)reader.GetInt64(2);
                group.Idle = (int)reader.GetInt64(3);
                group.Waiting = (int)reader.GetInt64(4);
                stats.LongestQuerySeconds = Math.Max(stats.LongestQuerySeconds, Math.Round(reader.GetDouble(5), 1));
            }
            stats.ConnectionsTotal = stats.PooledConnections.Total + stats.DirectConnections.Total;
            stats.ConnectionsActive = stats.PooledConnections.Active + stats.DirectConnections.Active;
            stats.ConnectionsIdle = stats.PooledConnections.Idle + stats.DirectConnections.Idle;
            stats.ConnectionsWaiting = stats.PooledConnections.Waiting + stats.DirectConnections.Waiting;
        }

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT relname,
                       pg_total_relation_size(relid) / 1048576.0 AS size_mb,
                       n_live_tup, n_dead_tup, last_autovacuum, last_autoanalyze
                FROM pg_stat_user_tables
                ORDER BY pg_total_relation_size(relid) DESC LIMIT 8
                """;
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                stats.TopTables.Add(new TableStat(
                    reader.GetString(0),
                    Math.Round(reader.GetDouble(1), 1),
                    reader.GetInt64(2),
                    reader.GetInt64(3),
                    reader.IsDBNull(4) ? null : reader.GetFieldValue<DateTimeOffset>(4),
                    reader.IsDBNull(5) ? null : reader.GetFieldValue<DateTimeOffset>(5)));
            }
        }

        return stats;
    }
}
