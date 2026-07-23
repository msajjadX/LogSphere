# LogSphere API Contract v1

Base URL: `/api/v1`. All responses use the platform envelope:

```json
{ "success": true, "statusCode": "LOG-200", "statusDetails": "OK", "data": {}, "correlationId": "..." }
```

Errors: `success:false`, `statusCode` like `LOG-VALIDATION-001`, `LOG-AUTH-001`, `LOG-FORBIDDEN-001`,
`LOG-NOTFOUND-001`, `LOG-RATELIMIT-001`, `LOG-SERVER-001`; proper HTTP codes (400/401/403/404/413/429/500/503).
`X-Correlation-ID` accepted and always echoed (header + envelope).

## Authentication

* **Ingestion** (machine): header `X-LogSphere-Key: ls_<keyId>.<secret>`. Key resolves tenant/project/application/environment server-side.
* **Dashboard/Management** (human): `Authorization: Bearer <JWT>` from `POST /auth/login`.

---

## 1. Ingestion (API-key auth)

### POST /events — single event · POST /events/batch — `{ "events": [ ... ] }` (≤500 / 5 MB)

Event envelope (fields marked ⊙ are resolved/overridden server-side from the API key):

```json
{
  "schemaVersion": "1.0",
  "eventId": "0197f3a2-...-guid",           // client-generated GUID; idempotency key (optional; server generates if absent)
  "eventType": "ApiRequest|ApiResponse|Audit|Exception|Performance|Integration|BackgroundJob|Security|Custom|Application",
  "eventTimestamp": "2026-07-18T10:00:00Z",
  "severity": "Trace|Debug|Information|Warning|Error|Critical",
  "status": "Started|InProgress|Completed|Failed|Retrying|Cancelled|TimedOut|PartiallyCompleted",  // optional
  "correlationId": "…", "traceId": "…", "spanId": "…", "parentSpanId": "…", "sequence": 3,
  "module": "Payments", "component": "ReconciliationService",
  "message": "Payment reconciled",
  "actionName": "PaymentReconciled",
  "userId": "u-123", "sessionId": "…", "userName": "…",
  "businessEntityType": "Payment", "businessEntityId": "PAY-991",
  "http": { "method":"POST", "route":"/api/payments", "statusCode":200, "clientIp":"10.0.0.1", "userAgent":"…",
            "requestSize":812, "responseSize":233 },
  "durationMs": 152.7, "dbDurationMs": 40.1, "externalDurationMs": 60.0,
  "machineName": "pod-7", "applicationVersion": "2.4.1", "deploymentVersion": "rel-2026-07-01",
  "requestData": { }, "responseData": { },
  "exception": { "type":"NullReferenceException", "message":"…", "stackTrace":"…", "errorCode":"PAY-500",
                 "className":"ReconServiceImpl", "methodName":"Reconcile", "fileName":"Recon.cs", "lineNumber":88,
                 "innerExceptions": [ { "type":"…", "message":"…" } ] },
  "properties": { "any": "project-specific JSON" }
}
```

Response 202: `data: { "accepted": 12, "rejected": [{ "index": 3, "reason": "…" }] }`.

## 2. Auth

* `POST /auth/login` `{username,password}` → `{ token, expiresAt, user:{ id, username, displayName, roles:[...], grants:[...] } }`
* `GET /auth/me` → same user object. `POST /auth/change-password` `{currentPassword,newPassword}`.

## 3. Query (JWT; all results auto-filtered by the caller's grants)

* `POST /query/logs` — body:
  ```json
  { "from":"…","to":"…","tenantId":null,"projectId":null,"applicationId":null,"environmentId":null,
    "module":null,"component":null,"eventTypes":[],"severities":[],"statuses":[],
    "correlationId":null,"traceId":null,"userId":null,
    "businessEntityType":null,"businessEntityId":null,"actionName":null,
    "httpRoute":null,"httpStatusCode":null,"exceptionType":null,"fingerprint":null,
    "applicationVersion":null,"machineName":null,"text":null,
    "afterId":null, "limit":100, "order":"desc" }
  ```
  → `data: { items:[LogEventSummary], nextCursor }`. Summary: envelope columns w/o bodies.
* `GET /query/logs/{eventId}` → full detail (bodies only if `can_view_bodies`).
* `POST /query/logs/tail` — same filter, returns events with `id > afterId` (poll every 2–3 s for live tail).
* `GET /query/traces/{traceId}` → `{ spans:[{spanId,parentSpanId,name,start,durationMs,eventType,severity,eventId}], events:[...] }`
* `GET /query/correlations/{correlationId}` → all events in flow.
* `POST /query/stats/overview` `{from,to,projectId?}` → totals, errorRate, warningRate, avgDurationMs, p95DurationMs, p99DurationMs, requestsPerMinute, activeExceptionGroups, topProjects[], topFailingRoutes[], topModules[], recentCritical[], severityCounts{}, queueDepth, ingestionHealthy.
* `POST /query/stats/timeseries` `{from,to,projectId?,intervalMinutes,metric:"count|errors|avgDuration"}` → `[{bucket,value}]` (grouped by severity for count).
* `POST /export/logs` — filter + `{format:"csv|json", limit≤50000}` → file download (audited; redaction enforced; requires `can_export`).

## 4. Exceptions

* `POST /exceptions/groups/search` `{from,to,projectId?,status?,text?,limit}` → groups: `{id,fingerprint,exceptionType,message,module,projectId,projectName,firstSeen,lastSeen,totalCount,lastHourCount,last24hCount,status,assignedToUserId,assignedToName,sampleEventId,affectedVersions[]}`
* `GET /exceptions/groups/{id}` → group + trend buckets + recent occurrences (event summaries).
* `PATCH /exceptions/groups/{id}` `{status?, assignedToUserId?, notes?, linkedIssueUrl?}` (status: New|Investigating|Identified|Resolved|Ignored|Recurring).

## 5. Audit Explorer

* `POST /query/audit` `{from,to,projectId?,actorUserId?,actionName?,entityType?,entityId?,limit,afterId}` → audit events incl. `oldValues,newValues,changedFields,reason,sourceIp` (subject to grants).

## 6. Alerts

* `GET/POST /alerts/rules`, `GET/PUT/DELETE /alerts/rules/{id}` — rule: `{id,name,enabled,tenantId,projectId?,environmentId?,conditionType:"ErrorCountThreshold|ErrorRatePercent|SameFingerprintCount|DurationP95Threshold|SeverityDetected|AuthFailureCount|IngestSilence|QueueDepth",threshold,windowMinutes,cooldownMinutes,severityFilter?,channels:[channelId]}`
* `GET /alerts/occurrences?state=open|all&projectId=` → `{id,ruleId,ruleName,triggeredAt,state:"Open|Acknowledged|Resolved",summary,details,projectId}`
* `POST /alerts/occurrences/{id}/ack` · `POST /alerts/occurrences/{id}/resolve`
* `GET/POST /alerts/channels`, `PUT/DELETE /alerts/channels/{id}` — `{id,name,type:"Email|Webhook",target,enabled}`

## 7. Saved Searches

* `GET/POST /searches`, `PUT/DELETE /searches/{id}` — `{id,name,filter:<logs filter json>,shared,pinned}`

## 8. Admin (role-gated)

* Tenants `GET/POST /admin/tenants`, `PUT /admin/tenants/{id}` — `{id,name,code,isActive}`
* Projects `GET/POST /admin/projects`, `PUT /admin/projects/{id}` — `{id,tenantId,name,code,description,isActive}`
* Applications `GET/POST /admin/applications`, `PUT /admin/applications/{id}` — `{id,projectId,name,code,isActive}`
* Environments `GET /admin/environments` (Production/Staging/Development/Testing seeded)
* Credentials `GET /admin/applications/{appId}/credentials`, `POST …/credentials {environmentId,expiresAt?,ipAllowlist?}` → **plaintext key returned once**; `POST /admin/credentials/{id}/revoke`
* Users `GET/POST /admin/users`, `PUT /admin/users/{id}`, `POST /admin/users/{id}/reset-password`; grants embedded: `[{role,tenantId,projectId?,environmentId?,categories?,minSeverity?,canViewBodies,canExport,canViewSecurity}]`
* Redaction rules `GET/POST /admin/redaction-rules`, `PUT/DELETE …/{id}` — `{id,tenantId?,projectId?,keyPattern,isRegex,strategy:"Remove|Redact|MaskLast4|Hash",appliesTo:"All|Request|Response|Properties|Headers",enabled}`
* Retention `GET/POST /admin/retention-policies`, `PUT/DELETE …/{id}` — `{id,tenantId?,projectId?,environmentId?,eventType?,severity?,retentionDays,archiveBeforeDrop,legalHold}`
* `GET /admin/audit-access` — dashboard access/export audit trail.

## 9. System

* `GET /health` (anonymous liveness) · `GET /health/ready`
* `GET /system/metrics` (JWT) → `{queueDepth,deadLetterCount,eventsPerSecond,avgIngestLatencyMs,failedWrites,sanitizationFailures,authFailures,dbHealthy,workers:[{name,lastHeartbeat,healthy}],storage:[{partition,sizeMb,rows}]}`
* `GET /system/dead-letters?limit=` (admin) → safe diagnostics of quarantined events.

## Seeded data (dev)

Users: `admin/Admin#12345` (SuperAdmin). Demo tenant `our-company`, projects `etea-exams`, `restaurant-erp`
with applications + one Development credential each (printed by seeder to console/log for demo ingestion).
