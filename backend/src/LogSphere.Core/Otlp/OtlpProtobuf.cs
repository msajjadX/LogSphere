using System.Buffers.Binary;
using System.Text;
using System.Text.Json.Nodes;

namespace LogSphere.Core.Otlp;

/// <summary>
/// Minimal OTLP protobuf wire-format transcoder. Decodes ExportLogsServiceRequest /
/// ExportTraceServiceRequest binary payloads into the OTLP/JSON JsonObject shape that
/// <see cref="OtlpTranslator"/> already consumes — so protobuf and JSON share one translation
/// path — and encodes the tiny response/status messages. Hand-written against the published
/// opentelemetry-proto schemas (stable v1) to stay dependency-free and fully offline.
/// Unknown fields are skipped per protobuf rules, so schema additions don't break decoding.
/// </summary>
public static class OtlpProtobuf
{
    public const int MaxPayloadBytes = 32 * 1024 * 1024;
    private const int MaxDepth = 48;

    // ------------------------------------------------------------------ public API

    /// <summary>ExportLogsServiceRequest → { "resourceLogs": [...] }. Throws FormatException on malformed input.</summary>
    public static JsonObject DecodeLogsRequest(ReadOnlySpan<byte> payload)
    {
        var resourceLogs = new JsonArray();
        var r = new Reader(payload, 0);
        while (r.TryReadTag(out var field, out var wire))
        {
            if (field == 1 && wire == 2) resourceLogs.Add(ParseResourceGroup(r.ReadBytes(), isLogs: true, 1));
            else r.Skip(wire);
        }
        return new JsonObject { ["resourceLogs"] = resourceLogs };
    }

    /// <summary>ExportTraceServiceRequest → { "resourceSpans": [...] }. Throws FormatException on malformed input.</summary>
    public static JsonObject DecodeTracesRequest(ReadOnlySpan<byte> payload)
    {
        var resourceSpans = new JsonArray();
        var r = new Reader(payload, 0);
        while (r.TryReadTag(out var field, out var wire))
        {
            if (field == 1 && wire == 2) resourceSpans.Add(ParseResourceGroup(r.ReadBytes(), isLogs: false, 1));
            else r.Skip(wire);
        }
        return new JsonObject { ["resourceSpans"] = resourceSpans };
    }

    /// <summary>Export*ServiceResponse. Success with no rejects is an empty message (zero bytes);
    /// partial success nests { rejected = 1 (varint), error_message = 2 } under field 1.</summary>
    public static byte[] EncodeExportResponse(long rejected, string? errorMessage)
    {
        if (rejected <= 0) return Array.Empty<byte>();
        var inner = new Writer();
        inner.WriteVarintField(1, (ulong)rejected);
        if (!string.IsNullOrEmpty(errorMessage)) inner.WriteStringField(2, errorMessage);
        var outer = new Writer();
        outer.WriteBytesField(1, inner.ToArray());
        return outer.ToArray();
    }

    /// <summary>google.rpc.Status { code = 1, message = 2 } — the OTLP/HTTP error body.</summary>
    public static byte[] EncodeStatus(int code, string message)
    {
        var w = new Writer();
        w.WriteVarintField(1, (ulong)code);
        w.WriteStringField(2, message);
        return w.ToArray();
    }

    // ------------------------------------------------- ResourceLogs / ResourceSpans

    /// <summary>ResourceLogs and ResourceSpans share their field layout:
    /// resource = 1, scope_logs / scope_spans = 2.</summary>
    private static JsonObject ParseResourceGroup(ReadOnlySpan<byte> data, bool isLogs, int depth)
    {
        Guard(depth);
        var scopes = new JsonArray();
        var result = new JsonObject();
        var r = new Reader(data, depth);
        while (r.TryReadTag(out var field, out var wire))
        {
            if (wire != 2) { r.Skip(wire); continue; }
            switch (field)
            {
                case 1: result["resource"] = ParseResource(r.ReadBytes(), depth + 1); break;
                case 2: scopes.Add(ParseScopeGroup(r.ReadBytes(), isLogs, depth + 1)); break;
                default: r.Skip(2); break;
            }
        }
        result[isLogs ? "scopeLogs" : "scopeSpans"] = scopes;
        return result;
    }

    private static JsonObject ParseResource(ReadOnlySpan<byte> data, int depth)
    {
        Guard(depth);
        var attributes = new JsonArray();
        var r = new Reader(data, depth);
        while (r.TryReadTag(out var field, out var wire))
        {
            if (field == 1 && wire == 2) attributes.Add(ParseKeyValue(r.ReadBytes(), depth + 1));
            else r.Skip(wire);
        }
        return new JsonObject { ["attributes"] = attributes };
    }

    /// <summary>ScopeLogs / ScopeSpans: scope = 1, log_records / spans = 2.</summary>
    private static JsonObject ParseScopeGroup(ReadOnlySpan<byte> data, bool isLogs, int depth)
    {
        Guard(depth);
        var records = new JsonArray();
        var result = new JsonObject();
        var r = new Reader(data, depth);
        while (r.TryReadTag(out var field, out var wire))
        {
            if (wire != 2) { r.Skip(wire); continue; }
            switch (field)
            {
                case 1: result["scope"] = ParseScope(r.ReadBytes(), depth + 1); break;
                case 2:
                    records.Add(isLogs ? ParseLogRecord(r.ReadBytes(), depth + 1) : ParseSpan(r.ReadBytes(), depth + 1));
                    break;
                default: r.Skip(2); break;
            }
        }
        result[isLogs ? "logRecords" : "spans"] = records;
        return result;
    }

    private static JsonObject ParseScope(ReadOnlySpan<byte> data, int depth)
    {
        Guard(depth);
        var scope = new JsonObject();
        var r = new Reader(data, depth);
        while (r.TryReadTag(out var field, out var wire))
        {
            switch (field)
            {
                case 1 when wire == 2: scope["name"] = r.ReadString(); break;
                case 2 when wire == 2: scope["version"] = r.ReadString(); break;
                default: r.Skip(wire); break;
            }
        }
        return scope;
    }

    // ------------------------------------------------------------------ LogRecord

    private static JsonObject ParseLogRecord(ReadOnlySpan<byte> data, int depth)
    {
        Guard(depth);
        var rec = new JsonObject();
        var attributes = new JsonArray();
        var r = new Reader(data, depth);
        while (r.TryReadTag(out var field, out var wire))
        {
            switch (field)
            {
                case 1 when wire == 1: rec["timeUnixNano"] = r.ReadFixed64().ToString(); break;
                case 2 when wire == 0: rec["severityNumber"] = (long)r.ReadVarint(); break;
                case 3 when wire == 2: rec["severityText"] = r.ReadString(); break;
                case 5 when wire == 2: rec["body"] = ParseAnyValue(r.ReadBytes(), depth + 1); break;
                case 6 when wire == 2: attributes.Add(ParseKeyValue(r.ReadBytes(), depth + 1)); break;
                case 9 when wire == 2: rec["traceId"] = ToHexId(r.ReadBytes()); break;
                case 10 when wire == 2: rec["spanId"] = ToHexId(r.ReadBytes()); break;
                case 11 when wire == 1: rec["observedTimeUnixNano"] = r.ReadFixed64().ToString(); break;
                default: r.Skip(wire); break;
            }
        }
        if (attributes.Count > 0) rec["attributes"] = attributes;
        return rec;
    }

    // ----------------------------------------------------------------------- Span

    private static JsonObject ParseSpan(ReadOnlySpan<byte> data, int depth)
    {
        Guard(depth);
        var span = new JsonObject();
        var attributes = new JsonArray();
        var events = new JsonArray();
        var r = new Reader(data, depth);
        while (r.TryReadTag(out var field, out var wire))
        {
            switch (field)
            {
                case 1 when wire == 2: span["traceId"] = ToHexId(r.ReadBytes()); break;
                case 2 when wire == 2: span["spanId"] = ToHexId(r.ReadBytes()); break;
                case 4 when wire == 2: span["parentSpanId"] = ToHexId(r.ReadBytes()); break;
                case 5 when wire == 2: span["name"] = r.ReadString(); break;
                case 6 when wire == 0: span["kind"] = (long)r.ReadVarint(); break;
                case 7 when wire == 1: span["startTimeUnixNano"] = r.ReadFixed64().ToString(); break;
                case 8 when wire == 1: span["endTimeUnixNano"] = r.ReadFixed64().ToString(); break;
                case 9 when wire == 2: attributes.Add(ParseKeyValue(r.ReadBytes(), depth + 1)); break;
                case 11 when wire == 2: events.Add(ParseSpanEvent(r.ReadBytes(), depth + 1)); break;
                case 15 when wire == 2: span["status"] = ParseStatus(r.ReadBytes(), depth + 1); break;
                default: r.Skip(wire); break;
            }
        }
        if (attributes.Count > 0) span["attributes"] = attributes;
        if (events.Count > 0) span["events"] = events;
        return span;
    }

    private static JsonObject ParseSpanEvent(ReadOnlySpan<byte> data, int depth)
    {
        Guard(depth);
        var ev = new JsonObject();
        var attributes = new JsonArray();
        var r = new Reader(data, depth);
        while (r.TryReadTag(out var field, out var wire))
        {
            switch (field)
            {
                case 1 when wire == 1: ev["timeUnixNano"] = r.ReadFixed64().ToString(); break;
                case 2 when wire == 2: ev["name"] = r.ReadString(); break;
                case 3 when wire == 2: attributes.Add(ParseKeyValue(r.ReadBytes(), depth + 1)); break;
                default: r.Skip(wire); break;
            }
        }
        if (attributes.Count > 0) ev["attributes"] = attributes;
        return ev;
    }

    private static JsonObject ParseStatus(ReadOnlySpan<byte> data, int depth)
    {
        Guard(depth);
        var status = new JsonObject();
        var r = new Reader(data, depth);
        while (r.TryReadTag(out var field, out var wire))
        {
            switch (field)
            {
                case 2 when wire == 2: status["message"] = r.ReadString(); break;
                case 3 when wire == 0: status["code"] = (long)r.ReadVarint(); break;
                default: r.Skip(wire); break;
            }
        }
        return status;
    }

    // ------------------------------------------------------------ KeyValue / AnyValue

    private static JsonObject ParseKeyValue(ReadOnlySpan<byte> data, int depth)
    {
        Guard(depth);
        var kv = new JsonObject();
        var r = new Reader(data, depth);
        while (r.TryReadTag(out var field, out var wire))
        {
            switch (field)
            {
                case 1 when wire == 2: kv["key"] = r.ReadString(); break;
                case 2 when wire == 2: kv["value"] = ParseAnyValue(r.ReadBytes(), depth + 1); break;
                default: r.Skip(wire); break;
            }
        }
        return kv;
    }

    private static JsonObject ParseAnyValue(ReadOnlySpan<byte> data, int depth)
    {
        Guard(depth);
        var v = new JsonObject();
        var r = new Reader(data, depth);
        while (r.TryReadTag(out var field, out var wire))
        {
            switch (field)
            {
                case 1 when wire == 2: v["stringValue"] = r.ReadString(); break;
                case 2 when wire == 0: v["boolValue"] = r.ReadVarint() != 0; break;
                case 3 when wire == 0: v["intValue"] = ((long)r.ReadVarint()).ToString(); break;
                case 4 when wire == 1: v["doubleValue"] = BitConverter.Int64BitsToDouble((long)r.ReadFixed64()); break;
                case 5 when wire == 2: v["arrayValue"] = ParseAnyList(r.ReadBytes(), isKvList: false, depth + 1); break;
                case 6 when wire == 2: v["kvlistValue"] = ParseAnyList(r.ReadBytes(), isKvList: true, depth + 1); break;
                case 7 when wire == 2: v["bytesValue"] = Convert.ToBase64String(r.ReadBytes()); break;
                default: r.Skip(wire); break;
            }
        }
        return v;
    }

    private static JsonObject ParseAnyList(ReadOnlySpan<byte> data, bool isKvList, int depth)
    {
        Guard(depth);
        var values = new JsonArray();
        var r = new Reader(data, depth);
        while (r.TryReadTag(out var field, out var wire))
        {
            if (field == 1 && wire == 2)
                values.Add(isKvList ? ParseKeyValue(r.ReadBytes(), depth + 1) : ParseAnyValue(r.ReadBytes(), depth + 1));
            else r.Skip(wire);
        }
        return new JsonObject { ["values"] = values };
    }

    // ------------------------------------------------------------------- plumbing

    private static string ToHexId(ReadOnlySpan<byte> bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

    private static void Guard(int depth)
    {
        if (depth > MaxDepth) throw new FormatException("OTLP payload nests too deep.");
    }

    /// <summary>Forward-only protobuf wire reader over a span. Wire types: 0 varint,
    /// 1 fixed64, 2 length-delimited, 5 fixed32.</summary>
    private ref struct Reader(ReadOnlySpan<byte> data, int depth)
    {
        private readonly ReadOnlySpan<byte> _data = data;
        private readonly int _depth = depth;
        private int _pos;

        public bool TryReadTag(out int field, out int wire)
        {
            field = 0; wire = 0;
            if (_pos >= _data.Length) return false;
            var tag = ReadVarint();
            field = (int)(tag >> 3);
            wire = (int)(tag & 0x7);
            if (field == 0) throw new FormatException("Invalid protobuf field number 0.");
            return true;
        }

        public ulong ReadVarint()
        {
            ulong result = 0;
            var shift = 0;
            while (true)
            {
                if (_pos >= _data.Length) throw new FormatException("Truncated varint.");
                var b = _data[_pos++];
                result |= (ulong)(b & 0x7F) << shift;
                if ((b & 0x80) == 0) return result;
                shift += 7;
                if (shift >= 64) throw new FormatException("Varint too long.");
            }
        }

        public ulong ReadFixed64()
        {
            if (_pos + 8 > _data.Length) throw new FormatException("Truncated fixed64.");
            var v = BinaryPrimitives.ReadUInt64LittleEndian(_data.Slice(_pos, 8));
            _pos += 8;
            return v;
        }

        public uint ReadFixed32()
        {
            if (_pos + 4 > _data.Length) throw new FormatException("Truncated fixed32.");
            var v = BinaryPrimitives.ReadUInt32LittleEndian(_data.Slice(_pos, 4));
            _pos += 4;
            return v;
        }

        public ReadOnlySpan<byte> ReadBytes()
        {
            var len = (int)ReadVarint();
            if (len < 0 || _pos + len > _data.Length) throw new FormatException("Truncated length-delimited field.");
            var slice = _data.Slice(_pos, len);
            _pos += len;
            return slice;
        }

        public string ReadString() => Encoding.UTF8.GetString(ReadBytes());

        public void Skip(int wire)
        {
            switch (wire)
            {
                case 0: ReadVarint(); break;
                case 1: ReadFixed64(); break;
                case 2: ReadBytes(); break;
                case 5: ReadFixed32(); break;
                default: throw new FormatException($"Unsupported wire type {wire}.");
            }
        }
    }

    /// <summary>Tiny protobuf writer for the response messages (and test payload construction).</summary>
    internal sealed class Writer
    {
        private readonly MemoryStream _ms = new();

        public void WriteVarintField(int field, ulong value) { WriteTag(field, 0); WriteVarint(value); }
        public void WriteFixed64Field(int field, ulong value)
        {
            WriteTag(field, 1);
            Span<byte> buf = stackalloc byte[8];
            BinaryPrimitives.WriteUInt64LittleEndian(buf, value);
            _ms.Write(buf);
        }
        public void WriteStringField(int field, string value) => WriteBytesField(field, Encoding.UTF8.GetBytes(value));
        public void WriteBytesField(int field, byte[] value)
        {
            WriteTag(field, 2);
            WriteVarint((ulong)value.Length);
            _ms.Write(value, 0, value.Length);
        }

        private void WriteTag(int field, int wire) => WriteVarint((ulong)((field << 3) | wire));
        private void WriteVarint(ulong value)
        {
            while (value >= 0x80)
            {
                _ms.WriteByte((byte)(value | 0x80));
                value >>= 7;
            }
            _ms.WriteByte((byte)value);
        }

        public byte[] ToArray() => _ms.ToArray();
    }
}
