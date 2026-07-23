using LogSphere.Core.Auth;
using LogSphere.Core.Fingerprinting;
using LogSphere.Core.Models;
using LogSphere.Core.Repositories;
using Npgsql;
using Xunit;

namespace LogSphere.Tests;

public class FingerprinterTests
{
    private static ExceptionInfo Ex(string stack) => new()
    {
        Type = "NullReferenceException",
        Message = "Object reference not set",
        ClassName = "PaymentService",
        MethodName = "Charge",
        StackTrace = stack
    };

    [Fact]
    public void Same_exception_different_ids_and_lines_share_fingerprint()
    {
        var a = ExceptionFingerprinter.Compute(
            Ex("at PaymentService.Charge(Guid id) in /src/Pay.cs:line 42\nat Pipeline.Invoke()"), "Payments");
        var b = ExceptionFingerprinter.Compute(
            Ex("at PaymentService.Charge(Guid id) in /src/Pay.cs:line 97\nat Pipeline.Invoke()"), "Payments");
        Assert.Equal(a, b);
    }

    [Fact]
    public void Different_exception_types_differ()
    {
        var a = ExceptionFingerprinter.Compute(Ex("at A.B()"), "Payments");
        var other = new ExceptionInfo { Type = "TimeoutException", ClassName = "PaymentService", MethodName = "Charge", StackTrace = "at A.B()" };
        var b = ExceptionFingerprinter.Compute(other, "Payments");
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Guids_numbers_and_strings_are_normalized_out()
    {
        var normalized = ExceptionFingerprinter.NormalizeStack(
            "at Svc.Do(Guid 3f2504e0-4f89-11d3-9a0c-0305e82c3301) in C:\\app\\Svc.cs:line 10\nat X.Y(\"literal\")");
        Assert.DoesNotContain("3f2504e0", normalized);
        Assert.DoesNotContain("line 10", normalized);
        Assert.DoesNotContain("literal", normalized);
    }
}

public class PasswordHasherTests
{
    [Fact]
    public void Roundtrip_verifies() =>
        Assert.True(PasswordHasher.Verify("S3cure#Pass!", PasswordHasher.Hash("S3cure#Pass!")));

    [Fact]
    public void Wrong_password_fails() =>
        Assert.False(PasswordHasher.Verify("wrong", PasswordHasher.Hash("right-password")));

    [Fact]
    public void Hashes_are_salted() =>
        Assert.NotEqual(PasswordHasher.Hash("same"), PasswordHasher.Hash("same"));
}

public class AccessControlTests
{
    private static UserContext User(params AccessGrant[] grants) =>
        new() { UserId = Guid.NewGuid(), Username = "test", Grants = grants.ToList() };

    [Fact]
    public void SuperAdmin_is_unrestricted()
    {
        using var cmd = new NpgsqlCommand();
        var predicate = User(new AccessGrant { Role = Roles.SuperAdmin }).BuildEventPredicate(cmd);
        Assert.Equal("TRUE", predicate);
    }

    [Fact]
    public void No_grants_means_no_rows()
    {
        using var cmd = new NpgsqlCommand();
        Assert.Equal("FALSE", User().BuildEventPredicate(cmd));
    }

    [Fact]
    public void Project_scoped_grant_filters_by_project_and_hides_security()
    {
        var projectId = Guid.NewGuid();
        using var cmd = new NpgsqlCommand();
        var predicate = User(new AccessGrant { Role = Roles.Developer, ProjectId = projectId })
            .BuildEventPredicate(cmd);
        Assert.Contains("project_id", predicate);
        Assert.Contains("<> 'Security'", predicate);
        Assert.Single(cmd.Parameters);
        Assert.Equal(projectId, cmd.Parameters[0].Value);
    }

    [Fact]
    public void Security_grant_does_not_exclude_security_events()
    {
        using var cmd = new NpgsqlCommand();
        var predicate = User(new AccessGrant { Role = Roles.SecurityAuditor, CanViewSecurity = true })
            .BuildEventPredicate(cmd);
        Assert.DoesNotContain("<> 'Security'", predicate);
    }

    [Fact]
    public void Body_visibility_is_scoped()
    {
        var projectA = Guid.NewGuid();
        var projectB = Guid.NewGuid();
        var user = User(new AccessGrant { Role = Roles.Support, ProjectId = projectA, CanViewBodies = true },
                        new AccessGrant { Role = Roles.Support, ProjectId = projectB, CanViewBodies = false });
        Assert.True(user.CanViewBodies(null, projectA));
        Assert.False(user.CanViewBodies(null, projectB));
    }

    [Fact]
    public void Severity_floor_is_applied()
    {
        using var cmd = new NpgsqlCommand();
        var predicate = User(new AccessGrant { Role = Roles.ReadOnly, MinSeverity = 4 }).BuildEventPredicate(cmd);
        Assert.Contains("severity >=", predicate);
    }

    // Regression: a tenant-scoped grant must NOT be treated as platform-wide in the exceptions
    // repository. VisibleScopes returns null projectIds for such a grant; the scope clause must
    // still restrict by tenant rather than degrade to TRUE (cross-tenant read/write break).
    [Fact]
    public void Exception_scope_restricts_tenant_scoped_grant()
    {
        var tenantId = Guid.NewGuid();
        using var cmd = new NpgsqlCommand();
        var clause = ExceptionGroupRepository.ScopeClause(
            cmd, User(new AccessGrant { Role = Roles.ReadOnly, TenantId = tenantId }), "g");
        Assert.Contains("tenant_id = ANY", clause);
        Assert.NotEqual("TRUE", clause);
    }

    [Fact]
    public void Exception_scope_is_unrestricted_only_for_platform_wide_grant()
    {
        using var cmd = new NpgsqlCommand();
        Assert.Equal("TRUE", ExceptionGroupRepository.ScopeClause(cmd, User(new AccessGrant { Role = Roles.SuperAdmin }), "g"));
        // a grant with neither tenant nor project set is genuinely platform-wide
        Assert.Equal("TRUE", ExceptionGroupRepository.ScopeClause(cmd, User(new AccessGrant { Role = Roles.ReadOnly }), "g"));
    }

    [Fact]
    public void Exception_scope_restricts_project_scoped_grant()
    {
        var projectId = Guid.NewGuid();
        using var cmd = new NpgsqlCommand();
        var clause = ExceptionGroupRepository.ScopeClause(
            cmd, User(new AccessGrant { Role = Roles.Developer, ProjectId = projectId }), "g");
        Assert.Contains("project_id = ANY", clause);
        Assert.NotEqual("TRUE", clause);
    }
}

public class SdkSanitizerTests
{
    [Fact]
    public void Sdk_layer_redacts_default_secrets()
    {
        var sanitizer = new LogSphere.Sdk.ClientSanitizer();
        var node = System.Text.Json.Nodes.JsonNode.Parse(
            """{ "password": "hunter2", "nested": { "Api-Key": "abc123" }, "ok": 1 }""");
        var result = sanitizer.Sanitize(node)!.ToJsonString();
        Assert.DoesNotContain("hunter2", result);
        Assert.DoesNotContain("abc123", result);
        Assert.Contains("\"ok\":1", result);
    }

    [Fact]
    public void Business_scope_restores_previous_context()
    {
        using (LogSphere.Sdk.CorrelationContext.BeginBusinessScope("Payment", "P-1"))
        {
            Assert.Equal("Payment", LogSphere.Sdk.CorrelationContext.BusinessEntityType);
            using (LogSphere.Sdk.CorrelationContext.BeginBusinessScope("Invoice", "I-9"))
                Assert.Equal("Invoice", LogSphere.Sdk.CorrelationContext.BusinessEntityType);
            Assert.Equal("Payment", LogSphere.Sdk.CorrelationContext.BusinessEntityType);
        }
        Assert.Null(LogSphere.Sdk.CorrelationContext.BusinessEntityType);
    }
}

public class EnvelopeValidationTests
{
    [Theory]
    [InlineData("ApiRequest", true)]
    [InlineData("Exception", true)]
    [InlineData("Custom", true)]
    [InlineData("NotAType", false)]
    [InlineData(null, false)]
    public void Event_types_are_controlled(string? type, bool expected) =>
        Assert.Equal(expected, EventTypes.IsValid(type));

    [Theory]
    [InlineData("Information", 2)]
    [InlineData("critical", 5)]
    [InlineData("TRACE", 0)]
    public void Severities_parse_case_insensitively(string name, short expected) =>
        Assert.Equal(expected, Severities.Parse(name));

    [Fact]
    public void Unknown_severity_is_rejected() => Assert.Null(Severities.Parse("Fatal"));
}
