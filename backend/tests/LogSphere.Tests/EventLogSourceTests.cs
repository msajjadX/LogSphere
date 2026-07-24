using LogSphere.Tail;
using Xunit;

namespace LogSphere.Tests;

public class EventLogSourceTests
{
    private static TailSource Source(Action<TailSource>? mutate = null)
    {
        var s = new TailSource { EventLog = "Application" };
        mutate?.Invoke(s);
        s.Validate();
        return s;
    }

    // -------------------------------------------------------------- level parsing

    [Theory]
    [InlineData(null, 4)]          // default: Information
    [InlineData("Critical", 1)]
    [InlineData("error", 2)]
    [InlineData("WARNING", 3)]
    [InlineData("Info", 4)]
    [InlineData("Verbose", 5)]
    public void MinLevel_parses_names(string? name, int expected) =>
        Assert.Equal((byte)expected, EventLogSource.ParseMinLevel(name));

    [Fact]
    public void Unknown_minLevel_is_rejected_by_validation()
    {
        var s = new TailSource { EventLog = "Application", MinLevel = "Loud" };
        Assert.Throws<InvalidOperationException>(() => s.Validate());
    }

    [Theory]
    [InlineData(1, "Critical")]
    [InlineData(2, "Error")]
    [InlineData(3, "Warning")]
    [InlineData(4, "Information")]
    [InlineData(0, "Information")] // LogAlways
    [InlineData(5, "Trace")]
    public void Levels_map_to_logsphere_severities(int level, string expected) =>
        Assert.Equal(expected, EventLogSource.MapSeverity((byte)level));

    // -------------------------------------------------------------- query builder

    [Fact]
    public void Default_query_filters_level_and_position()
    {
        var q = EventLogSource.BuildQuery(Source(), 1200);
        Assert.Equal("*[System[EventRecordID > 1200 and (Level >= 1 and Level <= 4)]]", q);
    }

    [Fact]
    public void EventIds_and_providers_become_or_groups()
    {
        var q = EventLogSource.BuildQuery(Source(s =>
        {
            s.MinLevel = "Error";
            s.EventIds = new List<int> { 4625, 4740 };
            s.Providers = new List<string> { "MSSQLSERVER", ".NET Runtime" };
        }), 0);

        Assert.Contains("EventRecordID > 0", q);
        Assert.Contains("(Level >= 1 and Level <= 2)", q);
        Assert.Contains("(EventID=4625 or EventID=4740)", q);
        Assert.Contains("Provider[@Name='MSSQLSERVER' or @Name='.NET Runtime']", q);
    }

    [Fact]
    public void Verbose_minLevel_drops_the_level_clause_entirely()
    {
        var q = EventLogSource.BuildQuery(Source(s => s.MinLevel = "Verbose"), 7);
        Assert.DoesNotContain("Level", q);
        Assert.Equal("*[System[EventRecordID > 7]]", q);
    }

    // ---------------------------------------------------------------- validation

    [Fact]
    public void Source_must_be_file_or_eventlog_not_both_or_neither()
    {
        Assert.Throws<InvalidOperationException>(() => new TailSource().Validate());
        Assert.Throws<InvalidOperationException>(() =>
            new TailSource { Path = "C:/x/*.log", EventLog = "System" }.Validate());
    }

    [Fact]
    public void EventLog_source_skips_file_parser_validation()
    {
        // parser/pattern requirements must not apply to event-log sources
        var s = new TailSource { EventLog = "System", Parser = "regex" }; // no pattern set
        s.Validate(); // must not throw
    }
}
