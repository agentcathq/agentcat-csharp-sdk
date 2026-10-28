using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using AgentCat.Internal.Injection;
using AgentCat.Tests.TestSupport;

namespace AgentCat.Tests.Injection;

public class ArgumentStripperTests
{
    private static readonly IReadOnlyDictionary<string, IReadOnlyCollection<string>> Registry =
        new Dictionary<string, IReadOnlyCollection<string>>
        {
            ["add_todo"] = new[] { "session_id", "agent_id", "context" },
            ["deploy"] = new[] { "agent_id", "context" },
            ["plain"] = Array.Empty<string>(),
        };

    private static Dictionary<string, JsonElement> Args(string json)
    {
        var result = new Dictionary<string, JsonElement>();
        using var doc = JsonDocument.Parse(json);
        foreach (var property in doc.RootElement.EnumerateObject())
        {
            result[property.Name] = property.Value.Clone();
        }
        return result;
    }

    private static void AssertKeys(IDictionary<string, JsonElement>? args, params string[] expected) =>
        Assert.Equal(expected, args!.Keys.ToArray());

    [Fact]
    public void Strips_exactly_what_was_injected()
    {
        var args = Args("""{"text":"x","session_id":"ses_1","agent_id":"agt_1","context":"why"}""");
        var result = ArgumentStripper.Strip(args, "add_todo", Registry);
        AssertKeys(result, "text");
        Assert.Equal("x", result!["text"].GetString());
    }

    [Fact]
    public void Preserves_the_customers_own_session_id_param()
    {
        var args = Args("""{"session_id":"prod-42","agent_id":"agt_1","context":"why"}""");
        var result = ArgumentStripper.Strip(args, "deploy", Registry);
        AssertKeys(result, "session_id");
    }

    [Fact]
    public void Falls_back_to_stripping_all_three_when_no_registry_entry_exists()
    {
        var args = Args("""{"text":"x","session_id":"a","agent_id":"b","context":"c"}""");
        AssertKeys(ArgumentStripper.Strip(args, "unlisted_tool", Registry), "text");
        AssertKeys(ArgumentStripper.Strip(Args("""{"text":"x","session_id":"a"}"""), "any", null), "text");
        AssertKeys(ArgumentStripper.Strip(args, null, Registry), "text");
    }

    [Fact]
    public void Fallback_never_strips_get_more_tools_own_context()
    {
        var args = Args("""{"context":"need a tool","session_id":"a","agent_id":"b"}""");
        var result = ArgumentStripper.Strip(args, "get_more_tools", null);
        AssertKeys(result, "context");
    }

    [Fact]
    public void Strip_returns_a_new_dictionary_and_leaves_the_original_intact()
    {
        var args = Args("""{"text":"x","session_id":"ses_1","context":"why"}""");
        var result = ArgumentStripper.Strip(args, "add_todo", Registry);
        Assert.NotSame(args, result);
        Assert.IsType<Dictionary<string, JsonElement>>(result);
        AssertKeys(args, "text", "session_id", "context");
        Assert.Equal("ses_1", args["session_id"].GetString());
    }

    [Fact]
    public void Keeps_the_original_argument_order()
    {
        var args = Args("""{"z":1,"session_id":"a","b":2,"a":3}""");
        AssertKeys(ArgumentStripper.Strip(args, "add_todo", Registry), "z", "b", "a");
    }

    [Fact]
    public void Returns_the_same_instance_when_nothing_is_removed()
    {
        var args = Args("""{"text":"x"}""");
        Assert.Same(args, ArgumentStripper.Strip(args, "add_todo", Registry));
    }

    [Fact]
    public void Registry_entry_with_empty_set_strips_nothing()
    {
        var args = Args("""{"text":"x","session_id":"a","agent_id":"b","context":"c"}""");
        Assert.Same(args, ArgumentStripper.Strip(args, "plain", Registry));
    }

    [Fact]
    public void Null_arguments_return_null()
    {
        Assert.Null(ArgumentStripper.Strip(null, "add_todo", Registry));
    }

    [Fact]
    public void Strips_the_MCP_call_arguments_in_place_of_the_original()
    {
        var parameters = Mcp.CallToolParams("""{"name":"add_todo","arguments":{"text":"x","session_id":"ses_1","context":"why"}}""");
        var original = parameters.Arguments;

        parameters.Arguments = ArgumentStripper.Strip(parameters.Arguments, parameters.Name, Registry);

        AssertKeys(parameters.Arguments, "text");
        AssertKeys(original, "text", "session_id", "context");
    }

    [Fact]
    public void Uses_the_registry_set_for_lookups()
    {
        var registry = new Dictionary<string, IReadOnlyCollection<string>>
        {
            ["t"] = new HashSet<string>(StringComparer.Ordinal) { "context" },
        };
        var args = Args("""{"Context":"kept","context":"dropped","session_id":"kept"}""");
        AssertKeys(ArgumentStripper.Strip(args, "t", registry), "Context", "session_id");
    }
}
