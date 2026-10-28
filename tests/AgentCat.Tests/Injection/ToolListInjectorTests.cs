using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCat.Internal.Injection;
using AgentCat.Internal.Logging;
using AgentCat.Internal.Text;
using AgentCat.Tests.TestSupport;

namespace AgentCat.Tests.Injection;

public class ToolListInjectorTests
{
    private const string CustomerSchema = """{"type":"object","properties":{"text":{"type":"string"}},"required":["text"]}""";
    private const string ObjectOutputSchema = """{"type":"object","properties":{"count":{"type":"number"}},"required":["count"],"additionalProperties":false}""";
    private const string AddTodo = """{"name":"add_todo","inputSchema":""" + CustomerSchema + "}";
    private const string EchoTool = """{"name":"echo","description":"Echo","inputSchema":{"type":"object","properties":{"msg":{"type":"string"}},"required":["msg"]}}""";
    private const string OwnSessionTool = """{"name":"own_session","inputSchema":{"type":"object","properties":{"session_id":{"type":"string"}}}}""";
    private const string ComposedTool = """{"name":"union_tool","inputSchema":{"type":"object","oneOf":[{"properties":{"kind":{"const":"a"}}},{"properties":{"kind":{"const":"b"}}}]}}""";
    private const string PlainTool = """{"name":"normal","inputSchema":{"type":"object","properties":{"q":{"type":"string"}}}}""";
    private const string ShadowWarning = "WARN: This server registers its own \"get_more_tools\" tool, which is shadowed by AgentCat's report-missing tool: calls to it are answered by AgentCat and never reach your handler. Rename your tool, or set enableReportMissing: false to keep yours reachable.";
    private const string FailureSeparator = "; listing it unmodified - ";

    private static readonly string[] TodoTools =
    {
        """{"name":"add_todo","description":"Add a new todo item","inputSchema":{"type":"object","properties":{"text":{"type":"string","description":"The text of the todo item"}},"required":["text"],"additionalProperties":false,"$schema":"http://json-schema.org/draft-07/schema#"}}""",
        """{"name":"list_todos","description":"List all todo items","inputSchema":{"type":"object","properties":{}}}""",
        """{"name":"complete_todo","description":"Mark a todo item as completed","inputSchema":{"type":"object","properties":{"id":{"type":"string","description":"The ID of the todo to complete"}},"required":["id"]}}""",
        """{"name":"get_stats","description":"Get todo stats","inputSchema":{"type":"object","properties":{}},"outputSchema":{"type":"object","properties":{"count":{"type":"number","description":"Number of todos"}},"required":["count"],"additionalProperties":false}}""",
    };

    private static readonly KeyValuePair<string, string>[] Tokens =
    {
        new(AgentFacingText.SessionIdParamDescription, "$SID"),
        new(AgentFacingText.AgentIdParamDescription, "$AID"),
        new(AgentFacingText.DefaultContextParameterDescription, "$CTX"),
        new(AgentFacingText.McpSessionFieldDescription, "$MSF"),
        new(AgentFacingText.McpSessionFieldDescriptionHookMode, "$MSH"),
        new(AgentFacingText.McpSessionSessionIdDescription, "$MSS"),
        new(AgentFacingText.McpSessionAgentIdDescription, "$MSA"),
        new(AgentFacingText.McpSessionStatusDescription, "$MST"),
        new(AgentFacingText.SessionIdParamPattern, "$PAT"),
    };

    public static TheoryData<string, bool, string?, string, string, string, string> EdgeVectors() => new()
    {
        { """{"name":"t","inputSchema":null,"description":"d"}""", false, null,
          """{"name":"t","inputSchema":{"type":"object","properties":{"session_id":{"type":"string","description":"$SID","pattern":"$PAT"},"agent_id":{"type":"string","description":"$AID"},"context":{"type":"string","description":"$CTX"}},"required":["session_id","agent_id","context"]},"description":"d"}""",
          """{"t":["agent_id","context","session_id"]}""", """[]""",
          """[]""" },
        { """{"name":"t","inputSchema":{"type":"object","required":["q"],"properties":null}}""", false, null,
          """{"name":"t","inputSchema":{"type":"object","required":["q","session_id","agent_id","context"],"properties":{"session_id":{"type":"string","description":"$SID","pattern":"$PAT"},"agent_id":{"type":"string","description":"$AID"},"context":{"type":"string","description":"$CTX"}}}}""",
          """{"t":["agent_id","context","session_id"]}""", """[]""",
          """[]""" },
        { """{"name":"t","inputSchema":{"required":["q"],"type":"object"}}""", false, null,
          """{"name":"t","inputSchema":{"required":["q","session_id","agent_id","context"],"type":"object","properties":{"session_id":{"type":"string","description":"$SID","pattern":"$PAT"},"agent_id":{"type":"string","description":"$AID"},"context":{"type":"string","description":"$CTX"}}}}""",
          """{"t":["agent_id","context","session_id"]}""", """[]""",
          """[]""" },
        { """{"name":"t","inputSchema":{"type":"object","properties":{"session_id":false,"z":1}}}""", false, null,
          """{"name":"t","inputSchema":{"type":"object","properties":{"session_id":{"type":"string","description":"$SID","pattern":"$PAT"},"z":1,"agent_id":{"type":"string","description":"$AID"},"context":{"type":"string","description":"$CTX"}},"required":["session_id","agent_id","context"]}}""",
          """{"t":["agent_id","context","session_id"]}""", """[]""",
          """[]""" },
        { """{"name":"t","inputSchema":{"type":"object","properties":{"context":"","z":1}}}""", false, null,
          """{"name":"t","inputSchema":{"type":"object","properties":{"context":{"type":"string","description":"$CTX"},"z":1,"session_id":{"type":"string","description":"$SID","pattern":"$PAT"},"agent_id":{"type":"string","description":"$AID"}},"required":["session_id","agent_id","context"]}}""",
          """{"t":["agent_id","context","session_id"]}""", """[]""",
          """[]""" },
        { """{"name":"t","inputSchema":{"type":"object","properties":{},"required":null}}""", false, null,
          """{"name":"t","inputSchema":{"type":"object","properties":{"session_id":{"type":"string","description":"$SID","pattern":"$PAT"},"agent_id":{"type":"string","description":"$AID"},"context":{"type":"string","description":"$CTX"}},"required":["session_id","agent_id","context"]}}""",
          """{"t":["agent_id","context","session_id"]}""", """[]""",
          """[]""" },
        { """{"name":"t","inputSchema":{"type":"object","required":[1,null,"session_id"]}}""", false, null,
          """{"name":"t","inputSchema":{"type":"object","required":[1,null,"session_id","agent_id","context"],"properties":{"session_id":{"type":"string","description":"$SID","pattern":"$PAT"},"agent_id":{"type":"string","description":"$AID"},"context":{"type":"string","description":"$CTX"}}}}""",
          """{"t":["agent_id","context","session_id"]}""", """[]""",
          """[]""" },
        { """{"name":"t","inputSchema":{"type":"object","additionalProperties":0}}""", false, null,
          """{"name":"t","inputSchema":{"type":"object","additionalProperties":0,"properties":{"session_id":{"type":"string","description":"$SID","pattern":"$PAT"},"agent_id":{"type":"string","description":"$AID"},"context":{"type":"string","description":"$CTX"}},"required":["session_id","agent_id","context"]}}""",
          """{"t":["agent_id","context","session_id"]}""", """[]""",
          """[]""" },
        { """{"name":"u","inputSchema":{"oneOf":false,"type":"object"}}""", false, null,
          """{"name":"u","inputSchema":{"oneOf":false,"type":"object","properties":{"session_id":{"type":"string","description":"$SID","pattern":"$PAT"},"agent_id":{"type":"string","description":"$AID"},"context":{"type":"string","description":"$CTX"}},"required":["session_id","agent_id","context"]}}""",
          """{"u":["agent_id","context","session_id"]}""", """[]""",
          """[]""" },
        { """{"name":"u","inputSchema":{"oneOf":[]}}""", false, null,
          """{"name":"u","inputSchema":{"oneOf":[]}}""",
          """{"u":[]}""", """[]""",
          """["WARN: Tool \"u\" has complex schema (oneOf/allOf/anyOf). Skipping handle injection.","WARN: Tool \"u\" has complex schema (oneOf/allOf/anyOf). Skipping context injection."]""" },
        { """{"name":"u","inputSchema":{"anyOf":[{}],"properties":{"session_id":{"type":"string"}}}}""", false, null,
          """{"name":"u","inputSchema":{"anyOf":[{}],"properties":{"session_id":{"type":"string"}}}}""",
          """{"u":[]}""", """[]""",
          """["WARN: Tool \"u\" has complex schema (oneOf/allOf/anyOf). Skipping handle injection.","WARN: Tool \"u\" has complex schema (oneOf/allOf/anyOf). Skipping context injection."]""" },
        { """{"name":"u","inputSchema":{"oneOf":[{}],"properties":{"context":{"type":"string"}}}}""", false, null,
          """{"name":"u","inputSchema":{"oneOf":[{}],"properties":{"context":{"type":"string"}}}}""",
          """{"u":[]}""", """[]""",
          """["WARN: Tool \"u\" has complex schema (oneOf/allOf/anyOf). Skipping handle injection.","WARN: Tool \"u\" already has 'context' parameter. Skipping context injection."]""" },
        { """{"name":"t","outputSchema":{"type":"object","properties":{"mcp_session":null}}}""", false, null,
          """{"name":"t","outputSchema":{"type":"object","properties":{"mcp_session":{"type":"object","description":"$MSF","properties":{"session_id":{"type":"string","description":"$MSS"},"agent_id":{"type":"string","description":"$MSA"},"status":{"type":"string","enum":["issued","active","unrecognized"],"description":"$MST"}}}}},"inputSchema":{"type":"object","properties":{"session_id":{"type":"string","description":"$SID","pattern":"$PAT"},"agent_id":{"type":"string","description":"$AID"},"context":{"type":"string","description":"$CTX"}},"required":["session_id","agent_id","context"]}}""",
          """{"t":["agent_id","context","session_id"]}""", """["t"]""",
          """[]""" },
        { """{"name":"t","inputSchema":{"type":"object"},"outputSchema":{"type":"object","properties":null,"required":[]}}""", false, null,
          """{"name":"t","inputSchema":{"type":"object","properties":{"session_id":{"type":"string","description":"$SID","pattern":"$PAT"},"agent_id":{"type":"string","description":"$AID"},"context":{"type":"string","description":"$CTX"}},"required":["session_id","agent_id","context"]},"outputSchema":{"type":"object","properties":{"mcp_session":{"type":"object","description":"$MSF","properties":{"session_id":{"type":"string","description":"$MSS"},"agent_id":{"type":"string","description":"$MSA"},"status":{"type":"string","enum":["issued","active","unrecognized"],"description":"$MST"}}}},"required":[]}}""",
          """{"t":["agent_id","context","session_id"]}""", """["t"]""",
          """[]""" },
        { """{"name":"t","outputSchema":{"type":"object","properties":{"mcp_session":{"type":"string"}}}}""", false, null,
          """{"name":"t","outputSchema":{"type":"object","properties":{"mcp_session":{"type":"string"}}},"inputSchema":{"type":"object","properties":{"session_id":{"type":"string","description":"$SID","pattern":"$PAT"},"agent_id":{"type":"string","description":"$AID"},"context":{"type":"string","description":"$CTX"}},"required":["session_id","agent_id","context"]}}""",
          """{"t":["agent_id","context","session_id"]}""", """[]""",
          """["WARN: Tool \"t\" already declares 'mcp_session' in outputSchema. Skipping injection."]""" },
        { """{"name":"t","outputSchema":{"allOf":[{}]}}""", false, null,
          """{"name":"t","outputSchema":{"allOf":[{}]},"inputSchema":{"type":"object","properties":{"session_id":{"type":"string","description":"$SID","pattern":"$PAT"},"agent_id":{"type":"string","description":"$AID"},"context":{"type":"string","description":"$CTX"}},"required":["session_id","agent_id","context"]}}""",
          """{"t":["agent_id","context","session_id"]}""", """[]""",
          """["WARN: Tool \"t\" has complex outputSchema (oneOf/allOf/anyOf). Skipping mcp_session injection; mint-back stays content-only for this tool."]""" },
        { """{"name":"t","inputSchema":{"type":"object","properties":{"agent_id":{"type":"string"}}}}""", false, null,
          """{"name":"t","inputSchema":{"type":"object","properties":{"agent_id":{"type":"string"},"session_id":{"type":"string","description":"$SID","pattern":"$PAT"},"context":{"type":"string","description":"$CTX"}},"required":["session_id","context"]}}""",
          """{"t":["context","session_id"]}""", """[]""",
          """["WARN: Tool \"t\" already has 'agent_id' parameter. Skipping agent_id injection."]""" },
        { """{"inputSchema":{"type":"object"}}""", false, null,
          """{"inputSchema":{"type":"object","properties":{"session_id":{"type":"string","description":"$SID","pattern":"$PAT"},"agent_id":{"type":"string","description":"$AID"},"context":{"type":"string","description":"$CTX"}},"required":["session_id","agent_id","context"]}}""",
          """{"unknown":["agent_id","context","session_id"]}""", """[]""",
          """[]""" },
        { """{"name":"","inputSchema":{"type":"object"}}""", false, null,
          """{"name":"","inputSchema":{"type":"object","properties":{"session_id":{"type":"string","description":"$SID","pattern":"$PAT"},"agent_id":{"type":"string","description":"$AID"},"context":{"type":"string","description":"$CTX"}},"required":["session_id","agent_id","context"]}}""",
          """{"unknown":["agent_id","context","session_id"]}""", """[]""",
          """[]""" },
        { """{"name":"t"}""", false, "",
          """{"name":"t","inputSchema":{"type":"object","properties":{"session_id":{"type":"string","description":"$SID","pattern":"$PAT"},"agent_id":{"type":"string","description":"$AID"},"context":{"type":"string","description":"$CTX"}},"required":["session_id","agent_id","context"]}}""",
          """{"t":["agent_id","context","session_id"]}""", """[]""",
          """[]""" },
        { """{"name":"t","outputSchema":{"type":"object"}}""", true, null,
          """{"name":"t","outputSchema":{"type":"object","properties":{"mcp_session":{"type":"object","description":"$MSH","properties":{"agent_id":{"type":"string","description":"$MSA"}}}}},"inputSchema":{"type":"object","properties":{"agent_id":{"type":"string","description":"$AID"},"context":{"type":"string","description":"$CTX"}},"required":["agent_id","context"]}}""",
          """{"t":["agent_id","context"]}""", """["t"]""",
          """[]""" },
        { """{"name":"t","inputSchema":{"type":"object"},"outputSchema":"x"}""", false, null,
          """{"name":"t","inputSchema":{"type":"object","properties":{"context":{"type":"string","description":"$CTX"}},"required":["context"]},"outputSchema":"x"}""",
          """{"t":["context"]}""", """[]""",
          """["WARN: Handle injection failed for tool \"t\"; listing it unmodified - "]""" },
        { """{"name":"t","inputSchema":{"type":"object","properties":5}}""", false, null,
          """{"name":"t","inputSchema":{"type":"object","properties":5}}""",
          """{"t":[]}""", """[]""",
          """["WARN: Handle injection failed for tool \"t\"; listing it unmodified - ","WARN: Context injection failed for tool \"t\"; listing it unmodified - "]""" },
        { """{"name":"t","inputSchema":true}""", false, null,
          """{"name":"t","inputSchema":true}""",
          """{"t":[]}""", """[]""",
          """["WARN: Handle injection failed for tool \"t\"; listing it unmodified - ","WARN: Context injection failed for tool \"t\"; listing it unmodified - "]""" },
    };

    private const string NullToolHandleFailure = "WARN: Handle injection failed for tool \"unknown\"; listing it unmodified - TypeError: Cannot read properties of null (reading 'name')";
    private const string NullToolContextFailure = "WARN: Context injection failed for tool \"unknown\"; listing it unmodified - TypeError: Cannot read properties of null (reading 'name')";

    public static TheoryData<bool, bool, bool, bool, string, string, string[]> NullEntryVectors() => new()
    {
        { true, false, true, true,
          """{"name":"a","inputSchema":{"type":"object","properties":{"session_id":{"type":"string","description":"$SID","pattern":"$PAT"},"context":{"type":"string","description":"$CTX"}},"required":["session_id","context"]}}""",
          "a=context,session_id;get_more_tools=session_id",
          new[] { NullToolHandleFailure, NullToolContextFailure } },
        { true, true, true, true,
          """{"name":"a","inputSchema":{"type":"object","properties":{"session_id":{"type":"string","description":"$SID","pattern":"$PAT"},"agent_id":{"type":"string","description":"$AID"},"context":{"type":"string","description":"$CTX"}},"required":["session_id","agent_id","context"]}}""",
          "a=agent_id,context,session_id;get_more_tools=agent_id,session_id",
          new[] { NullToolHandleFailure, NullToolContextFailure } },
        { true, false, false, true,
          """{"name":"a","inputSchema":{"type":"object","properties":{"session_id":{"type":"string","description":"$SID","pattern":"$PAT"}},"required":["session_id"]}}""",
          "a=session_id;get_more_tools=session_id",
          new[] { NullToolHandleFailure } },
        { false, false, true, true,
          """{"name":"a","inputSchema":{"type":"object","properties":{"context":{"type":"string","description":"$CTX"}},"required":["context"]}}""",
          "a=context;get_more_tools=",
          new[] { NullToolContextFailure } },
        { false, false, false, false,
          """{"name":"a"}""",
          "a=",
          Array.Empty<string>() },
    };

    private static string OwnSessionError(string tool) =>
        "ERROR: Tool \"" + tool + "\" already declares a 'session_id' parameter. AgentCat will not inject its own, and calls to this tool are published without a session, so they cannot be correlated. Your parameter is untouched and still reaches your handler. If you already manage sessions, pass a resolveSessionId hook to track() \u2014 AgentCat will derive its session from your identifier and stop injecting session_id entirely.";

    private static InjectionSettings Settings(bool session = true, bool agent = true, bool context = false, bool reportMissing = false, string? custom = null)
        => new(reportMissing, session, agent, context, custom);

    private static InjectionSettings ContextOnly() => Settings(session: false, agent: false, context: true);

    private static JsonObject Tool(string json) => JsonNode.Parse(json)!.AsObject();

    private static InjectedList Build(InjectionSettings settings, params string[] tools)
        => Build(settings, new InjectionState(), NullAgentCatLog.Instance, tools);

    private static InjectedList Build(InjectionSettings settings, InjectionState state, IAgentCatLog log, params string[] tools)
        => ToolListInjector.Build(settings, tools.Select(Tool).ToArray(), state, log);

    private static JsonObject Named(InjectedList result, string name) => result.Tools.Single(t => (string?)t?["name"] == name)!;

    private static JsonNode InputSchema(InjectedList result, int index = 0) => result.Tools[index]!["inputSchema"]!;

    private static JsonNode? Property(JsonNode schema, string name) => schema["properties"]![name];

    private static string[] Keys(JsonNode? node) => node!.AsObject().Select(p => p.Key).ToArray();

    private static string[] Strings(JsonNode? node) => node!.AsArray().Select(n => n!.GetValue<string>()).ToArray();

    private static string[] Sorted(IEnumerable<string> values) => values.OrderBy(v => v, StringComparer.Ordinal).ToArray();

    private static string[] S(params string[] values) => values;

    private static JsonObject StringProperty(string description) => new() { ["type"] = "string", ["description"] = description };

    private static string StructuredTool(string name, string outputSchema)
        => """{"name":""" + JsonSerializer.Serialize(name) + ""","inputSchema":""" + CustomerSchema + ""","outputSchema":""" + outputSchema + "}";

    private static string Canonical(IReadOnlyDictionary<string, IReadOnlyCollection<string>> registry)
        => string.Join(";", registry.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key + "=" + string.Join(",", Sorted(p.Value))));

    private static string Canonical(JsonObject registry)
        => string.Join(";", registry.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key + "=" + string.Join(",", Sorted(Strings(p.Value)))));

    private static JsonNode? Tokenize(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                return new JsonObject(obj.Select(p => new KeyValuePair<string, JsonNode?>(p.Key, Tokenize(p.Value))));
            case JsonArray array:
                return new JsonArray(array.Select(Tokenize).ToArray());
            case JsonValue value when value.GetValueKind() == JsonValueKind.String:
                var text = value.GetValue<string>();
                foreach (var token in Tokens)
                {
                    if (token.Key == text)
                    {
                        return JsonValue.Create(token.Value);
                    }
                }

                return JsonValue.Create(text);
            default:
                return node?.DeepClone();
        }
    }

    private static string WithoutFailureDetail(string line)
    {
        var index = line.IndexOf(FailureSeparator, StringComparison.Ordinal);
        return index < 0 ? line : line.Substring(0, index + FailureSeparator.Length);
    }

    [Fact]
    public void Injects_session_id_and_agent_id_after_customer_params_both_required()
    {
        var result = Build(Settings(), AddTodo);

        var schema = InputSchema(result);
        Assert.Equal(S("text", "session_id", "agent_id"), Keys(schema["properties"]));
        Assert.Equal(S("text", "session_id", "agent_id"), Strings(schema["required"]));
        var sessionId = Property(schema, "session_id")!;
        Assert.Equal(S("type", "description", "pattern"), Keys(sessionId));
        Assert.Equal("string", (string?)sessionId["type"]);
        Assert.Equal(AgentFacingText.SessionIdParamDescription, (string?)sessionId["description"]);
        Assert.Equal(AgentFacingText.SessionIdParamPattern, (string?)sessionId["pattern"]);
        Assert.Equal(StringProperty(AgentFacingText.AgentIdParamDescription).ToJsonString(), Property(schema, "agent_id")!.ToJsonString());
        Assert.Equal(S("agent_id", "session_id"), Sorted(result.Registry["add_todo"]));
    }

    [Fact]
    public void Creates_the_required_array_when_the_schema_has_none()
    {
        var result = Build(Settings(), """{"name":"bare","inputSchema":{"type":"object","properties":{}}}""");

        Assert.Equal(S("session_id", "agent_id"), Strings(InputSchema(result)["required"]));
    }

    [Fact]
    public void Does_not_duplicate_session_id_in_an_existing_required_array()
    {
        var result = Build(Settings(agent: false), """{"name":"weird_session","inputSchema":{"type":"object","properties":{},"required":["session_id"]}}""");

        Assert.Equal(S("session_id"), Strings(InputSchema(result)["required"]));
    }

    [Fact]
    public void Does_not_duplicate_agent_id_in_an_existing_required_array()
    {
        var result = Build(Settings(session: false), """{"name":"weird","inputSchema":{"type":"object","properties":{},"required":["agent_id"]}}""");

        Assert.Equal(S("agent_id"), Strings(InputSchema(result)["required"]));
    }

    [Fact]
    public void Leaves_required_untouched_when_the_customer_declares_agent_id()
    {
        var log = new RecordingLog();

        var result = Build(
            Settings(session: false),
            new InjectionState(),
            log,
            """{"name":"owns_agent","inputSchema":{"type":"object","properties":{"agent_id":{"type":"string","description":"mine"}},"required":[]}}""");

        Assert.Empty(InputSchema(result)["required"]!.AsArray());
        Assert.Equal("mine", (string?)Property(InputSchema(result), "agent_id")!["description"]);
        Assert.Empty(result.Registry["owns_agent"]);
        Assert.Equal(S("WARN: Tool \"owns_agent\" already has 'agent_id' parameter. Skipping agent_id injection."), log.Lines);
    }

    [Fact]
    public void Full_order_with_context_injector_customer_session_id_agent_id_context()
    {
        var result = Build(Settings(context: true), AddTodo);

        Assert.Equal(S("text", "session_id", "agent_id", "context"), Keys(InputSchema(result)["properties"]));
        Assert.Equal(S("text", "session_id", "agent_id", "context"), Strings(InputSchema(result)["required"]));
        Assert.Equal(S("agent_id", "context", "session_id"), Sorted(result.Registry["add_todo"]));
    }

    [Fact]
    public void Hook_mode_no_session_id_agent_id_uses_the_same_single_description()
    {
        var result = Build(Settings(session: false), AddTodo);

        var schema = InputSchema(result);
        Assert.Null(Property(schema, "session_id"));
        Assert.Equal(AgentFacingText.AgentIdParamDescription, (string?)Property(schema, "agent_id")!["description"]);
        Assert.Equal(S("text", "agent_id"), Strings(schema["required"]));
        Assert.Equal(S("agent_id"), Sorted(result.Registry["add_todo"]));
    }

    [Fact]
    public void Skips_a_param_the_tool_already_defines_and_does_not_record_it()
    {
        var result = Build(
            Settings(),
            """{"name":"deploy","inputSchema":{"type":"object","properties":{"text":{"type":"string"},"session_id":{"type":"string","description":"customer's own"}},"required":["text"]}}""");

        var sessionId = Property(InputSchema(result), "session_id")!;
        Assert.Equal("customer's own", (string?)sessionId["description"]);
        Assert.Equal(S("type", "description"), Keys(sessionId));
        Assert.Equal(S("text", "agent_id"), Strings(InputSchema(result)["required"]));
        Assert.Equal(S("agent_id"), Sorted(result.Registry["deploy"]));
    }

    [Fact]
    public void Skips_complex_schemas_entirely()
    {
        var log = new RecordingLog();

        var result = Build(Settings(), new InjectionState(), log, """{"name":"odd","inputSchema":{"oneOf":[{"type":"object"}]}}""");

        Assert.Null(InputSchema(result)["properties"]);
        Assert.Null(InputSchema(result)["required"]);
        Assert.Empty(result.Registry["odd"]);
        Assert.Equal(S("WARN: Tool \"odd\" has complex schema (oneOf/allOf/anyOf). Skipping handle injection."), log.Lines);
    }

    [Fact]
    public void Removes_additionalProperties_false_and_handles_missing_schemas()
    {
        var result = Build(
            Settings(),
            """{"name":"strict","inputSchema":{"type":"object","properties":{"text":{"type":"string"}},"required":["text"],"additionalProperties":false}}""",
            """{"name":"bare"}""");

        Assert.False(InputSchema(result, 0).AsObject().ContainsKey("additionalProperties"));
        Assert.Equal(S("session_id", "agent_id"), Keys(InputSchema(result, 1)["properties"]));
        Assert.Equal(S("session_id", "agent_id"), Strings(InputSchema(result, 1)["required"]));
    }

    [Fact]
    public void Does_not_skip_get_more_tools_handles_are_injected_context_stays_bespoke()
    {
        var result = Build(
            Settings(context: true),
            """{"name":"get_more_tools","inputSchema":{"type":"object","properties":{"context":{"type":"string","description":"bespoke"}},"required":["context"]}}""");

        var schema = InputSchema(result);
        Assert.Equal(S("context", "session_id", "agent_id"), Keys(schema["properties"]));
        Assert.Equal("bespoke", (string?)Property(schema, "context")!["description"]);
        Assert.Equal(S("context", "session_id", "agent_id"), Strings(schema["required"]));
        Assert.Equal(S("agent_id", "session_id"), Sorted(result.Registry["get_more_tools"]));
    }

    [Fact]
    public void Agent_id_copy_prescribes_the_self_chosen_model_harness_nonce_format()
    {
        var copy = AgentFacingText.AgentIdParamDescription;

        Assert.Contains("required on every call including your first", copy, StringComparison.Ordinal);
        Assert.Contains("opus-4.80-1m|claude-code|k3n9x", copy, StringComparison.Ordinal);
        Assert.Contains("never inherited", copy, StringComparison.Ordinal);
        Assert.DoesNotContain("the server will issue", copy, StringComparison.Ordinal);
        Assert.DoesNotContain("session_id", copy, StringComparison.Ordinal);
    }

    [Fact]
    public void Injects_mcp_session_into_a_plain_object_outputSchema_and_registers_the_tool()
    {
        var result = Build(Settings(), StructuredTool("get_stats", ObjectOutputSchema));

        var output = result.Tools[0]!["outputSchema"]!;
        var mcpSession = output["properties"]!["mcp_session"]!;
        Assert.Equal(S("type", "description", "properties"), Keys(mcpSession));
        Assert.Equal("object", (string?)mcpSession["type"]);
        Assert.Equal(AgentFacingText.McpSessionFieldDescription, (string?)mcpSession["description"]);
        Assert.Equal(S("session_id", "agent_id", "status"), Keys(mcpSession["properties"]));
        var status = new JsonObject
        {
            ["type"] = "string",
            ["enum"] = new JsonArray("issued", "active", "unrecognized"),
            ["description"] = AgentFacingText.McpSessionStatusDescription,
        };
        Assert.Equal(status.ToJsonString(), mcpSession["properties"]!["status"]!.ToJsonString());
        Assert.Equal(S("type", "properties", "required", "additionalProperties"), Keys(output));
        Assert.Equal(S("count"), Strings(output["required"]));
        Assert.False(output["additionalProperties"]!.GetValue<bool>());
        Assert.Equal("""{"type":"number"}""", output["properties"]!["count"]!.ToJsonString());
        Assert.Contains("get_stats", result.OutputRegistry);
    }

    [Fact]
    public void Sub_properties_track_modes_hook_mode_drops_session_id_and_status_tracking_off_drops_agent_id()
    {
        var hook = Build(Settings(session: false), StructuredTool("t", ObjectOutputSchema));
        var hookProperty = hook.Tools[0]!["outputSchema"]!["properties"]!["mcp_session"]!;
        Assert.Equal(S("agent_id"), Keys(hookProperty["properties"]));
        Assert.Equal(AgentFacingText.McpSessionFieldDescriptionHookMode, (string?)hookProperty["description"]);

        var sessionOnly = Build(Settings(agent: false), StructuredTool("t", ObjectOutputSchema));
        var sessionProperty = sessionOnly.Tools[0]!["outputSchema"]!["properties"]!["mcp_session"]!;
        Assert.Equal(S("session_id", "status"), Keys(sessionProperty["properties"]));
        Assert.Equal(AgentFacingText.McpSessionFieldDescription, (string?)sessionProperty["description"]);
    }

    [Fact]
    public void Does_not_mutate_the_customers_outputSchema_object()
    {
        var tool = Tool(StructuredTool("t", ObjectOutputSchema));

        ToolListInjector.Build(Settings(), new[] { tool }, new InjectionState(), NullAgentCatLog.Instance);

        Assert.False(tool["outputSchema"]!["properties"]!.AsObject().ContainsKey("mcp_session"));
        Assert.Equal(ObjectOutputSchema, tool["outputSchema"]!.ToJsonString());
    }

    [Fact]
    public void Skips_complex_outputSchema_and_does_not_register()
    {
        const string Complex = """{"oneOf":[{"type":"object","properties":{"a":{"type":"string"}}}]}""";
        var log = new RecordingLog();

        var result = Build(Settings(), new InjectionState(), log, StructuredTool("poly", Complex));

        Assert.Equal(Complex, result.Tools[0]!["outputSchema"]!.ToJsonString());
        Assert.Empty(result.OutputRegistry);
        Assert.NotNull(Property(InputSchema(result), "session_id"));
        Assert.Equal(
            S("WARN: Tool \"poly\" has complex outputSchema (oneOf/allOf/anyOf). Skipping mcp_session injection; mint-back stays content-only for this tool."),
            log.Lines);
    }

    [Fact]
    public void Composed_inputSchema_early_return_skips_output_injection_too_nothing_registered()
    {
        var result = Build(
            Settings(),
            """{"name":"mixed","inputSchema":{"oneOf":[{"type":"object","properties":{"a":{"type":"string"}}}]},"outputSchema":""" + ObjectOutputSchema + "}");

        Assert.Equal(ObjectOutputSchema, result.Tools[0]!["outputSchema"]!.ToJsonString());
        Assert.Empty(result.OutputRegistry);
    }

    [Fact]
    public void Collision_a_customer_declared_mcp_session_is_never_clobbered()
    {
        var log = new RecordingLog();

        var result = Build(Settings(), new InjectionState(), log, StructuredTool("t", """{"type":"object","properties":{"mcp_session":{"type":"string"}}}"""));

        Assert.Equal("""{"type":"string"}""", result.Tools[0]!["outputSchema"]!["properties"]!["mcp_session"]!.ToJsonString());
        Assert.Empty(result.OutputRegistry);
        Assert.Equal(S("WARN: Tool \"t\" already declares 'mcp_session' in outputSchema. Skipping injection."), log.Lines);
    }

    [Fact]
    public void No_outputSchema_untouched_nothing_registered()
    {
        var result = Build(Settings(), """{"name":"t","inputSchema":""" + CustomerSchema + "}");

        Assert.False(result.Tools[0]!.ContainsKey("outputSchema"));
        Assert.Empty(result.OutputRegistry);
    }

    [Fact]
    public void No_injection_at_all_leaves_outputSchema_alone()
    {
        var result = Build(Settings(session: false, agent: false), StructuredTool("t", ObjectOutputSchema));

        Assert.Equal(ObjectOutputSchema, result.Tools[0]!["outputSchema"]!.ToJsonString());
        Assert.Empty(result.OutputRegistry);
    }

    [Fact]
    public void Reports_a_session_id_collision_once_per_tool_as_an_error_with_remediation()
    {
        var state = new InjectionState();
        var log = new RecordingLog();

        for (var pass = 0; pass < 3; pass++)
        {
            Build(Settings(agent: false), state, log, OwnSessionTool);
        }

        var collisions = log.Lines.Where(l => l.Contains("own_session")).ToArray();
        Assert.Equal(S(OwnSessionError("own_session")), collisions);
        Assert.Contains("resolveSessionId", collisions[0], StringComparison.Ordinal);
        Assert.Contains("without a session", collisions[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Reports_two_different_colliding_tools_each_once()
    {
        const string ToolA = """{"name":"tool_a","inputSchema":{"type":"object","properties":{"session_id":{"type":"string"}}}}""";
        const string ToolB = """{"name":"tool_b","inputSchema":{"type":"object","properties":{"session_id":{"type":"string"}}}}""";
        var state = new InjectionState();
        var log = new RecordingLog();

        Build(Settings(agent: false), state, log, ToolA, ToolB);
        Build(Settings(agent: false), state, log, ToolA, ToolB);

        Assert.Equal(S(OwnSessionError("tool_a"), OwnSessionError("tool_b")), log.Lines);
    }

    [Fact]
    public void Conflict_reports_are_stable_per_state_and_independent_across_states()
    {
        var stateA = new InjectionState();
        var stateB = new InjectionState();

        Assert.True(stateA.TryReportConflict("tool_x"));
        Assert.False(stateA.TryReportConflict("tool_x"));
        Assert.True(stateB.TryReportConflict("tool_x"));
        Assert.True(stateA.TryWarnCustomerOwnedGetMoreTools());
        Assert.False(stateA.TryWarnCustomerOwnedGetMoreTools());
        Assert.True(stateB.TryWarnCustomerOwnedGetMoreTools());
    }

    [Fact]
    public void Build_dedupes_per_state_repeat_listings_log_once_a_second_state_logs_its_own_once()
    {
        var serverA = new InjectionState();
        var serverB = new InjectionState();
        var log = new RecordingLog();

        for (var pass = 0; pass < 3; pass++)
        {
            Build(Settings(agent: false), serverA, log, OwnSessionTool);
        }

        Assert.Single(log.Lines, l => l.Contains("own_session"));

        Build(Settings(agent: false), serverB, log, OwnSessionTool);
        Build(Settings(agent: false), serverB, log, OwnSessionTool);

        Assert.Equal(2, log.Lines.Count(l => l.Contains("own_session")));
    }

    [Fact]
    public void Records_only_the_tools_whose_own_schema_declares_session_id()
    {
        var state = new InjectionState();

        Build(Settings(agent: false), state, NullAgentCatLog.Instance, OwnSessionTool, ComposedTool, PlainTool);

        Assert.True(state.IsSessionParamDeclared("own_session"));
        Assert.False(state.IsSessionParamDeclared("union_tool"));
        Assert.False(state.IsSessionParamDeclared("normal"));
    }

    [Fact]
    public void Records_a_composed_schema_that_also_declares_session_id_at_its_root()
    {
        var state = new InjectionState();

        Build(
            Settings(agent: false),
            state,
            NullAgentCatLog.Instance,
            """{"name":"union_with_own_session","inputSchema":{"type":"object","oneOf":[{"properties":{"kind":{"const":"a"}}},{"properties":{"kind":{"const":"b"}}}],"properties":{"session_id":{"type":"string"}}}}""");

        Assert.True(state.IsSessionParamDeclared("union_with_own_session"));
    }

    [Fact]
    public void Declared_session_params_are_kept_per_state()
    {
        var serverA = new InjectionState();
        var serverB = new InjectionState();

        Build(Settings(agent: false), serverA, NullAgentCatLog.Instance, OwnSessionTool);

        Assert.True(serverA.IsSessionParamDeclared("own_session"));
        Assert.False(serverB.IsSessionParamDeclared("own_session"));
    }

    [Fact]
    public void Records_nothing_in_hook_mode_where_session_id_is_never_injected()
    {
        var state = new InjectionState();
        var log = new RecordingLog();

        Build(Settings(session: false, agent: false), state, log, OwnSessionTool);
        Build(Settings(session: false, agent: true), state, log, OwnSessionTool);

        Assert.False(state.IsSessionParamDeclared("own_session"));
        Assert.Empty(log.Lines);
    }

    [Fact]
    public void Skips_injection_for_tools_with_existing_context_parameter()
    {
        var log = new RecordingLog();

        var result = Build(
            ContextOnly(),
            new InjectionState(),
            log,
            """{"name":"test-tool","inputSchema":{"type":"object","properties":{"context":{"type":"number","description":"existing"}}}}""");

        Assert.Equal("""{"type":"number","description":"existing"}""", Property(InputSchema(result), "context")!.ToJsonString());
        Assert.Empty(result.Registry["test-tool"]);
        Assert.Equal(S("WARN: Tool \"test-tool\" already has 'context' parameter. Skipping context injection."), log.Lines);
    }

    [Fact]
    public void Warns_with_tool_name_when_context_exists()
    {
        var log = new RecordingLog();

        Build(ContextOnly(), new InjectionState(), log, """{"name":"my-special-tool","inputSchema":{"type":"object","properties":{"context":{"type":"boolean"}}}}""");

        Assert.Contains("my-special-tool", Assert.Single(log.Lines), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("union-tool", """{"oneOf":[{"type":"object","properties":{"email":{"type":"string"}}},{"type":"object","properties":{"phone":{"type":"string"}}}]}""")]
    [InlineData("intersection-tool", """{"allOf":[{"type":"object","properties":{"a":{"type":"string"}}},{"type":"object","properties":{"b":{"type":"string"}}}]}""")]
    [InlineData("anyof-tool", """{"anyOf":[{"type":"string"},{"type":"number"}]}""")]
    public void Skips_context_injection_for_composed_schemas(string name, string schema)
    {
        var log = new RecordingLog();

        var result = Build(ContextOnly(), new InjectionState(), log, """{"name":""" + JsonSerializer.Serialize(name) + ""","inputSchema":""" + schema + "}");

        Assert.Null(InputSchema(result)["properties"]);
        Assert.Equal(schema, InputSchema(result).ToJsonString());
        Assert.Equal(S("WARN: Tool \"" + name + "\" has complex schema (oneOf/allOf/anyOf). Skipping context injection."), log.Lines);
    }

    [Fact]
    public void Injects_context_and_removes_additionalProperties_false_constraint()
    {
        var result = Build(
            ContextOnly(),
            """{"name":"strict-tool","inputSchema":{"type":"object","properties":{"name":{"type":"string"}},"additionalProperties":false}}""");

        Assert.Equal("string", (string?)Property(InputSchema(result), "context")!["type"]);
        Assert.False(InputSchema(result).AsObject().ContainsKey("additionalProperties"));
    }

    [Fact]
    public void Preserves_additionalProperties_true_when_injecting()
    {
        var result = Build(
            ContextOnly(),
            """{"name":"flexible-tool","inputSchema":{"type":"object","properties":{"name":{"type":"string"}},"additionalProperties":true}}""");

        Assert.Equal("string", (string?)Property(InputSchema(result), "context")!["type"]);
        Assert.True(InputSchema(result)["additionalProperties"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData("""{"name":"empty-tool","inputSchema":{}}""", null)]
    [InlineData("""{"name":"no-schema-tool"}""", "object")]
    [InlineData("""{"name":"type-only-tool","inputSchema":{"type":"object"}}""", "object")]
    public void Handles_edge_case_schemas(string tool, string? expectedType)
    {
        var result = Build(ContextOnly(), tool);

        Assert.Equal("string", (string?)Property(InputSchema(result), "context")!["type"]);
        Assert.Equal(expectedType, (string?)InputSchema(result)["type"]);
    }

    [Fact]
    public void Skips_get_more_tools_entirely()
    {
        var result = Build(ContextOnly(), """{"name":"get_more_tools","inputSchema":{}}""", """{"name":"other-tool","inputSchema":{}}""");

        Assert.Null(InputSchema(result, 0)["properties"]);
        Assert.NotNull(Property(InputSchema(result, 1), "context"));
    }

    [Fact]
    public void Handles_mixed_valid_and_complex_schemas()
    {
        var log = new RecordingLog();

        var result = Build(
            ContextOnly(),
            new InjectionState(),
            log,
            """{"name":"valid-tool","inputSchema":{"type":"object","properties":{}}}""",
            """{"name":"complex-tool","inputSchema":{"oneOf":[{"type":"string"}]}}""",
            """{"name":"collision-tool","inputSchema":{"type":"object","properties":{"context":{"type":"number"}}}}""",
            """{"name":"strict-tool","inputSchema":{"type":"object","properties":{"name":{"type":"string"}},"additionalProperties":false}}""");

        Assert.NotNull(Property(InputSchema(result, 0), "context"));
        Assert.Null(InputSchema(result, 1)["properties"]);
        Assert.Equal("number", (string?)Property(InputSchema(result, 2), "context")!["type"]);
        Assert.NotNull(Property(InputSchema(result, 3), "context"));
        Assert.False(InputSchema(result, 3).AsObject().ContainsKey("additionalProperties"));
        Assert.Equal(2, log.Lines.Count);
    }

    [Fact]
    public void Adds_get_more_tools_when_report_missing_is_enabled()
    {
        var result = Build(Settings(agent: false, context: true, reportMissing: true), TodoTools);

        var getMoreTools = Named(result, "get_more_tools");
        Assert.Equal(AgentFacingText.GetMoreToolsDescription, (string?)getMoreTools["description"]);
        Assert.Contains("context", Strings(getMoreTools["inputSchema"]!["required"]));
        Assert.Equal(
            """{"title":"Get More Tools","readOnlyHint":true,"destructiveHint":false,"idempotentHint":true,"openWorldHint":false}""",
            getMoreTools["annotations"]!.ToJsonString());
    }

    [Fact]
    public void Does_not_add_get_more_tools_when_report_missing_is_disabled()
    {
        var result = Build(Settings(agent: false, context: true, reportMissing: false), TodoTools);

        Assert.DoesNotContain(result.Tools, t => (string?)t?["name"] == "get_more_tools");
        Assert.Equal(S("add_todo", "list_todos", "complete_todo", "get_stats"), result.Tools.Select(t => (string)t!["name"]!).ToArray());
    }

    [Fact]
    public void Adds_get_more_tools_without_context_injection_even_when_context_is_enabled()
    {
        var result = Build(Settings(agent: false, context: true, reportMissing: true), TodoTools);

        var getMoreTools = InputSchema(result, result.Tools.Count - 1);
        Assert.Contains("context", Strings(getMoreTools["required"]));
        Assert.Equal(AgentFacingText.GetMoreToolsContextDescription, (string?)Property(getMoreTools, "context")!["description"]);
        var addTodo = Named(result, "add_todo")["inputSchema"]!;
        Assert.Equal(StringProperty(AgentFacingText.DefaultContextParameterDescription).ToJsonString(), Property(addTodo, "context")!.ToJsonString());
        Assert.Contains("context", Strings(addTodo["required"]));
    }

    [Fact]
    public void Warns_once_when_the_customer_owns_get_more_tools()
    {
        const string Customer = """{"name":"get_more_tools","description":"customer version","inputSchema":{"type":"object","properties":{}}}""";
        var state = new InjectionState();
        var log = new RecordingLog();

        var result = Build(Settings(agent: false, context: true, reportMissing: true), state, log, Customer, EchoTool);
        Build(Settings(agent: false, context: true, reportMissing: true), state, log, Customer, EchoTool);

        var listed = Assert.Single(result.Tools, t => (string?)t?["name"] == "get_more_tools");
        Assert.Equal("customer version", (string?)listed!["description"]);
        Assert.Equal(S(ShadowWarning), log.Lines.Where(l => l.Contains("shadow")).ToArray());
    }

    [Fact]
    public void Adds_context_parameter_to_tools_without_inputSchema()
    {
        var result = Build(ContextOnly(), """{"name":"simple_tool","description":"A simple tool"}""");

        var schema = InputSchema(result);
        Assert.Equal("object", (string?)schema["type"]);
        Assert.Equal("string", (string?)Property(schema, "context")!["type"]);
        Assert.Contains("context", Strings(schema["required"]));
        Assert.Equal(S("name", "description", "inputSchema"), Keys(result.Tools[0]!));
    }

    [Fact]
    public void Adds_context_parameter_to_tools_with_existing_inputSchema()
    {
        var result = Build(
            ContextOnly(),
            """{"name":"existing_tool","description":"A tool with existing schema","inputSchema":{"type":"object","properties":{"text":{"type":"string","description":"Some text"}},"required":["text"]}}""");

        var schema = InputSchema(result);
        Assert.NotNull(Property(schema, "text"));
        Assert.Equal("string", (string?)Property(schema, "context")!["type"]);
        Assert.Equal(S("text", "context"), Strings(schema["required"]));
    }

    [Fact]
    public void Does_not_duplicate_context_parameter_if_already_exists()
    {
        var result = Build(
            ContextOnly(),
            """{"name":"tool_with_context","description":"A tool that already has context","inputSchema":{"type":"object","properties":{"context":{"type":"string","description":"Existing context"}},"required":["context"]}}""");

        Assert.Equal("Existing context", (string?)Property(InputSchema(result), "context")!["description"]);
        Assert.Equal(S("context"), Strings(InputSchema(result)["required"]));
    }

    [Fact]
    public void Handles_tools_with_empty_required_array()
    {
        var result = Build(
            ContextOnly(),
            """{"name":"optional_tool","description":"A tool with no required fields","inputSchema":{"type":"object","properties":{"optional":{"type":"string"}},"required":[]}}""");

        Assert.Equal(S("context"), Strings(InputSchema(result)["required"]));
    }

    [Fact]
    public void Listing_injects_the_default_context_description_into_every_tool()
    {
        var result = Build(Settings(agent: false, context: true, reportMissing: true), TodoTools);

        foreach (var name in S("add_todo", "list_todos", "complete_todo", "get_stats"))
        {
            Assert.Equal(
                StringProperty(AgentFacingText.DefaultContextParameterDescription).ToJsonString(),
                Property(Named(result, name)["inputSchema"]!, "context")!.ToJsonString());
        }
    }

    [Fact]
    public void Custom_context_description_is_used_verbatim()
    {
        var descriptions = S(
            "Explain your reasoning for this action",
            "Why? (explain in detail) - \"Be specific!\"",
            string.Concat(Enumerable.Repeat("Describe the goal behind this call in detail. ", 6)));

        foreach (var description in descriptions)
        {
            var result = Build(Settings(agent: false, context: true, reportMissing: true, custom: description), TodoTools);

            foreach (var name in S("add_todo", "list_todos", "complete_todo", "get_stats"))
            {
                Assert.Equal(description, (string?)Property(Named(result, name)["inputSchema"]!, "context")!["description"]);
            }
        }
    }

    [Fact]
    public void Appends_get_more_tools_and_injects_session_id_and_context()
    {
        var result = Build(Settings(agent: false, context: true, reportMissing: true), EchoTool);

        Assert.Equal(S("echo", "get_more_tools"), result.Tools.Select(t => (string)t!["name"]!).ToArray());
        Assert.NotNull(Property(InputSchema(result), "session_id"));
        Assert.NotNull(Property(InputSchema(result), "context"));
        Assert.Equal(S("context", "session_id"), Sorted(result.Registry["echo"]));
        Assert.Equal(S("session_id"), Sorted(result.Registry["get_more_tools"]));
        Assert.Empty(result.OutputRegistry);
    }

    [Fact]
    public void Skips_handle_injection_when_tracing_is_off_but_still_registers_empty_entries()
    {
        var result = Build(Settings(session: false, agent: false, context: false, reportMissing: true), EchoTool);

        Assert.Null(Property(InputSchema(result), "session_id"));
        Assert.Equal(Tool(EchoTool).ToJsonString(), result.Tools[0]!.ToJsonString());
        Assert.Empty(result.Registry["echo"]);
        Assert.Empty(result.Registry["get_more_tools"]);
    }

    [Fact]
    public void Lists_a_boolean_schema_tool_uninjected_while_the_healthy_tool_keeps_its_handles()
    {
        const string Poison = """{"name":"poison","description":"p","inputSchema":true}""";
        var log = new RecordingLog();

        var result = Build(Settings(agent: false, context: true, reportMissing: true), new InjectionState(), log, Poison, EchoTool);

        Assert.Equal(Tool(Poison).ToJsonString(), Named(result, "poison").ToJsonString());
        Assert.NotNull(Property(Named(result, "echo")["inputSchema"]!, "session_id"));
        Assert.NotNull(Property(Named(result, "echo")["inputSchema"]!, "context"));
        Assert.Contains(log.Lines, l => l.Contains("poison"));
    }

    [Fact]
    public void Inputs_are_not_mutated()
    {
        var fixtures = Fixtures.Json("handles/listing-fixtures.json");
        var input = JsonNode.Parse(fixtures["input_tools"]!.ToJsonString())!.AsArray().Select(t => t!.AsObject()).ToArray();
        var before = input.Select(t => t.ToJsonString()).ToArray();

        var result = ToolListInjector.Build(Settings(context: true, reportMissing: true), input, new InjectionState(), NullAgentCatLog.Instance);

        Assert.Equal(before, input.Select(t => t.ToJsonString()).ToArray());
        foreach (var tool in result.Tools)
        {
            Assert.NotNull(tool);
            Assert.DoesNotContain(input, original => ReferenceEquals(original, tool));
            Assert.Null(tool.Parent);
            tool["inputSchema"]!.AsObject()["added_later"] = true;
            if (tool["outputSchema"] is JsonObject output)
            {
                output["properties"]!.AsObject().Clear();
            }

            tool["name"] = "renamed";
        }

        Assert.Equal(before, input.Select(t => t.ToJsonString()).ToArray());
    }

    [Fact]
    public void Concurrent_listings_share_state_safely()
    {
        const int Threads = 8;
        const int BuildsPerThread = 100;
        var fixtures = Fixtures.Json("handles/listing-fixtures.json");
        var inputJson = fixtures["input_tools"]!.ToJsonString();
        var expected = fixtures["defaults"]!["tools"]!.AsArray().Select(t => t!.ToJsonString()).ToArray();
        var collidingNames = Enumerable.Range(0, BuildsPerThread).Select(i => "collide_" + i).ToArray();
        var inputs = Enumerable.Range(0, Threads)
            .Select(_ => collidingNames
                .Select(name => JsonNode.Parse(inputJson)!.AsArray().Select(t => t!.AsObject())
                    .Concat(new[] { Tool(OwnSessionTool.Replace("own_session", name)) })
                    .ToArray())
                .ToArray())
            .ToArray();
        var settings = Settings(agent: false, context: true, reportMissing: true);
        var state = new InjectionState();
        var log = new RecordingLog();
        var failures = new ConcurrentQueue<Exception>();
        using var start = new Barrier(Threads);

        var threads = inputs.Select(listings => new Thread(() =>
        {
            for (var i = 0; i < BuildsPerThread; i++)
            {
                start.SignalAndWait();
                try
                {
                    var result = ToolListInjector.Build(settings, listings[i], state, log);
                    var fixtureTools = result.Tools.Where(t => (string?)t?["name"] != collidingNames[i]).Select(t => t!.ToJsonString());
                    if (!fixtureTools.SequenceEqual(expected) || result.Registry.Count != 7 || Sorted(result.Registry[collidingNames[i]]).Length != 1)
                    {
                        throw new InvalidOperationException("listing " + i + " differs from the TS output");
                    }
                }
                catch (Exception ex)
                {
                    failures.Enqueue(ex);
                }
            }
        })).ToArray();
        foreach (var thread in threads)
        {
            thread.Start();
        }

        foreach (var thread in threads)
        {
            thread.Join();
        }

        Assert.Empty(failures);
        Assert.True(state.IsSessionParamDeclared("own_session"));
        Assert.All(collidingNames, name => Assert.True(state.IsSessionParamDeclared(name)));
        var errors = log.Lines.Where(l => l.StartsWith("ERROR:", StringComparison.Ordinal)).ToArray();
        Assert.Single(errors, OwnSessionError("own_session"));
        Assert.Equal(
            Sorted(collidingNames.Select(OwnSessionError).Concat(new[] { OwnSessionError("own_session") })),
            Sorted(errors));
    }

    [Theory]
    [InlineData("""{"type":"object","properties":[]}""")]
    [InlineData("""{"type":"object","properties":"x"}""")]
    [InlineData("""{"type":"object","properties":{},"required":5}""")]
    public void Malformed_schema_parts_fall_back_to_the_original_tool(string schema)
    {
        var bad = Tool("""{"name":"bad","inputSchema":""" + schema + "}");
        var log = new RecordingLog();

        var result = ToolListInjector.Build(
            Settings(context: true, reportMissing: true),
            new[] { bad, Tool(EchoTool) },
            new InjectionState(),
            log);

        var listed = Named(result, "bad");
        Assert.NotSame(bad, listed);
        Assert.Equal(bad.ToJsonString(), listed.ToJsonString());
        Assert.Empty(result.Registry["bad"]);
        Assert.Equal(S("msg", "session_id", "agent_id", "context"), Keys(Named(result, "echo")["inputSchema"]!["properties"]));
        Assert.Equal(S("context", "session_id", "agent_id"), Keys(Named(result, "get_more_tools")["inputSchema"]!["properties"]));
        Assert.Equal(
            S("WARN: Handle injection failed for tool \"bad\"" + FailureSeparator, "WARN: Context injection failed for tool \"bad\"" + FailureSeparator),
            log.Lines.Select(WithoutFailureDetail).ToArray());
        Assert.All(log.Lines, l => Assert.True(l.Length > WithoutFailureDetail(l).Length));
    }

    [Theory]
    [MemberData(nameof(EdgeVectors))]
    public void Edge_vectors_match_the_TS_output(
        string input, bool hookMode, string? custom, string tool, string registry, string outputRegistry, string logs)
    {
        var log = new RecordingLog();
        var settings = new InjectionSettings(false, !hookMode, true, true, custom);

        var result = ToolListInjector.Build(settings, new[] { Tool(input) }, new InjectionState(), log);

        Assert.Equal(JsonNode.Parse(tool)!.ToJsonString(), Tokenize(result.Tools[0])!.ToJsonString());
        Assert.Equal(Canonical(JsonNode.Parse(registry)!.AsObject()), Canonical(result.Registry));
        Assert.Equal(Strings(JsonNode.Parse(outputRegistry)), Sorted(result.OutputRegistry));
        Assert.Equal(Strings(JsonNode.Parse(logs)), log.Lines.Select(WithoutFailureDetail).ToArray());
    }

    [Theory]
    [MemberData(nameof(NullEntryVectors))]
    public void Null_entries_pass_through_with_the_TS_failure_lines(
        bool session, bool agent, bool context, bool reportMissing, string tool, string registry, string[] logs)
    {
        var log = new RecordingLog();

        var result = ToolListInjector.Build(
            Settings(session, agent, context, reportMissing),
            new JsonObject?[] { null, Tool("""{"name":"a"}""") },
            new InjectionState(),
            log);

        Assert.Equal(reportMissing ? 3 : 2, result.Tools.Count);
        Assert.Null(result.Tools[0]);
        Assert.Equal(JsonNode.Parse(tool)!.ToJsonString(), Tokenize(result.Tools[1])!.ToJsonString());
        if (reportMissing)
        {
            Assert.Equal("get_more_tools", (string?)result.Tools[2]!["name"]);
        }

        Assert.Equal(registry, Canonical(result.Registry));
        Assert.Empty(result.OutputRegistry);
        Assert.Equal(logs, log.Lines.ToArray());
    }

    [Fact]
    public void Null_entry_rolls_back_the_unknown_registry_entry_like_TS()
    {
        const string Unnamed = """{"inputSchema":{"type":"object"}}""";
        var settings = Settings(agent: false, context: true);

        var before = ToolListInjector.Build(settings, new JsonObject?[] { Tool(Unnamed), null }, new InjectionState(), NullAgentCatLog.Instance);
        var after = ToolListInjector.Build(settings, new JsonObject?[] { null, Tool(Unnamed) }, new InjectionState(), NullAgentCatLog.Instance);

        Assert.Equal(S("session_id", "context"), Keys(before.Tools[0]!["inputSchema"]!["properties"]));
        Assert.Null(before.Tools[1]);
        Assert.Equal("unknown=", Canonical(before.Registry));
        Assert.Null(after.Tools[0]);
        Assert.Equal(S("session_id", "context"), Keys(after.Tools[1]!["inputSchema"]!["properties"]));
        Assert.Equal("unknown=context,session_id", Canonical(after.Registry));
    }
}
