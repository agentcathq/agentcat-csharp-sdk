using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AgentCat.Internal.Events;
using AgentCat.Internal.Json;

namespace AgentCat.Tests.Events;

public class EventStagesTests
{
    private const string Hook = "hook";
    private const string Redact = "redact";
    private const string Sanitize = "sanitize";
    private const string Truncate = "truncate";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static AgentCatEvent Event() => new()
    {
        Id = "evt_1",
        SessionId = "ses_1",
        EventType = "mcp:tools/call",
        Timestamp = new DateTimeOffset(2025, 1, 15, 12, 0, 0, TimeSpan.Zero),
    };

    private static async Task RunAsync(AgentCatEvent e, string stage)
    {
        switch (stage)
        {
            case Hook:
                Assert.True(await EventRedactor.ApplyEventHookAsync(e, (x, _) => new ValueTask<AgentCatEvent?>(x), Ct));
                break;
            case Redact:
                await EventRedactor.RedactStringsAsync(e, (s, _) => new ValueTask<string>(s), Ct);
                break;
            case Sanitize:
                EventSanitizer.Sanitize(e);
                break;
            default:
                EventTruncator.Truncate(e);
                break;
        }
    }

    private static async Task RunPipelineAsync(AgentCatEvent e)
    {
        foreach (var stage in new[] { Hook, Redact, Sanitize, Truncate })
        {
            await RunAsync(e, stage);
        }
    }

    private static AgentCatEvent NonFiniteEvent()
    {
        var e = Event();
        e.Parameters = new JsonObject { ["r"] = double.NaN, ["i"] = double.PositiveInfinity, ["n"] = double.NegativeInfinity };
        e.Response = new JsonObject
        {
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "ok" }),
            ["structuredContent"] = new JsonObject
            {
                ["values"] = new JsonArray(float.NaN, float.PositiveInfinity, float.NegativeInfinity, 1),
            },
        };
        return e;
    }

    [Fact]
    public async Task Code_built_non_finite_numbers_pass_every_stage_and_end_as_TS_markers()
    {
        var e = NonFiniteEvent();

        await RunPipelineAsync(e);

        Assert.Equal("{\"r\":\"[NaN]\",\"i\":\"[Infinity]\",\"n\":\"[-Infinity]\"}", e.Parameters!.ToJsonString());
        Assert.Equal(
            "{\"content\":[{\"type\":\"text\",\"text\":\"ok\"}],\"structuredContent\":{\"values\":[\"[NaN]\",\"[Infinity]\",\"[-Infinity]\",1]}}",
            e.Response!.ToJsonString());
    }

    [Theory]
    [InlineData(Hook)]
    [InlineData(Redact)]
    [InlineData(Sanitize)]
    public async Task Stages_before_truncation_keep_code_built_non_finite_numbers(string stage)
    {
        var e = NonFiniteEvent();

        await RunAsync(e, stage);

        Assert.True(double.IsNaN(e.Parameters!["r"]!.GetValue<double>()));
        Assert.Equal(double.PositiveInfinity, e.Parameters["i"]!.GetValue<double>());
        Assert.Equal(double.NegativeInfinity, e.Parameters["n"]!.GetValue<double>());
        var values = e.Response!["structuredContent"]!["values"]!.AsArray();
        Assert.True(float.IsNaN(values[0]!.GetValue<float>()));
        Assert.Equal(float.PositiveInfinity, values[1]!.GetValue<float>());
        Assert.Equal(float.NegativeInfinity, values[2]!.GetValue<float>());
    }

    private static JsonObject CaseInsensitiveRoot() => new(new JsonNodeOptions { PropertyNameCaseInsensitive = true })
    {
        ["x"] = "1",
        ["nested"] = new JsonObject(new JsonNodeOptions()) { ["a"] = "lower", ["A"] = "upper" },
    };

    [Theory]
    [InlineData(Hook)]
    [InlineData(Redact)]
    [InlineData(Sanitize)]
    [InlineData(Truncate)]
    [InlineData("pipeline")]
    public async Task Case_insensitive_roots_holding_keys_that_differ_only_in_case_survive(string stage)
    {
        var e = Event();
        e.Parameters = CaseInsensitiveRoot();
        e.Response = CaseInsensitiveRoot();
        e.IdentifyActorData = CaseInsensitiveRoot();
        e.Error = CaseInsensitiveRoot();

        if (stage == "pipeline")
        {
            await RunPipelineAsync(e);
        }
        else
        {
            await RunAsync(e, stage);
        }

        foreach (var node in new[] { e.Parameters, e.Response, e.IdentifyActorData, e.Error })
        {
            Assert.Equal("{\"x\":\"1\",\"nested\":{\"a\":\"lower\",\"A\":\"upper\"}}", node!.ToJsonString());
        }
    }

    private static JsonValue DeepWrappedValue(int levels, string leaf)
    {
        var chain = new WrappedChain { Leaf = leaf };
        for (var i = 1; i < levels; i++)
        {
            chain = new WrappedChain { Next = chain };
        }

        return JsonValue.Create(chain, StagesTestJsonContext.Default.WrappedChain)!;
    }

    private static string DeepChainJson(int levels, string leaf)
    {
        var json = "{\"Leaf\":\"" + leaf + "\",\"Next\":null}";
        for (var i = 1; i < levels; i++)
        {
            json = "{\"Leaf\":null,\"Next\":" + json + "}";
        }

        return json;
    }

    [Theory]
    [InlineData(Redact)]
    [InlineData(Sanitize)]
    public async Task Json_values_wrapping_containers_deeper_than_64_levels_are_copied(string stage)
    {
        var e = Event();
        e.Parameters = new JsonObject { ["w"] = DeepWrappedValue(100, "deep") };
        e.Response = DeepWrappedValue(100, "deep");

        await RunAsync(e, stage);

        Assert.Equal("{\"w\":" + DeepChainJson(100, "deep") + "}", e.Parameters!.ToJsonString());
        Assert.Equal(DeepChainJson(100, "deep"), e.Response!.ToJsonString());
    }

    [Fact]
    public void Json_values_wrapping_containers_deeper_than_64_levels_are_normalized()
    {
        var e = Event();
        e.Parameters = new JsonObject { ["w"] = DeepWrappedValue(100, "deep") };

        EventTruncator.Truncate(e);

        var expected = "\"[Object]\"";
        for (var i = 0; i < EventTruncator.MaxDepth - 1; i++)
        {
            expected = "{\"Leaf\":null,\"Next\":" + expected + "}";
        }

        Assert.Equal("{\"w\":" + expected + "}", e.Parameters!.ToJsonString());
    }
}

internal sealed class WrappedChain
{
    public string? Leaf { get; set; }

    public WrappedChain? Next { get; set; }
}

[JsonSourceGenerationOptions(MaxDepth = JsJson.MaxDepth)]
[JsonSerializable(typeof(WrappedChain))]
internal sealed partial class StagesTestJsonContext : JsonSerializerContext;
