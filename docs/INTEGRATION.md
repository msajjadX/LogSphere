# LogSphere Integration Guide

> **Purpose:** give this document to a developer or an AI coding assistant to integrate any
> application with the LogSphere centralized logging platform. It is self-contained — no other
> documentation is required.
>
> **⚠ BEFORE YOU START — ask the user for these values (never invent or hardcode guesses):**
> 1. **API key** for this application (format `ls_xxxxxxxx.xxxxxxxx...`). Each application has its
>    own key, created in the LogSphere dashboard under Admin → Applications → Credentials.
> 2. **Base URL** of the LogSphere server (default: `http://localhost:8080`).
>
> Store both in the application's configuration (environment variable / appsettings / .env) —
> **never commit the API key to source control.**

---

## 1. How it works

Your application sends structured JSON events to one HTTP endpoint. LogSphere authenticates the
API key (which identifies your tenant/project/application/environment — you never send those),
sanitizes sensitive fields server-side, queues durably, and makes events searchable in the
dashboard within ~1 second. Sending logs must **never block or crash the host application**:
always send asynchronously, in batches, and swallow/queue failures locally.

## 2. The endpoint

```
POST {BASE_URL}/api/v1/events/batch
Headers:
  Content-Type: application/json
  X-LogSphere-Key: {API_KEY}
  X-Correlation-ID: {current correlation id}   (optional but recommended)

Body: { "events": [ <event>, <event>, ... ] }    // max 500 events / 5 MB per batch
```

Single-event variant: `POST {BASE_URL}/api/v1/events` with one event object as the body.

**Response** `202 Accepted`:
```json
{ "success": true, "statusCode": "LOG-202", "statusDetails": "...", 
  "data": { "accepted": 3, "rejected": [] }, "correlationId": "..." }
```
Errors: `401` invalid/revoked key · `400` malformed events (see `data.rejected[].reason`) ·
`413` batch too large · `429` rate limited (back off) · `5xx` server issue (retry with backoff).

## 3. The event object

Only `eventType` is required; send whatever fields are relevant. Unknown fields are ignored.

```jsonc
{
  "eventType": "ApiRequest",          // REQUIRED — see §4 for the 10 types
  "severity": "Information",          // Trace | Debug | Information | Warning | Error | Critical (default Information)
  "eventTimestamp": "2026-07-18T10:00:00Z",  // ISO-8601 UTC; defaults to server time
  "eventId": "a1b2c3d4-...",          // client GUID; makes retries idempotent (recommended)
  "status": "Completed",              // Started|InProgress|Completed|Failed|Retrying|Cancelled|TimedOut|PartiallyCompleted

  "message": "Human-readable summary of what happened",
  "module": "Payments",               // logical module inside your app
  "component": "ReconciliationService",
  "actionName": "PaymentReconciled",  // machine-friendly action/event name

  // --- correlation (see §5) ---
  "correlationId": "req-8f14e45f",    // same value for all events of one request/workflow
  "traceId": "…", "spanId": "…", "parentSpanId": "…",

  // --- who / what ---
  "userId": "u-1001", "userName": "Ali Khan", "sessionId": "…",
  "businessEntityType": "Invoice",    // lets users search all history of one business record
  "businessEntityId": "INV-2026-991",

  // --- HTTP context (for ApiRequest / ApiResponse) ---
  "http": { "method": "POST", "route": "/api/invoices", "statusCode": 200,
            "clientIp": "10.0.0.5", "userAgent": "…", "requestSize": 812, "responseSize": 233 },

  // --- timing ---
  "durationMs": 152.7, "dbDurationMs": 40.1, "externalDurationMs": 60.0,

  // --- environment info ---
  "machineName": "srv-01", "applicationVersion": "2.4.1", "deploymentVersion": "rel-45",

  // --- payloads (JSON objects; server redacts sensitive keys automatically) ---
  "requestData":  { },                // request body / input (≤64 KB)
  "responseData": { },                // response body / output (≤64 KB)
  "properties":   { "any": "custom structured data" },   // (≤32 KB)

  // --- for eventType = Exception ---
  "exception": {
    "type": "NullReferenceException", "message": "…", "stackTrace": "…",
    "errorCode": "PAY-500", "className": "ReconService", "methodName": "Reconcile",
    "fileName": "Recon.cs", "lineNumber": 88,
    "innerExceptions": [ { "type": "…", "message": "…" } ]
  }
}
```

## 4. Event types — when to use which

| eventType | Use for | Key fields to include |
|---|---|---|
| `ApiRequest` | An incoming HTTP request arrived | `http.method`, `http.route`, `correlationId`, `requestData` |
| `ApiResponse` | The request finished | `http.statusCode`, `durationMs`, `status`, same `correlationId` |
| `Exception` | Any caught/unhandled error | `exception{...}`, `severity: "Error"` or `"Critical"` |
| `Audit` | A user changed business data (immutable trail) | `actionName`, `userId`, `businessEntityType/Id`, `requestData` = **old values**, `responseData` = **new values**, `properties.changedFields`, `properties.reason` |
| `Performance` | A measured operation | `actionName`, `durationMs`, `dbDurationMs` |
| `Integration` | Call to an external service/API | `actionName` = service+endpoint, `durationMs`, `http.statusCode`, `properties.retryCount` |
| `BackgroundJob` | Scheduled/queued job run | `actionName` = job name, `status`, `durationMs`, `properties.jobRunId` |
| `Security` | Login failures, invalid tokens, lockouts, permission changes | `actionName`, `userId`, `http.clientIp`, `severity: "Warning"`+ |
| `Custom` | Domain events ("ExamStarted", "PaymentReconciled") | `actionName`, `businessEntityType/Id`, `properties` |
| `Application` | General app log lines (bridge from your logger) | `message`, `severity`, `module` |

**Minimum useful integration:** ApiRequest + ApiResponse per HTTP request, Exception for every
error, Audit for every business-data change. Add the rest as needed.

## 5. Correlation — the most important habit

Pick (or accept from the caller via the `X-Correlation-ID` request header) **one correlation id
per incoming request/workflow**, and put that same value in the `correlationId` field of **every
event that request produces** (request, response, exceptions, audits, integration calls). Pass it
onward to downstream services in the `X-Correlation-ID` header. This is what lets LogSphere show
the complete story of one request with one click.

## 6. Sending rules (implement these!)

1. **Never block the request path.** Queue events in memory; a background task sends batches
   (e.g., every 2 s or every 100–200 events, whichever first).
2. **Never crash on logging failure.** Catch all send errors; retry with exponential backoff
   (e.g., 0.5 s → 1 s → 2 s, max 3 tries), then drop the batch (optionally spill to a local file).
3. **Bound the queue** (e.g., 10,000 events); drop oldest when full.
4. **Timeout small** (≤5 s per HTTP call).
5. **Do not send secrets.** LogSphere redacts common sensitive keys (password, token, cvv, card
   number, cnic, otp…) server-side, but do not rely on it: skip request bodies for auth/payment
   endpoints entirely.
6. **Do not send binary data** (files, images) — send metadata only (name, size, MIME, hash).

## 7. Examples

### curl (smoke test)

```bash
curl -X POST "{BASE_URL}/api/v1/events/batch" \
  -H "Content-Type: application/json" \
  -H "X-LogSphere-Key: {API_KEY}" \
  -d '{"events":[{"eventType":"Application","severity":"Information","message":"integration smoke test","module":"Setup"}]}'
```

### .NET (ASP.NET Core) — preferred: the LogSphere SDK

If the LogSphere SDK project/package (`LogSphere.Sdk`) is available, use it — it implements
everything in §5 and §6 automatically:

```csharp
builder.Services.AddCentralLogging(options =>
{
    options.Endpoint = configuration["LogSphere:Endpoint"];   // {BASE_URL}
    options.ProjectKey = configuration["LogSphere:Key"];      // {API_KEY}
    options.ApplicationName = "My API";
    options.Environment = "Production";
});
// early in the pipeline:
app.UseLogSphere();   // correlation + auto request/response + exception capture

// audit example:
await auditLogger.LogAsync("InvoiceApproved", "Invoice", invoiceId,
    oldValues: before, newValues: after, reason: "Manager approval");

// custom event:
activityLogger.LogEvent("PaymentReconciled", new { gateway, amount },
    entityType: "Payment", entityId: paymentId);
```

Without the SDK, implement a small sender per §6 posting to `/api/v1/events/batch`.

### Node.js / JavaScript (no dependency)

```js
const QUEUE = [];
const BASE_URL = process.env.LOGSPHERE_URL;   // ask user
const API_KEY  = process.env.LOGSPHERE_KEY;   // ask user

function logEvent(e) {                        // call from anywhere; never throws
  if (QUEUE.length < 10000) QUEUE.push({ eventTimestamp: new Date().toISOString(), ...e });
}

setInterval(async () => {
  if (QUEUE.length === 0) return;
  const events = QUEUE.splice(0, 200);
  try {
    await fetch(`${BASE_URL}/api/v1/events/batch`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', 'X-LogSphere-Key': API_KEY },
      body: JSON.stringify({ events }),
      signal: AbortSignal.timeout(5000),
    });
  } catch { QUEUE.unshift(...events.slice(0, 10000 - QUEUE.length)); } // retry next tick
}, 2000);

// usage in an Express middleware:
app.use((req, res, next) => {
  const correlationId = req.get('X-Correlation-ID') || crypto.randomUUID().replace(/-/g, '');
  res.set('X-Correlation-ID', correlationId);
  const start = Date.now();
  logEvent({ eventType: 'ApiRequest', correlationId, module: 'Http',
             message: `${req.method} ${req.path}`,
             http: { method: req.method, route: req.path, clientIp: req.ip, userAgent: req.get('user-agent') } });
  res.on('finish', () => logEvent({
    eventType: 'ApiResponse', correlationId, durationMs: Date.now() - start,
    severity: res.statusCode >= 500 ? 'Error' : res.statusCode >= 400 ? 'Warning' : 'Information',
    status: res.statusCode >= 400 ? 'Failed' : 'Completed',
    message: `${res.statusCode} ${req.method} ${req.path}`,
    http: { method: req.method, route: req.path, statusCode: res.statusCode } }));
  next();
});
```

### Python

```python
import atexit, threading, queue, uuid, requests, datetime

BASE_URL = "..."   # ask user
API_KEY  = "..."   # ask user
_q = queue.Queue(maxsize=10000)

def log_event(e: dict):                      # call from anywhere; never raises
    e.setdefault("eventTimestamp", datetime.datetime.utcnow().isoformat() + "Z")
    try: _q.put_nowait(e)
    except queue.Full: pass

def _pump():
    while True:
        batch = [_q.get()]
        while len(batch) < 200 and not _q.empty(): batch.append(_q.get_nowait())
        try:
            requests.post(f"{BASE_URL}/api/v1/events/batch",
                          json={"events": batch},
                          headers={"X-LogSphere-Key": API_KEY}, timeout=5)
        except Exception: pass               # never break the app because of logging

threading.Thread(target=_pump, daemon=True).start()

# usage:
log_event({"eventType": "Exception", "severity": "Error", "module": "Billing",
           "correlationId": str(uuid.uuid4().hex),
           "exception": {"type": "ValueError", "message": "bad amount", "stackTrace": "..."}})
```

## 8. Verifying the integration

1. Send the curl smoke test above → expect HTTP 202 with `"accepted": 1`.
2. Open the LogSphere dashboard → **Log Explorer** → the event appears within seconds.
3. Trigger an error in the app → it appears on the **Exceptions** page, grouped by fingerprint.
4. Click any event's **correlation** link → all events of that request are shown together.

## 9. Checklist for the implementer

- [ ] API key and base URL obtained from the user and stored in config (not in code)
- [ ] Correlation id created/propagated per request and echoed in the response header
- [ ] ApiRequest + ApiResponse logged for every HTTP request (skip health checks)
- [ ] All exceptions logged with `exception{...}` details
- [ ] Audit events for business-data changes (old/new values)
- [ ] Background batching, bounded queue, retries, timeouts — logging can never break the app
- [ ] No secrets/binary data sent; auth & payment endpoint bodies excluded
- [ ] Smoke test passed and events visible in the dashboard
