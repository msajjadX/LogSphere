using Npgsql;

namespace LogSphere.Core.Data;

/// <summary>Thin wrapper over a pooled NpgsqlDataSource.</summary>
public sealed class Db : IAsyncDisposable
{
    public NpgsqlDataSource DataSource { get; }

    public Db(string connectionString)
    {
        // Keepalives + short idle lifetime so stale pooled connections (e.g. after a
        // database restart) are detected and pruned instead of failing user requests.
        var csb = new NpgsqlConnectionStringBuilder(connectionString)
        {
            TcpKeepAlive = true,
            KeepAlive = 30,
            ConnectionIdleLifetime = 60,
            ConnectionPruningInterval = 10,
            // dashboard page loads fire ~8 parallel queries; keep a warm floor so bursts
            // never depend on a batch of brand-new physical connections
            MinPoolSize = 10
        };
        var builder = new NpgsqlDataSourceBuilder(csb.ConnectionString);
        builder.EnableDynamicJson();
        DataSource = builder.Build();
    }

    public ValueTask<NpgsqlConnection> OpenAsync(CancellationToken ct = default) => DataSource.OpenConnectionAsync(ct);

    /// <summary>Opens N parallel connections once (then returns them to the pool) so the first
    /// user-facing burst after startup hits a warm pool. Best-effort; failures are swallowed.</summary>
    public async Task WarmUpAsync(int connections = 10, CancellationToken ct = default)
    {
        try
        {
            var opened = await Task.WhenAll(Enumerable.Range(0, connections).Select(async _ =>
            {
                try { return await DataSource.OpenConnectionAsync(ct); }
                catch { return null; }
            }));
            foreach (var conn in opened)
            {
                if (conn is not null) await conn.DisposeAsync();
            }
        }
        catch { /* warmup must never block startup */ }
    }

    public NpgsqlCommand Cmd(string sql) => DataSource.CreateCommand(sql);

    public async Task<bool> IsHealthyAsync(CancellationToken ct = default)
    {
        try
        {
            await using var cmd = Cmd("SELECT 1");
            await cmd.ExecuteScalarAsync(ct);
            return true;
        }
        catch { return false; }
    }

    public ValueTask DisposeAsync() => DataSource.DisposeAsync();
}

public static class NpgsqlExtensions
{
    public static NpgsqlParameter P(this NpgsqlCommand cmd, string name, object? value)
    {
        var p = cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return p;
    }

    public static T? Get<T>(this NpgsqlDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        if (reader.IsDBNull(ordinal)) return default;
        var value = reader.GetValue(ordinal);
        if (value is T t) return t;
        return (T)Convert.ChangeType(value, Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T));
    }
}
