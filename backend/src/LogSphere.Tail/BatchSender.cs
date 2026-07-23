using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace LogSphere.Tail;

/// <summary>Ships envelopes to POST /api/v1/events/batch with retries. The tailer only advances
/// file offsets after a successful send, so delivery is at-least-once with no disk buffer.</summary>
internal sealed class BatchSender : IDisposable
{
    private readonly HttpClient _http;
    private readonly TailConfig _config;
    public long Sent { get; private set; }
    public long Rejected { get; private set; }

    public BatchSender(TailConfig config)
    {
        _config = config;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        _http.DefaultRequestHeaders.Add("X-LogSphere-Key", config.Key);
    }

    /// <summary>Sends everything in chunks; true only when ALL batches were accepted.</summary>
    public async Task<bool> SendAsync(IReadOnlyList<JsonObject> events, CancellationToken ct)
    {
        for (var i = 0; i < events.Count; i += _config.BatchSize)
        {
            var chunk = events.Skip(i).Take(_config.BatchSize).ToList();
            if (!await SendBatchAsync(chunk, ct)) return false;
        }
        return true;
    }

    private async Task<bool> SendBatchAsync(List<JsonObject> chunk, CancellationToken ct)
    {
        var body = new JsonObject { ["events"] = new JsonArray(chunk.Select(e => (JsonNode)e.DeepClone()).ToArray()) };
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                var res = await _http.PostAsJsonAsync($"{_config.Endpoint}/api/v1/events/batch", body, ct);
                if (res.IsSuccessStatusCode)
                {
                    // count server-side rejections (malformed events) — they will not be retried:
                    // the server has durably decided; re-sending would duplicate the accepted ones
                    try
                    {
                        var parsed = JsonNode.Parse(await res.Content.ReadAsStringAsync(ct));
                        var rejected = parsed?["data"]?["rejected"] as JsonArray;
                        Rejected += rejected?.Count ?? 0;
                        Sent += chunk.Count - (rejected?.Count ?? 0);
                    }
                    catch { Sent += chunk.Count; }
                    return true;
                }
                if (res.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                {
                    Console.Error.WriteLine($"[tail] FATAL: API key rejected (401) - check the key.");
                    return false; // no point retrying an auth failure
                }
                Console.Error.WriteLine($"[tail] batch send got HTTP {(int)res.StatusCode} (attempt {attempt}/3)");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return false; }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[tail] batch send failed: {ex.Message} (attempt {attempt}/3)");
            }
            if (attempt < 3) await Task.Delay(TimeSpan.FromSeconds(attempt * 2), ct);
        }
        return false;
    }

    public void Dispose() => _http.Dispose();
}
