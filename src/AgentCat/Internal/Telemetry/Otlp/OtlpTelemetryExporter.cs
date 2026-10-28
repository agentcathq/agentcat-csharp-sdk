using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using AgentCat.Internal.Ids;
using AgentCat.Internal.Json;
using AgentCat.Internal.Logging;
using AgentCat.Internal.Runtime;
using AgentCat.Internal.Text;

namespace AgentCat.Internal.Telemetry.Otlp;

// TS OtlpExporter: one OTLP/HTTP JSON span per event, posted to the customer's collector.
internal sealed class OtlpTelemetryExporter : ITelemetryExporter
{
    private const string TracesPath = "/v1/traces";

    private readonly string _name;
    private readonly AgentCatRuntime _runtime;
    private readonly string _url;
    private readonly IReadOnlyList<KeyValuePair<string, string>> _headers;

    internal OtlpTelemetryExporter(string name, OtlpExporter config, AgentCatRuntime runtime)
    {
        _name = name;
        _runtime = runtime;
        var url = config.Endpoint.TrimEnd('/');
        _url = url.EndsWith(TracesPath, StringComparison.Ordinal) ? url : url + TracesPath;
        var headers = new List<KeyValuePair<string, string>>();
        if (config.Headers is { } configured)
        {
            foreach (var header in configured)
            {
                headers.Add(new KeyValuePair<string, string>(header.Key, header.Value));
            }
        }

        _headers = headers;
    }

    public void Export(AgentCatEvent e)
    {
        var eventId = e.Id;
        ExportRequest request;
        try
        {
            request = new ExportRequest(_url, "application/json", Build(e))
            {
                Headers = _headers,
                Completed = response => OnCompleted(eventId, response),
            };
        }
        catch (Exception ex)
        {
            throw new JsError("Error", "OTLP export error: " + JsError.Text(ex));
        }

        _runtime.Exports.Send(request);
    }

    private void OnCompleted(string eventId, ExportResponse response)
    {
        if (response.Ok)
        {
            _runtime.Log.WriteRoutine("Successfully exported event to OTLP: " + eventId);
            return;
        }

        var error = response.Error
            ?? "Error: OTLP export failed: " + response.Status.ToString(CultureInfo.InvariantCulture) + " " + response.StatusText;
        _runtime.Log.Write(TelemetryManager.ExportFailedLine(_name, "OTLP export error: " + error));
    }

    private byte[] Build(AgentCatEvent e)
    {
        var traceId = TraceIds.GetTraceId(e.SessionId, _runtime.Random);
        var spanId = TraceIds.GetSpanId(e.Id, _runtime.Random);
        var start = e.Timestamp.ToUnixTimeMilliseconds() * 1_000_000L;
        var end = e.Duration is { } duration && duration != 0 ? start + duration * 1_000_000L : start;

        var w = new PayloadWriter();
        w.StartObject();
        w.Property("resourceSpans");
        w.StartArray();
        w.StartObject();
        w.Property("resource");
        w.StartObject();
        w.Property("attributes");
        w.StartArray();
        StringAttribute(w, "service.name", Or(e.ServerName, "mcp-server"));
        StringAttribute(w, "service.version", Or(e.ServerVersion, "unknown"));
        w.EndArray();
        w.EndObject();
        w.Property("scopeSpans");
        w.StartArray();
        w.StartObject();
        w.Property("scope");
        w.StartObject();
        w.Property("name");
        w.String("agentcat");
        w.Property("version");
        w.String(Or(e.AgentCatVersion, "unknown"));
        w.EndObject();
        w.Property("spans");
        w.StartArray();
        w.StartObject();
        w.Property("traceId");
        w.String(traceId);
        w.Property("spanId");
        w.String(spanId);
        w.Property("name");
        w.String(Or(e.EventType, "mcp.event"));
        w.Property("kind");
        w.Number(2L);
        w.Property("startTimeUnixNano");
        w.String(start.ToString(CultureInfo.InvariantCulture));
        w.Property("endTimeUnixNano");
        w.String(end.ToString(CultureInfo.InvariantCulture));
        w.Property("attributes");
        w.StartArray();
        StringAttribute(w, "source", WireNames.Source);
        StringAttribute(w, "mcp.event_type", e.EventType);
        StringAttribute(w, "mcp.session_id", e.SessionId);
        StringAttribute(w, "mcp.project_id", e.ProjectId);
        StringAttribute(w, "mcp.resource_name", e.ResourceName);
        StringAttribute(w, "mcp.user_intent", e.UserIntent);
        StringAttribute(w, "mcp.actor_id", e.IdentifyActorGivenId);
        StringAttribute(w, "mcp.actor_name", e.IdentifyActorName);
        StringAttribute(w, "mcp.client_name", e.ClientName);
        StringAttribute(w, "mcp.client_version", e.ClientVersion);
        if (e.Tags is { } tags)
        {
            foreach (var tag in tags)
            {
                StringAttribute(w, "agentcat.tag." + tag.Key, tag.Value);
            }
        }

        if (e.Properties is { } properties)
        {
            StringAttribute(w, "agentcat.properties", Encoding.UTF8.GetString(JsJson.ToUtf8(properties)));
        }

        w.EndArray();
        w.Property("status");
        w.StartObject();
        w.Property("code");
        w.Number(e.IsError == true ? 2L : 1L);
        w.EndObject();
        w.EndObject();
        w.EndArray();
        w.EndObject();
        w.EndArray();
        w.EndObject();
        w.EndArray();
        w.EndObject();
        return w.ToArray();
    }

    // JS `value || fallback`.
    private static string Or(string? value, string fallback) => string.IsNullOrEmpty(value) ? fallback : value!;

    // TS filters the attributes whose stringValue is falsy: a missing field, an empty string.
    private static void StringAttribute(PayloadWriter w, string key, string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        w.StartObject();
        w.Property("key");
        w.String(key);
        w.Property("value");
        w.StartObject();
        w.Property("stringValue");
        w.String(value);
        w.EndObject();
        w.EndObject();
    }
}
