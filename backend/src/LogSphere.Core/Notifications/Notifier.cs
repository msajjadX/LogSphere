using System.Net;
using System.Net.Http.Json;
using System.Net.Mail;
using System.Net.Sockets;
using LogSphere.Core.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace LogSphere.Core.Notifications;

/// <summary>Dispatches alert notifications to Email (SMTP) and Webhook (Slack/Teams-compatible JSON) channels.</summary>
public sealed class Notifier(IHttpClientFactory httpFactory, IConfiguration config, ILogger<Notifier> logger)
{
    public async Task DispatchAsync(IEnumerable<NotificationChannel> channels, string title, string body, CancellationToken ct)
    {
        foreach (var channel in channels.Where(c => c.Enabled))
        {
            try
            {
                switch (channel.Type)
                {
                    case "Email":
                        await SendEmailAsync(channel.Target, title, body, ct);
                        break;
                    case "Webhook":
                        await SendWebhookAsync(channel.Target, title, body, ct);
                        break;
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Notification dispatch failed for channel {Channel} ({Type})", channel.Name, channel.Type);
            }
        }
    }

    private async Task SendEmailAsync(string to, string subject, string body, CancellationToken ct)
    {
        var host = config["Smtp:Host"];
        if (string.IsNullOrEmpty(host))
        {
            logger.LogInformation("SMTP not configured; skipping email to {To}: {Subject}", to, subject);
            return;
        }
        using var client = new SmtpClient(host, config.GetValue("Smtp:Port", 587));
        if (config["Smtp:Username"] is { Length: > 0 } username)
            client.Credentials = new System.Net.NetworkCredential(username, config["Smtp:Password"]);
        client.EnableSsl = config.GetValue("Smtp:UseSsl", true);
        using var message = new MailMessage(config["Smtp:From"] ?? "logsphere@localhost", to, subject, body);
        await client.SendMailAsync(message, ct);
    }

    private async Task SendWebhookAsync(string url, string title, string body, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            return;

        // SSRF guard: refuse to call loopback/private/link-local targets unless explicitly allowed,
        // so an admin-created channel cannot reach cloud metadata or internal services.
        if (!config.GetValue("Notifications:AllowPrivateWebhookTargets", false) &&
            !await IsPublicHostAsync(uri.Host, ct))
        {
            logger.LogWarning("Blocked webhook to non-public host {Host}", uri.Host);
            return;
        }

        var client = httpFactory.CreateClient("notifier");
        client.Timeout = TimeSpan.FromSeconds(10);
        // "text" works for Slack and most generic receivers; extra fields help Teams/other consumers
        await client.PostAsJsonAsync(uri, new { text = $"*{title}*\n{body}", title, message = body }, ct);
    }

    /// <summary>True only when every resolved address of the host is a routable public address.
    /// Fails closed (returns false) on resolution errors or when no addresses resolve.</summary>
    private async Task<bool> IsPublicHostAsync(string host, CancellationToken ct)
    {
        try
        {
            IPAddress[] addresses = IPAddress.TryParse(host, out var literal)
                ? new[] { literal }
                : await Dns.GetHostAddressesAsync(host, ct);
            return addresses.Length > 0 && addresses.All(IsPublicAddress);
        }
        catch (Exception ex) when (ex is SocketException or ArgumentException)
        {
            return false;
        }
    }

    private static bool IsPublicAddress(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();

        if (IPAddress.IsLoopback(ip)) return false;                       // 127.0.0.0/8, ::1
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            if (b[0] == 10) return false;                                 // 10.0.0.0/8
            if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return false;    // 172.16.0.0/12
            if (b[0] == 192 && b[1] == 168) return false;                 // 192.168.0.0/16
            if (b[0] == 169 && b[1] == 254) return false;                 // 169.254.0.0/16 link-local (incl. cloud metadata)
            if (b[0] == 100 && b[1] >= 64 && b[1] <= 127) return false;   // 100.64.0.0/10 CGNAT
            if (b[0] == 0) return false;                                  // 0.0.0.0/8
            return true;
        }
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal) return false;
            var b = ip.GetAddressBytes();
            if ((b[0] & 0xFE) == 0xFC) return false;                      // fc00::/7 unique local
            return true;
        }
        return false;
    }
}
