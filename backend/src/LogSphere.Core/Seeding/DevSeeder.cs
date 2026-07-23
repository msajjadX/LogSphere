using System.Text.Json.Nodes;
using LogSphere.Core.Auth;
using LogSphere.Core.Data;
using LogSphere.Core.Models;
using LogSphere.Core.Repositories;
using LogSphere.Core.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace LogSphere.Core.Seeding;

/// <summary>Development seeding: admin account, demo tenant/projects/applications with credentials,
/// and a batch of realistic synthetic events so dashboards are populated out of the box.</summary>
public sealed class DevSeeder(
    Db db,
    AdminRepository admin,
    ApiKeyService apiKeys,
    IngestService ingest,
    IConfiguration config,
    ILogger<DevSeeder> logger)
{
    public async Task SeedAsync(CancellationToken ct)
    {
        await SeedAdminAsync(ct);
        if (config.GetValue("Seed:DemoData", false))
            await SeedDemoAsync(ct);
    }

    private async Task SeedAdminAsync(CancellationToken ct)
    {
        var existing = await admin.GetUserByUsernameAsync("admin", ct);
        if (existing is not null) return;

        // Use the configured password if present, otherwise generate a random one (never a baked-in
        // default that survives to production). Either way the account is flagged must-change so the
        // initial credential cannot be used beyond the first forced rotation.
        var configured = config["Seed:AdminPassword"];
        var generated = string.IsNullOrWhiteSpace(configured);
        var password = generated ? "Chg#" + Guid.NewGuid().ToString("N")[..16] : configured!;

        var userId = await admin.UpsertUserAsync(null, new UpsertUserRequest
        {
            Username = "admin",
            DisplayName = "Platform Administrator",
            Password = password,
            IsActive = true,
            Grants = new List<AccessGrant>
            {
                new() { Role = Roles.SuperAdmin, CanViewBodies = true, CanExport = true, CanViewSecurity = true }
            }
        }, ct);
        await admin.SetPasswordAsync(userId, password, mustChange: true, ct);
        if (generated)
            logger.LogWarning("Seeded admin user 'admin' with a generated one-time password: {Password} — sign in and change it now", password);
        else
            logger.LogWarning("Seeded admin user 'admin' (must change password at first login)");
    }

    private async Task SeedDemoAsync(CancellationToken ct)
    {
        await using (var check = db.Cmd("SELECT count(*) FROM tenants"))
        {
            if ((long)(await check.ExecuteScalarAsync(ct) ?? 0L) > 0) return;
        }

        logger.LogInformation("Seeding demo tenant, projects, applications and sample events...");

        var tenant = await admin.UpsertTenantAsync(new Tenant { Name = "Our Company", Code = "our-company" }, ct);

        var etea = await admin.UpsertProjectAsync(new Project
        {
            TenantId = tenant.Id, Name = "ETEA Examination Platform", Code = "etea-exams",
            Description = "Candidate registration, examinations and results"
        }, ct);
        var erp = await admin.UpsertProjectAsync(new Project
        {
            TenantId = tenant.Id, Name = "Restaurant ERP", Code = "restaurant-erp",
            Description = "Billing, inventory and payments"
        }, ct);

        var regApi = await admin.UpsertApplicationAsync(new Application { ProjectId = etea.Id, Name = "Candidate Registration API", Code = "registration-api" }, ct);
        var examApi = await admin.UpsertApplicationAsync(new Application { ProjectId = etea.Id, Name = "Exam Engine", Code = "exam-engine" }, ct);
        var billingApi = await admin.UpsertApplicationAsync(new Application { ProjectId = erp.Id, Name = "Billing API", Code = "billing-api" }, ct);

        foreach (var (app, name) in new[] { (regApi, "registration-api"), (examApi, "exam-engine"), (billingApi, "billing-api") })
        {
            var (_, key) = await apiKeys.CreateAsync(app.Id, new CreateCredentialRequest { EnvironmentId = 3 }, ct);
            logger.LogWarning("DEMO API KEY for {App}: {Key}", name, key);
        }

        await SeedEventsAsync(tenant.Id, etea.Id, regApi.Id, examApi.Id, erp.Id, billingApi.Id, ct);
    }

    private async Task SeedEventsAsync(Guid tenantId, Guid eteaId, Guid regApiId, Guid examApiId,
        Guid erpId, Guid billingApiId, CancellationToken ct)
    {
        var random = new Random(42);
        var now = DateTimeOffset.UtcNow;
        string[] routes = { "/api/candidates", "/api/candidates/{id}", "/api/exams/schedule", "/api/documents/verify", "/api/payments", "/api/invoices" };
        string[] modules = { "CandidateRegistration", "DocumentVerification", "ExamScheduling", "Payments", "Billing" };
        string[] users = { "u-1001", "u-1002", "u-2001", "system" };

        foreach (var (projectId, appId, appName) in new[]
                 {
                     (eteaId, regApiId, "registration-api"),
                     (eteaId, examApiId, "exam-engine"),
                     (erpId, billingApiId, "billing-api")
                 })
        {
            var ctx = new IngestContext(Guid.NewGuid(), tenantId, projectId, appId, 3, "seed", appName, 100000, null);
            var events = new List<JsonNode?>();

            for (var i = 0; i < 600; i++)
            {
                var ts = now.AddMinutes(-random.Next(0, 24 * 60));
                var correlationId = Guid.NewGuid().ToString("N")[..24];
                var traceId = Guid.NewGuid().ToString("N");
                var route = routes[random.Next(routes.Length)];
                var module = modules[random.Next(modules.Length)];
                var userId = users[random.Next(users.Length)];
                var isError = random.Next(100) < 8;
                var duration = Math.Round(random.NextDouble() * (isError ? 4000 : 800) + 20, 1);
                var statusCode = isError ? (random.Next(2) == 0 ? 500 : 502) : 200;

                events.Add(new JsonObject
                {
                    ["eventType"] = "ApiRequest",
                    ["severity"] = "Information",
                    ["eventTimestamp"] = ts.UtcDateTime,
                    ["correlationId"] = correlationId,
                    ["traceId"] = traceId,
                    ["spanId"] = "root",
                    ["module"] = module,
                    ["message"] = $"HTTP POST {route}",
                    ["userId"] = userId,
                    ["http"] = new JsonObject { ["method"] = "POST", ["route"] = route, ["clientIp"] = $"10.0.0.{random.Next(1, 250)}", ["userAgent"] = "demo-client/1.0" },
                    ["requestData"] = new JsonObject { ["sample"] = true, ["password"] = "should-never-be-stored", ["name"] = "Demo Candidate" },
                    ["machineName"] = $"pod-{random.Next(1, 4)}",
                    ["applicationVersion"] = "2.4.1"
                });
                events.Add(new JsonObject
                {
                    ["eventType"] = "ApiResponse",
                    ["severity"] = isError ? "Error" : "Information",
                    ["status"] = isError ? "Failed" : "Completed",
                    ["eventTimestamp"] = ts.AddMilliseconds(duration).UtcDateTime,
                    ["correlationId"] = correlationId,
                    ["traceId"] = traceId,
                    ["spanId"] = "response",
                    ["parentSpanId"] = "root",
                    ["module"] = module,
                    ["message"] = $"HTTP {statusCode} {route} in {duration} ms",
                    ["durationMs"] = duration,
                    ["http"] = new JsonObject { ["method"] = "POST", ["route"] = route, ["statusCode"] = statusCode },
                    ["machineName"] = $"pod-{random.Next(1, 4)}",
                    ["applicationVersion"] = "2.4.1"
                });

                if (isError)
                {
                    var exTypes = new[]
                    {
                        ("NullReferenceException", "Object reference not set to an instance of an object.", "DocumentVerificationService", "VerifyAsync"),
                        ("TimeoutException", "The operation timed out after 30000 ms.", "PaymentGatewayClient", "ChargeAsync"),
                        ("InvalidOperationException", "Sequence contains no matching element.", "ExamScheduler", "AssignSeat")
                    };
                    var (exType, exMsg, cls, method) = exTypes[random.Next(exTypes.Length)];
                    events.Add(new JsonObject
                    {
                        ["eventType"] = "Exception",
                        ["severity"] = random.Next(10) == 0 ? "Critical" : "Error",
                        ["eventTimestamp"] = ts.AddMilliseconds(duration - 5).UtcDateTime,
                        ["correlationId"] = correlationId,
                        ["traceId"] = traceId,
                        ["spanId"] = "exception",
                        ["parentSpanId"] = "root",
                        ["module"] = module,
                        ["message"] = exMsg,
                        ["userId"] = userId,
                        ["exception"] = new JsonObject
                        {
                            ["type"] = exType,
                            ["message"] = exMsg,
                            ["className"] = cls,
                            ["methodName"] = method,
                            ["stackTrace"] = $"at {cls}.{method}(Request request) in /src/{cls}.cs:line {random.Next(40, 200)}\nat MiddlewarePipeline.Invoke(HttpContext context)",
                            ["errorCode"] = $"E-{exType[..4].ToUpperInvariant()}"
                        },
                        ["machineName"] = $"pod-{random.Next(1, 4)}",
                        ["applicationVersion"] = "2.4.1"
                    });
                }

                if (random.Next(100) < 15)
                {
                    var entityId = $"CAND-{random.Next(10000, 99999)}";
                    events.Add(new JsonObject
                    {
                        ["eventType"] = "Audit",
                        ["severity"] = "Information",
                        ["status"] = "Completed",
                        ["eventTimestamp"] = ts.AddSeconds(1).UtcDateTime,
                        ["correlationId"] = correlationId,
                        ["module"] = module,
                        ["actionName"] = random.Next(2) == 0 ? "CandidateApproved" : "ApplicationUpdated",
                        ["message"] = "Record updated by reviewer",
                        ["userId"] = userId,
                        ["userName"] = $"Reviewer {userId}",
                        ["businessEntityType"] = "CandidateApplication",
                        ["businessEntityId"] = entityId,
                        ["http"] = new JsonObject { ["clientIp"] = $"10.0.0.{random.Next(1, 250)}" },
                        ["requestData"] = new JsonObject { ["status"] = "Pending", ["remarks"] = "" },
                        ["responseData"] = new JsonObject { ["status"] = "Approved", ["remarks"] = "Verified" },
                        ["properties"] = new JsonObject
                        {
                            ["changedFields"] = new JsonArray("status", "remarks"),
                            ["reason"] = "Document verification passed"
                        }
                    });
                }

                if (random.Next(100) < 5)
                {
                    events.Add(new JsonObject
                    {
                        ["eventType"] = "Security",
                        ["severity"] = "Warning",
                        ["eventTimestamp"] = ts.UtcDateTime,
                        ["correlationId"] = correlationId,
                        ["module"] = "Authentication",
                        ["actionName"] = "FailedLogin",
                        ["message"] = "Failed login attempt",
                        ["userId"] = userId,
                        ["http"] = new JsonObject { ["clientIp"] = $"203.0.113.{random.Next(1, 250)}" }
                    });
                }
            }

            foreach (var chunk in events.Chunk(200))
                await ingest.IngestAsync(chunk, ctx, null, ct);
        }
        logger.LogInformation("Demo events enqueued; the persistence worker will store them shortly.");
    }
}
