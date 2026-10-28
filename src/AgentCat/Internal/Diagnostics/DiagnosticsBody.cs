using System.Collections.Generic;
using System.Text.Json.Nodes;
using AgentCat.Internal.Json;

namespace AgentCat.Internal.Diagnostics;

internal sealed class DiagnosticsRecord
{
    internal DiagnosticsRecord(string timeUnixNano, int severityNumber, string severityText, string body, IReadOnlyList<KeyValuePair<string, string>> attributes)
    {
        TimeUnixNano = timeUnixNano;
        SeverityNumber = severityNumber;
        SeverityText = severityText;
        Body = body;
        Attributes = attributes;
    }

    internal string TimeUnixNano { get; }

    internal int SeverityNumber { get; }

    internal string SeverityText { get; }

    internal string Body { get; }

    internal IReadOnlyList<KeyValuePair<string, string>> Attributes { get; }
}

/// <summary>The OTLP logs request body, as <c>JSON.stringify</c> writes it.</summary>
internal static class DiagnosticsBody
{
    internal const string ScopeName = "agentcat-diagnostics";

    internal static byte[] Build(
        IReadOnlyList<KeyValuePair<string, string>> resource,
        string scopeVersion,
        IReadOnlyList<DiagnosticsRecord> records)
    {
        var logRecords = new JsonArray();
        foreach (var record in records)
        {
            logRecords.Add((JsonNode?)new JsonObject
            {
                ["timeUnixNano"] = JsonValue.Create(record.TimeUnixNano),
                ["severityNumber"] = JsonValue.Create(record.SeverityNumber),
                ["severityText"] = JsonValue.Create(record.SeverityText),
                ["body"] = new JsonObject { ["stringValue"] = JsonValue.Create(record.Body) },
                ["attributes"] = Attributes(record.Attributes),
            });
        }

        var root = new JsonObject
        {
            ["resourceLogs"] = new JsonArray(new JsonObject
            {
                ["resource"] = new JsonObject { ["attributes"] = Attributes(resource) },
                ["scopeLogs"] = new JsonArray(new JsonObject
                {
                    ["scope"] = new JsonObject
                    {
                        ["name"] = JsonValue.Create(ScopeName),
                        ["version"] = JsonValue.Create(scopeVersion),
                    },
                    ["logRecords"] = logRecords,
                }),
            }),
        };
        return JsJson.ToUtf8(root);
    }

    private static JsonArray Attributes(IReadOnlyList<KeyValuePair<string, string>> attributes)
    {
        var array = new JsonArray();
        foreach (var attribute in attributes)
        {
            array.Add((JsonNode?)new JsonObject
            {
                ["key"] = JsonValue.Create(attribute.Key),
                ["value"] = new JsonObject { ["stringValue"] = JsonValue.Create(attribute.Value) },
            });
        }

        return array;
    }
}
