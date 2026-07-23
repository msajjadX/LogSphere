using System.Text.Json.Nodes;
using LogSphere.Core.Sanitization;
using Xunit;

namespace LogSphere.Tests;

public class SanitizationTests
{
    private readonly SanitizationEngine _engine = new("test-key");
    private readonly SanitizationLimits _limits = new();

    private (JsonNode? Node, SanitizationResult Result) Run(JsonNode node) =>
        _engine.Sanitize(node, RedactionRuleSet.Default, _limits);

    [Theory]
    [InlineData("password")]
    [InlineData("Password")]
    [InlineData("PASSWORD")]
    [InlineData("user_password")]
    [InlineData("client_secret")]
    [InlineData("api_key")]
    [InlineData("apiKey")]
    [InlineData("access_token")]
    [InlineData("refresh_token")]
    [InlineData("private_key")]
    [InlineData("connectionString")]
    [InlineData("cvv")]
    [InlineData("otp")]
    public void Secret_keys_are_removed_entirely(string key)
    {
        var fakeSecret = "SUPER-SECRET-VALUE-12345";
        var (result, stats) = Run(new JsonObject { [key] = fakeSecret, ["safe"] = "ok" });
        var json = result!.ToJsonString();
        Assert.DoesNotContain(fakeSecret, json);
        Assert.True(stats.Applied);
        Assert.Equal("ok", result!["safe"]!.GetValue<string>());
    }

    [Fact]
    public void Nested_and_array_secrets_are_sanitized()
    {
        var node = JsonNode.Parse("""
            {
              "level1": {
                "items": [ { "Authorization": "Bearer abc.def.ghi" }, { "data": { "PIN": "9876" } } ],
                "cookie": "session=deadbeef"
              }
            }
            """)!;
        var (result, stats) = Run(node);
        var json = result!.ToJsonString();
        Assert.DoesNotContain("abc.def.ghi", json);
        Assert.DoesNotContain("9876", json);
        Assert.DoesNotContain("deadbeef", json);
        Assert.True(stats.FieldsSanitized >= 3);
    }

    [Fact]
    public void MaskLast4_preserves_only_tail()
    {
        var (result, _) = Run(new JsonObject { ["card_number"] = "4111111111111111" });
        var value = result!["card_number"]!.GetValue<string>();
        Assert.EndsWith("1111", value);
        Assert.DoesNotContain("4111111111111111", value);
        Assert.StartsWith("*", value);
    }

    [Fact]
    public void Query_strings_are_sanitized()
    {
        var (sanitized, count) = _engine.SanitizeQueryString("page=1&password=hunter2&x=y", RedactionRuleSet.Default);
        Assert.DoesNotContain("hunter2", sanitized);
        Assert.Contains("page=1", sanitized);
        Assert.Equal(1, count);
    }

    [Fact]
    public void Oversized_strings_are_truncated_with_marker()
    {
        var big = new string('a', 20_000);
        var (result, stats) = Run(new JsonObject { ["data"] = big });
        Assert.True(stats.Truncated);
        Assert.Contains("[TRUNCATED]", result!["data"]!.GetValue<string>());
    }

    [Fact]
    public void Control_characters_are_stripped_to_prevent_log_forging()
    {
        // ANSI escape (ESC) and BEL injected into a log message must not survive sanitization
        var input = "line1" + (char)27 + "[31mforged" + (char)7 + " end";
        var (result, _) = Run(new JsonObject { ["msg"] = input });
        var value = result!["msg"]!.GetValue<string>();
        Assert.DoesNotContain((char)27, value);
        Assert.DoesNotContain((char)7, value);
        Assert.Contains("line1", value);
        Assert.Contains("end", value);
    }

    [Fact]
    public void Size_enforcement_replaces_huge_payloads()
    {
        var obj = new JsonObject();
        for (var i = 0; i < 500; i++) obj[$"k{i}"] = new string('x', 500);
        var (node, truncated) = SanitizationEngine.EnforceSize(obj, 10_000);
        Assert.True(truncated);
        Assert.True(node!["_truncated"]!.GetValue<bool>());
    }

    [Fact]
    public void Project_specific_regex_rules_apply()
    {
        var rules = new RedactionRuleSet(new[]
        {
            new LogSphere.Core.Models.RedactionRule
                { KeyPattern = "^cust_ref_\\d+$", IsRegex = true, Strategy = "Redact", AppliesTo = "All", Enabled = true }
        });
        var (result, stats) = _engine.Sanitize(new JsonObject { ["cust_ref_42"] = "sensitive" }, rules, _limits);
        Assert.Equal("[REDACTED]", result!["cust_ref_42"]!.GetValue<string>());
        Assert.True(stats.Applied);
    }
}
