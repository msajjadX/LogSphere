using System.Net.Http.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Microsoft.Extensions.Options;

namespace LogSphere.Sdk;

/// <summary>Asynchronous, non-blocking event pipeline: bounded channel → background batcher →
/// HTTP sender with retry, jittered backoff, circuit breaker and optional disk-backed offline buffer.
/// A logging outage never blocks or crashes the host application.</summary>
public sealed class LogSphereClient : IAsyncDisposable
{
    private readonly LogSphereOptions _options;
    private readonly ClientSanitizer _sanitizer;
    private readonly IHttpClientFactory _httpFactory;
    private readonly Channel<JsonObject> _channel;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _pumpTask;

    private int _consecutiveFailures;
    private DateTimeOffset _circuitOpenUntil = DateTimeOffset.MinValue;
    private long _droppedEvents;

    public long DroppedEvents => Interlocked.Read(ref _droppedEvents);

    private void Diag(string message, Exception? ex = null)
    {
        try { _options.OnDiagnostic?.Invoke(message, ex); } catch { /* diagnostics must never throw */ }
    }

    // No ILogger here, on purpose: the transport must not depend on the logging pipeline it
    // feeds (the bridge provider would make ILoggerFactory ⇄ LogSphereClient a DI cycle, and
    // "send failed" events would loop back into the sender). Diagnostics go to OnDiagnostic.
    public LogSphereClient(IOptions<LogSphereOptions> options, IHttpClientFactory httpFactory)
    {
        _options = options.Value;
        _sanitizer = new ClientSanitizer(_options.ExtraSensitiveKeys);
        _httpFactory = httpFactory;
        _channel = Channel.CreateBounded<JsonObject>(new BoundedChannelOptions(_options.BufferCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true
        });
        _pumpTask = Task.Run(() => PumpAsync(_cts.Token));
    }

    /// <summary>Enqueues an event (fire-and-forget with bounded buffering; never throws to the caller).</summary>
    public void Submit(JsonObject envelope)
    {
        try
        {
            EnrichWithContext(envelope);
            envelope["requestData"] = _sanitizer.Sanitize(envelope["requestData"]);
            envelope["responseData"] = _sanitizer.Sanitize(envelope["responseData"]);
            envelope["properties"] = _sanitizer.Sanitize(envelope["properties"]);
            if (!_channel.Writer.TryWrite(envelope))
                Interlocked.Increment(ref _droppedEvents);
        }
        catch (Exception ex)
        {
            // local diagnostics only — logging failures must never cascade
            Diag("LogSphere submit failed", ex);
        }
    }

    private void EnrichWithContext(JsonObject e)
    {
        e["schemaVersion"] ??= "1.0";
        e["eventId"] ??= Guid.NewGuid().ToString();
        e["eventTimestamp"] ??= DateTimeOffset.UtcNow.UtcDateTime;
        e["severity"] ??= "Information";
        e["correlationId"] ??= CorrelationContext.CorrelationId;
        e["traceId"] ??= CorrelationContext.TraceId;
        e["spanId"] ??= CorrelationContext.SpanId;
        e["userId"] ??= CorrelationContext.UserId;
        e["userName"] ??= CorrelationContext.UserName;
        e["sessionId"] ??= CorrelationContext.SessionId;
        e["businessEntityType"] ??= CorrelationContext.BusinessEntityType;
        e["businessEntityId"] ??= CorrelationContext.BusinessEntityId;
        // every event of a request — audits included — carries the caller's IP + agent,
        // so the Audit Explorer's "Source IP" is populated without any per-call code
        if (CorrelationContext.ClientIp is not null || CorrelationContext.UserAgent is not null)
        {
            if (e["http"] is not JsonObject http) e["http"] = http = new JsonObject();
            http["clientIp"] ??= CorrelationContext.ClientIp;
            http["userAgent"] ??= CorrelationContext.UserAgent;
        }
        e["machineName"] ??= Environment.MachineName;
        e["applicationVersion"] ??= _options.ApplicationVersion;
        e["deploymentVersion"] ??= _options.DeploymentVersion;
    }

    // ---------------------------------------------------------------- background pump

    private async Task PumpAsync(CancellationToken ct)
    {
        await ResendOfflineBufferAsync(ct);
        var batch = new List<JsonObject>(_options.BatchSize);
        var timer = new PeriodicTimer(_options.FlushInterval);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                batch.Clear();
                var deadline = Task.Delay(_options.FlushInterval, ct);
                while (batch.Count < _options.BatchSize)
                {
                    if (_channel.Reader.TryRead(out var item)) { batch.Add(item); continue; }
                    var read = _channel.Reader.WaitToReadAsync(ct).AsTask();
                    var completed = await Task.WhenAny(read, deadline);
                    if (completed == deadline) break;
                    if (!await read) return; // channel completed
                }
                if (batch.Count == 0) continue;
                await SendBatchAsync(batch, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (Exception ex)
            {
                Diag("LogSphere pump error", ex);
            }
        }

        // final flush on shutdown (best effort, short window)
        try
        {
            batch.Clear();
            while (_channel.Reader.TryRead(out var item) && batch.Count < _options.BatchSize) batch.Add(item);
            if (batch.Count > 0)
                await SendBatchAsync(batch, new CancellationTokenSource(TimeSpan.FromSeconds(3)).Token);
        }
        catch { /* shutting down */ }
        timer.Dispose();
    }

    private async Task SendBatchAsync(List<JsonObject> batch, CancellationToken ct)
    {
        if (DateTimeOffset.UtcNow < _circuitOpenUntil)
        {
            WriteOfflineBuffer(batch);
            return;
        }

        var payload = new JsonObject { ["events"] = new JsonArray(batch.Select(b => (JsonNode)b.DeepClone()).ToArray()) };

        for (var attempt = 0; attempt <= _options.MaxRetries; attempt++)
        {
            try
            {
                var client = _httpFactory.CreateClient("logsphere");
                client.Timeout = _options.HttpTimeout;
                using var request = new HttpRequestMessage(HttpMethod.Post,
                    _options.Endpoint.TrimEnd('/') + "/api/v1/events/batch");
                request.Headers.Add("X-LogSphere-Key", _options.ProjectKey);
                if (CorrelationContext.CorrelationId is { } cid)
                    request.Headers.Add("X-Correlation-ID", cid);
                request.Content = JsonContent.Create(payload);

                using var response = await client.SendAsync(request, ct);
                if (response.IsSuccessStatusCode)
                {
                    _consecutiveFailures = 0;
                    return;
                }
                if ((int)response.StatusCode is 400 or 401 or 403 or 413)
                {
                    // non-retryable: drop with local diagnostic
                    Diag($"LogSphere rejected batch: {response.StatusCode}");
                    return;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                Diag($"LogSphere send attempt {attempt + 1} failed", ex);
            }

            if (attempt < _options.MaxRetries)
            {
                var backoff = TimeSpan.FromMilliseconds(
                    Math.Min(8000, 250 * Math.Pow(2, attempt)) * (0.5 + Random.Shared.NextDouble()));
                try { await Task.Delay(backoff, ct); } catch (OperationCanceledException) { return; }
            }
        }

        if (++_consecutiveFailures >= _options.CircuitBreakerThreshold)
        {
            _circuitOpenUntil = DateTimeOffset.UtcNow + _options.CircuitBreakerCooldown;
            _consecutiveFailures = 0;
            Diag($"LogSphere circuit opened until {_circuitOpenUntil:T}; buffering locally");
        }
        WriteOfflineBuffer(batch);
    }

    // ---------------------------------------------------------------- offline buffer

    private void WriteOfflineBuffer(List<JsonObject> batch)
    {
        var dir = _options.OfflineBufferDirectory;
        if (dir is null)
        {
            Interlocked.Add(ref _droppedEvents, batch.Count);
            return;
        }
        try
        {
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, "logsphere-buffer.jsonl");
            var info = new FileInfo(file);
            if (info.Exists && info.Length > _options.OfflineBufferMaxBytes)
            {
                Interlocked.Add(ref _droppedEvents, batch.Count);
                return;
            }
            File.AppendAllLines(file, batch.Select(b => b.ToJsonString()));
        }
        catch (Exception ex)
        {
            Interlocked.Add(ref _droppedEvents, batch.Count);
            Diag("Offline buffer write failed", ex);
        }
    }

    private async Task ResendOfflineBufferAsync(CancellationToken ct)
    {
        var dir = _options.OfflineBufferDirectory;
        if (dir is null) return;
        var file = Path.Combine(dir, "logsphere-buffer.jsonl");
        if (!File.Exists(file)) return;
        try
        {
            var lines = await File.ReadAllLinesAsync(file, ct);
            File.Delete(file);
            foreach (var chunk in lines.Chunk(_options.BatchSize))
            {
                var batch = chunk
                    .Select(l => { try { return JsonNode.Parse(l) as JsonObject; } catch { return null; } })
                    .Where(o => o is not null)
                    .Select(o => o!)
                    .ToList();
                if (batch.Count > 0) await SendBatchAsync(batch, ct);
            }
        }
        catch (Exception ex)
        {
            Diag("Offline buffer replay failed", ex);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();
        _cts.CancelAfter(TimeSpan.FromSeconds(4));
        try { await _pumpTask.WaitAsync(TimeSpan.FromSeconds(5)); } catch { /* best effort */ }
        _cts.Dispose();
    }
}
