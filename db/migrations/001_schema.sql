-- LogSphere schema v1  (PostgreSQL 15+)
-- Applied automatically by the API's SchemaMigrator (tracked in schema_migrations).

CREATE EXTENSION IF NOT EXISTS pg_trgm;

-- ============================================================ reference data
CREATE TABLE IF NOT EXISTS environments (
    id          smallint PRIMARY KEY,
    name        text NOT NULL UNIQUE
);

CREATE TABLE IF NOT EXISTS severities (
    id          smallint PRIMARY KEY,
    name        text NOT NULL UNIQUE
);

-- ============================================================ tenancy
CREATE TABLE IF NOT EXISTS tenants (
    id          uuid PRIMARY KEY,
    name        text NOT NULL,
    code        text NOT NULL UNIQUE,
    is_active   boolean NOT NULL DEFAULT true,
    created_at  timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS projects (
    id          uuid PRIMARY KEY,
    tenant_id   uuid NOT NULL REFERENCES tenants(id),
    name        text NOT NULL,
    code        text NOT NULL,
    description text,
    is_active   boolean NOT NULL DEFAULT true,
    created_at  timestamptz NOT NULL DEFAULT now(),
    UNIQUE (tenant_id, code)
);

CREATE TABLE IF NOT EXISTS applications (
    id          uuid PRIMARY KEY,
    project_id  uuid NOT NULL REFERENCES projects(id),
    name        text NOT NULL,
    code        text NOT NULL,
    is_active   boolean NOT NULL DEFAULT true,
    created_at  timestamptz NOT NULL DEFAULT now(),
    UNIQUE (project_id, code)
);

-- API keys: plaintext form "ls_<key_id>.<secret>"; only HMAC-SHA256(secret) stored.
CREATE TABLE IF NOT EXISTS application_credentials (
    id              uuid PRIMARY KEY,
    application_id  uuid NOT NULL REFERENCES applications(id),
    environment_id  smallint NOT NULL REFERENCES environments(id),
    key_id          text NOT NULL UNIQUE,
    secret_hash     text NOT NULL,
    ip_allowlist    text[],
    rate_limit_per_minute int NOT NULL DEFAULT 6000,
    expires_at      timestamptz,
    revoked_at      timestamptz,
    created_at      timestamptz NOT NULL DEFAULT now(),
    last_used_at    timestamptz
);
CREATE INDEX IF NOT EXISTS ix_credentials_app ON application_credentials(application_id);

-- ============================================================ users & authz
CREATE TABLE IF NOT EXISTS users (
    id                   uuid PRIMARY KEY,
    username             text NOT NULL UNIQUE,
    display_name         text NOT NULL,
    email                text,
    password_hash        text NOT NULL,           -- PBKDF2: iter.salt.hash (base64)
    is_active            boolean NOT NULL DEFAULT true,
    must_change_password boolean NOT NULL DEFAULT false,
    created_at           timestamptz NOT NULL DEFAULT now(),
    last_login_at        timestamptz
);

CREATE TABLE IF NOT EXISTS user_access_grants (
    id              uuid PRIMARY KEY,
    user_id         uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    role            text NOT NULL CHECK (role IN
        ('SuperAdmin','TenantAdmin','ProjectAdmin','Developer','Support',
         'SecurityAuditor','ComplianceAuditor','ReadOnly')),
    tenant_id       uuid REFERENCES tenants(id),
    project_id      uuid REFERENCES projects(id),
    environment_id  smallint REFERENCES environments(id),
    categories      text[],            -- null = all event types
    min_severity    smallint,          -- null = all severities
    can_view_bodies boolean NOT NULL DEFAULT false,
    can_export      boolean NOT NULL DEFAULT false,
    can_view_security boolean NOT NULL DEFAULT false
);
CREATE INDEX IF NOT EXISTS ix_grants_user ON user_access_grants(user_id);

-- ============================================================ configuration
CREATE TABLE IF NOT EXISTS redaction_rules (
    id          uuid PRIMARY KEY,
    tenant_id   uuid REFERENCES tenants(id),      -- null = global
    project_id  uuid REFERENCES projects(id),     -- null = tenant/global scope
    key_pattern text NOT NULL,
    is_regex    boolean NOT NULL DEFAULT false,
    strategy    text NOT NULL CHECK (strategy IN ('Remove','Redact','MaskLast4','Hash')),
    applies_to  text NOT NULL DEFAULT 'All' CHECK (applies_to IN ('All','Request','Response','Properties','Headers')),
    enabled     boolean NOT NULL DEFAULT true,
    created_at  timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS retention_policies (
    id              uuid PRIMARY KEY,
    tenant_id       uuid REFERENCES tenants(id),
    project_id      uuid REFERENCES projects(id),
    environment_id  smallint REFERENCES environments(id),
    event_type      text,                          -- null = all
    severity        smallint,                      -- null = all
    retention_days  int NOT NULL CHECK (retention_days > 0),
    archive_before_drop boolean NOT NULL DEFAULT false,
    legal_hold      boolean NOT NULL DEFAULT false,
    created_at      timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS notification_channels (
    id          uuid PRIMARY KEY,
    tenant_id   uuid REFERENCES tenants(id),
    name        text NOT NULL,
    type        text NOT NULL CHECK (type IN ('Email','Webhook')),
    target      text NOT NULL,                     -- email address or webhook URL
    enabled     boolean NOT NULL DEFAULT true
);

CREATE TABLE IF NOT EXISTS alert_rules (
    id               uuid PRIMARY KEY,
    tenant_id        uuid NOT NULL REFERENCES tenants(id),
    project_id       uuid REFERENCES projects(id),
    environment_id   smallint REFERENCES environments(id),
    name             text NOT NULL,
    enabled          boolean NOT NULL DEFAULT true,
    condition_type   text NOT NULL CHECK (condition_type IN
        ('ErrorCountThreshold','ErrorRatePercent','SameFingerprintCount','DurationP95Threshold',
         'SeverityDetected','AuthFailureCount','IngestSilence','QueueDepth')),
    threshold        numeric NOT NULL,
    window_minutes   int NOT NULL DEFAULT 5,
    cooldown_minutes int NOT NULL DEFAULT 15,
    severity_filter  smallint,
    channels         uuid[] NOT NULL DEFAULT '{}',
    created_at       timestamptz NOT NULL DEFAULT now(),
    last_triggered_at timestamptz
);

CREATE TABLE IF NOT EXISTS alert_occurrences (
    id              uuid PRIMARY KEY,
    rule_id         uuid NOT NULL REFERENCES alert_rules(id) ON DELETE CASCADE,
    project_id      uuid,
    dedup_key       text NOT NULL,
    triggered_at    timestamptz NOT NULL DEFAULT now(),
    state           text NOT NULL DEFAULT 'Open' CHECK (state IN ('Open','Acknowledged','Resolved')),
    summary         text NOT NULL,
    details         jsonb,
    acknowledged_by uuid, acknowledged_at timestamptz,
    resolved_by     uuid, resolved_at timestamptz
);
CREATE INDEX IF NOT EXISTS ix_alert_occ_state ON alert_occurrences(state, triggered_at DESC);

CREATE TABLE IF NOT EXISTS saved_searches (
    id          uuid PRIMARY KEY,
    user_id     uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    name        text NOT NULL,
    filter      jsonb NOT NULL,
    shared      boolean NOT NULL DEFAULT false,
    pinned      boolean NOT NULL DEFAULT false,
    created_at  timestamptz NOT NULL DEFAULT now()
);

-- ============================================================ exception grouping
CREATE TABLE IF NOT EXISTS exception_groups (
    id               uuid PRIMARY KEY,
    fingerprint      text NOT NULL,
    tenant_id        uuid NOT NULL,
    project_id       uuid NOT NULL,
    exception_type   text,
    message          text,
    module           text,
    first_seen       timestamptz NOT NULL,
    last_seen        timestamptz NOT NULL,
    total_count      bigint NOT NULL DEFAULT 0,
    status           text NOT NULL DEFAULT 'New' CHECK (status IN
                        ('New','Investigating','Identified','Resolved','Ignored','Recurring')),
    assigned_to      uuid REFERENCES users(id),
    notes            text,
    linked_issue_url text,
    sample_event_id  uuid,
    affected_versions text[] NOT NULL DEFAULT '{}',
    UNIQUE (project_id, fingerprint)
);
CREATE INDEX IF NOT EXISTS ix_exgroups_lastseen ON exception_groups(project_id, last_seen DESC);

-- ============================================================ durable ingest queue
CREATE TABLE IF NOT EXISTS ingest_queue (
    id          bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    event_id    uuid NOT NULL,
    payload     jsonb NOT NULL,        -- fully sanitized, context-resolved envelope
    enqueued_at timestamptz NOT NULL DEFAULT now(),
    claimed_at  timestamptz,
    claimed_by  text,
    attempts    int NOT NULL DEFAULT 0
);
CREATE INDEX IF NOT EXISTS ix_queue_claim ON ingest_queue(claimed_at NULLS FIRST, id);

CREATE TABLE IF NOT EXISTS dead_letter_events (
    id          bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    event_id    uuid,
    payload     jsonb,
    reason      text NOT NULL,
    attempts    int NOT NULL DEFAULT 0,
    failed_at   timestamptz NOT NULL DEFAULT now()
);

-- ============================================================ platform audit & health
CREATE TABLE IF NOT EXISTS access_audit (
    id          bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    user_id     uuid,
    action      text NOT NULL,          -- Login, Query, ViewEvent, Export, AdminChange...
    details     jsonb,
    ip          text,
    occurred_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS ix_access_audit_time ON access_audit(occurred_at DESC);

CREATE TABLE IF NOT EXISTS worker_heartbeats (
    name           text PRIMARY KEY,
    last_heartbeat timestamptz NOT NULL,
    details        jsonb
);

-- ============================================================ the event store
CREATE TABLE IF NOT EXISTS log_events (
    id                    bigint GENERATED ALWAYS AS IDENTITY,
    event_id              uuid NOT NULL,
    tenant_id             uuid NOT NULL,
    project_id            uuid NOT NULL,
    application_id        uuid NOT NULL,
    environment_id        smallint NOT NULL,
    module                text,
    component             text,
    event_type            text NOT NULL CHECK (event_type IN
        ('ApiRequest','ApiResponse','Audit','Exception','Performance','Integration',
         'BackgroundJob','Security','Custom','Application')),
    severity              smallint NOT NULL,           -- 0 Trace .. 5 Critical
    status                text CHECK (status IN
        ('Started','InProgress','Completed','Failed','Retrying','Cancelled','TimedOut','PartiallyCompleted')),
    event_timestamp       timestamptz NOT NULL,
    received_timestamp    timestamptz NOT NULL DEFAULT now(),
    correlation_id        text,
    trace_id              text,
    span_id               text,
    parent_span_id        text,
    sequence              int,
    user_id               text,
    session_id            text,
    user_name             text,
    business_entity_type  text,
    business_entity_id    text,
    action_name           text,
    message               text,
    duration_ms           double precision,
    db_duration_ms        double precision,
    external_duration_ms  double precision,
    http_method           text,
    http_route            text,
    http_status_code      int,
    client_ip             text,
    user_agent            text,
    request_size          int,
    response_size         int,
    machine_name          text,
    application_version   text,
    deployment_version    text,
    exception_type        text,
    exception_message     text,
    exception_fingerprint text,
    error_code            text,
    properties            jsonb,
    request_data          jsonb,
    response_data         jsonb,
    exception_data        jsonb,
    sanitization_applied  boolean NOT NULL DEFAULT false,
    sanitized_field_count int NOT NULL DEFAULT 0,
    truncated             boolean NOT NULL DEFAULT false,
    schema_version        text NOT NULL DEFAULT '1.0'
) PARTITION BY RANGE (event_timestamp);

-- idempotency: same event resent lands on the same (event_id, event_timestamp)
CREATE UNIQUE INDEX IF NOT EXISTS ux_logev_eventid ON log_events(event_id, event_timestamp);
CREATE INDEX IF NOT EXISTS ix_logev_proj_time   ON log_events(project_id, event_timestamp DESC);
CREATE INDEX IF NOT EXISTS ix_logev_id          ON log_events(id);
CREATE INDEX IF NOT EXISTS ix_logev_corr        ON log_events(correlation_id) WHERE correlation_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS ix_logev_trace       ON log_events(trace_id) WHERE trace_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS ix_logev_entity      ON log_events(business_entity_type, business_entity_id)
    WHERE business_entity_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS ix_logev_fp          ON log_events(exception_fingerprint)
    WHERE exception_fingerprint IS NOT NULL;
CREATE INDEX IF NOT EXISTS ix_logev_sev_time    ON log_events(severity, event_timestamp DESC) WHERE severity >= 4;
CREATE INDEX IF NOT EXISTS ix_logev_type_time   ON log_events(event_type, event_timestamp DESC);
CREATE INDEX IF NOT EXISTS ix_logev_props_gin   ON log_events USING gin (properties jsonb_path_ops);
CREATE INDEX IF NOT EXISTS ix_logev_msg_trgm    ON log_events USING gin (message gin_trgm_ops);

-- default partition catches clock-skewed strays; health check alarms if it grows
CREATE TABLE IF NOT EXISTS log_events_default PARTITION OF log_events DEFAULT;

-- helper used by SchemaMigrator and the Retention worker
CREATE OR REPLACE FUNCTION ensure_log_partition(p_month date) RETURNS text AS $$
DECLARE
    p_start date := date_trunc('month', p_month)::date;
    p_end   date := (date_trunc('month', p_month) + interval '1 month')::date;
    p_name  text := 'log_events_' || to_char(p_start, 'YYYY_MM');
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_class WHERE relname = p_name) THEN
        EXECUTE format('CREATE TABLE %I PARTITION OF log_events FOR VALUES FROM (%L) TO (%L)',
                       p_name, p_start, p_end);
    END IF;
    RETURN p_name;
END $$ LANGUAGE plpgsql;

SELECT ensure_log_partition(now()::date);
SELECT ensure_log_partition((now() + interval '1 month')::date);

-- audit & security events are immutable
-- Immutable except for the retention worker, which sets logsphere.retention = 'on'
-- for policy-driven deletion (UPDATE is never allowed for audit/security rows).
CREATE OR REPLACE FUNCTION prevent_audit_mutation() RETURNS trigger AS $$
BEGIN
    IF OLD.event_type IN ('Audit','Security')
       AND (TG_OP = 'UPDATE' OR coalesce(current_setting('logsphere.retention', true), '') <> 'on') THEN
        RAISE EXCEPTION 'audit/security log events are immutable';
    END IF;
    IF TG_OP = 'DELETE' THEN RETURN OLD; END IF;
    RETURN NEW;
END $$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS trg_logev_immutable ON log_events;
CREATE TRIGGER trg_logev_immutable
    BEFORE UPDATE OR DELETE ON log_events
    FOR EACH ROW EXECUTE FUNCTION prevent_audit_mutation();
