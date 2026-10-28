using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json.Nodes;
using AgentCat.Internal.Ids;
using AgentCat.Internal.Json;
using AgentCat.Internal.Logging;
using AgentCat.Internal.Runtime;
using AgentCat.Internal.Text;

namespace AgentCat.Internal.Telemetry.PostHog;

// TS PostHogExporter: each event becomes one batch request holding a capture event, then an $exception event for a
// failed call, then an $ai_span event for a tool call with EnableAITracing. Export never throws.
internal sealed class PostHogTelemetryExporter : ITelemetryExporter
{
    private const string DefaultHost = "https://us.i.posthog.com";
    private const string ToolCall = "mcp:tools/call";

    private readonly AgentCatRuntime _runtime;
    private readonly string _batchUrl;
    private readonly string _apiKey;
    private readonly bool _enableAITracing;

    internal PostHogTelemetryExporter(PostHogExporter config, AgentCatRuntime runtime)
    {
        _runtime = runtime;
        _enableAITracing = config.EnableAITracing;
        var host = string.IsNullOrEmpty(config.Host) ? DefaultHost : config.Host!;
        if (host.Length > 0 && host[host.Length - 1] == '/')
        {
            host = host.Substring(0, host.Length - 1);
        }

        _batchUrl = host + "/batch";
        _apiKey = config.ApiKey;
        runtime.Log.WriteRoutine("PostHogExporter: Initialized with endpoint " + _batchUrl);
    }

    // TS mapEventType.
    internal static string MapEventType(string eventType)
    {
        switch (eventType)
        {
            case "mcp:tools/call":
                return "mcp_tool_call";
            case "mcp:tools/list":
                return "mcp_tools_list";
            case "mcp:initialize":
                return "mcp_initialize";
            case "mcp:resources/read":
                return "mcp_resource_read";
            case "mcp:resources/list":
                return "mcp_resources_list";
            case "mcp:prompts/get":
                return "mcp_prompt_get";
            case "mcp:prompts/list":
                return "mcp_prompts_list";
            default:
                var rest = eventType.StartsWith("mcp:", StringComparison.Ordinal) ? eventType.Substring(4) : eventType;
                return "mcp_" + rest.Replace('/', '_');
        }
    }

    public void Export(AgentCatEvent e)
    {
        byte[] body;
        try
        {
            var batch = new List<Item> { CaptureItem(e) };
            if (e.IsError == true && JsSemantics.IsTruthy(e.Error))
            {
                batch.Add(ExceptionItem(e));
            }

            if (_enableAITracing && e.EventType == ToolCall)
            {
                batch.Add(AiSpanItem(e));
            }

            _runtime.Log.WriteRoutine("PostHogExporter: Sending " + batch.Count.ToString(CultureInfo.InvariantCulture) + " event(s) for " + e.Id);
            body = Serialize(batch);
        }
        catch (Exception ex)
        {
            _runtime.Log.Write("PostHog export error: " + JsError.Text(ex));
            return;
        }

        var eventId = e.Id;
        _runtime.Exports.Send(new ExportRequest(_batchUrl, "application/json", body)
        {
            ReadsFailureBody = true,
            Completed = response => OnCompleted(eventId, response),
        });
    }

    private void OnCompleted(string eventId, ExportResponse response)
    {
        if (response.Error is { } error)
        {
            _runtime.Log.Write("PostHog export error: " + error);
            return;
        }

        if (response.Ok)
        {
            _runtime.Log.WriteRoutine("PostHog export success - Event: " + eventId);
            return;
        }

        _runtime.Log.Write("PostHog export failed - Status: " + response.Status.ToString(CultureInfo.InvariantCulture)
            + ", Body: " + (response.Body ?? ""));
    }

    private byte[] Serialize(List<Item> batch)
    {
        var w = new PayloadWriter();
        w.StartObject();
        w.Property("api_key");
        w.String(_apiKey);
        w.Property("batch");
        w.StartArray();
        foreach (var item in batch)
        {
            w.StartObject();
            w.Property("event");
            w.String(item.Event);
            w.Property("distinct_id");
            w.String(item.DistinctId);
            w.Property("properties");
            item.Properties.Write(w);
            w.Property("timestamp");
            w.Date(item.Timestamp);
            w.Property("type");
            w.String("capture");
            w.EndObject();
        }

        w.EndArray();
        w.EndObject();
        return w.ToArray();
    }

    private Item CaptureItem(AgentCatEvent e)
    {
        var p = new Props();
        p.Set("source", Value.Of(WireNames.Source));
        if (!string.IsNullOrEmpty(e.SessionId))
        {
            p.Set("$session_id", Value.Of(UuidV7.FromPrefixedId(e.SessionId)));
        }

        Resource(p, e);
        if (e.Duration is { } duration)
        {
            p.Set("duration_ms", Value.Of((long)duration));
        }

        Truthy(p, "server_name", e.ServerName);
        Truthy(p, "server_version", e.ServerVersion);
        Truthy(p, "client_name", e.ClientName);
        Truthy(p, "client_version", e.ClientVersion);
        Truthy(p, "project_id", e.ProjectId);
        Truthy(p, "user_intent", e.UserIntent);
        if (e.IsError is { } isError)
        {
            p.Set("is_error", Value.Of(isError));
        }

        if (e.Parameters is { } parameters)
        {
            p.Set("parameters", Value.Of(parameters));
        }

        if (e.Response is { } response)
        {
            p.Set("response", Value.Of(response));
        }

        var set = new Props();
        if (!string.IsNullOrEmpty(e.IdentifyActorName))
        {
            set.Set("name", Value.Of(e.IdentifyActorName!));
        }

        if (e.IdentifyActorData is { } data)
        {
            foreach (var entry in data)
            {
                set.Set(entry.Key, Value.Of(entry.Value));
            }
        }

        if (set.Count > 0)
        {
            p.Set("$set", Value.Of(set));
        }

        Spread(p, e);
        return new Item(MapEventType(e.EventType ?? ""), DistinctId(e), p, Timestamp(e));
    }

    private Item ExceptionItem(AgentCatEvent e)
    {
        var p = new Props();
        p.Set("$exception_source", Value.Of("backend"));
        if (!string.IsNullOrEmpty(e.SessionId))
        {
            p.Set("$session_id", Value.Of(UuidV7.FromPrefixedId(e.SessionId)));
        }

        if (JsonTreeCopy.Expand(e.Error!) is JsonObject error)
        {
            Member(p, error, "message", "$exception_message");
            Member(p, error, "type", "$exception_type");
            Member(p, error, "stack", "$exception_stacktrace");
        }

        Resource(p, e);
        Truthy(p, "server_name", e.ServerName);
        Truthy(p, "server_version", e.ServerVersion);
        Truthy(p, "client_name", e.ClientName);
        Truthy(p, "client_version", e.ClientVersion);
        return new Item("$exception", DistinctId(e), p, Timestamp(e));
    }

    private Item AiSpanItem(AgentCatEvent e)
    {
        var p = new Props();
        p.Set("$ai_trace_id", Value.Of(UuidV7.FromPrefixedId(string.IsNullOrEmpty(e.SessionId) ? e.Id : e.SessionId)));
        p.Set("$ai_span_id", Value.Of(UuidV7.FromPrefixedId(e.Id)));
        p.Set("$ai_span_name", Value.Of(string.IsNullOrEmpty(e.ResourceName) ? "unknown_tool" : e.ResourceName!));
        p.Set("$ai_is_error", Value.Of(e.IsError == true));
        p.Set("source", Value.Of(WireNames.Source));
        if (!string.IsNullOrEmpty(e.SessionId))
        {
            p.Set("$ai_session_id", Value.Of("agentcat_" + e.SessionId));
            p.Set("$session_id", Value.Of(UuidV7.FromPrefixedId(e.SessionId)));
        }

        if (e.Duration is { } duration)
        {
            p.Set("$ai_latency", Value.Of(duration / 1000.0));
        }

        if (e.IsError == true && JsSemantics.IsTruthy(e.Error))
        {
            p.Set("$ai_error", Value.Of(e.Error));
        }

        if (e.Parameters is { } parameters)
        {
            p.Set("$ai_input_state", Value.Of(parameters));
        }

        if (e.Response is { } response)
        {
            p.Set("$ai_output_state", Value.Of(response));
        }

        Truthy(p, "server_name", e.ServerName);
        Truthy(p, "client_name", e.ClientName);
        Spread(p, e);
        return new Item("$ai_span", DistinctId(e), p, Timestamp(e));
    }

    // JS `identifyActorGivenId || sessionId || "anonymous"`.
    private static string DistinctId(AgentCatEvent e)
        => !string.IsNullOrEmpty(e.IdentifyActorGivenId) ? e.IdentifyActorGivenId!
            : !string.IsNullOrEmpty(e.SessionId) ? e.SessionId
            : "anonymous";

    private DateTimeOffset Timestamp(AgentCatEvent e) => e.Timestamp == default ? _runtime.Clock.UtcNow : e.Timestamp;

    private static void Resource(Props p, AgentCatEvent e)
    {
        if (string.IsNullOrEmpty(e.ResourceName))
        {
            return;
        }

        p.Set("resource_name", Value.Of(e.ResourceName!));
        if (e.EventType == ToolCall)
        {
            p.Set("tool_name", Value.Of(e.ResourceName!));
        }
    }

    // JS `if (value) properties[name] = value`.
    private static void Truthy(Props p, string name, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            p.Set(name, Value.Of(value!));
        }
    }

    // A truthy member of an error object, copied as it is.
    private static void Member(Props p, JsonObject error, string name, string key)
    {
        foreach (var property in error)
        {
            if (string.Equals(property.Key, name, StringComparison.Ordinal))
            {
                if (JsSemantics.IsTruthy(property.Value))
                {
                    p.Set(key, Value.Of(property.Value));
                }

                return;
            }
        }
    }

    // Customer tags, then customer properties, each as a top-level property that can replace any earlier key in place.
    private static void Spread(Props p, AgentCatEvent e)
    {
        if (e.Tags is { } tags)
        {
            foreach (var tag in tags)
            {
                p.Set(tag.Key, Value.Of(tag.Value));
            }
        }

        if (e.Properties is { } properties)
        {
            foreach (var property in properties)
            {
                p.Set(property.Key, Value.Of(property.Value));
            }
        }
    }

    private sealed class Item
    {
        internal Item(string name, string distinctId, Props properties, DateTimeOffset timestamp)
        {
            Event = name;
            DistinctId = distinctId;
            Properties = properties;
            Timestamp = timestamp;
        }

        internal string Event { get; }

        internal string DistinctId { get; }

        internal Props Properties { get; }

        internal DateTimeOffset Timestamp { get; }
    }

    // A JS object's own properties: insertion order, and a key that is set again keeps its place.
    private sealed class Props
    {
        private readonly List<string> _keys = new();
        private readonly List<Value> _values = new();
        private readonly Dictionary<string, int> _index = new(StringComparer.Ordinal);

        internal int Count => _keys.Count;

        internal void Set(string key, Value value)
        {
            if (_index.TryGetValue(key, out var at))
            {
                _values[at] = value;
                return;
            }

            _index[key] = _keys.Count;
            _keys.Add(key);
            _values.Add(value);
        }

        internal void Write(PayloadWriter w)
        {
            w.StartObject();
            for (var i = 0; i < _keys.Count; i++)
            {
                w.Property(_keys[i]);
                _values[i].Write(w);
            }

            w.EndObject();
        }
    }

    private enum Kind
    {
        Text,
        Integer,
        Real,
        Flag,
        Tree,
        Object,
    }

    private readonly struct Value
    {
        private readonly Kind _kind;
        private readonly string? _text;
        private readonly long _integer;
        private readonly double _real;
        private readonly JsonNode? _node;
        private readonly Props? _object;

        private Value(Kind kind, string? text = null, long integer = 0, double real = 0, JsonNode? node = null, Props? obj = null)
        {
            _kind = kind;
            _text = text;
            _integer = integer;
            _real = real;
            _node = node;
            _object = obj;
        }

        internal static Value Of(string? text) => new(Kind.Text, text: text);

        internal static Value Of(long number) => new(Kind.Integer, integer: number);

        internal static Value Of(double number) => new(Kind.Real, real: number);

        internal static Value Of(bool flag) => new(Kind.Flag, integer: flag ? 1 : 0);

        internal static Value Of(JsonNode? node) => new(Kind.Tree, node: node);

        internal static Value Of(Props obj) => new(Kind.Object, obj: obj);

        internal void Write(PayloadWriter w)
        {
            switch (_kind)
            {
                case Kind.Text:
                    w.String(_text);
                    break;
                case Kind.Integer:
                    w.Number(_integer);
                    break;
                case Kind.Real:
                    w.Number(_real);
                    break;
                case Kind.Flag:
                    w.Bool(_integer != 0);
                    break;
                case Kind.Tree:
                    w.Node(_node);
                    break;
                default:
                    _object!.Write(w);
                    break;
            }
        }
    }
}
