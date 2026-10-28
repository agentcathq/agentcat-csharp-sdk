using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCat.Internal.Ids;
using AgentCat.Internal.Json;
using AgentCat.Internal.Logging;
using AgentCat.Internal.Runtime;
using AgentCat.Internal.Text;

namespace AgentCat.Internal.Telemetry.Sentry;

// TS SentryExporter: each event becomes a log envelope, then a transaction envelope with EnableTracing, then an error
// envelope when the call failed. Each request starts after the answer to the one before: an answer that is not 2xx
// logs its body and the sequence goes on; a request that gets no answer ends it. Export never throws.
internal sealed class SentryTelemetryExporter : ITelemetryExporter
{
    private const string ContentType = "application/x-sentry-envelope";

    private static readonly byte[] LogItemHeader =
        Encoding.UTF8.GetBytes("{\"type\":\"log\",\"item_count\":1,\"content_type\":\"application/vnd.sentry.items.log+json\"}");

    private static readonly byte[] TransactionItemHeader = Encoding.UTF8.GetBytes("{\"type\":\"transaction\"}");

    private static readonly byte[] EventItemHeader = Encoding.UTF8.GetBytes("{\"type\":\"event\",\"content_type\":\"application/json\"}");

    private readonly AgentCatRuntime _runtime;
    private readonly string _endpoint;
    private readonly IReadOnlyList<KeyValuePair<string, string>> _headers;
    private readonly string? _environment;
    private readonly string? _release;
    private readonly bool _enableTracing;

    internal SentryTelemetryExporter(SentryExporter config, AgentCatRuntime runtime)
    {
        var dsn = SentryDsn.Parse(config.Dsn);
        _runtime = runtime;
        _endpoint = dsn.Endpoint;
        _headers = new[]
        {
            new KeyValuePair<string, string>("X-Sentry-Auth", "Sentry sentry_version=7, sentry_client=agentcat/1.0.0, sentry_key=" + dsn.PublicKey),
        };
        _environment = config.Environment;
        _release = config.Release;
        _enableTracing = config.EnableTracing;
        runtime.Log.WriteRoutine("SentryExporter: Initialized with endpoint " + _endpoint);
    }

    public void Export(AgentCatEvent e)
    {
        Step first;
        try
        {
            first = Steps(e);
        }
        catch (Exception ex)
        {
            WriteError(JsError.Text(ex));
            return;
        }

        Run(first, e.Id);
    }

    // Every envelope of the event, built now, so the event is not read after Export returns. TS builds each envelope
    // just before its request, so a step that cannot be built keeps its error, and the error ends the sequence when the
    // step is reached, after the steps before it are sent. Random bytes are drawn in TS order.
    private Step Steps(AgentCatEvent e)
    {
        var log = LogStep(e);
        var last = log;
        if (_enableTracing)
        {
            last = last.Next = Guarded(TransactionStep, e);
        }

        if (e.IsError == true && last.Failure is null)
        {
            last.Next = Guarded(ErrorStep, e);
        }

        return log;
    }

    private static Step Guarded(Func<AgentCatEvent, Step> build, AgentCatEvent e)
    {
        try
        {
            return build(e);
        }
        catch (Exception ex)
        {
            return new Step(ex);
        }
    }

    private void Run(Step? step, string eventId)
    {
        if (step is null)
        {
            return;
        }

        if (step.Failure is { } failure)
        {
            WriteError(JsError.Text(failure));
            return;
        }

        _runtime.Log.WriteRoutine(step.SendingLine);
        _runtime.Exports.Send(new ExportRequest(_endpoint, ContentType, step.Body)
        {
            Headers = _headers,
            ReadsFailureBody = true,
            Completed = response => OnCompleted(step, eventId, response),
        });
    }

    private void OnCompleted(Step step, string eventId, ExportResponse response)
    {
        if (response.Error is { } error)
        {
            WriteError(error);
            return;
        }

        if (response.Ok)
        {
            _runtime.Log.WriteRoutine("Sentry " + step.Kind + " export success - Event: " + eventId);
        }
        else
        {
            _runtime.Log.Write("Sentry " + step.Kind + " export failed - Status: " + response.Status.ToString(CultureInfo.InvariantCulture)
                + ", Body: " + (response.Body ?? ""));
        }

        Run(step.Next, eventId);
    }

    private void WriteError(string error) => _runtime.Log.Write("Sentry export error: " + error);

    // TS eventToLog and createLogEnvelope.
    private Step LogStep(AgentCatEvent e)
    {
        var timestamp = Seconds(e);
        var traceId = TraceIds.GetTraceId(e.SessionId, _runtime.Random);
        var eventId = TraceIds.GetSpanId(e.Id, _runtime.Random) + TraceIds.GetSpanId(e.Id, _runtime.Random);

        var w = new PayloadWriter();
        w.StartObject();
        w.Property("items");
        w.StartArray();
        w.StartObject();
        w.Property("timestamp");
        w.Number(timestamp);
        w.Property("trace_id");
        w.String(traceId);
        w.Property("event_id");
        w.String(eventId);
        w.Property("level");
        w.String(e.IsError == true ? "error" : "info");
        w.Property("body");
        w.String(string.IsNullOrEmpty(e.ResourceName)
            ? "MCP " + Or(e.EventType, "event")
            : "MCP " + Or(e.EventType, "event") + ": " + e.ResourceName);
        w.Property("attributes");
        w.StartObject();
        StringAttribute(w, "eventType", e.EventType);
        StringAttribute(w, "resourceName", e.ResourceName);
        StringAttribute(w, "serverName", e.ServerName);
        StringAttribute(w, "clientName", e.ClientName);
        StringAttribute(w, "sessionId", e.SessionId);
        StringAttribute(w, "projectId", e.ProjectId);
        if (e.Duration is { } duration)
        {
            w.Property("duration_ms");
            w.StartObject();
            w.Property("value");
            w.Number((long)duration);
            w.Property("type");
            w.String("double");
            w.EndObject();
        }

        StringAttribute(w, "actorId", e.IdentifyActorGivenId);
        StringAttribute(w, "actorName", e.IdentifyActorName);
        StringAttribute(w, "userIntent", e.UserIntent);
        StringAttribute(w, "serverVersion", e.ServerVersion);
        StringAttribute(w, "clientVersion", e.ClientVersion);
        if (e.IsError is { } isError)
        {
            w.Property("isError");
            w.StartObject();
            w.Property("value");
            w.Bool(isError);
            w.Property("type");
            w.String("boolean");
            w.EndObject();
        }

        w.EndObject();
        w.EndObject();
        w.EndArray();
        w.EndObject();
        return new Step(
            "log",
            "SentryExporter: Sending log for event " + e.Id + " to Sentry",
            Envelope(eventId, LogItemHeader, w.ToArray(), trailingNewline: true));
    }

    // TS eventToTransaction and createTransactionEnvelope. The timestamp is the end of the transaction.
    private Step TransactionStep(AgentCatEvent e)
    {
        var end = Seconds(e);
        var start = e.Duration is { } duration && duration != 0 ? end - (duration / 1000.0) : end;
        var traceId = TraceIds.GetTraceId(e.SessionId, _runtime.Random);
        var spanId = TraceIds.GetSpanId(e.Id, _runtime.Random);
        var name = TransactionName(e);
        var eventId = TraceIds.GetSpanId(e.Id, _runtime.Random) + TraceIds.GetSpanId(null, _runtime.Random);

        var w = new PayloadWriter();
        w.StartObject();
        w.Property("type");
        w.String("transaction");
        w.Property("event_id");
        w.String(eventId);
        w.Property("timestamp");
        w.Number(end);
        w.Property("start_timestamp");
        w.Number(start);
        w.Property("transaction");
        w.String(name);
        w.Property("contexts");
        w.StartObject();
        w.Property("trace");
        w.StartObject();
        w.Property("trace_id");
        w.String(traceId);
        w.Property("span_id");
        w.String(spanId);
        w.Property("op");
        w.String(Or(e.EventType, "mcp.event"));
        w.Property("status");
        w.String(e.IsError == true ? "internal_error" : "ok");
        w.EndObject();
        AgentCatContext(w, e);
        w.EndObject();
        Tags(w, e);
        Extra(w, e);
        w.EndObject();
        return new Step(
            "transaction",
            "SentryExporter: Sending transaction " + eventId + " to Sentry",
            Envelope(eventId, TransactionItemHeader, w.ToArray(), trailingNewline: false));
    }

    // TS eventToErrorEvent and createErrorEnvelope. With tracing, TS builds a second transaction for the error event
    // and takes its trace ID, its span ID as the parent (the same value as span_id) and its op; that transaction's own
    // event ID is never used, so its random half is not drawn.
    private Step ErrorStep(AgentCatEvent e)
    {
        var traceId = TraceIds.GetTraceId(e.SessionId, _runtime.Random);
        var parentSpanId = _enableTracing ? TraceIds.GetSpanId(e.Id, _runtime.Random) : null;
        Describe(e.Error, out var type, out var value);
        var spanId = TraceIds.GetSpanId(e.Id, _runtime.Random);
        var timestamp = Seconds(e);
        var eventId = TraceIds.GetSpanId(e.Id, _runtime.Random) + TraceIds.GetSpanId(null, _runtime.Random);

        var w = new PayloadWriter();
        w.StartObject();
        w.Property("type");
        w.String("event");
        w.Property("event_id");
        w.String(eventId);
        w.Property("timestamp");
        w.Number(timestamp);
        w.Property("level");
        w.String("error");
        w.Property("exception");
        w.StartObject();
        w.Property("values");
        w.StartArray();
        w.StartObject();
        w.Property("type");
        w.String(type);
        w.Property("value");
        w.String(value);
        w.Property("mechanism");
        w.StartObject();
        w.Property("type");
        w.String("mcp_tool_call");
        w.Property("handled");
        w.Bool(false);
        w.EndObject();
        w.EndObject();
        w.EndArray();
        w.EndObject();
        w.Property("contexts");
        w.StartObject();
        w.Property("trace");
        w.StartObject();
        w.Property("trace_id");
        w.String(traceId);
        w.Property("span_id");
        w.String(spanId);
        if (parentSpanId is not null)
        {
            w.Property("parent_span_id");
            w.String(parentSpanId);
        }

        w.Property("op");
        w.String(Or(e.EventType, "mcp.event"));
        w.EndObject();
        AgentCatContext(w, e);
        w.Property("mcp");
        w.StartObject();
        Defined(w, "resource_name", e.ResourceName);
        Defined(w, "session_id", e.SessionId);
        Defined(w, "event_type", e.EventType);
        Defined(w, "user_intent", e.UserIntent);
        w.EndObject();
        w.EndObject();
        Tags(w, e);
        Extra(w, e);
        w.Property("transaction");
        w.String(TransactionName(e));
        w.EndObject();
        return new Step(
            "error",
            "SentryExporter: Sending error event " + eventId + " to Sentry for Issue creation",
            Envelope(eventId, EventItemHeader, w.ToArray(), trailingNewline: false));
    }

    // The envelope header, the item header and the item, one line each; only the log envelope ends with "\n". TS reads
    // sent_at when it builds each envelope, after the answer to the request before; here each envelope reads the clock
    // once when Export builds it.
    private byte[] Envelope(string eventId, byte[] itemHeader, byte[] item, bool trailingNewline)
    {
        var w = new PayloadWriter();
        w.StartObject();
        w.Property("event_id");
        w.String(eventId);
        w.Property("sent_at");
        w.Date(_runtime.Clock.UtcNow);
        w.EndObject();
        var header = w.ToArray();

        var body = new byte[header.Length + 1 + itemHeader.Length + 1 + item.Length + (trailingNewline ? 1 : 0)];
        Buffer.BlockCopy(header, 0, body, 0, header.Length);
        var at = header.Length;
        body[at++] = (byte)'\n';
        Buffer.BlockCopy(itemHeader, 0, body, at, itemHeader.Length);
        at += itemHeader.Length;
        body[at++] = (byte)'\n';
        Buffer.BlockCopy(item, 0, body, at, item.Length);
        if (trailingNewline)
        {
            body[body.Length - 1] = (byte)'\n';
        }

        return body;
    }

    // TS buildTags: customer tags are namespaced and sent as they are, a null value as null.
    private void Tags(PayloadWriter w, AgentCatEvent e)
    {
        w.Property("tags");
        w.StartObject();
        w.Property("source");
        w.String(WireNames.Source);
        Truthy(w, "environment", _environment);
        Truthy(w, "release", _release);
        Truthy(w, "event_type", e.EventType);
        Truthy(w, "resource", e.ResourceName);
        Truthy(w, "server_name", e.ServerName);
        Truthy(w, "client_name", e.ClientName);
        Truthy(w, "actor_id", e.IdentifyActorGivenId);
        if (e.Tags is { } tags)
        {
            foreach (var tag in tags)
            {
                w.Property("agentcat." + tag.Key);
                w.String(tag.Value);
            }
        }

        w.EndObject();
    }

    // TS buildExtra.
    private static void Extra(PayloadWriter w, AgentCatEvent e)
    {
        w.Property("extra");
        w.StartObject();
        Truthy(w, "session_id", e.SessionId);
        Truthy(w, "project_id", e.ProjectId);
        Truthy(w, "user_intent", e.UserIntent);
        Truthy(w, "actor_name", e.IdentifyActorName);
        Truthy(w, "server_version", e.ServerVersion);
        Truthy(w, "client_version", e.ClientVersion);
        if (e.Duration is { } duration)
        {
            w.Property("duration_ms");
            w.Number((long)duration);
        }

        if (JsSemantics.IsTruthy(e.Error))
        {
            w.Property("error");
            w.Node(e.Error);
        }

        w.EndObject();
    }

    // TS buildContexts: the event's properties after the trace context.
    private static void AgentCatContext(PayloadWriter w, AgentCatEvent e)
    {
        if (e.Properties is { } properties)
        {
            w.Property("agentcat");
            w.Node(properties);
        }
    }

    // TS: a truthy string error is the value; an object or array gives String(message) when it has a "message" key,
    // else its JSON, and String(type) when it has a "type" key; any other error keeps the defaults.
    private static void Describe(JsonNode? error, out string type, out string value)
    {
        type = "ToolCallError";
        value = "Unknown error";
        if (!JsSemantics.IsTruthy(error))
        {
            return;
        }

        var node = JsonTreeCopy.Expand(error!);
        if (node is JsonObject obj)
        {
            value = TryGetMember(obj, "message", out var message) ? JsSemantics.ToJsString(message) : JsonText(obj);
            if (TryGetMember(obj, "type", out var errorType))
            {
                type = JsSemantics.ToJsString(errorType);
            }
        }
        else if (node is JsonArray)
        {
            value = JsonText(node);
        }
        else if (node.GetValueKind() == JsonValueKind.String)
        {
            value = JsSemantics.ToJsString(node);
        }
    }

    // JS `name in obj` for a JSON object: an own key, matched exactly.
    private static bool TryGetMember(JsonObject obj, string name, out JsonNode? member)
    {
        foreach (var property in obj)
        {
            if (string.Equals(property.Key, name, StringComparison.Ordinal))
            {
                member = property.Value;
                return true;
            }
        }

        member = null;
        return false;
    }

    private static string JsonText(JsonNode node) => Encoding.UTF8.GetString(JsJson.ToUtf8(node));

    // JS `new Date(timestamp).getTime() / 1000`.
    private static double Seconds(AgentCatEvent e) => e.Timestamp.ToUnixTimeMilliseconds() / 1000.0;

    private static string TransactionName(AgentCatEvent e) => string.IsNullOrEmpty(e.ResourceName)
        ? Or(e.EventType, "mcp.event")
        : Or(e.EventType, "mcp") + " - " + e.ResourceName;

    private static void StringAttribute(PayloadWriter w, string name, string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        w.Property(name);
        w.StartObject();
        w.Property("value");
        w.String(value);
        w.Property("type");
        w.String("string");
        w.EndObject();
    }

    // JS `if (value) obj[name] = value`.
    private static void Truthy(PayloadWriter w, string name, string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        w.Property(name);
        w.String(value);
    }

    // A JS undefined is left out; an empty string is sent.
    private static void Defined(PayloadWriter w, string name, string? value)
    {
        if (value is null)
        {
            return;
        }

        w.Property(name);
        w.String(value);
    }

    // JS `value || fallback`.
    private static string Or(string? value, string fallback) => string.IsNullOrEmpty(value) ? fallback : value!;

    // One envelope of the sequence, or the error that kept it from being built.
    private sealed class Step
    {
        internal Step(string kind, string sendingLine, byte[] body)
        {
            Kind = kind;
            SendingLine = sendingLine;
            Body = body;
        }

        internal Step(Exception failure)
        {
            Kind = "";
            SendingLine = "";
            Body = Array.Empty<byte>();
            Failure = failure;
        }

        internal string Kind { get; }

        internal string SendingLine { get; }

        internal byte[] Body { get; }

        internal Exception? Failure { get; }

        internal Step? Next { get; set; }
    }
}
