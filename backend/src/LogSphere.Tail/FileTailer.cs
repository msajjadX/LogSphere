using System.Text;
using System.Text.Json.Nodes;

namespace LogSphere.Tail;

/// <summary>
/// One poll cycle over every source: expand the glob, read each file's new bytes (complete lines
/// only), parse to envelopes, send, and only then advance the stored offset — a failed send means
/// the same bytes are re-read next cycle (at-least-once, no disk buffer needed).
/// Files are opened with ReadWrite|Delete sharing so the producing application is never blocked.
/// </summary>
internal sealed class FileTailer(TailConfig config, StateStore state, BatchSender sender)
{
    // per-file parser state that must survive across cycles
    private readonly Dictionary<string, W3CFields> _w3c = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, MultilineAssembler> _assemblers = new(StringComparer.OrdinalIgnoreCase);

    public async Task<int> RunCycleAsync(CancellationToken ct)
    {
        var produced = 0;
        foreach (var source in config.Sources)
        {
            foreach (var file in Expand(source.Path))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    produced += await TailFileAsync(file, source, ct);
                }
                catch (IOException) { /* locked/vanished mid-cycle — retry next poll */ }
                catch (UnauthorizedAccessException) { /* permissions — skip */ }
            }
        }
        state.Save();
        return produced;
    }

    private static IEnumerable<string> Expand(string glob)
    {
        var dir = Path.GetDirectoryName(glob);
        var pattern = Path.GetFileName(glob);
        if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(pattern)) yield break;
        if (!Directory.Exists(dir)) yield break;
        foreach (var f in Directory.EnumerateFiles(dir, pattern)) yield return f;
    }

    private async Task<int> TailFileAsync(string file, TailSource source, CancellationToken ct)
    {
        await using var fs = new FileStream(file, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);

        var offset = state.ResolveOffset(file, fs, source.FromBeginning);
        if (fs.Length <= offset) return 0; // nothing new

        fs.Position = offset;
        var toRead = (int)Math.Min(fs.Length - offset, config.MaxReadBytesPerCycle);
        var buffer = new byte[toRead];
        var read = await fs.ReadAsync(buffer.AsMemory(0, toRead), ct);
        if (read == 0) return 0;

        // consume only COMPLETE lines; a partially-written last line stays for the next cycle
        var lastNewline = Array.LastIndexOf(buffer, (byte)'\n', read - 1);
        if (lastNewline < 0) return 0; // no complete line yet
        var consumed = lastNewline + 1;

        var text = Encoding.UTF8.GetString(buffer, 0, consumed);
        var w3c = _w3c.TryGetValue(file, out var w) ? w : _w3c[file] = new W3CFields();
        var assembler = _assemblers.TryGetValue(file, out var a) ? a : _assemblers[file] = new MultilineAssembler(source);

        var events = new List<JsonObject>();
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.TrimEnd('\r');
            if (trimmed.Length == 0) continue;
            if (assembler.Push(trimmed, w3c) is { } evt) events.Add(evt);
        }
        if (assembler.Flush() is { } trailing) events.Add(trailing);

        if (events.Count > 0 && !await sender.SendAsync(events, source.Key ?? config.Key, ct))
            return 0; // send failed → offset NOT advanced → same bytes re-read next cycle

        state.Advance(file, offset + consumed);
        return events.Count;
    }
}
