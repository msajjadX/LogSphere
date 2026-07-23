namespace LogSphere.Core.Models;

public static class EventTypes
{
    public const string ApiRequest = "ApiRequest";
    public const string ApiResponse = "ApiResponse";
    public const string Audit = "Audit";
    public const string Exception = "Exception";
    public const string Performance = "Performance";
    public const string Integration = "Integration";
    public const string BackgroundJob = "BackgroundJob";
    public const string Security = "Security";
    public const string Custom = "Custom";
    public const string Application = "Application";

    public static readonly string[] All =
    {
        ApiRequest, ApiResponse, Audit, Exception, Performance,
        Integration, BackgroundJob, Security, Custom, Application
    };

    public static bool IsValid(string? value) => value is not null && Array.IndexOf(All, value) >= 0;
}

public static class Severities
{
    public static readonly string[] Names = { "Trace", "Debug", "Information", "Warning", "Error", "Critical" };

    public static short? Parse(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var i = Array.FindIndex(Names, n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
        return i >= 0 ? (short)i : null;
    }

    public static string Name(short id) => id >= 0 && id < Names.Length ? Names[id] : "Information";
}

public static class OperationStatuses
{
    public static readonly string[] All =
    {
        "Started", "InProgress", "Completed", "Failed", "Retrying", "Cancelled", "TimedOut", "PartiallyCompleted"
    };

    public static bool IsValid(string? value) => value is not null && Array.IndexOf(All, value) >= 0;
}

public static class Roles
{
    public const string SuperAdmin = "SuperAdmin";
    public const string TenantAdmin = "TenantAdmin";
    public const string ProjectAdmin = "ProjectAdmin";
    public const string Developer = "Developer";
    public const string Support = "Support";
    public const string SecurityAuditor = "SecurityAuditor";
    public const string ComplianceAuditor = "ComplianceAuditor";
    public const string ReadOnly = "ReadOnly";

    public static readonly string[] All =
    {
        SuperAdmin, TenantAdmin, ProjectAdmin, Developer, Support,
        SecurityAuditor, ComplianceAuditor, ReadOnly
    };
}

public static class ExceptionGroupStatuses
{
    public static readonly string[] All = { "New", "Investigating", "Identified", "Resolved", "Ignored", "Recurring" };
    public static bool IsValid(string? value) => value is not null && Array.IndexOf(All, value) >= 0;
}
