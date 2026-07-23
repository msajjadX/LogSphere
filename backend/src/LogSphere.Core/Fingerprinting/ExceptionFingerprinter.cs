using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using LogSphere.Core.Models;

namespace LogSphere.Core.Fingerprinting;

/// <summary>Produces a stable fingerprint for grouping repeated exceptions.
/// Dynamic values (numbers, guids, hex ids, quoted strings) are normalized out.</summary>
public static partial class ExceptionFingerprinter
{
    [GeneratedRegex(@"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}")]
    private static partial Regex GuidRegex();

    [GeneratedRegex(@"0x[0-9a-fA-F]+|\b\d+\b")]
    private static partial Regex NumberRegex();

    [GeneratedRegex("\"[^\"]*\"|'[^']*'")]
    private static partial Regex QuotedRegex();

    [GeneratedRegex(@":line \d+", RegexOptions.IgnoreCase)]
    private static partial Regex LineNumberRegex();

    public static string Compute(ExceptionInfo ex, string? module)
    {
        var sb = new StringBuilder();
        sb.Append(ex.Type ?? "UnknownException").Append('|');
        sb.Append(module ?? "").Append('|');
        sb.Append(ex.ClassName ?? "").Append('|');
        sb.Append(ex.MethodName ?? "").Append('|');
        sb.Append(ex.ErrorCode ?? "").Append('|');
        sb.Append(NormalizeStack(ex.StackTrace));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(hash).ToLowerInvariant()[..32];
    }

    /// <summary>Keeps the top frames (method identity), removes line numbers, ids and literals.</summary>
    public static string NormalizeStack(string? stackTrace)
    {
        if (string.IsNullOrWhiteSpace(stackTrace)) return "";
        var frames = stackTrace
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => l.StartsWith("at ", StringComparison.OrdinalIgnoreCase) || l.Contains(" in "))
            .Take(10)
            .Select(l =>
            {
                l = LineNumberRegex().Replace(l, "");
                l = GuidRegex().Replace(l, "<guid>");
                l = QuotedRegex().Replace(l, "<str>");
                l = NumberRegex().Replace(l, "<n>");
                var inIdx = l.IndexOf(" in ", StringComparison.OrdinalIgnoreCase);
                return inIdx > 0 ? l[..inIdx] : l; // drop file paths (differ per machine)
            });
        return string.Join(';', frames);
    }
}
