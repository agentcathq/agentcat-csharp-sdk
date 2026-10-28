using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCat.Internal.Logging;
using AgentCat.Internal.Text;

namespace AgentCat.Internal.Injection;

internal static class ToolListInjector
{
    private const string NameKey = "name";
    private const string InputSchemaKey = "inputSchema";
    private const string OutputSchemaKey = "outputSchema";
    private const string TypeKey = "type";
    private const string DescriptionKey = "description";
    private const string PropertiesKey = "properties";
    private const string RequiredKey = "required";
    private const string AdditionalPropertiesKey = "additionalProperties";
    private const string UnknownToolName = "unknown";
    private const string FailureSuffix = "; listing it unmodified - ";
    private const string NullToolFailure = "TypeError: Cannot read properties of null (reading 'name')";

    private const string CustomerOwnedGetMoreToolsWarning =
        "WARN: This server registers its own \"get_more_tools\" tool, which is shadowed by AgentCat's report-missing tool: "
        + "calls to it are answered by AgentCat and never reach your handler. "
        + "Rename your tool, or set enableReportMissing: false to keep yours reachable.";

    internal static InjectedList Build(InjectionSettings settings, IReadOnlyList<JsonObject?> tools, InjectionState state, IAgentCatLog log)
    {
        var drafts = new List<ToolDraft>(tools.Count + 1);
        for (var i = 0; i < tools.Count; i++)
        {
            drafts.Add(new ToolDraft(tools[i]));
        }

        if (settings.EnableReportMissing)
        {
            if (!drafts.Exists(d => d.IsGetMoreTools))
            {
                drafts.Add(new ToolDraft(GetMoreTools.CreateDescriptor()));
            }
            else if (state.TryWarnCustomerOwnedGetMoreTools())
            {
                log.Write(CustomerOwnedGetMoreToolsWarning);
            }
        }

        var registry = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var outputRegistry = new HashSet<string>(StringComparer.Ordinal);

        if (settings.InjectSessionId || settings.InjectAgentId)
        {
            foreach (var draft in drafts)
            {
                InjectHandles(draft, settings, state, log, registry, outputRegistry);
            }
        }

        if (settings.EnableToolCallContext)
        {
            var description = settings.CustomContextDescription is { Length: > 0 } custom
                ? custom
                : AgentFacingText.DefaultContextParameterDescription;
            foreach (var draft in drafts)
            {
                if (!draft.IsGetMoreTools)
                {
                    InjectContext(draft, description, log, registry);
                }
            }
        }

        var listed = new JsonObject?[drafts.Count];
        var finalRegistry = new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.Ordinal);
        foreach (var entry in registry)
        {
            finalRegistry.Add(entry.Key, entry.Value);
        }

        for (var i = 0; i < drafts.Count; i++)
        {
            listed[i] = drafts[i].Materialize();
            if (drafts[i].Name is { Length: > 0 } name && !finalRegistry.ContainsKey(name))
            {
                finalRegistry.Add(name, Array.Empty<string>());
            }
        }

        return new InjectedList(listed, finalRegistry, outputRegistry);
    }

    private static void InjectHandles(
        ToolDraft draft,
        InjectionSettings settings,
        InjectionState state,
        IAgentCatLog log,
        Dictionary<string, HashSet<string>> registry,
        HashSet<string> outputRegistry)
    {
        var toolName = draft.ToolName;
        string failure;
        if (draft.IsNull)
        {
            failure = NullToolFailure;
        }
        else
        {
            try
            {
                AddHandleParameters(draft, toolName, settings, state, log, registry, outputRegistry);
                return;
            }
            catch (Exception ex)
            {
                failure = ErrorText.Of(ex);
            }
        }

        registry.Remove(toolName);
        outputRegistry.Remove(toolName);
        log.Write("WARN: Handle injection failed for tool \"" + toolName + "\"" + FailureSuffix + failure);
    }

    private static void AddHandleParameters(
        ToolDraft draft,
        string toolName,
        InjectionSettings settings,
        InjectionState state,
        IAgentCatLog log,
        Dictionary<string, HashSet<string>> registry,
        HashSet<string> outputRegistry)
    {
        var original = draft.InputSchema;
        if (IsComposed(original))
        {
            if (settings.InjectSessionId && JsSemantics.IsTruthy(Member(Member(original, PropertiesKey), WireNames.SessionIdParam)))
            {
                state.DeclareSessionParam(toolName);
            }

            log.Write("WARN: Tool \"" + toolName + "\" has complex schema (oneOf/allOf/anyOf). Skipping handle injection.");
            return;
        }

        var schema = CopyInputSchema(original);
        var properties = EnsureProperties(schema);
        RemoveAdditionalPropertiesFalse(schema);

        if (settings.InjectSessionId)
        {
            if (JsSemantics.IsTruthy(Member(properties, WireNames.SessionIdParam)))
            {
                state.DeclareSessionParam(toolName);
                if (state.TryReportConflict(toolName))
                {
                    log.Write(SessionIdConflict(toolName));
                }
            }
            else
            {
                properties[WireNames.SessionIdParam] = new JsonObject
                {
                    [TypeKey] = "string",
                    [DescriptionKey] = AgentFacingText.SessionIdParamDescription,
                    ["pattern"] = AgentFacingText.SessionIdParamPattern,
                };
                Record(registry, toolName, WireNames.SessionIdParam);
                AddToRequired(schema, WireNames.SessionIdParam);
            }
        }

        if (settings.InjectAgentId)
        {
            if (JsSemantics.IsTruthy(Member(properties, WireNames.AgentIdParam)))
            {
                log.Write("WARN: Tool \"" + toolName + "\" already has '" + WireNames.AgentIdParam + "' parameter. Skipping agent_id injection.");
            }
            else
            {
                properties[WireNames.AgentIdParam] = StringProperty(AgentFacingText.AgentIdParamDescription);
                Record(registry, toolName, WireNames.AgentIdParam);
                AddToRequired(schema, WireNames.AgentIdParam);
            }
        }

        var outputSchema = WithMcpSession(draft.OutputSchema, settings, toolName, log);
        draft.ReplaceInputSchema(schema);
        if (outputSchema is not null)
        {
            draft.ReplaceOutputSchema(outputSchema);
            outputRegistry.Add(toolName);
        }
    }

    private static JsonObject? WithMcpSession(JsonNode? outputSchema, InjectionSettings settings, string toolName, IAgentCatLog log)
    {
        if (!JsSemantics.IsTruthy(outputSchema))
        {
            return null;
        }

        if (IsComposed(outputSchema))
        {
            log.Write("WARN: Tool \"" + toolName + "\" has complex outputSchema (oneOf/allOf/anyOf). Skipping "
                + WireNames.McpSessionKey + " injection; mint-back stays content-only for this tool.");
            return null;
        }

        var copy = CopyObject(outputSchema, OutputSchemaKey);
        var properties = EnsureProperties(copy);
        if (JsSemantics.IsTruthy(Member(properties, WireNames.McpSessionKey)))
        {
            log.Write("WARN: Tool \"" + toolName + "\" already declares '" + WireNames.McpSessionKey + "' in outputSchema. Skipping injection.");
            return null;
        }

        var fields = new JsonObject();
        if (settings.InjectSessionId)
        {
            fields[WireNames.SessionIdParam] = StringProperty(AgentFacingText.McpSessionSessionIdDescription);
        }

        if (settings.InjectAgentId)
        {
            fields[WireNames.AgentIdParam] = StringProperty(AgentFacingText.McpSessionAgentIdDescription);
        }

        if (settings.InjectSessionId)
        {
            fields[WireNames.McpSessionStatusKey] = new JsonObject
            {
                [TypeKey] = "string",
                ["enum"] = new JsonArray(WireNames.McpSessionStatusIssued, WireNames.McpSessionStatusActive, WireNames.McpSessionStatusUnrecognized),
                [DescriptionKey] = AgentFacingText.McpSessionStatusDescription,
            };
        }

        properties[WireNames.McpSessionKey] = new JsonObject
        {
            [TypeKey] = "object",
            [DescriptionKey] = settings.InjectSessionId
                ? AgentFacingText.McpSessionFieldDescription
                : AgentFacingText.McpSessionFieldDescriptionHookMode,
            [PropertiesKey] = fields,
        };
        return copy;
    }

    private static void InjectContext(ToolDraft draft, string description, IAgentCatLog log, Dictionary<string, HashSet<string>> registry)
    {
        var toolName = draft.ToolName;
        string failure;
        if (draft.IsNull)
        {
            failure = NullToolFailure;
        }
        else
        {
            try
            {
                AddContextParameter(draft, toolName, description, log, registry);
                return;
            }
            catch (Exception ex)
            {
                failure = ErrorText.Of(ex);
            }
        }

        if (registry.TryGetValue(toolName, out var injected))
        {
            injected.Remove(WireNames.ContextParam);
        }

        log.Write("WARN: Context injection failed for tool \"" + toolName + "\"" + FailureSuffix + failure);
    }

    private static void AddContextParameter(
        ToolDraft draft,
        string toolName,
        string description,
        IAgentCatLog log,
        Dictionary<string, HashSet<string>> registry)
    {
        var current = draft.InputSchema;
        if (JsSemantics.IsTruthy(Member(Member(current, PropertiesKey), WireNames.ContextParam)))
        {
            log.Write("WARN: Tool \"" + toolName + "\" already has '" + WireNames.ContextParam + "' parameter. Skipping context injection.");
            return;
        }

        if (IsComposed(current))
        {
            log.Write("WARN: Tool \"" + toolName + "\" has complex schema (oneOf/allOf/anyOf). Skipping context injection.");
            return;
        }

        var schema = CopyInputSchema(current);
        var properties = EnsureProperties(schema);
        RemoveAdditionalPropertiesFalse(schema);
        properties[WireNames.ContextParam] = StringProperty(description);
        Record(registry, toolName, WireNames.ContextParam);
        AddToRequired(schema, WireNames.ContextParam);
        draft.ReplaceInputSchema(schema);
    }

    private static JsonObject CopyInputSchema(JsonNode? schema)
    {
        if (!JsSemantics.IsTruthy(schema))
        {
            return new JsonObject
            {
                [TypeKey] = "object",
                [PropertiesKey] = new JsonObject(),
                [RequiredKey] = new JsonArray(),
            };
        }

        return CopyObject(schema, InputSchemaKey);
    }

    private static JsonObject CopyObject(JsonNode? node, string name)
        => node is JsonObject obj ? obj.DeepClone().AsObject() : throw Unexpected(name, "an object");

    private static JsonObject EnsureProperties(JsonObject schema)
    {
        var properties = Member(schema, PropertiesKey);
        if (!JsSemantics.IsTruthy(properties))
        {
            var created = new JsonObject();
            schema[PropertiesKey] = created;
            return created;
        }

        return properties as JsonObject ?? throw Unexpected(PropertiesKey, "an object");
    }

    private static void RemoveAdditionalPropertiesFalse(JsonObject schema)
    {
        if (Member(schema, AdditionalPropertiesKey) is JsonValue value && value.GetValueKind() == JsonValueKind.False)
        {
            schema.Remove(AdditionalPropertiesKey);
        }
    }

    private static void AddToRequired(JsonObject schema, string parameter)
    {
        var required = Member(schema, RequiredKey);
        if (required is JsonArray names)
        {
            foreach (var name in names)
            {
                if (IsString(name, parameter))
                {
                    return;
                }
            }

            names.Add((JsonNode)parameter);
        }
        else if (!JsSemantics.IsTruthy(required))
        {
            schema[RequiredKey] = new JsonArray(parameter);
        }
        else
        {
            throw Unexpected(RequiredKey, "an array");
        }
    }

    private static void Record(Dictionary<string, HashSet<string>> registry, string toolName, string parameter)
    {
        if (!registry.TryGetValue(toolName, out var injected))
        {
            injected = new HashSet<string>(StringComparer.Ordinal);
            registry.Add(toolName, injected);
        }

        injected.Add(parameter);
    }

    private static JsonObject StringProperty(string description) => new()
    {
        [TypeKey] = "string",
        [DescriptionKey] = description,
    };

    private static JsonNode? Member(JsonNode? node, string key)
        => node is JsonObject obj && obj.TryGetPropertyValue(key, out var value) ? value : null;

    private static bool IsComposed(JsonNode? schema)
        => JsSemantics.IsTruthy(Member(schema, "oneOf"))
            || JsSemantics.IsTruthy(Member(schema, "allOf"))
            || JsSemantics.IsTruthy(Member(schema, "anyOf"));

    private static bool IsString(JsonNode? node, string expected)
    {
        if (node is not JsonValue value)
        {
            return false;
        }

        if (value.TryGetValue<JsonElement>(out var element))
        {
            return element.ValueKind == JsonValueKind.String && element.ValueEquals(expected);
        }

        return value.TryGetValue<string>(out var text) && string.Equals(text, expected, StringComparison.Ordinal);
    }

    private static string? StringValue(JsonNode? node)
    {
        if (node is not JsonValue value)
        {
            return null;
        }

        if (value.TryGetValue<JsonElement>(out var element))
        {
            return element.ValueKind == JsonValueKind.String ? element.GetString() : null;
        }

        return value.TryGetValue<string>(out var text) ? text : null;
    }

    private static InvalidOperationException Unexpected(string member, string expected)
        => new("'" + member + "' is not " + expected);

    private static string SessionIdConflict(string toolName) =>
        "ERROR: Tool \"" + toolName + "\" already declares a '" + WireNames.SessionIdParam + "' parameter. "
        + "AgentCat will not inject its own, and calls to this tool are published without a session, "
        + "so they cannot be correlated. Your parameter is untouched and still reaches your handler. "
        + "If you already manage sessions, pass a resolveSessionId hook to track() \u2014 AgentCat will "
        + "derive its session from your identifier and stop injecting " + WireNames.SessionIdParam + " entirely.";

    private sealed class ToolDraft
    {
        private readonly JsonObject? _source;
        private JsonObject? _inputSchema;
        private JsonObject? _outputSchema;

        internal ToolDraft(JsonObject? source)
        {
            _source = source;
            Name = StringValue(Member(source, NameKey));
        }

        internal string? Name { get; }

        internal bool IsNull => _source is null;

        internal string ToolName => Name is { Length: > 0 } name ? name : UnknownToolName;

        internal bool IsGetMoreTools => string.Equals(Name, WireNames.GetMoreToolsName, StringComparison.Ordinal);

        internal JsonNode? InputSchema => _inputSchema ?? Member(_source, InputSchemaKey);

        internal JsonNode? OutputSchema => _outputSchema ?? Member(_source, OutputSchemaKey);

        internal void ReplaceInputSchema(JsonObject schema) => _inputSchema = schema;

        internal void ReplaceOutputSchema(JsonObject schema) => _outputSchema = schema;

        internal JsonObject? Materialize()
        {
            if (_source is null)
            {
                return null;
            }

            var copy = new JsonObject(_source.Options);
            foreach (var property in _source)
            {
                copy[property.Key] = IsReplaced(property.Key) ? null : property.Value?.DeepClone();
            }

            if (_inputSchema is not null)
            {
                copy[InputSchemaKey] = _inputSchema;
            }

            if (_outputSchema is not null)
            {
                copy[OutputSchemaKey] = _outputSchema;
            }

            return copy;
        }

        private bool IsReplaced(string key)
            => (_inputSchema is not null && key == InputSchemaKey) || (_outputSchema is not null && key == OutputSchemaKey);
    }
}
