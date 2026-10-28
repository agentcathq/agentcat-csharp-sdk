using System.Text.Json.Nodes;
using AgentCat.Internal.Injection;
using AgentCat.Tests.TestSupport;

namespace AgentCat.Tests.Injection;

public class GetMoreToolsTests
{
    private static JsonNode Golden(string key) => Fixtures.Json("handles/strings.json")[key]!;

    [Fact]
    public void Descriptor_equals_the_TS_descriptor_including_key_order()
    {
        Assert.Equal(Golden("GET_MORE_TOOLS_DESCRIPTOR").ToJsonString(), GetMoreTools.CreateDescriptor().ToJsonString());
    }

    [Fact]
    public void Descriptor_is_a_fresh_object_on_every_call()
    {
        var first = GetMoreTools.CreateDescriptor();
        var second = GetMoreTools.CreateDescriptor();

        first["inputSchema"]!["properties"]!.AsObject().Add("extra", "x");
        first.Remove("annotations");

        Assert.NotSame(first, second);
        Assert.Equal(Golden("GET_MORE_TOOLS_DESCRIPTOR").ToJsonString(), second.ToJsonString());
    }

    [Fact]
    public void Reply_equals_the_TS_result()
    {
        var reply = GetMoreTools.Reply("need a calendar tool", new RecordingLog());

        Assert.Equal(Golden("GET_MORE_TOOLS_RESULT").ToJsonString(), reply.ToJsonString());
    }

    [Theory]
    [InlineData("need a calendar tool", 20)]
    [InlineData("", 0)]
    [InlineData(null, 0)]
    [InlineData("\uD83D\uDE00 emoji", 8)]
    public void Reply_logs_the_context_length_in_UTF16_units(string? context, int length)
    {
        var log = new RecordingLog();

        GetMoreTools.Reply(context, log);

        Assert.Equal(new[] { "Missing tool reported (context length: " + length + ")" }, log.Lines);
    }
}
