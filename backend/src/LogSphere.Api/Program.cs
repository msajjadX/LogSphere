using System.Text;
using LogSphere.Api.Endpoints;
using LogSphere.Api.Infrastructure;
using LogSphere.Core.Auth;
using LogSphere.Core.Data;
using LogSphere.Core.Notifications;
using LogSphere.Core.Repositories;
using LogSphere.Core.Sanitization;
using LogSphere.Core.Seeding;
using LogSphere.Core.Services;
using LogSphere.Core.Workers;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

// ---------------------------------------------------------------- secret validation
// Outside Development, refuse to start on missing/placeholder secrets rather than silently
// falling back to the well-known dev defaults (forgeable JWTs, guessable API-key pepper).
if (!builder.Environment.IsDevelopment())
{
    var problems = new List<string>();
    var jwt = builder.Configuration["Jwt:Secret"];
    if (string.IsNullOrWhiteSpace(jwt) || jwt == JwtService.DevFallbackSecret || jwt.Length < 32)
        problems.Add("Jwt:Secret (must be set, >= 32 chars, and not the dev default)");
    var pepper = Environment.GetEnvironmentVariable("LOGSPHERE_KEY_PEPPER");
    if (string.IsNullOrWhiteSpace(pepper) || pepper == "logsphere-dev-pepper-change-in-prod")
        problems.Add("LOGSPHERE_KEY_PEPPER (must be set and not the dev default)");
    if (string.IsNullOrWhiteSpace(builder.Configuration["Sanitization:HashKey"]))
        problems.Add("Sanitization:HashKey (must be set)");
    if (problems.Count > 0)
        throw new InvalidOperationException(
            $"Refusing to start in environment '{builder.Environment.EnvironmentName}': " +
            $"required secrets are unset or use insecure defaults: {string.Join("; ", problems)}. " +
            "Configure them via environment variables before starting.");
}

// ---------------------------------------------------------------- services
var connectionString = builder.Configuration.GetConnectionString("LogSphere")
    ?? "Host=localhost;Port=5432;Database=logsphere;Username=postgres;Password=postgres";

builder.Services.AddSingleton(new Db(connectionString));
builder.Services.AddSingleton<SchemaMigrator>();
builder.Services.AddSingleton<QueueRepository>();
builder.Services.AddSingleton<EventRepository>();
builder.Services.AddSingleton<StatsRepository>();
builder.Services.AddSingleton<AdminRepository>();
builder.Services.AddSingleton<AlertRepository>();
builder.Services.AddSingleton<ExceptionGroupRepository>();
builder.Services.AddSingleton<SearchRepository>();
builder.Services.AddSingleton<SystemRepository>();
builder.Services.AddSingleton<RulesProvider>();
builder.Services.AddSingleton(new SanitizationEngine(
    builder.Configuration["Sanitization:HashKey"] is { Length: > 0 } hashKey ? hashKey : null));
builder.Services.AddSingleton(sp => new EnrichmentService(
    builder.Configuration["Enrichment:GeoIpDatabasePath"],
    sp.GetRequiredService<ILogger<EnrichmentService>>()));
builder.Services.AddSingleton<IngestService>();
builder.Services.AddSingleton<ApiKeyService>();
builder.Services.AddSingleton<JwtService>();
builder.Services.AddScoped<CurrentUserResolver>();
builder.Services.AddSingleton<LoginThrottle>();
builder.Services.AddSingleton<ScopeService>();
builder.Services.AddSingleton<Notifier>();
builder.Services.AddSingleton<DevSeeder>();
builder.Services.AddHttpClient();

builder.Services.AddHostedService<PersistenceWorker>();
builder.Services.AddHostedService<AlertWorker>();
builder.Services.AddHostedService<RetentionWorker>();

var jwtSecret = builder.Configuration["Jwt:Secret"] is { Length: > 0 } configuredSecret
    ? configuredSecret
    : JwtService.DevFallbackSecret;
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = "logsphere",
            ValidateAudience = true,
            ValidAudience = "logsphere-dashboard",
            ValidateLifetime = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSecret)),
            ClockSkew = TimeSpan.FromMinutes(1)
        };
    });
builder.Services.AddAuthorization();

builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
    policy.WithOrigins(builder.Configuration.GetSection("Cors:Origins").Get<string[]>()
            ?? new[] { "http://localhost:5173" })
        .AllowAnyHeader()
        .AllowAnyMethod()
        .WithExposedHeaders("X-Correlation-ID", "Content-Disposition")));

builder.WebHost.ConfigureKestrel(kestrel =>
{
    kestrel.Limits.MaxRequestBodySize = 10 * 1024 * 1024; // 10 MB hard cap (batch limit is 5 MB)
});

var app = builder.Build();

// ---------------------------------------------------------------- startup: migrate + seed
// Database:AutoMigrate=false turns off apply-at-startup (for gated production pipelines);
// migrations are then run explicitly with:  dotnet LogSphere.Api.dll --migrate-only
var migrateOnly = args.Contains("--migrate-only");
var autoMigrate = app.Configuration.GetValue("Database:AutoMigrate", true);
var migrationsDir = app.Configuration["Database:MigrationsPath"]
    ?? Path.GetFullPath(Path.Combine(app.Environment.ContentRootPath, "..", "..", "..", "db", "migrations"));

if (Directory.Exists(migrationsDir))
{
    if (autoMigrate || migrateOnly)
    {
        using var scope = app.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<SchemaMigrator>().MigrateAsync(migrationsDir);
        await scope.ServiceProvider.GetRequiredService<DevSeeder>().SeedAsync(CancellationToken.None);
    }
    else
    {
        app.Logger.LogInformation("AutoMigrate is disabled; schema is managed via --migrate-only runs");
    }
}
else
{
    app.Logger.LogWarning("Migrations directory not found at {Path}; skipping migration", migrationsDir);
}

if (migrateOnly)
{
    app.Logger.LogInformation("--migrate-only: migrations applied, exiting without serving");
    return;
}

{
    using var scope = app.Services.CreateScope();
    await scope.ServiceProvider.GetRequiredService<Db>().WarmUpAsync();
}

// ---------------------------------------------------------------- pipeline
// Behind the nginx container, the socket address is Docker-internal — X-Forwarded-For (set by
// deploy/nginx.conf) carries the real caller. This rewrites RemoteIpAddress FIRST so everything
// downstream (access audit, login throttle, API-key IP allowlists) sees the true client.
// ForwardLimit=1 trusts only the rightmost XFF entry — the one our own proxy appended — so a
// client-supplied spoofed header is ignored.
var forwarded = new ForwardedHeadersOptions
{
    ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor |
                       Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto,
    ForwardLimit = 1,
};
forwarded.KnownIPNetworks.Clear(); // the proxy's container IP is dynamic — trust the immediate peer
forwarded.KnownProxies.Clear();
app.UseForwardedHeaders(forwarded);
app.UseMiddleware<CorrelationMiddleware>();
app.UseMiddleware<ErrorHandlingMiddleware>();
app.UseCors();
app.UseAuthentication();
app.UseAuthorization();

IngestEndpoints.Map(app);
OtlpEndpoints.Map(app);
AuthEndpoints.Map(app);
QueryEndpoints.Map(app);
OperationalEndpoints.Map(app);
AdminEndpoints.Map(app);
SystemEndpoints.Map(app);

app.Run();

public partial class Program;
