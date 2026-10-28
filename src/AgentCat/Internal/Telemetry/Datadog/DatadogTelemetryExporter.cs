using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using AgentCat.Internal.Ids;
using AgentCat.Internal.Json;
using AgentCat.Internal.Logging;
using AgentCat.Internal.Runtime;
using AgentCat.Internal.Text;

namespace AgentCat.Internal.Telemetry.Datadog;

// TS DatadogExporter: each event becomes one log entry and one metrics request, sent together.
internal sealed class DatadogTelemetryExporter : ITelemetryExporter
{
    private readonly AgentCatRuntime _runtime;
    private readonly string _logsUrl;
    private readonly string _metricsUrl;
    private readonly string _service;
    private readonly string? _env;
    private readonly IReadOnlyList<KeyValuePair<string, string>> _headers;

    internal DatadogTelemetryExporter(DatadogExporter config, AgentCatRuntime runtime)
    {
        _runtime = runtime;
        _service = config.Service;
        _env = config.Env;
        var site = config.Site;
        if (site.StartsWith("https://", StringComparison.Ordinal))
        {
            site = site.Substring("https://".Length);
        }
        else if (site.StartsWith("http://", StringComparison.Ordinal))
        {
            site = site.Substring("http://".Length);
        }

        if (site.Length > 0 && site[site.Length - 1] == '/')
        {
            site = site.Substring(0, site.Length - 1);
        }

        _logsUrl = "https://http-intake.logs." + site + "/api/v2/logs";
        _metricsUrl = "https://api." + site + "/api/v1/series";
        _headers = new[] { new KeyValuePair<string, string>("DD-API-KEY", config.ApiKey) };
    }

    public void Export(AgentCatEvent e)
    {
        _runtime.Log.Write("DatadogExporter: Sending event immediately to Datadog");
        var logBody = BuildLog(e);
        var metricsBody = BuildMetrics(e, out var seriesCount);
        _runtime.Log.WriteRoutine("DatadogExporter: Metrics URL: " + _metricsUrl);
        _runtime.Log.Write("DatadogExporter: Sending " + seriesCount.ToString(CultureInfo.InvariantCulture) + " metric series");

        Send(_logsUrl, logBody, "logs");
        Send(_metricsUrl, metricsBody, "metrics");
    }

    private void Send(string url, byte[] body, string kind)
    {
        _runtime.Exports.Send(new ExportRequest(url, "application/json", body)
        {
            Headers = _headers,
            Completed = response => OnCompleted(kind, response),
        });
    }

    private void OnCompleted(string kind, ExportResponse response)
    {
        if (response.Error is { } error)
        {
            _runtime.Log.Write("Datadog " + kind + " network error: " + error);
            return;
        }

        var status = response.Status.ToString(CultureInfo.InvariantCulture);
        _runtime.Log.Write(response.Ok
            ? "Datadog " + kind + " success - Status: " + status
            : "Datadog " + kind + " failed - Status: " + status);
    }

    private byte[] BuildLog(AgentCatEvent e)
    {
        var tags = new List<string>();
        if (!string.IsNullOrEmpty(_env))
        {
            tags.Add("env:" + _env);
        }

        if (!string.IsNullOrEmpty(e.EventType))
        {
            tags.Add("event_type:" + e.EventType.Replace('/', '.'));
        }

        if (!string.IsNullOrEmpty(e.ResourceName))
        {
            tags.Add("resource:" + e.ResourceName);
        }

        if (e.IsError == true)
        {
            tags.Add("error:true");
        }

        tags.Add("source:" + WireNames.Source);
        if (e.Tags is { } customerTags)
        {
            foreach (var tag in customerTags)
            {
                var key = SanitizeKey(tag.Key);
                if (tag.Value is null)
                {
                    throw new JsError("TypeError", "value.replace is not a function");
                }

                tags.Add("agentcat." + key + ":" + tag.Value.Replace(',', '_'));
            }
        }

        var isError = e.IsError == true;
        var w = new PayloadWriter();
        w.StartArray();
        w.StartObject();
        w.Property("message");
        w.String(Or(e.EventType, "unknown") + " - " + Or(e.ResourceName, "unknown"));
        w.Property("service");
        w.String(_service);
        w.Property("ddsource");
        w.String(WireNames.Source);
        w.Property("ddtags");
        w.String(string.Join(",", tags));
        w.Property("timestamp");
        w.Number(e.Timestamp.ToUnixTimeMilliseconds());
        w.Property("status");
        w.String(isError ? "error" : "info");
        w.Property("dd");
        w.StartObject();
        w.Property("trace_id");
        w.String(TraceIds.GetDatadogTraceId(e.SessionId, _runtime.Random));
        w.Property("span_id");
        w.String(TraceIds.GetDatadogSpanId(e.Id, _runtime.Random));
        w.EndObject();
        w.Property("mcp");
        w.StartObject();
        w.Property("session_id");
        w.String(e.SessionId);
        w.Property("event_id");
        w.String(e.Id);
        w.Property("event_type");
        w.String(e.EventType);
        Optional(w, "resource", e.ResourceName);
        if (e.Duration is { } duration)
        {
            w.Property("duration_ms");
            w.Number((long)duration);
        }

        Optional(w, "user_intent", e.UserIntent);
        Optional(w, "actor_id", e.IdentifyActorGivenId);
        Optional(w, "actor_name", e.IdentifyActorName);
        Optional(w, "client_name", e.ClientName);
        Optional(w, "client_version", e.ClientVersion);
        Optional(w, "server_name", e.ServerName);
        Optional(w, "server_version", e.ServerVersion);
        if (e.IsError is { } flag)
        {
            w.Property("is_error");
            w.Bool(flag);
        }

        if (e.Error is { } error)
        {
            w.Property("error");
            w.Node(error);
        }

        if (e.Tags is { } mcpTags)
        {
            w.Property("tags");
            w.StartObject();
            foreach (var tag in mcpTags)
            {
                w.Property(tag.Key);
                w.String(tag.Value);
            }

            w.EndObject();
        }

        if (e.Properties is { } properties)
        {
            w.Property("properties");
            w.Node(properties);
        }

        w.EndObject();
        if (isError && JsSemantics.IsTruthy(e.Error))
        {
            w.Property("error");
            w.StartObject();
            w.Property("message");
            w.String(ErrorMessage(e.Error!));
            w.EndObject();
        }

        w.EndObject();
        w.EndArray();
        return w.ToArray();
    }

    private byte[] BuildMetrics(AgentCatEvent e, out int seriesCount)
    {
        var ms = e.Timestamp.ToUnixTimeMilliseconds();
        if (ms == 0)
        {
            ms = _runtime.Clock.UtcNow.ToUnixTimeMilliseconds();
        }

        var seconds = (long)Math.Floor(ms / 1000.0);
        var tags = new List<string> { "service:" + _service };
        if (!string.IsNullOrEmpty(_env))
        {
            tags.Add("env:" + _env);
        }

        if (!string.IsNullOrEmpty(e.EventType))
        {
            tags.Add("event_type:" + e.EventType.Replace('/', '.'));
        }

        if (!string.IsNullOrEmpty(e.ResourceName))
        {
            tags.Add("resource:" + e.ResourceName);
        }

        var w = new PayloadWriter();
        w.StartObject();
        w.Property("series");
        w.StartArray();
        seriesCount = 1;
        Series(w, "mcp.events.count", "count", seconds, 1, tags);
        if (e.Duration is { } duration && duration != 0)
        {
            seriesCount++;
            Series(w, "mcp.event.duration", "gauge", seconds, duration, tags);
        }

        if (e.IsError == true)
        {
            seriesCount++;
            Series(w, "mcp.errors.count", "count", seconds, 1, tags);
        }

        w.EndArray();
        w.EndObject();
        return w.ToArray();
    }

    private static void Series(PayloadWriter w, string metric, string type, long seconds, long value, List<string> tags)
    {
        w.StartObject();
        w.Property("metric");
        w.String(metric);
        w.Property("type");
        w.String(type);
        w.Property("points");
        w.StartArray();
        w.StartArray();
        w.Number(seconds);
        w.Number(value);
        w.EndArray();
        w.EndArray();
        w.Property("tags");
        w.StartArray();
        foreach (var tag in tags)
        {
            w.String(tag);
        }

        w.EndArray();
        w.EndObject();
    }

    // TS `typeof error === "string" ? error : JSON.stringify(error)`.
    private static string ErrorMessage(System.Text.Json.Nodes.JsonNode error)
        => error is System.Text.Json.Nodes.JsonValue value && value.TryGetValue<string>(out var text)
            ? text
            : Encoding.UTF8.GetString(JsJson.ToUtf8(error));

    // TS `key.toLowerCase().replace(/[\s:,]+/g, "_")`, with the ECMAScript whitespace set.
    private static string SanitizeKey(string key)
    {
        var lower = key.ToLowerInvariant();
        var builder = new StringBuilder(lower.Length);
        var inRun = false;
        foreach (var c in lower)
        {
            if (c == ':' || c == ',' || JsSemantics.IsWhiteSpaceOrLineTerminator(c))
            {
                if (!inRun)
                {
                    builder.Append('_');
                    inRun = true;
                }
            }
            else
            {
                builder.Append(c);
                inRun = false;
            }
        }

        return builder.ToString();
    }

    private static void Optional(PayloadWriter w, string name, string? value)
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
}
