#pragma warning disable CA1861, CA1305, CA1872, CA1850, CA1837, CA2016

using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCat.Internal;
using AgentCat.Internal.Diagnostics;
using AgentCat.Internal.Logging;
using AgentCat.Tests.TestSupport;
using SdkDiagnostics = AgentCat.Internal.Diagnostics.Diagnostics;

namespace AgentCat.Tests.Diagnostics;

public class DiagnosticsRecordTests
{
    private static DiagnosticsRecord Build(string line)
    {
        var h = new DiagnosticsHarness().ForceEnabled().Started();
        h.Write(line);
        return Assert.Single(h.Diagnostics.Buffered);
    }

    // TS: carries the raw message as the body, verbatim
    [Fact]
    public void Carries_the_raw_message_as_the_body_verbatim()
    {
        const string line = "[2026-01-01T00:00:00Z] Warning: boom";

        Assert.Equal(line, Build(line).Body);
    }

    // TS: infers severity from message content
    [Fact]
    public void Infers_severity_from_message_content()
    {
        Assert.Equal("WARN", Build("Warning: x").SeverityText);
        Assert.Equal("ERROR", Build("Failed to send event").SeverityText);
        Assert.Equal("ERROR", Build("Some error happened").SeverityText);
        Assert.Equal("INFO", Build("Initialized telemetry").SeverityText);
        Assert.Equal(13, Build("Warning: x").SeverityNumber);
        Assert.Equal(17, Build("Failed to send event").SeverityNumber);
        Assert.Equal(9, Build("Initialized telemetry").SeverityNumber);
    }

    // TS: prioritizes fail/error over the Warning prefix
    [Fact]
    public void Prioritizes_fail_error_over_the_Warning_prefix()
    {
        Assert.Equal("ERROR", Build("Warning: Failed to track server - boom").SeverityText);
        Assert.Equal("INFO", Build("warning: nothing here").SeverityText);
        Assert.Equal("INFO", Build("").SeverityText);
        Assert.Equal("ERROR", Build("ERROR: x").SeverityText);
        Assert.Equal("ERROR", Build("a FAIL b").SeverityText);
    }

    // TS: sets a nanosecond timestamp string
    [Fact]
    public void Sets_a_nanosecond_timestamp_string()
    {
        var h = new DiagnosticsHarness().ForceEnabled().Started();
        h.Clock.UtcNow = new DateTimeOffset(2026, 10, 16, 15, 4, 56, 821, TimeSpan.Zero);
        h.Write("hello");

        var record = Assert.Single(h.Diagnostics.Buffered);

        Assert.Matches("^[0-9]+$", record.TimeUnixNano);
        Assert.Equal("1792163096821000000", record.TimeUnixNano);
    }

    // TS: attaches sdk, node, and mcp versions as record attributes
    [Fact]
    public void Attaches_sdk_dotnet_and_mcp_versions_as_record_attributes()
    {
        var m = Build("hello").Attributes.ToDictionary(a => a.Key, a => a.Value);

        Assert.Equal(SdkInfo.Version, m["agentcat.sdk.version"]);
        Assert.Equal(SdkInfo.RuntimeVersion, m["process.runtime.version"]);
        Assert.Equal(LogLine.McpVersion, m["agentcat.mcp_sdk.version"]);
        Assert.Equal(3, m.Count);
    }

    private static (string[] Lines, string Compact, JsonNode Root) Golden()
    {
        var lines = Fixtures.Text("exporters/golden2-output.txt").Replace("\r\n", "\n").Split('\n');
        var start = Array.FindIndex(lines, l => l == "=== diagnostics");
        var json = string.Join("\n", lines.Skip(start + 2));
        var root = JsonNode.Parse(json)!;
        var compact = root.ToJsonString(new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        return (lines, compact, root);
    }

    private static List<KeyValuePair<string, string>> Attributes(JsonNode? attributes)
        => attributes!.AsArray().Select(a => new KeyValuePair<string, string>((string)a!["key"]!, (string)a["value"]!["stringValue"]!)).ToList();

    private static List<DiagnosticsRecord> GoldenRecords(JsonNode root)
        => root["resourceLogs"]![0]!["scopeLogs"]![0]!["logRecords"]!.AsArray()
            .Select(r => new DiagnosticsRecord(
                (string)r!["timeUnixNano"]!,
                (int)r["severityNumber"]!,
                (string)r["severityText"]!,
                (string)r["body"]!["stringValue"]!,
                Attributes(r["attributes"])))
            .ToList();

    [Fact]
    public void Body_matches_the_TS_golden_request()
    {
        var (_, compact, root) = Golden();
        var resource = Attributes(root["resourceLogs"]![0]!["resource"]!["attributes"]);
        var records = GoldenRecords(root);

        var body = DiagnosticsBody.Build(resource, (string)root["resourceLogs"]![0]!["scopeLogs"]![0]!["scope"]!["version"]!, records);

        Assert.Equal(1543, body.Length);
        Assert.Equal(compact, Encoding.UTF8.GetString(body));
    }

    [Fact]
    public void Each_golden_line_gets_its_golden_severity()
    {
        var (_, _, root) = Golden();

        foreach (var record in GoldenRecords(root))
        {
            var (number, text) = SdkDiagnostics.Severity(record.Body);
            Assert.Equal(record.SeverityNumber, number);
            Assert.Equal(record.SeverityText, text);
        }
    }

    [Fact]
    public void Golden_request_uses_the_default_endpoint_and_token()
    {
        var lines = Golden().Lines;
        var request = lines.Single(l => l.StartsWith("https://otel.agentcat.com/v1/logs ", StringComparison.Ordinal));

        Assert.Equal("https://otel.agentcat.com/v1/logs", SdkDiagnostics.ResolveEndpoint(_ => null));
        Assert.Contains("\"Authorization\":\"Bearer " + SdkDiagnostics.ResolveToken(_ => null) + "\"", request);
    }

    [Fact]
    public void Non_ASCII_text_is_not_escaped()
    {
        var record = new DiagnosticsRecord("1000000", 9, "INFO", "café ☃ <&>", Array.Empty<KeyValuePair<string, string>>());

        var text = Encoding.UTF8.GetString(DiagnosticsBody.Build(Array.Empty<KeyValuePair<string, string>>(), "1.0.0", new[] { record }));

        Assert.Contains("\"stringValue\":\"café ☃ <&>\"", text);
        Assert.Contains("\"severityNumber\":9", text);
    }
}
