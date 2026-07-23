using LogSphere.Tail;

// logsphere-tail — single-binary log file tailing agent.
//   logsphere-tail [path/to/tail.yaml]   (default ./tail.yaml)
//   logsphere-tail --once [config]       one pass then exit (testing / cron use)
//
// See docs/DESIGN-CAPTURE-ROADMAP.md §2 for the config reference.

var args_ = Environment.GetCommandLineArgs().Skip(1).ToArray();
var once = args_.Contains("--once");
var configPath = args_.FirstOrDefault(a => a != "--once") ?? "tail.yaml";

if (!File.Exists(configPath))
{
    Console.Error.WriteLine($"logsphere-tail: config not found: {Path.GetFullPath(configPath)}");
    Console.Error.WriteLine("""
        Minimal tail.yaml:

          endpoint: http://localhost:8080
          key: ${LOGSPHERE_KEY}
          sources:
            - path: "C:/apps/legacy/logs/*.log"
              parser: plain
              module: LegacyApp
        """);
    return 2;
}

TailConfig config;
try
{
    config = TailConfig.Load(configPath);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"logsphere-tail: bad config: {ex.Message}");
    return 2;
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    try { cts.Cancel(); } catch (ObjectDisposedException) { /* already exiting */ }
};

var state = new StateStore(config.StateFile);
state.Load();
using var sender = new BatchSender(config);
var tailer = new FileTailer(config, state, sender);

Console.WriteLine($"[tail] watching {config.Sources.Count} source(s) -> {config.Endpoint} " +
                  $"(poll {config.PollSeconds}s{(once ? ", single pass" : "")})");

try
{
    while (!cts.IsCancellationRequested)
    {
        var produced = await tailer.RunCycleAsync(cts.Token);
        if (produced > 0)
            Console.WriteLine($"[tail] {DateTimeOffset.Now:HH:mm:ss} sent {produced} event(s) " +
                              $"(total {sender.Sent}, rejected {sender.Rejected})");
        if (once) break;
        await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, config.PollSeconds)), cts.Token);
    }
}
catch (OperationCanceledException) { /* graceful shutdown */ }

Console.WriteLine($"[tail] done. sent {sender.Sent} event(s), {sender.Rejected} rejected by server.");
return 0;
