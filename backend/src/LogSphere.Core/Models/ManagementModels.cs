using System.Text.Json.Nodes;

namespace LogSphere.Core.Models;

public sealed class Tenant
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string Code { get; set; } = "";
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class Project
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public string Name { get; set; } = "";
    public string Code { get; set; } = "";
    public string? Description { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class Application
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public string? ProjectName { get; set; }
    public string Name { get; set; } = "";
    public string Code { get; set; } = "";
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class CredentialInfo
{
    public Guid Id { get; set; }
    public Guid ApplicationId { get; set; }
    public short EnvironmentId { get; set; }
    public string KeyId { get; set; } = "";
    public string[]? IpAllowlist { get; set; }
    public int RateLimitPerMinute { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }
}

public sealed class CreateCredentialRequest
{
    public short EnvironmentId { get; set; } = 3;
    public DateTimeOffset? ExpiresAt { get; set; }
    public string[]? IpAllowlist { get; set; }
    public int RateLimitPerMinute { get; set; } = 6000;
}

public sealed class UserAccount
{
    public Guid Id { get; set; }
    public string Username { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string? Email { get; set; }
    public bool IsActive { get; set; } = true;
    public bool MustChangePassword { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastLoginAt { get; set; }
    public List<AccessGrant> Grants { get; set; } = new();
}

public sealed class AccessGrant
{
    public Guid Id { get; set; }
    public string Role { get; set; } = Roles.ReadOnly;
    public Guid? TenantId { get; set; }
    public Guid? ProjectId { get; set; }
    public short? EnvironmentId { get; set; }
    public string[]? Categories { get; set; }
    public short? MinSeverity { get; set; }
    public bool CanViewBodies { get; set; }
    public bool CanExport { get; set; }
    public bool CanViewSecurity { get; set; }
}

public sealed class UpsertUserRequest
{
    public string Username { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string? Email { get; set; }
    public string? Password { get; set; }
    public bool IsActive { get; set; } = true;
    public List<AccessGrant> Grants { get; set; } = new();
}

public sealed class RedactionRule
{
    public Guid Id { get; set; }
    public Guid? TenantId { get; set; }
    public Guid? ProjectId { get; set; }
    public string KeyPattern { get; set; } = "";
    public bool IsRegex { get; set; }
    public string Strategy { get; set; } = "Redact";
    public string AppliesTo { get; set; } = "All";
    public bool Enabled { get; set; } = true;
}

public sealed class RetentionPolicy
{
    public Guid Id { get; set; }
    public Guid? TenantId { get; set; }
    public Guid? ProjectId { get; set; }
    public short? EnvironmentId { get; set; }
    public string? EventType { get; set; }
    public short? Severity { get; set; }
    public int RetentionDays { get; set; } = 30;
    public bool ArchiveBeforeDrop { get; set; }
    public bool LegalHold { get; set; }
}

public sealed class NotificationChannel
{
    public Guid Id { get; set; }
    public Guid? TenantId { get; set; }
    public string Name { get; set; } = "";
    public string Type { get; set; } = "Webhook";
    public string Target { get; set; } = "";
    public bool Enabled { get; set; } = true;
}

public sealed class AlertRule
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid? ProjectId { get; set; }
    public short? EnvironmentId { get; set; }
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public string ConditionType { get; set; } = "ErrorCountThreshold";
    public decimal Threshold { get; set; }
    public int WindowMinutes { get; set; } = 5;
    public int CooldownMinutes { get; set; } = 15;
    public short? SeverityFilter { get; set; }
    /// <summary>IngestSilence only: watch a specific module (contains match). Null = whole scope.</summary>
    public string? ModuleFilter { get; set; }
    /// <summary>IngestSilence only: watch a specific action name (contains match).</summary>
    public string? ActionFilter { get; set; }
    /// <summary>IngestSilence only: trigger when the window saw ≤ this many events (0 = total silence).</summary>
    public int MinCount { get; set; }
    /// <summary>Schedule (any condition type): rule is evaluated only inside this window. Minutes
    /// since midnight in <see cref="TimeZone"/>; from &gt; to wraps past midnight. Null = always.</summary>
    public int? ActiveFromMinute { get; set; }
    public int? ActiveToMinute { get; set; }
    /// <summary>Days the rule is active (0 = Sunday … 6 = Saturday). Null/empty = every day.</summary>
    public short[]? ActiveDays { get; set; }
    /// <summary>IANA time zone the schedule is expressed in (e.g. "Asia/Karachi"). Null = UTC.</summary>
    public string? TimeZone { get; set; }
    public Guid[] Channels { get; set; } = Array.Empty<Guid>();
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastTriggeredAt { get; set; }
}

public sealed class AlertOccurrence
{
    public Guid Id { get; set; }
    public Guid RuleId { get; set; }
    public string? RuleName { get; set; }
    public Guid? ProjectId { get; set; }
    public DateTimeOffset TriggeredAt { get; set; }
    public string State { get; set; } = "Open";
    public string Summary { get; set; } = "";
    public JsonNode? Details { get; set; }
    public Guid? AcknowledgedBy { get; set; }
    public DateTimeOffset? AcknowledgedAt { get; set; }
    public Guid? ResolvedBy { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
}

public sealed class SavedSearch
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string Name { get; set; } = "";
    public JsonNode? Filter { get; set; }
    public bool Shared { get; set; }
    public bool Pinned { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class ExceptionGroup
{
    public Guid Id { get; set; }
    public string Fingerprint { get; set; } = "";
    public Guid TenantId { get; set; }
    public Guid ProjectId { get; set; }
    public string? ProjectName { get; set; }
    public string? ApplicationName { get; set; }
    public string? EnvironmentName { get; set; }
    public string? ExceptionType { get; set; }
    public string? Message { get; set; }
    public string? Module { get; set; }
    public DateTimeOffset FirstSeen { get; set; }
    public DateTimeOffset LastSeen { get; set; }
    public long TotalCount { get; set; }
    public long LastHourCount { get; set; }
    public long Last24hCount { get; set; }
    public string Status { get; set; } = "New";
    public Guid? AssignedTo { get; set; }
    public string? AssignedToName { get; set; }
    public string? Notes { get; set; }
    public string? LinkedIssueUrl { get; set; }
    public Guid? SampleEventId { get; set; }
    public string[] AffectedVersions { get; set; } = Array.Empty<string>();
}

public sealed class PatchExceptionGroupRequest
{
    public string? Status { get; set; }
    public Guid? AssignedToUserId { get; set; }
    public string? Notes { get; set; }
    public string? LinkedIssueUrl { get; set; }
}

public sealed class AccessAuditEntry
{
    public long Id { get; set; }
    public Guid? UserId { get; set; }
    public string? Username { get; set; }
    public string Action { get; set; } = "";
    public JsonNode? Details { get; set; }
    public string? Ip { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
}

public sealed class DeadLetterEntry
{
    public long Id { get; set; }
    public Guid? EventId { get; set; }
    public JsonNode? Payload { get; set; }
    public string Reason { get; set; } = "";
    public int Attempts { get; set; }
    public DateTimeOffset FailedAt { get; set; }
}
