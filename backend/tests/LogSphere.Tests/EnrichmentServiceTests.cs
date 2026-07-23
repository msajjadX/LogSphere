using LogSphere.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LogSphere.Tests;

public class EnrichmentServiceTests
{
    private static EnrichmentService Svc(string? geoDbPath = null) =>
        new(geoDbPath, NullLogger<EnrichmentService>.Instance);

    // ------------------------------------------------------------- user agent

    [Fact]
    public void Chrome_on_windows_parses_browser_version_and_os()
    {
        using var svc = Svc();
        var ua = svc.ParseUserAgent(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36");

        Assert.NotNull(ua);
        Assert.Equal("Chrome", (string?)ua!["browser"]);
        Assert.StartsWith("126", (string?)ua["browserVersion"]);
        Assert.Equal("Windows", (string?)ua["os"]);
    }

    [Fact]
    public void Mobile_safari_reports_device()
    {
        using var svc = Svc();
        var ua = svc.ParseUserAgent(
            "Mozilla/5.0 (iPhone; CPU iPhone OS 17_5 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.5 Mobile/15E148 Safari/604.1");

        Assert.NotNull(ua);
        Assert.Equal("Mobile Safari", (string?)ua!["browser"]);
        Assert.Equal("iOS", (string?)ua["os"]);
        Assert.Equal("iPhone", (string?)ua["device"]);
    }

    [Fact]
    public void Curl_is_recognized()
    {
        using var svc = Svc();
        var ua = svc.ParseUserAgent("curl/8.5.0");
        Assert.NotNull(ua);
        Assert.Equal("curl", (string?)ua!["browser"]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_user_agent_yields_null(string? input)
    {
        using var svc = Svc();
        Assert.Null(svc.ParseUserAgent(input));
    }

    // ------------------------------------------------------------------ geoip

    [Fact]
    public void Geo_disabled_without_database()
    {
        using var svc = Svc();
        Assert.False(svc.GeoIpEnabled);
        Assert.Null(svc.GeoLookup("8.8.8.8"));
    }

    [Fact]
    public void Missing_database_path_disables_geo_instead_of_throwing()
    {
        using var svc = Svc(@"C:\does\not\exist\GeoLite2-City.mmdb");
        Assert.False(svc.GeoIpEnabled);
        Assert.Null(svc.GeoLookup("8.8.8.8"));
    }

    [Theory]
    [InlineData("10.1.2.3")]
    [InlineData("172.16.31.9")]
    [InlineData("192.168.1.10")]
    [InlineData("127.0.0.1")]
    [InlineData("169.254.9.9")]
    [InlineData("not-an-ip")]
    [InlineData("")]
    [InlineData(null)]
    public void Private_or_invalid_addresses_never_resolve(string? ip)
    {
        using var svc = Svc();
        Assert.Null(svc.GeoLookup(ip));
    }
}
