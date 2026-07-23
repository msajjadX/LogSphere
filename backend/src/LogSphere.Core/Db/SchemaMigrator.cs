using LogSphere.Core.Data;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace LogSphere.Core.Data;

/// <summary>Applies db/migrations/*.sql in filename order, tracked in schema_migrations.
/// A PostgreSQL advisory lock makes this safe when multiple API replicas start at once:
/// one instance migrates while the others wait, then see everything already applied.</summary>
public sealed class SchemaMigrator(Db db, ILogger<SchemaMigrator> logger)
{
    /// <summary>Arbitrary but fixed application-wide lock id for "LogSphere schema migration".</summary>
    private const long MigrationLockId = 727_001_001;

    public async Task MigrateAsync(string migrationsDirectory, CancellationToken ct = default)
    {
        // one dedicated connection holds the advisory lock for the whole run;
        // session-level locks are released automatically when the connection closes
        await using var conn = await db.OpenAsync(ct);

        logger.LogInformation("Acquiring schema migration lock…");
        await using (var lockCmd = new NpgsqlCommand("SELECT pg_advisory_lock($1)", conn))
        {
            lockCmd.Parameters.AddWithValue(MigrationLockId);
            lockCmd.CommandTimeout = 300; // wait up to 5 min for another replica to finish
            await lockCmd.ExecuteNonQueryAsync(ct);
        }

        try
        {
            await using (var create = new NpgsqlCommand(
                "CREATE TABLE IF NOT EXISTS schema_migrations (name text PRIMARY KEY, applied_at timestamptz NOT NULL DEFAULT now())",
                conn))
            {
                await create.ExecuteNonQueryAsync(ct);
            }

            var applied = new HashSet<string>();
            await using (var read = new NpgsqlCommand("SELECT name FROM schema_migrations", conn))
            await using (var reader = await read.ExecuteReaderAsync(ct))
            {
                while (await reader.ReadAsync(ct)) applied.Add(reader.GetString(0));
            }

            foreach (var file in Directory.GetFiles(migrationsDirectory, "*.sql").OrderBy(f => f, StringComparer.Ordinal))
            {
                var name = Path.GetFileName(file);
                if (applied.Contains(name)) continue;
                logger.LogInformation("Applying migration {Migration}", name);
                var sql = await File.ReadAllTextAsync(file, ct);
                await using var tx = await conn.BeginTransactionAsync(ct);
                await using (var cmd = new NpgsqlCommand(sql, conn, tx))
                {
                    cmd.CommandTimeout = 600;
                    await cmd.ExecuteNonQueryAsync(ct);
                }
                await using (var mark = new NpgsqlCommand("INSERT INTO schema_migrations(name) VALUES ($1)", conn, tx))
                {
                    mark.Parameters.AddWithValue(name);
                    await mark.ExecuteNonQueryAsync(ct);
                }
                await tx.CommitAsync(ct);
            }
        }
        finally
        {
            await using var unlock = new NpgsqlCommand("SELECT pg_advisory_unlock($1)", conn);
            unlock.Parameters.AddWithValue(MigrationLockId);
            await unlock.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }
}
