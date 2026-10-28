using System.Reflection;
using System.Text.RegularExpressions;
using AgentCat.Internal.Logging;
using ModelContextProtocol.Server;

namespace AgentCat.Tests.Logging;

public class LogLineTests
{
    private static readonly Regex PrefixShape = new(@"^\[sdk=\S+ dotnet=\S+ mcp=\d+\.\d+\.\d+(-\S+)?\]$");

    [Fact]
    public void Prefix_names_sdk_dotnet_and_mcp_versions()
    {
        Assert.Matches(PrefixShape, LogLine.Prefix);

        var core = typeof(McpServerOptions).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
        var plus = core.IndexOf('+');
        var expected = plus >= 0 ? core.Substring(0, plus) : core;
        Assert.EndsWith(" mcp=" + expected + "]", LogLine.Prefix);
    }

    [Fact]
    public void Line_has_the_iso_time_the_prefix_and_the_message()
    {
        var clock = new DateTimeOffset(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);

        Assert.Equal("[2026-10-19T12:00:00.000Z] " + LogLine.Prefix + " hello", LogLine.Format(clock, "hello"));
    }

    [Fact]
    public void Line_converts_a_local_time_to_utc_with_milliseconds()
    {
        var local = new DateTimeOffset(2026, 10, 19, 14, 0, 0, 7, TimeSpan.FromHours(2));

        Assert.StartsWith("[2026-10-19T12:00:00.007Z] ", LogLine.Format(local, "m"));
    }

    [Fact]
    public void Line_has_no_line_break()
    {
        Assert.DoesNotContain("\n", LogLine.Format(DateTimeOffset.UtcNow, "m"));
    }

    // TS: prefixes sink entries with sdk, node, and mcp versions
    [Fact]
    public void Prefixes_sink_entries_with_sdk_dotnet_and_mcp_versions()
    {
        var line = LogLine.Format(DateTimeOffset.UtcNow, "Test message");

        Assert.Matches(new Regex(@"^\[\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z\] \[sdk=\S+ dotnet=\S+ mcp=\S+\] Test message$"), line);
    }

    // TS: caches the prefix so repeated logging stays consistent
    [Fact]
    public void Caches_the_prefix_so_repeated_logging_stays_consistent()
    {
        var first = LogLine.Format(DateTimeOffset.UtcNow, "a");
        var second = LogLine.Format(DateTimeOffset.UtcNow, "b");

        Assert.Same(LogLine.Prefix, LogLine.Prefix);
        Assert.Contains(" " + LogLine.Prefix + " a", first);
        Assert.Contains(" " + LogLine.Prefix + " b", second);
    }
}
