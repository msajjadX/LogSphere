# Capture Roadmap — Closing the Collection Gaps

Design for the three capture-side gaps agreed as priorities, in build order:

1. **OTLP receiver** — accept OpenTelemetry from any language/agent (most leveraged)
2. **LogSphere.Tail** — a tiny tailing agent for file/stdout sources (legacy onboarding)
3. **Ingest enrichment + fine-grained absence alerts** — GeoIP, user-agent, dead-man switches

Non-negotiable principles carried through every item:

- **Identity always comes from the API key.** Tenant/project/application/environment are resolved
  server-side from `X-LogSphere-Key` (the existing `IngestContext`); nothing in any new payload
  format can override them.
- **Everything funnels into `IngestService.IngestAsync`.** New receivers are *translators to the
  envelope*, not parallel pipelines — so sanitization, size caps, rate limiting, durable queueing
  and the workers apply unchanged.
- **No new outbound network dependencies.** The deployment environment has restricted egress;
  every feature below works fully offline.

---

## 1. OTLP receiver

### Why first
One endpoint makes every OTel-instrumented application (Java, Go, Python, Node, .NET — often
auto-instrumented with zero code changes) a LogSphere source without writing per-language SDKs.
The proprietary envelope stays the native format; OTLP becomes a dialect.

### Surface

| | |
|---|---|
| Endpoints | `POST /api/v1/otlp/v1/logs`, `POST /api/v1/otlp/v1/traces` (OTLP/HTTP) |
| Content types | Phase A: `application/json` (OTLP/JSON) · Phase B: `application/x-protobuf` |
| Auth | `X-LogSphere-Key` header — identical to `/events`; exporters pass it via their `headers` option |
| Exporter config (app side) | `OTEL_EXPORTER_OTLP_ENDPOINT=https://logs.example/api/v1/otlp`<br>`OTEL_EXPORTER_OTLP_PROTOCOL=http/json` (Phase A)<br>`OTEL_EXPORTER_OTLP_HEADERS=X-LogSphere-Key=ls_xxx.yyy` |
| Response | OTLP `ExportLogsServiceResponse` / `ExportTraceServiceResponse` with `partialSuccess` populated from the existing `IngestResult.Rejected` list |
| gRPC | Out of scope (exporters all support http; Kestrel gRPC can be added later without design change) |

Phase A (OTLP/JSON) requires **zero new NuGet packages** — `System.Text.Json` over the documented
OTLP/JSON schema. Phase B adds `Google.Protobuf` + vendored OTLP `.proto` files; note the NuGet
restore constraint (offline cache) — vendor the generated C# instead of depending on
`OpenTelemetry.Proto` if the cache lacks it.

### Mapping: OTLP → LogSphere envelope

Resource attributes (per `ResourceLogs`/`ResourceSpans`, applied to every record in the batch):

| OTel semantic convention | Envelope field |
|---|---|
| `service.name` | `module` (fallback when record has none) |
| `service.version` | `applicationVersion` |
| `host.name` | `machineName` |
| `deployment.environment.name` | *ignored* (environment comes from the key) — logged into `properties.otel.env` for reference |
| all other resource attrs | `properties.resource.*` |

LogRecord:

| OTLP field | Envelope field |
|---|---|
| `timeUnixNano` | `eventTimestamp` |
| `severityNumber` 1–4 / 5–8 / 9–12 / 13–16 / 17–20 / 21–24 | `severity` Trace / Debug / Information / Warning / Error / Critical |
| `body` (string or any-value) | `message` (any-value JSON-stringified) |
| `traceId` / `spanId` | `traceId` / `spanId` |
| `attributes` | `properties.*` |
| attr `exception.type` / `exception.message` / `exception.stacktrace` | `exception.{type,message,stackTrace}` + `eventType = Exception` |
| attr `enduser.id` | `userId` |
| attr `session.id` | `sessionId` |
| attr `code.namespace` / `code.function` | `component` / `properties` |
| otherwise | `eventType = Application` |

Span (each span becomes one envelope):

| OTLP field | Envelope field |
|---|---|
| `name` | `actionName` |
| `endTimeUnixNano` | `eventTimestamp` (matches SDK semantics: timestamp = operation end) |
| `endTime - startTime` | `durationMs` |
| `traceId` / `spanId` / `parentSpanId` | same |
| `status.code == ERROR` | `severity = Error` (else `Information`), `status = Failed` (else `Completed`) |
| span events named `exception` | `exception.*` from the event's attributes |
| `http.request.method`, `http.route`, `http.response.status_code`, `client.address`, `user_agent.original` | `http.{method,route,statusCode,clientIp,userAgent}` |
| `db.system` + `db.statement` (CLIENT span) | `properties.db.*`; duration also copied to `dbDurationMs` |

Span kind → `eventType` defaults:

| SpanKind | eventType |
|---|---|
| SERVER | `ApiRequest` |
| CLIENT with `db.system` | `Performance` |
| CLIENT with `http.*` | `Integration` |
| PRODUCER / CONSUMER | `BackgroundJob` |
| INTERNAL | `Application` |

### Implementation shape

`OtlpEndpoints.cs` (new, in Api) + `OtlpTranslator.cs` (new, in Core, pure function
`ResourceBatch -> List<JsonObject>` returning ready envelopes). Endpoints authenticate exactly like
`IngestEndpoints.AuthenticateAsync`, translate, chunk to ≤500, call `IngestAsync`. Translator is
unit-testable with golden OTLP/JSON samples. Estimated effort: **Phase A ~2–3 days, Phase B ~1–2 days.**

---

## 2. LogSphere.Tail — single-binary tailing agent

### Why
Everything that will never call an API — legacy apps writing files, nginx/IIS access logs,
third-party software, `docker logs` — becomes ingestable. One small tool, installed next to the
files.

### Shape
Self-contained .NET binary (`dotnet publish -p:PublishAot`), no runtime install; runs as a Windows
service, systemd unit, console app, or Docker sidecar. Config is one YAML file:

```yaml
endpoint: https://logs.example
key: ${LOGSPHERE_KEY}            # env-var substitution; never in the file
state: C:\ProgramData\logsphere-tail\offsets.json
sources:
  - path: "C:/inetpub/logs/LogFiles/W3SVC1/*.log"
    parser: w3c                   # built-ins: json | w3c | regex | plain
    eventType: ApiRequest
    module: IIS
  - path: "D:/apps/legacy-payroll/logs/*.txt"
    parser: regex
    pattern: '^(?<ts>\S+ \S+) \[(?<sev>\w+)\] (?<msg>.*)$'
    timestampFormat: "yyyy-MM-dd HH:mm:ss"
    severityMap: { WARN: Warning, ERR: Error, FATAL: Critical }
    multilineStart: '^\d{4}-\d{2}-\d{2}'   # stack traces glue to the previous event
    eventType: Application
    module: LegacyPayroll
```

### Behaviors (all required for correctness)
- **Offsets file** — resume where it left off across restarts; per-file inode/creation-time identity
  so log **rotation** (rename + recreate) is detected and the new file starts at 0.
- **Multi-line stitching** — a line not matching `multilineStart` appends to the previous event's
  `message` (bounded at the envelope's 4 000-char cap).
- **Batching & resilience** — reuse the SDK pipeline semantics: 200-event batches, 2 s flush,
  retries with backoff, circuit breaker, bounded disk overflow buffer (JSONL). The agent shares
  code with `LogSphere.Sdk` where possible (`LogSphere.Sdk` already implements all of this).
- **Plain parser fallback** — `parser: plain` sends each line as `message` with receive-time
  timestamp and a fixed severity; zero configuration beyond the path.

Non-goals: no metrics collection, no Windows Event Log (later), no central agent management —
config is a file next to the binary. Estimated effort: **~1 week** including packaging.

---

## 3. Ingest enrichment + fine-grained absence alerts

### 3a. GeoIP + user-agent enrichment (server-side, offline)

Hook: end of `IngestService.BuildStoragePayload`, after sanitization (enrichment must never see
unsanitized payloads and must never fail ingestion — wrap in try/catch, best-effort).

- **GeoIP** — MaxMind **GeoLite2-City.mmdb** read via `MaxMind.Db` (single small package; the
  `.mmdb` is a local file — config `Enrichment:GeoIpDatabasePath`, hot-swappable, updated manually
  or by a cron; no egress needed). Skip private/RFC-1918 addresses. Output:
  `properties.geo = { country, countryCode, city }` — coarse only, no lat/lon (privacy).
- **User agent** — `UAParser` (offline regex db). Output:
  `properties.ua = { browser, browserVersion, os, device }`.
- Storage: `properties` jsonb — **no schema migration**. If country/browser later need to be
  first-class filter columns, add generated columns then; not now.
- UI: chips in the event detail drawer (`geo.country`, `ua.browser os`); dashboard breakdowns later.

Estimated effort: **~1–2 days** + the GeoLite2 download step documented in the manual
(free MaxMind account required — flag to the team).

### 3b. Absence alerts — extend `IngestSilence`

Already exists: `ConditionType = "IngestSilence"` triggers when a project/environment scope has
**zero events** in the window. Gap: it cannot watch one job or module, which is the actual
dead-man-switch use case ("nightly payroll interface logged nothing").

Extension (small):

- `alert_rules` migration: add nullable `module_filter text`, `action_filter text`,
  `min_count int default 0`.
- `AlertWorker.IngestSilence`: append `AND e.module ILIKE @mod` / `AND e.action_name ILIKE @act`
  when set; trigger when `count <= min_count` (default 0 keeps existing behavior byte-compatible).
- Alerts UI: two optional text fields + "expected at least N events" on the IngestSilence rule form.
- Message becomes actionable: *"No `NIGHTLY_SYNC` events from module `PayrollInterface` in the last
  1 440 min."*

Estimated effort: **~1 day** end to end.

---

## Build order & status

| Phase | Item | Effort | Status |
|---|---|---|---|
| 1 | OTLP/JSON receiver (logs + traces) | 2–3 d | ✅ Built — `OtlpTranslator` + `OtlpEndpoints`, 17 unit tests |
| 2 | Absence-alert extension (3b) | 1 d | ✅ Built — migration 005, worker filters + min_count, rule-form UI |
| 3 | GeoIP + UA enrichment (3a) | 1–2 d | ✅ Built — `EnrichmentService`; UA active now, GeoIP lights up when a GeoLite2-City.mmdb is configured (`GEOIP_DB_PATH` in deploy/.env) |
| 4 | LogSphere.Tail agent | ~1 w | ✅ Built — `backend/src/LogSphere.Tail`, single binary (`backend/dist-tail/logsphere-tail.exe`), sample config `deploy/tail.sample.yaml`; E2E-verified (resume, rotation, multiline) |
| 5 | OTLP protobuf content-type | 1–2 d | ✅ Built — `OtlpProtobuf` hand-written wire transcoder (zero deps); both encodings share the one translator; E2E-verified |

All five phases complete (2026-07-24). The receiver now accepts OTLP over http/protobuf (the
exporter default) and http/json; protobuf payloads are transcoded to the OTLP/JSON shape so both
paths exercise identical translation and ingest code.

Risks called out: NuGet offline cache must contain `MaxMind.Db` / `UAParser` / `Google.Protobuf`
before their phases start (mirror them while egress is available); GeoLite2 licensing needs a free
MaxMind account; the Tail agent needs a code-signing/deployment story for production servers.
