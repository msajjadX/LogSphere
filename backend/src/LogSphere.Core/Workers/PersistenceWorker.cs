using System.Text.Json.Nodes;
using LogSphere.Core.Repositories;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LogSphere.Core.Workers;

/// <summary>Consumes the durable ingest queue and persists events into partitioned storage.
/// Idempotent (event_id conflict = no-op); poison batches degrade to per-item handling and dead-letter.</summary>
public sealed class PersistenceWorker(
    QueueRepository queue,
    EventRepository events,
    ExceptionGroupRepository exceptionGroups,
    StatsRepository stats,
    SystemRepository system,
    IConfiguration config,
    ILogger<PersistenceWorker> logger) : BackgroundService
{
    /// <summary>Best-effort rollup increment: log_events is the source of truth, so a stats
    /// failure must never fail (and thus re-deliver) an already-persisted batch.</summary>
    private async Task IncrementStatsAsync(IReadOnlyList<JsonObject> insertedPayloads, CancellationToken ct)
    {
        if (insertedPayloads.Count == 0) return;
        try
        {
            await stats.IncrementAsync(insertedPayloads, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Stats rollup increment failed for {Count} events; dashboard aggregates may drift", insertedPayloads.Count);
        }
    }

    private readonly string _name = $"persistence@{Environment.MachineName}";

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!config.GetValue("Workers:Persistence:Enabled", true)) return;
        var batchSize = config.GetValue("Workers:Persistence:BatchSize", 200);
        var idleDelay = TimeSpan.FromMilliseconds(config.GetValue("Workers:Persistence:IdleDelayMs", 500));
        logger.LogInformation("Persistence worker started ({Name})", _name);
        var lastHeartbeat = DateTimeOffset.MinValue;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (DateTimeOffset.UtcNow - lastHeartbeat > TimeSpan.FromSeconds(30))
                {
                    await system.HeartbeatAsync("persistence", new { batchSize }, ct);
                    lastHeartbeat = DateTimeOffset.UtcNow;
                }

                var batch = await queue.ClaimBatchAsync(_name, batchSize, ct);
                if (batch.Count == 0)
                {
                    await Task.Delay(idleDelay, ct);
                    continue;
                }

                try
                {
                    var inserted = await events.InsertBatchAsync(batch.Select(b => b.Payload).ToList(), ct);
                    // only aggregate exception groups / stats for events that were actually inserted this
                    // pass, so a queue redelivery of an already-persisted event does not double-count.
                    var exceptions = batch
                        .Where(b => b.Payload["exceptionFingerprint"] is not null && inserted.Contains(b.EventId))
                        .Select(b => b.Payload)
                        .ToList();
                    if (exceptions.Count > 0)
                        await exceptionGroups.UpsertFromEventsAsync(exceptions, ct);
                    await IncrementStatsAsync(batch.Where(b => inserted.Contains(b.EventId)).Select(b => b.Payload).ToList(), ct);
                    await queue.CompleteAsync(batch.Select(b => b.Id).ToList(), ct);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    logger.LogError(ex, "Batch persist failed ({Count} events); retrying items individually", batch.Count);
                    foreach (var item in batch)
                    {
                        try
                        {
                            var insertedOne = await events.InsertBatchAsync(new[] { item.Payload }, ct);
                            if (item.Payload["exceptionFingerprint"] is not null && insertedOne.Contains(item.EventId))
                                await exceptionGroups.UpsertFromEventsAsync(new[] { item.Payload }, ct);
                            if (insertedOne.Contains(item.EventId))
                                await IncrementStatsAsync(new[] { item.Payload }, ct);
                            await queue.CompleteAsync(new[] { item.Id }, ct);
                        }
                        catch (Exception itemEx) when (!ct.IsCancellationRequested)
                        {
                            await queue.FailAsync(new[] { item }, itemEx.Message, ct);
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (Exception ex)
            {
                logger.LogError(ex, "Persistence loop error; backing off");
                try { await Task.Delay(TimeSpan.FromSeconds(5), ct); } catch (OperationCanceledException) { }
            }
        }
    }
}
