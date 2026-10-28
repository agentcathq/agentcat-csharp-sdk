using System.Text.Json.Nodes;
using AgentCat.Internal.Injection;
using AgentCat.Tests.TestSupport;

namespace AgentCat.Tests.Injection;

public class ListingFixtureTests
{
    private static string[] Sorted(IEnumerable<string> values) => values.OrderBy(v => v, StringComparer.Ordinal).ToArray();

    [Theory]
    [InlineData("defaults", true, true, false, true, null)]
    [InlineData("agent_tracking", true, true, true, true, null)]
    [InlineData("hook_mode_agent_tracking", true, false, true, true, null)]
    [InlineData("hook_mode_no_agent", true, false, false, true, null)]
    [InlineData("tracing_off", true, false, false, true, null)]
    [InlineData("context_off_report_missing_off", false, true, false, false, null)]
    [InlineData("custom_context_description", true, true, false, true, "Why?")]
    public void Build_matches_the_TS_listing(
        string key, bool reportMissing, bool injectSessionId, bool injectAgentId, bool context, string? customDescription)
    {
        var fixtures = Fixtures.Json("handles/listing-fixtures.json");
        var input = fixtures["input_tools"]!.AsArray().Select(t => t!.AsObject()).ToArray();
        var expected = fixtures[key]!;
        var settings = new InjectionSettings(reportMissing, injectSessionId, injectAgentId, context, customDescription);

        var result = ToolListInjector.Build(settings, input, new InjectionState(), new RecordingLog());

        var expectedTools = expected["tools"]!.AsArray();
        Assert.Equal(expectedTools.Count, result.Tools.Count);
        for (var i = 0; i < expectedTools.Count; i++)
        {
            Assert.Equal(expectedTools[i]!.ToJsonString(), result.Tools[i]!.ToJsonString());
        }

        var expectedRegistry = expected["registry"]!.AsObject();
        Assert.Equal(Sorted(expectedRegistry.Select(p => p.Key)), Sorted(result.Registry.Keys));
        foreach (var entry in expectedRegistry)
        {
            Assert.Equal(Sorted(entry.Value!.AsArray().Select(n => n!.GetValue<string>())), Sorted(result.Registry[entry.Key]));
        }

        Assert.Equal(
            Sorted(expected["outputRegistry"]!.AsArray().Select(n => n!.GetValue<string>())),
            Sorted(result.OutputRegistry));
    }
}
