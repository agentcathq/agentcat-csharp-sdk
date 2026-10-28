using System.Text.RegularExpressions;
using AgentCat.Internal.Engine;
using AgentCat.Internal.Logging;
using AgentCat.Internal.Telemetry;
using Microsoft.Extensions.Logging;

namespace AgentCat.Tests.Logging;

public sealed class HostLogSourceTests
{
    public static TheoryData<string, LogLevel> RealLines => new()
    {
        { AgentCatTracker.LogToHostWarning, LogLevel.Warning },
        { ExportChannel.DroppedLine(3), LogLevel.Warning },
        { EngineInstaller.AlternateHandlerWarning, LogLevel.Warning },
        { TelemetryManager.ExportFailedLine("otlp", "boom"), LogLevel.Error },
        { "Warning: Failed to track server - boom", LogLevel.Error },
        { "FAİL", LogLevel.Information },
        { "ERRoR", LogLevel.Error },
        { "Warning: nothing", LogLevel.Warning },
    };

    // Each reason says why a text-rule line is safe: its dynamic part is AgentCat's own text, a failure that
    // names itself, or a constant.
    private static readonly Dictionary<string, string> Allowed = new(StringComparer.Ordinal)
    {
        ["AgentCatClient.cs: \"Warning: Failed to publish custom event - \""] = "failure",
        ["AgentCatEngine.cs: \"Warning: Failed to track server - \" + Error"] = "failure",
        ["AgentCatPostConfigure.cs: \"Warning: Failed to track server - \" + Error"] = "failure",
        ["AgentCatRuntime.cs: message"] = "forward",
        ["AgentCatTracker.cs: \"Warning: Failed to track server - \" + Error"] = "failure",
        ["AspNetCoreRequest.cs: Off + MissingMember + \" was not found\""] = "member",
        ["CallToolSeam.cs: \"Warning: AgentCat post-handler processing f"] = "failure",
        ["CallToolSeam.cs: \"Warning: AgentCat tracing failed for tool \""] = "failure",
        ["DatadogTelemetryExporter.cs: \"Datadog \" + kind + \" network error: \" + err"] = "failure",
        ["DatadogTelemetryExporter.cs: \"DatadogExporter: Sending \" + seriesCount.To"] = "count",
        ["DatadogTelemetryExporter.cs: response.Ok ? \"Datadog \" + kind + \" success"] = "status",
        ["EnabledGate.cs: \"WARN: \" + Variable + \" \\\"\" + value + \"\\\" is"] = "WARN",
        ["EngineInstaller.cs: warning"] = "constant",
        ["EventProcessor.cs: \"Failed to deliver AgentCat event after retr"] = "failure",
        ["EventProcessor.cs: \"Failed to redact event (event-level hook):"] = "failure",
        ["EventProcessor.cs: \"Failed to redact event: \" + ErrorText.Of(ex"] = "failure",
        ["EventProcessor.cs: \"Failed to resolve pending hook results: \" +"] = "failure",
        ["EventProcessor.cs: \"Failed to sanitize event: \" + ErrorText.Of("] = "failure",
        ["EventProcessor.cs: \"Failed to send event \" + e.Id + \", retrying"] = "failure",
        ["EventProcessor.cs: \"Failed to truncate event: \" + ErrorText.Of("] = "failure",
        ["EventProcessor.cs: \"Telemetry export error: \" + ErrorText.Of(ex"] = "failure",
        ["ExportChannel.cs: DroppedLine(count)"] = "WARN",
        ["ExporterFactory.cs: \"Failed to initialize exporter \" + name + \":"] = "failure",
        ["GetMoreTools.cs: \"Missing tool reported (context length: \" +"] = "count",
        ["ListToolsSeam.cs: \"Warning: AgentCat tool-list injection faile"] = "failure",
        ["OtlpTelemetryExporter.cs: TelemetryManager.ExportFailedLine(_name, \"OT"] = "failure",
        ["PendingHooks.cs: \"Warning: \" + label + \" hook did not settle"] = "label",
        ["PendingHooks.cs: \"Warning: \" + label + \" hook failed in backg"] = "failure",
        ["PendingHooks.cs: \"eventTags callback error: \" + ErrorText.Of("] = "failure",
        ["PendingHooks.cs: errorPrefix + ErrorText.Of(ex)"] = "failure",
        ["PostHogTelemetryExporter.cs: \"PostHog export error: \" + JsError.Text(ex)"] = "failure",
        ["PostHogTelemetryExporter.cs: \"PostHog export error: \" + error"] = "failure",
        ["PostHogTelemetryExporter.cs: \"PostHog export failed - Status: \" + respons"] = "failure",
        ["RegistryRebuilder.cs: FailedPrefix + ErrorText.Of(ex)"] = "failure",
        ["SentryTelemetryExporter.cs: \"Sentry \" + step.Kind + \" export failed - St"] = "failure",
        ["SentryTelemetryExporter.cs: \"Sentry export error: \" + error"] = "failure",
        ["TelemetryManager.cs: \"Initialized telemetry with \" + configs.Coun"] = "count",
        ["TelemetryManager.cs: ExportFailedLine(name, message)"] = "failure",
        ["ToolListInjector.cs: \"WARN: Context injection failed for tool \\\"\""] = "WARN",
        ["ToolListInjector.cs: \"WARN: Handle injection failed for tool \\\"\""] = "WARN",
        ["ToolListInjector.cs: \"WARN: Tool \\\"\" + toolName + \"\\\" already dec"] = "WARN",
        ["ToolListInjector.cs: \"WARN: Tool \\\"\" + toolName + \"\\\" already has"] = "WARN",
        ["ToolListInjector.cs: \"WARN: Tool \\\"\" + toolName + \"\\\" has complex"] = "WARN",
        ["ToolListInjector.cs: SessionIdConflict(toolName)"] = "ERROR",
    };

    [Theory]
    [MemberData(nameof(RealLines))]
    public void Real_lines_and_the_ASCII_fold_have_the_pinned_host_level(string line, LogLevel level)
        => Assert.Equal(level, HostLogLevel.Of(line));

    [Fact]
    public void Every_text_rule_log_call_with_a_dynamic_argument_is_allowed_by_name()
    {
        var root = FindSourceRoot();
        var unlisted = new List<string>();
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
                || file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
            {
                continue;
            }

            var text = File.ReadAllText(file);
            foreach (Match call in Regex.Matches(text, @"\w*[lL]og\??\.Write\("))
            {
                var argument = SingleArgument(text, call.Index + call.Length);
                if (argument is null || Regex.IsMatch(argument, "^\"(?:[^\"\\\\]|\\\\.)*\"$") || Regex.IsMatch(argument, @"^[A-Z]\w*(\.[A-Z]\w*)*$"))
                {
                    continue;
                }

                var key = Path.GetFileName(file) + ": " + (argument.Length > 44 ? argument.Substring(0, 44) : argument).TrimEnd();
                used.Add(key);
                if (!Allowed.ContainsKey(key))
                {
                    unlisted.Add(key);
                }
            }
        }

        Assert.True(unlisted.Count == 0, "Add a reason, or pass a level (WriteRoutine, or a LogSeverity): " + string.Join(" | ", unlisted));
        Assert.Empty(Allowed.Keys.Except(used));
    }

    private static string FindSourceRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "src", "AgentCat");
            if (Directory.Exists(candidate) && File.Exists(Path.Combine(dir.FullName, "README.md")))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("src/AgentCat not found.");
    }

    // The text of the only argument, or null when the call has more than one.
    private static string? SingleArgument(string text, int start)
    {
        var depth = 0;
        var parts = 0;
        var i = start;
        for (; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '"')
            {
                i++;
                while (i < text.Length && text[i] != '"')
                {
                    i += text[i] == '\\' ? 2 : 1;
                }

                continue;
            }

            if (c == '(' || c == '[' || c == '{')
            {
                depth++;
            }
            else if ((c == ')' || c == ']' || c == '}') && depth-- == 0)
            {
                break;
            }
            else if (c == ',' && depth == 0)
            {
                parts++;
            }
        }

        return parts > 0 ? null : Regex.Replace(text.Substring(start, i - start).Trim(), @"\s+", " ");
    }
}
