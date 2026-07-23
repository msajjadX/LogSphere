using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using MaxMind.Db;
using Microsoft.Extensions.Logging;
using UAParser;

namespace LogSphere.Core.Services;

/// <summary>
/// Best-effort, fully offline ingest enrichment: GeoIP (a local MaxMind GeoLite2-City.mmdb,
/// configured via Enrichment:GeoIpDatabasePath) and user-agent parsing (UAParser's embedded
/// regex database). Results land in the event's <c>properties.geo</c> / <c>properties.ua</c> —
/// no schema change, no egress. Every path is defensive: enrichment must never fail ingestion,
/// so a missing database, a bad IP, or a parser error simply yields null.
/// </summary>
public sealed class EnrichmentService : IDisposable
{
    private readonly Reader? _geo;
    private readonly Parser _ua = Parser.GetDefault();
    private readonly ILogger<EnrichmentService> _logger;

    public bool GeoIpEnabled => _geo is not null;

    /// <param name="geoIpDatabasePath">Path to a GeoLite2-City.mmdb, or null/empty to disable GeoIP.
    /// Comes from configuration key <c>Enrichment:GeoIpDatabasePath</c>.</param>
    public EnrichmentService(string? geoIpDatabasePath, ILogger<EnrichmentService> logger)
    {
        _logger = logger;
        var path = geoIpDatabasePath;
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            if (File.Exists(path))
            {
                _geo = new Reader(path);
                logger.LogInformation("GeoIP enrichment enabled ({Path})", path);
            }
            else
            {
                logger.LogWarning("GeoIP database not found at {Path} - geo enrichment disabled", path);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to open GeoIP database {Path} - geo enrichment disabled", path);
        }
    }

    /// <summary>Coarse location for a public IP: { country, countryCode, city }. Null for
    /// private/loopback/link-local addresses, unparseable input, or when disabled.</summary>
    public JsonObject? GeoLookup(string? ip)
    {
        if (_geo is null || string.IsNullOrWhiteSpace(ip)) return null;
        try
        {
            if (!IPAddress.TryParse(ip.Trim(), out var addr) || IsPrivate(addr)) return null;
            var hit = _geo.Find<Dictionary<string, object>>(addr);
            if (hit is null) return null;

            static string? Name(object? section)
            {
                if (section is not Dictionary<string, object> d) return null;
                return d.TryGetValue("names", out var names) && names is Dictionary<string, object> n &&
                       n.TryGetValue("en", out var en)
                    ? en as string
                    : null;
            }

            string? countryCode = null;
            if (hit.TryGetValue("country", out var c) && c is Dictionary<string, object> cd &&
                cd.TryGetValue("iso_code", out var iso))
                countryCode = iso as string;

            var country = Name(hit.GetValueOrDefault("country"));
            var city = Name(hit.GetValueOrDefault("city"));
            if (country is null && countryCode is null && city is null) return null;
            return new JsonObject { ["country"] = country, ["countryCode"] = countryCode, ["city"] = city };
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "GeoIP lookup failed for {Ip}", ip);
            return null;
        }
    }

    /// <summary>Parses a User-Agent header: { browser, browserVersion, os, device }. Null when
    /// the input is empty or nothing recognizable was found.</summary>
    public JsonObject? ParseUserAgent(string? userAgent)
    {
        if (string.IsNullOrWhiteSpace(userAgent)) return null;
        try
        {
            var info = _ua.Parse(userAgent);
            var browser = info.UA.Family is "Other" or "" ? null : info.UA.Family;
            var os = info.OS.Family is "Other" or "" ? null : info.OS.Family;
            var device = info.Device.Family is "Other" or "" ? null : info.Device.Family;
            if (browser is null && os is null && device is null) return null;

            var version = string.Join('.', new[] { info.UA.Major, info.UA.Minor }
                .Where(v => !string.IsNullOrEmpty(v)));
            return new JsonObject
            {
                ["browser"] = browser,
                ["browserVersion"] = version.Length > 0 ? version : null,
                ["os"] = os,
                ["device"] = device,
            };
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "User-agent parse failed");
            return null;
        }
    }

    private static bool IsPrivate(IPAddress addr)
    {
        if (IPAddress.IsLoopback(addr)) return true;
        if (addr.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = addr.GetAddressBytes();
            return b[0] == 10
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 169 && b[1] == 254)
                || b[0] == 0;
        }
        // IPv6: unique-local fc00::/7 and link-local fe80::/10
        var v6 = addr.GetAddressBytes();
        return (v6[0] & 0xFE) == 0xFC || (v6[0] == 0xFE && (v6[1] & 0xC0) == 0x80);
    }

    public void Dispose() => _geo?.Dispose();
}
