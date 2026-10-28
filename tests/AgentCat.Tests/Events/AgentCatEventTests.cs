using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCat.Internal.Events;

namespace AgentCat.Tests.Events;

public class AgentCatEventTests
{
    internal static AgentCatEvent Full() => new()
    {
        Id = "evt_1",
        SessionId = "ses_1",
        ProjectId = "proj_1",
        EventType = "mcp:tools/call",
        Timestamp = new DateTimeOffset(2026, 10, 19, 12, 0, 0, 123, TimeSpan.Zero),
        Duration = 42,
        InputTokens = 10,
        OutputTokens = 20,
        IpAddress = "10.0.0.1",
        SdkLanguage = "csharp",
        AgentCatVersion = "0.1.0",
        ServerName = "srv",
        ServerVersion = "1.0",
        ClientName = "cli",
        ClientVersion = "2.0",
        IdentifyActorGivenId = "u1",
        IdentifyActorName = "User One",
        IdentifyActorData = new JsonObject { ["plan"] = "pro", ["nested"] = new JsonObject { ["a"] = 1 } },
        ResourceName = "search",
        Parameters = new JsonObject { ["q"] = "x", ["list"] = new JsonArray(1, 2) },
        Response = new JsonObject { ["ok"] = true },
        UserIntent = "find",
        IsError = true,
        Error = new JsonObject { ["message"] = "boom" },
        Tags = new Dictionary<string, string> { ["k"] = "v" },
        Properties = new JsonObject { ["p"] = new JsonArray("a") },
    };

    [Fact]
    public void Defaults_are_empty_strings_and_nulls()
    {
        var e = new AgentCatEvent();
        Assert.Equal("", e.Id);
        Assert.Equal("", e.SessionId);
        Assert.Equal("", e.EventType);
        Assert.Null(e.ProjectId);
        Assert.Null(e.Duration);
        Assert.Null(e.InputTokens);
        Assert.Null(e.OutputTokens);
        Assert.Null(e.IdentifyActorData);
        Assert.Null(e.Parameters);
        Assert.Null(e.Response);
        Assert.Null(e.IsError);
        Assert.Null(e.Error);
        Assert.Null(e.Tags);
        Assert.Null(e.Properties);
    }

    [Fact]
    public void Clone_is_deep()
    {
        var original = Full();
        var before = EventShape.ToTsJson(original).ToJsonString();
        var clone = original.Clone();
        Assert.Equal(before, EventShape.ToTsJson(clone).ToJsonString());

        clone.IdentifyActorData!["plan"] = "free";
        ((JsonObject)clone.IdentifyActorData["nested"]!)["a"] = 2;
        ((JsonObject)clone.Parameters!)["q"] = "y";
        ((JsonArray)((JsonObject)clone.Parameters)["list"]!).Add(3);
        ((JsonObject)clone.Response!)["ok"] = false;
        ((JsonObject)clone.Error!)["message"] = "changed";
        ((JsonArray)clone.Properties!["p"]!).Add("b");
        clone.Tags!["k"] = "other";
        clone.Tags["new"] = "t";

        Assert.Equal(before, EventShape.ToTsJson(original).ToJsonString());
        Assert.NotSame(original.Tags, clone.Tags);
    }

    [Fact]
    public void Clone_keeps_null_nodes_and_tags_null()
    {
        var clone = new AgentCatEvent().Clone();
        Assert.Null(clone.Parameters);
        Assert.Null(clone.Tags);
    }

    [Fact]
    public void Clone_CopyInto_and_ToTsJson_cover_every_public_property()
    {
        var properties = typeof(AgentCatEvent).GetProperties(BindingFlags.Public | BindingFlags.Instance);
        var source = Populated(properties, null);

        var clone = source.Clone();
        var copy = new AgentCatEvent();
        EventShape.CopyInto(source, copy);
        var json = EventShape.ToTsJson(source).ToJsonString();

        foreach (var property in properties)
        {
            var expected = property.GetValue(source);
            var cloned = property.GetValue(clone);
            Assert.True(Render(expected) == Render(cloned), property.Name + " is not cloned.");
            Assert.True(
                expected is null || expected is string || expected.GetType().IsValueType || !ReferenceEquals(expected, cloned),
                property.Name + " is shared by the clone.");
            Assert.True(Equals(expected, property.GetValue(copy)), property.Name + " is not copied by CopyInto.");
            Assert.True(
                json != EventShape.ToTsJson(Populated(properties, property)).ToJsonString(),
                property.Name + " is missing from ToTsJson.");
        }
    }

    private static AgentCatEvent Populated(PropertyInfo[] properties, PropertyInfo? skip)
    {
        var e = new AgentCatEvent();
        for (var i = 0; i < properties.Length; i++)
        {
            if (properties[i] != skip)
            {
                properties[i].SetValue(e, DistinctValue(properties[i], i));
            }
        }

        return e;
    }

    private static object DistinctValue(PropertyInfo property, int index)
    {
        var type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
        if (type == typeof(string))
        {
            return "value of " + property.Name;
        }

        if (type == typeof(int))
        {
            return 1000 + index;
        }

        if (type == typeof(bool))
        {
            return true;
        }

        if (type == typeof(DateTimeOffset))
        {
            return new DateTimeOffset(2024, 2, 3, 4, 5, 6, 789, TimeSpan.FromHours(2));
        }

        if (type == typeof(JsonObject) || type == typeof(JsonNode))
        {
            return new JsonObject { ["of"] = property.Name };
        }

        if (type == typeof(IDictionary<string, string>))
        {
            return new Dictionary<string, string> { ["of"] = property.Name };
        }

        throw new InvalidOperationException("Give " + property.Name + " (" + property.PropertyType + ") a distinct test value.");
    }

    private static string? Render(object? value) => value switch
    {
        null => null,
        JsonNode node => node.ToJsonString(),
        IDictionary<string, string> tags => string.Join(";", tags.Select(t => t.Key + "=" + t.Value)),
        IFormattable formattable => formattable.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
        _ => value.ToString(),
    };

    [Fact]
    public void Clone_copies_trees_deeper_than_the_stack()
    {
        const int depth = 100_000;
        JsonNode node = new JsonArray("leaf", 1);
        for (var i = 0; i < depth; i++)
        {
            node = i % 2 == 0 ? new JsonObject { ["n"] = node } : new JsonArray(node);
        }

        var e = new AgentCatEvent { Parameters = node };

        var clone = e.Clone();

        var current = clone.Parameters;
        for (var i = 0; i < depth; i++)
        {
            current = current is JsonObject wrapper ? wrapper["n"] : ((JsonArray)current!)[0];
        }

        Assert.Equal("[\"leaf\",1]", current!.ToJsonString());
        Assert.NotSame(node, clone.Parameters);
    }

    [Fact]
    public void Clone_of_a_case_insensitive_object_keeps_both_keys()
    {
        var source = JsonObject.Create(JsonDocument.Parse("{\"id\":1,\"ID\":2}").RootElement, new JsonNodeOptions { PropertyNameCaseInsensitive = true });
        var e = new AgentCatEvent { Properties = source };

        var clone = e.Clone();

        Assert.Equal("{\"id\":1,\"ID\":2}", clone.Properties!.ToJsonString());
    }
}
