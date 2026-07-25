# LogSphere Database Design Notes

Authoritative DDL: [../db/migrations/001_schema.sql](../db/migrations/001_schema.sql) (applied
automatically at API startup, tracked in `schema_migrations`). Reference data + default redaction and
retention rules: `002_seed_reference.sql`.

## Entity groups

* **Tenancy**: `tenants → projects → applications` (+ `environments` reference). `application_credentials`
  stores only HMAC-SHA256 secret hashes; env-scoped, revocable, expirable, IP-allowlist, per-key rate limit.
* **AuthZ**: `users` (PBKDF2 password hashes) + `user_access_grants` (role + tenant/project/environment/
  category/severity scope + `can_view_bodies` / `can_export` / `can_view_security` flags).
* **Event store**: `log_events` — single hybrid envelope table for all 10 event categories.
* **Aggregates**: `exception_groups` (fingerprint rollup with workflow status).
* **Pipeline**: `ingest_queue` (durable queue), `dead_letter_events`.
* **Config**: `redaction_rules`, `retention_policies`, `alert_rules`, `alert_occurrences`,
  `notification_channels`, `saved_searches`.
* **Platform audit/health**: `access_audit` (dashboard logins/queries/exports/admin changes),
  `worker_heartbeats`.

## Why one hybrid event table

A single partitioned `log_events` table carries the common envelope in typed columns (fast filters)
plus four JSONB columns (`properties`, `request_data`, `response_data`, `exception_data`) for
variable payloads. This gives one write path, one partition/retention lifecycle, cross-category
correlation queries without UNIONs, and no fan-out joins — while `exception_groups` provides the
specialized aggregate the UI needs. This is the hybrid recommended in the product spec (§14).

## Partitioning

* RANGE partitions on `event_timestamp`, one per month (`log_events_YYYY_MM`), created by
  `ensure_log_partition()` — called at migration time and by the Retention worker (current + next month).
* `log_events_default` catches clock-skewed strays (ingestion also clamps timestamps to [now−30d, now+1h]).
* Retention = `DROP TABLE` of expired partitions (cheap); shorter per-category windows are enforced by
  batched row deletes inside the still-live partitions.
* Switching to daily partitions is a naming/function change only.

## Indexing strategy (per partition, no blanket indexing)

| Index | Serves |
|---|---|
| `(event_id, event_timestamp)` UNIQUE | Idempotent ingest (`ON CONFLICT DO NOTHING`) |
| `(project_id, event_timestamp DESC)` | Core explorer browse |
| `(id)` | Tail cursor / pagination |
| `(correlation_id)` partial | Flow reconstruction |
| `(trace_id)` partial | Trace viewer |
| `(business_entity_type, business_entity_id)` partial | Entity history |
| `(exception_fingerprint)` partial | Group occurrence lookups |
| `(severity, event_timestamp DESC) WHERE severity >= 4` | Error/critical dashboards |
| `(event_type, event_timestamp DESC)` | Category filters (audit explorer etc.) |
| GIN `properties jsonb_path_ops` | Custom-property containment search |
| GIN `message gin_trgm_ops` | Free-text search without wildcard scans |

## Integrity & security details

* Audit/Security rows are immutable: trigger `trg_logev_immutable` rejects UPDATE always and DELETE
  unless the session sets `logsphere.retention = 'on'` (used only by the Retention worker).
* All access paths use parameterized SQL; the dashboard's every event query is wrapped with a
  grant-derived predicate built server-side (`UserContext.BuildEventPredicate`).
* `ingest_queue` claims use `FOR UPDATE SKIP LOCKED` with a 5-minute visibility timeout and
  attempt counting; poison items move to `dead_letter_events` after 5 attempts.

## Capacity notes

Storage/day ≈ events/sec × 86,400 × avg event size (2–8 KB sanitized) × 1.4 (index overhead).
At 500 ev/s ≈ 120–350 GB/month before retention; monthly partitions keep pruning O(1).
Revisit PG-as-queue at sustained >2–3k ev/s (see DESIGN.md §13).

## PgBouncer caution

Do NOT point LogSphere at PgBouncer (or any transaction/session pooler) — connect the API
directly to PostgreSQL. The dashboard issues bursts of parallel queries that are cancelled when
users navigate; PostgreSQL cancel requests routed through a pooler can hit the wrong backend
session and corrupt another connection's protocol state (symptoms: `BindComplete while expecting
ReadyForQueryMessage`, sporadic `LOG-SERVER-001` on random endpoints). The API maintains its own
connection pool, so an external pooler adds no value here.
