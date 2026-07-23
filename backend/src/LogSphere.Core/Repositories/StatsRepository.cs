using System.Text.Json.Nodes;
using LogSphere.Core.Auth;
using LogSphere.Core.Data;
using LogSphere.Core.Models;
using Npgsql;

namespace LogSphere.Core.Repositories;

/// <summary>Pre-aggregated dashboard statistics (stats_minute* tables, migration 004).
/// The persistence worker calls <see cref="IncrementAsync"/> for every newly inserted event;
/// the overview/timeseries endpoints read the rollups instead of scanning log_events.
/// Users whose grants filter at row level (categories / min severity / hidden Security
/// events) fall back to the raw-scan path — rollups cannot honor those filters.</summary>
public sealed class StatsRepository(Db db, EventRepository events)
{
    /// <summary>Histogram upper bounds in ms; the last bucket is open-ended.</summary>
    private static readonly double[] HistBounds = { 50, 100, 250, 500, 1000, 3000, 10000 };

    // ---------------------------------------------------------------- write path

    private sealed class MinuteAgg
    {
        public long Total;
        public readonly long[] Sev = new long[6];
        public long Requests, Slow, DurCount;
        public double DurSum;
        public readonly long[] Hist = new long[8];
    }

    /// <summary>Aggregates the batch in memory and applies additive upserts. Callers pass only
    /// events that were actually inserted this pass, so queue redeliveries don't double-count.</summary>
    public async Task IncrementAsync(IReadOnlyList<JsonObject> payloads, CancellationToken ct)
    {
        if (payloads.Count == 0) return;

        var core = new Dictionary<(DateTime, Guid, Guid, short), MinuteAgg>();
        var modules = new Dictionary<(DateTime, Guid, Guid, short, string), (long Total, long Errors)>();
        var routes = new Dictionary<(DateTime, Guid, Guid, short, string), (long Total, long Failures)>();

        foreach (var p in payloads)
        {
            string? S(string key) => p[key]?.GetValue<string?>();
            double? D(string key) => p[key] is JsonValue v && v.TryGetValue<double>(out var d) ? d : null;
            int? I(string key) => p[key] is JsonValue v && v.TryGetValue<int>(out var i) ? i : null;

            if (!Guid.TryParse(S("tenantId"), out var tenant) ||
                !Guid.TryParse(S("projectId"), out var project) ||
                !DateTime.TryParse(S("eventTimestamp"), null,
                    System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
                    out var ts))
                continue; // malformed — the event insert itself would have failed anyway

            var env = (short)(I("environmentId") ?? 0);
            var bucket = new DateTime(ts.Year, ts.Month, ts.Day, ts.Hour, ts.Minute, 0, DateTimeKind.Utc);
            var severity = Math.Clamp(I("severity") ?? 2, 0, 5);
            var duration = D("durationMs");
            var eventType = S("eventType");

            var key = (bucket, tenant, project, env);
            if (!core.TryGetValue(key, out var agg)) core[key] = agg = new MinuteAgg();
            agg.Total++;
            agg.Sev[severity]++;
            if (eventType == "ApiRequest") agg.Requests++;
            if (duration is not null)
            {
                agg.DurCount++;
                agg.DurSum += duration.Value;
                if (duration > 3000) agg.Slow++;
                var h = 0;
                while (h < HistBounds.Length && duration.Value > HistBounds[h]) h++;
                agg.Hist[h]++;
            }

            var module = S("module");
            if (!string.IsNullOrEmpty(module))
            {
                var mk = (bucket, tenant, project, env, module);
                var cur = modules.GetValueOrDefault(mk);
                modules[mk] = (cur.Total + 1, cur.Errors + (severity >= 4 ? 1 : 0));
            }

            var route = S("httpRoute");
            if (!string.IsNullOrEmpty(route))
            {
                var rk = (bucket, tenant, project, env, route);
                var cur = routes.GetValueOrDefault(rk);
                routes[rk] = (cur.Total + 1, cur.Failures + ((I("httpStatusCode") ?? 0) >= 500 ? 1 : 0));
            }
        }

        if (core.Count == 0) return;

        await using var conn = await db.OpenAsync(ct);
        await using var batch = new NpgsqlBatch(conn);

        foreach (var ((bucket, tenant, project, env), a) in core)
        {
            var cmd = new NpgsqlBatchCommand("""
                INSERT INTO stats_minute (bucket, tenant_id, project_id, environment_id, total,
                    s0, s1, s2, s3, s4, s5, request_count, slow_count, duration_sum, duration_count,
                    d50, d100, d250, d500, d1000, d3000, d10000, dinf)
                VALUES (@b, @t, @p, @e, @total, @s0, @s1, @s2, @s3, @s4, @s5, @req, @slow, @dsum, @dcnt,
                    @h0, @h1, @h2, @h3, @h4, @h5, @h6, @h7)
                ON CONFLICT (bucket, tenant_id, project_id, environment_id) DO UPDATE SET
                    total = stats_minute.total + EXCLUDED.total,
                    s0 = stats_minute.s0 + EXCLUDED.s0, s1 = stats_minute.s1 + EXCLUDED.s1,
                    s2 = stats_minute.s2 + EXCLUDED.s2, s3 = stats_minute.s3 + EXCLUDED.s3,
                    s4 = stats_minute.s4 + EXCLUDED.s4, s5 = stats_minute.s5 + EXCLUDED.s5,
                    request_count = stats_minute.request_count + EXCLUDED.request_count,
                    slow_count = stats_minute.slow_count + EXCLUDED.slow_count,
                    duration_sum = stats_minute.duration_sum + EXCLUDED.duration_sum,
                    duration_count = stats_minute.duration_count + EXCLUDED.duration_count,
                    d50 = stats_minute.d50 + EXCLUDED.d50, d100 = stats_minute.d100 + EXCLUDED.d100,
                    d250 = stats_minute.d250 + EXCLUDED.d250, d500 = stats_minute.d500 + EXCLUDED.d500,
                    d1000 = stats_minute.d1000 + EXCLUDED.d1000, d3000 = stats_minute.d3000 + EXCLUDED.d3000,
                    d10000 = stats_minute.d10000 + EXCLUDED.d10000, dinf = stats_minute.dinf + EXCLUDED.dinf
                """);
            cmd.Parameters.AddWithValue("b", bucket);
            cmd.Parameters.AddWithValue("t", tenant);
            cmd.Parameters.AddWithValue("p", project);
            cmd.Parameters.AddWithValue("e", env);
            cmd.Parameters.AddWithValue("total", a.Total);
            for (var s = 0; s < 6; s++) cmd.Parameters.AddWithValue($"s{s}", a.Sev[s]);
            cmd.Parameters.AddWithValue("req", a.Requests);
            cmd.Parameters.AddWithValue("slow", a.Slow);
            cmd.Parameters.AddWithValue("dsum", a.DurSum);
            cmd.Parameters.AddWithValue("dcnt", a.DurCount);
            for (var h = 0; h < 8; h++) cmd.Parameters.AddWithValue($"h{h}", a.Hist[h]);
            batch.BatchCommands.Add(cmd);
        }

        foreach (var ((bucket, tenant, project, env, module), v) in modules)
        {
            var cmd = new NpgsqlBatchCommand("""
                INSERT INTO stats_minute_module (bucket, tenant_id, project_id, environment_id, module, total, errors)
                VALUES (@b, @t, @p, @e, @m, @total, @errors)
                ON CONFLICT (bucket, tenant_id, project_id, environment_id, module) DO UPDATE SET
                    total = stats_minute_module.total + EXCLUDED.total,
                    errors = stats_minute_module.errors + EXCLUDED.errors
                """);
            cmd.Parameters.AddWithValue("b", bucket);
            cmd.Parameters.AddWithValue("t", tenant);
            cmd.Parameters.AddWithValue("p", project);
            cmd.Parameters.AddWithValue("e", env);
            cmd.Parameters.AddWithValue("m", module);
            cmd.Parameters.AddWithValue("total", v.Total);
            cmd.Parameters.AddWithValue("errors", v.Errors);
            batch.BatchCommands.Add(cmd);
        }

        foreach (var ((bucket, tenant, project, env, route), v) in routes)
        {
            var cmd = new NpgsqlBatchCommand("""
                INSERT INTO stats_minute_route (bucket, tenant_id, project_id, environment_id, http_route, total, failures)
                VALUES (@b, @t, @p, @e, @r, @total, @failures)
                ON CONFLICT (bucket, tenant_id, project_id, environment_id, http_route) DO UPDATE SET
                    total = stats_minute_route.total + EXCLUDED.total,
                    failures = stats_minute_route.failures + EXCLUDED.failures
                """);
            cmd.Parameters.AddWithValue("b", bucket);
            cmd.Parameters.AddWithValue("t", tenant);
            cmd.Parameters.AddWithValue("p", project);
            cmd.Parameters.AddWithValue("e", env);
            cmd.Parameters.AddWithValue("r", route);
            cmd.Parameters.AddWithValue("total", v.Total);
            cmd.Parameters.AddWithValue("failures", v.Failures);
            batch.BatchCommands.Add(cmd);
        }

        await batch.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Purges rollup rows older than the retention horizon (called by the retention worker).</summary>
    public async Task PurgeAsync(int retentionDays, CancellationToken ct)
    {
        foreach (var table in new[] { "stats_minute", "stats_minute_module", "stats_minute_route" })
        {
            await using var cmd = db.Cmd($"DELETE FROM {table} WHERE bucket < now() - make_interval(days => $1)");
            cmd.Parameters.AddWithValue(retentionDays);
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    // ---------------------------------------------------------------- read path

    private static string BuildWhere(NpgsqlCommand cmd, StatsRequest req, UserContext user, string alias)
    {
        var clauses = new List<string>
        {
            $"{alias}.bucket >= @sw_from",
            $"{alias}.bucket <= @sw_to",
            user.BuildEventPredicate(cmd, alias), // scope-only for rollup-eligible users
        };
        cmd.Parameters.AddWithValue("sw_from", (req.From ?? DateTimeOffset.UtcNow.AddHours(-24)).UtcDateTime);
        cmd.Parameters.AddWithValue("sw_to", (req.To ?? DateTimeOffset.UtcNow).UtcDateTime);
        if (req.ProjectId is not null)
        {
            clauses.Add($"{alias}.project_id = @sw_proj");
            cmd.Parameters.AddWithValue("sw_proj", req.ProjectId.Value);
        }
        if (req.EnvironmentId is not null)
        {
            clauses.Add($"{alias}.environment_id = @sw_env");
            cmd.Parameters.AddWithValue("sw_env", req.EnvironmentId.Value);
        }
        return string.Join(" AND ", clauses);
    }

    /// <summary>Cumulative-walk percentile estimate over the duration histogram.</summary>
    private static double? ApproxPercentile(long[] hist, long count, double q)
    {
        if (count == 0) return null;
        var target = q * count;
        long cum = 0;
        for (var i = 0; i < hist.Length; i++)
        {
            var prev = cum;
            cum += hist[i];
            if (cum >= target && hist[i] > 0)
            {
                var lower = i == 0 ? 0 : HistBounds[i - 1];
                var upper = i < HistBounds.Length ? HistBounds[i] : HistBounds[^1] * 1.5; // open bucket: conservative
                var frac = (target - prev) / hist[i];
                return Math.Round(lower + frac * (upper - lower), 1);
            }
        }
        return HistBounds[^1];
    }

    public async Task<StatsOverview> GetOverviewAsync(StatsRequest req, UserContext user, CancellationToken ct)
    {
        if (user.HasRowLevelFilters)
            return await events.GetOverviewAsync(req, user, ct); // rollups can't honor row-level filters

        var overview = new StatsOverview();
        var windowMinutes = Math.Max(1, ((req.To ?? DateTimeOffset.UtcNow) - (req.From ?? DateTimeOffset.UtcNow.AddHours(-24))).TotalMinutes);

        await using var conn = await db.OpenAsync(ct);

        await using (var cmd = conn.CreateCommand())
        {
            var where = BuildWhere(cmd, req, user, "s");
            // note: sum(bigint) yields numeric in PostgreSQL — cast back to bigint/float for the reader
            cmd.CommandText = $"""
                SELECT coalesce(sum(total), 0)::bigint AS total,
                       coalesce(sum(s0), 0)::bigint AS s0, coalesce(sum(s1), 0)::bigint AS s1, coalesce(sum(s2), 0)::bigint AS s2,
                       coalesce(sum(s3), 0)::bigint AS s3, coalesce(sum(s4), 0)::bigint AS s4, coalesce(sum(s5), 0)::bigint AS s5,
                       coalesce(sum(request_count), 0)::bigint AS requests,
                       coalesce(sum(slow_count), 0)::bigint AS slow,
                       coalesce(sum(duration_sum), 0)::float AS dsum,
                       coalesce(sum(duration_count), 0)::bigint AS dcnt,
                       coalesce(sum(d50), 0)::bigint AS h0, coalesce(sum(d100), 0)::bigint AS h1, coalesce(sum(d250), 0)::bigint AS h2,
                       coalesce(sum(d500), 0)::bigint AS h3, coalesce(sum(d1000), 0)::bigint AS h4, coalesce(sum(d3000), 0)::bigint AS h5,
                       coalesce(sum(d10000), 0)::bigint AS h6, coalesce(sum(dinf), 0)::bigint AS h7
                FROM stats_minute s WHERE {where}
                """;
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                overview.TotalEvents = reader.Get<long>("total");
                for (short s = 0; s <= 5; s++)
                    overview.SeverityCounts[Severities.Name(s)] = reader.Get<long>($"s{s}");
                overview.ErrorCount = overview.SeverityCounts[Severities.Name(4)] + overview.SeverityCounts[Severities.Name(5)];
                overview.WarningCount = overview.SeverityCounts[Severities.Name(3)];
                overview.ErrorRate = overview.TotalEvents > 0 ? Math.Round(100.0 * overview.ErrorCount / overview.TotalEvents, 2) : 0;
                overview.WarningRate = overview.TotalEvents > 0 ? Math.Round(100.0 * overview.WarningCount / overview.TotalEvents, 2) : 0;
                overview.SlowRequests = reader.Get<long>("slow");
                overview.RequestsPerMinute = Math.Round(reader.Get<long>("requests") / windowMinutes, 2);
                var dcnt = reader.Get<long>("dcnt");
                overview.AvgDurationMs = dcnt > 0 ? Math.Round(reader.Get<double>("dsum") / dcnt, 1) : null;
                var hist = new long[8];
                for (var h = 0; h < 8; h++) hist[h] = reader.Get<long>($"h{h}");
                overview.P95DurationMs = ApproxPercentile(hist, dcnt, 0.95);
                overview.P99DurationMs = ApproxPercentile(hist, dcnt, 0.99);
            }
        }

        await using (var cmd = conn.CreateCommand())
        {
            var where = BuildWhere(cmd, req, user, "s");
            cmd.CommandText = $"""
                SELECT coalesce(p.name, s.project_id::text) AS name,
                       sum(s.total)::bigint AS total, sum(s.s4 + s.s5)::bigint AS errors
                FROM stats_minute s LEFT JOIN projects p ON p.id = s.project_id
                WHERE {where}
                GROUP BY 1 ORDER BY errors DESC, total DESC LIMIT 5
                """;
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                overview.TopProjects.Add(new NamedCount(reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2)));
        }

        await using (var cmd = conn.CreateCommand())
        {
            var where = BuildWhere(cmd, req, user, "s");
            cmd.CommandText = $"""
                SELECT s.module, sum(s.total)::bigint AS total, sum(s.errors)::bigint AS errors
                FROM stats_minute_module s
                WHERE {where}
                GROUP BY 1 HAVING sum(s.errors) > 0
                ORDER BY errors DESC LIMIT 5
                """;
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                overview.TopModules.Add(new NamedCount(reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2)));
        }

        await using (var cmd = conn.CreateCommand())
        {
            var where = BuildWhere(cmd, req, user, "s");
            cmd.CommandText = $"""
                SELECT s.http_route, sum(s.total)::bigint AS total, sum(s.failures)::bigint AS failures
                FROM stats_minute_route s
                WHERE {where}
                GROUP BY 1 HAVING sum(s.failures) > 0
                ORDER BY failures DESC LIMIT 5
                """;
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var total = reader.GetInt64(1);
                var failures = reader.GetInt64(2);
                overview.TopFailingRoutes.Add(new RouteFailure(reader.GetString(0), total, failures,
                    total > 0 ? Math.Round(100.0 * failures / total, 2) : 0));
            }
        }

        // recent errors/critical + active exception groups are already cheap indexed lookups
        var criticalPage = await events.QueryAsync(new LogQueryFilter
        {
            From = req.From, To = req.To, ProjectId = req.ProjectId,
            Severities = new List<string> { "Error", "Critical" }, Limit = 10
        }, user, ct);
        overview.RecentCritical = criticalPage.Items;

        await using (var cmd = conn.CreateCommand())
        {
            var (tenantIds, projectIds) = user.VisibleScopes();
            var clauses = new List<string> { "status NOT IN ('Resolved','Ignored')", "last_seen >= @xfrom" };
            cmd.Parameters.AddWithValue("xfrom", (req.From ?? DateTimeOffset.UtcNow.AddHours(-24)).UtcDateTime);
            if (req.ProjectId is not null) { clauses.Add("project_id = @xp"); cmd.Parameters.AddWithValue("xp", req.ProjectId.Value); }
            if (!user.IsSuperAdmin && !(tenantIds is null && projectIds is null))
            {
                var scopeParts = new List<string>();
                if (tenantIds is not null) { scopeParts.Add("tenant_id = ANY(@xts)"); cmd.Parameters.AddWithValue("xts", tenantIds); }
                if (projectIds is not null) { scopeParts.Add("project_id = ANY(@xps)"); cmd.Parameters.AddWithValue("xps", projectIds); }
                if (scopeParts.Count > 0) clauses.Add("(" + string.Join(" OR ", scopeParts) + ")");
            }
            cmd.CommandText = $"SELECT count(*) FROM exception_groups WHERE {string.Join(" AND ", clauses)}";
            overview.ActiveExceptionGroups = (long)(await cmd.ExecuteScalarAsync(ct) ?? 0L);
        }

        return overview;
    }

    public async Task<List<TimeseriesPoint>> GetTimeseriesAsync(StatsRequest req, UserContext user, CancellationToken ct)
    {
        // fingerprint filtering (exception detail trends) needs raw rows
        if (user.HasRowLevelFilters || !string.IsNullOrWhiteSpace(req.Fingerprint))
            return await events.GetTimeseriesAsync(req, user, ct);

        var from = (req.From ?? DateTimeOffset.UtcNow.AddHours(-24)).UtcDateTime;
        var interval = Math.Clamp(req.IntervalMinutes, 1, 24 * 60);

        await using var conn = await db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        var where = BuildWhere(cmd, req, user, "s");
        cmd.Parameters.AddWithValue("origin", from);
        cmd.Parameters.AddWithValue("iv", TimeSpan.FromMinutes(interval));

        cmd.CommandText = req.Metric switch
        {
            "errors" => $"""
                SELECT date_bin(@iv, s.bucket, @origin) AS b, 'errors' AS series, sum(s.s4 + s.s5)::float AS value
                FROM stats_minute s WHERE {where}
                GROUP BY 1 HAVING sum(s.s4 + s.s5) > 0 ORDER BY 1
                """,
            "avgDuration" => $"""
                SELECT date_bin(@iv, s.bucket, @origin) AS b, 'avgDuration' AS series,
                       coalesce(sum(s.duration_sum) / nullif(sum(s.duration_count), 0), 0) AS value
                FROM stats_minute s WHERE {where}
                GROUP BY 1 HAVING sum(s.duration_count) > 0 ORDER BY 1
                """,
            _ => $"""
                SELECT b, series, value FROM (
                    SELECT date_bin(@iv, s.bucket, @origin) AS b, 'Error' AS series, sum(s.s4 + s.s5)::float AS value
                    FROM stats_minute s WHERE {where} GROUP BY 1 HAVING sum(s.s4 + s.s5) > 0
                    UNION ALL
                    SELECT date_bin(@iv, s.bucket, @origin) AS b, 'Warning' AS series, sum(s.s3)::float AS value
                    FROM stats_minute s WHERE {where} GROUP BY 1 HAVING sum(s.s3) > 0
                    UNION ALL
                    SELECT date_bin(@iv, s.bucket, @origin) AS b, 'Info' AS series, sum(s.s0 + s.s1 + s.s2)::float AS value
                    FROM stats_minute s WHERE {where} GROUP BY 1 HAVING sum(s.s0 + s.s1 + s.s2) > 0
                ) u ORDER BY b
                """
        };

        var points = new List<TimeseriesPoint>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            points.Add(new TimeseriesPoint(
                reader.GetFieldValue<DateTimeOffset>(0), reader.GetString(1), reader.GetDouble(2)));
        return points;
    }
}
