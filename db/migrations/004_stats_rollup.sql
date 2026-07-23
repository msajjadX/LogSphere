-- 004: pre-aggregated dashboard statistics.
--
-- The Overview dashboard and timeseries charts previously scanned log_events at
-- request time. These minutely rollups are incremented by the persistence
-- worker as events are written (StatsRepository.IncrementAsync), so dashboard
-- reads become cheap range scans over tiny tables regardless of log volume.
-- log_events remains the source of truth; rollups are advisory aggregates.

CREATE TABLE IF NOT EXISTS stats_minute (
    bucket          timestamptz NOT NULL,          -- event_timestamp truncated to the minute (UTC)
    tenant_id       uuid        NOT NULL,
    project_id      uuid        NOT NULL,
    environment_id  smallint    NOT NULL,
    total           bigint      NOT NULL DEFAULT 0,
    s0              bigint      NOT NULL DEFAULT 0,  -- Trace
    s1              bigint      NOT NULL DEFAULT 0,  -- Debug
    s2              bigint      NOT NULL DEFAULT 0,  -- Information
    s3              bigint      NOT NULL DEFAULT 0,  -- Warning
    s4              bigint      NOT NULL DEFAULT 0,  -- Error
    s5              bigint      NOT NULL DEFAULT 0,  -- Critical
    request_count   bigint      NOT NULL DEFAULT 0,  -- event_type = 'ApiRequest'
    slow_count      bigint      NOT NULL DEFAULT 0,  -- duration_ms > 3000
    duration_sum    double precision NOT NULL DEFAULT 0,
    duration_count  bigint      NOT NULL DEFAULT 0,
    -- duration histogram (cumulative-walk approximate percentiles); upper bounds in ms
    d50             bigint      NOT NULL DEFAULT 0,  -- <= 50
    d100            bigint      NOT NULL DEFAULT 0,  -- 50..100
    d250            bigint      NOT NULL DEFAULT 0,  -- 100..250
    d500            bigint      NOT NULL DEFAULT 0,  -- 250..500
    d1000           bigint      NOT NULL DEFAULT 0,  -- 500..1000
    d3000           bigint      NOT NULL DEFAULT 0,  -- 1000..3000
    d10000          bigint      NOT NULL DEFAULT 0,  -- 3000..10000
    dinf            bigint      NOT NULL DEFAULT 0,  -- > 10000
    PRIMARY KEY (bucket, tenant_id, project_id, environment_id)
);
CREATE INDEX IF NOT EXISTS ix_stats_minute_bucket ON stats_minute (bucket);

-- per-module error attribution ("Top failing modules")
CREATE TABLE IF NOT EXISTS stats_minute_module (
    bucket          timestamptz NOT NULL,
    tenant_id       uuid        NOT NULL,
    project_id      uuid        NOT NULL,
    environment_id  smallint    NOT NULL,
    module          text        NOT NULL,
    total           bigint      NOT NULL DEFAULT 0,
    errors          bigint      NOT NULL DEFAULT 0,  -- severity >= 4
    PRIMARY KEY (bucket, tenant_id, project_id, environment_id, module)
);
CREATE INDEX IF NOT EXISTS ix_stats_minute_module_bucket ON stats_minute_module (bucket);

-- per-route failure attribution ("Top failing routes")
CREATE TABLE IF NOT EXISTS stats_minute_route (
    bucket          timestamptz NOT NULL,
    tenant_id       uuid        NOT NULL,
    project_id      uuid        NOT NULL,
    environment_id  smallint    NOT NULL,
    http_route      text        NOT NULL,
    total           bigint      NOT NULL DEFAULT 0,
    failures        bigint      NOT NULL DEFAULT 0,  -- http_status_code >= 500
    PRIMARY KEY (bucket, tenant_id, project_id, environment_id, http_route)
);
CREATE INDEX IF NOT EXISTS ix_stats_minute_route_bucket ON stats_minute_route (bucket);

-- ---------------------------------------------------------------------------
-- Backfill from existing events (runs once — migrations are tracked).
-- ---------------------------------------------------------------------------

INSERT INTO stats_minute (bucket, tenant_id, project_id, environment_id, total,
    s0, s1, s2, s3, s4, s5, request_count, slow_count, duration_sum, duration_count,
    d50, d100, d250, d500, d1000, d3000, d10000, dinf)
SELECT date_trunc('minute', event_timestamp), tenant_id, project_id, environment_id,
    count(*),
    count(*) FILTER (WHERE severity = 0),
    count(*) FILTER (WHERE severity = 1),
    count(*) FILTER (WHERE severity = 2),
    count(*) FILTER (WHERE severity = 3),
    count(*) FILTER (WHERE severity = 4),
    count(*) FILTER (WHERE severity = 5),
    count(*) FILTER (WHERE event_type = 'ApiRequest'),
    count(*) FILTER (WHERE duration_ms > 3000),
    coalesce(sum(duration_ms), 0),
    count(duration_ms),
    count(*) FILTER (WHERE duration_ms <= 50),
    count(*) FILTER (WHERE duration_ms > 50    AND duration_ms <= 100),
    count(*) FILTER (WHERE duration_ms > 100   AND duration_ms <= 250),
    count(*) FILTER (WHERE duration_ms > 250   AND duration_ms <= 500),
    count(*) FILTER (WHERE duration_ms > 500   AND duration_ms <= 1000),
    count(*) FILTER (WHERE duration_ms > 1000  AND duration_ms <= 3000),
    count(*) FILTER (WHERE duration_ms > 3000  AND duration_ms <= 10000),
    count(*) FILTER (WHERE duration_ms > 10000)
FROM log_events
GROUP BY 1, 2, 3, 4
ON CONFLICT (bucket, tenant_id, project_id, environment_id) DO NOTHING;

INSERT INTO stats_minute_module (bucket, tenant_id, project_id, environment_id, module, total, errors)
SELECT date_trunc('minute', event_timestamp), tenant_id, project_id, environment_id, module,
    count(*), count(*) FILTER (WHERE severity >= 4)
FROM log_events
WHERE module IS NOT NULL
GROUP BY 1, 2, 3, 4, 5
ON CONFLICT (bucket, tenant_id, project_id, environment_id, module) DO NOTHING;

INSERT INTO stats_minute_route (bucket, tenant_id, project_id, environment_id, http_route, total, failures)
SELECT date_trunc('minute', event_timestamp), tenant_id, project_id, environment_id, http_route,
    count(*), count(*) FILTER (WHERE http_status_code >= 500)
FROM log_events
WHERE http_route IS NOT NULL
GROUP BY 1, 2, 3, 4, 5
ON CONFLICT (bucket, tenant_id, project_id, environment_id, http_route) DO NOTHING;
