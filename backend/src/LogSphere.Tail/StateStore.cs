using System.Security.Cryptography;
using System.Text.Json;

namespace LogSphere.Tail;

/// <summary>Per-file read offsets, persisted as JSON so the agent resumes where it left off.
/// Rotation/truncation detection: each entry stores a signature (SHA-256 of the first 512 bytes);
/// when the signature no longer matches — or the file shrank below the stored offset — the file
/// on disk is a different/rewritten file and reading restarts from 0.</summary>
internal sealed class StateStore(string path)
{
    public sealed class FileState
    {
        public long Offset { get; set; }
        public string? Signature { get; set; }
        /// <summary>How many leading bytes the signature covers. Fixed at first sight so later
        /// appends to a short file never change the hashed region (which would look like rotation).</summary>
        public int SigBytes { get; set; }
    }

    private Dictionary<string, FileState> _files = new(StringComparer.OrdinalIgnoreCase);

    public void Load()
    {
        try
        {
            if (File.Exists(path))
                _files = JsonSerializer.Deserialize<Dictionary<string, FileState>>(File.ReadAllText(path))
                         ?? new(StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            _files = new(StringComparer.OrdinalIgnoreCase); // corrupt state: start fresh (at-least-once)
        }
    }

    public void Save()
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_files, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>Signature of exactly the first <paramref name="bytes"/> bytes — identifies
    /// "same file" across polls without being disturbed by appends.</summary>
    public static string? ComputeSignature(FileStream fs, int bytes)
    {
        if (bytes <= 0 || fs.Length < bytes) return null;
        try
        {
            var buf = new byte[bytes];
            fs.Position = 0;
            fs.ReadExactly(buf, 0, bytes);
            return Convert.ToHexString(SHA256.HashData(buf));
        }
        finally
        {
            fs.Position = 0;
        }
    }

    /// <summary>Resolves the starting offset for a file this cycle, handling first-sight,
    /// rotation and truncation.</summary>
    public long ResolveOffset(string filePath, FileStream fs, bool fromBeginning)
    {
        if (!_files.TryGetValue(filePath, out var state))
        {
            // first sight: skip pre-existing content unless configured otherwise
            var sigBytes = (int)Math.Min(512, fs.Length);
            _files[filePath] = new FileState
            {
                Offset = fromBeginning ? 0 : fs.Length,
                Signature = ComputeSignature(fs, sigBytes),
                SigBytes = sigBytes,
            };
            return _files[filePath].Offset;
        }

        // re-hash the SAME prefix length that was originally signed; a mismatch (or the file
        // shrinking below the offset / signed region) means rotation or truncation
        var current = state.SigBytes > 0 ? ComputeSignature(fs, state.SigBytes) : null;
        var rotated = fs.Length < state.Offset
                      || (state.Signature is not null && current != state.Signature);
        if (rotated)
        {
            var sigBytes = (int)Math.Min(512, fs.Length);
            state.Offset = 0; // a rotated/recreated file is NEW content — always read from the top
            state.Signature = ComputeSignature(fs, sigBytes);
            state.SigBytes = sigBytes;
        }
        else if (state.Signature is null && fs.Length > 0)
        {
            // file was empty at first sight; adopt its identity now that it has content
            var sigBytes = (int)Math.Min(512, fs.Length);
            state.Signature = ComputeSignature(fs, sigBytes);
            state.SigBytes = sigBytes;
        }
        return state.Offset;
    }

    public void Advance(string filePath, long newOffset)
    {
        if (_files.TryGetValue(filePath, out var state)) state.Offset = newOffset;
    }
}
