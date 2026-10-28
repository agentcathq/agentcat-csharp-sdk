#pragma warning disable CA1861, CA1305, CA1872, CA1850, CA1837, CA2016

using AgentCat.Tests.TestSupport;

namespace AgentCat.Tests.Diagnostics;

// The diagnostics sink gets every line the runtime log writes, so these tests read the lines of the engine's log.
public class DiagnosticsNoPayloadTests
{
    // TS: resolved identity is stamped on the event but never reaches the sink
    [Fact]
    public async Task Resolved_identity_is_stamped_on_the_event_but_never_reaches_the_sink()
    {
        var userId = "actor-" + Guid.NewGuid();
        var secretName = "name-" + Guid.NewGuid();
        var secretData = "data-" + Guid.NewGuid();
        await using var h = new EngineHarness(
            "test-project",
            new AgentCatOptions
            {
                EnableTracing = true,
                Identify = (_, _) => new ValueTask<UserIdentity?>(new UserIdentity(userId)
                {
                    UserName = secretName,
                    UserData = new System.Text.Json.Nodes.JsonObject { ["secret"] = secretData },
                }),
            });
        h.AddTool((string text) => "echo:" + text, "echo");

        await h.StartAsync();
        await h.CallToolAsync("echo", """{"text":"hi","context":"adding a todo"}""");
        var events = await h.FlushAsync();

        var toolCall = events.Single(e => (string?)e["eventType"] == "mcp:tools/call");
        Assert.Equal(userId, (string?)toolCall["identifyActorGivenId"]);
        Assert.Equal(secretName, (string?)toolCall["identifyActorName"]);
        var lines = h.Log.Lines;
        Assert.DoesNotContain(lines, l => l.Contains("with identity:"));
        Assert.DoesNotContain(lines, l => l.Contains(secretName));
        Assert.DoesNotContain(lines, l => l.Contains(secretData));
        // The per-event success line is the one place that carries the actor ID.
        Assert.All(lines.Where(l => l.Contains(userId)), l => Assert.StartsWith("Successfully sent event ", l, StringComparison.Ordinal));
    }

    // TS: emits start + complete setup beacons (metadata only) on track()
    [Fact]
    public async Task Emits_start_and_complete_setup_beacons_metadata_only_on_install()
    {
        await using var h = new EngineHarness("test-project", new AgentCatOptions { EnableTracing = true });
        h.AddTool((string text) => "echo:" + text, "echo");

        await h.StartAsync();

        var started = h.Log.Lines.Single(l => l.Contains("AgentCat setup started"));
        var complete = h.Log.Lines.Single(l => l.Contains("AgentCat setup complete"));
        Assert.Contains("test-project", started, StringComparison.Ordinal);
        Assert.Contains("sdk v" + McpHarness.McpMajor + "/options", started, StringComparison.Ordinal);
        Assert.Contains("signals ", started, StringComparison.Ordinal);
        Assert.Contains("test-project", complete, StringComparison.Ordinal);
        Assert.Contains("tracing=true", complete, StringComparison.Ordinal);
        Assert.Contains("reportMissing=true", complete, StringComparison.Ordinal);
    }

    // TS: report-missing log carries only the context length, not the text
    [Fact]
    public async Task Report_missing_log_carries_only_the_context_length_not_the_text()
    {
        var secret = "i-wish-there-was-a-tool-for-" + Guid.NewGuid();
        await using var h = new EngineHarness("test-project", new AgentCatOptions { EnableTracing = true });
        h.AddTool((string text) => "echo:" + text, "echo");

        await h.StartAsync();
        await h.CallToolAsync("get_more_tools", "{\"context\":\"" + secret + "\"}");
        await h.FlushAsync();

        var report = h.Log.Lines.Single(l => l.Contains("Missing tool reported"));
        Assert.Contains("context length: " + secret.Length, report, StringComparison.Ordinal);
        Assert.DoesNotContain(h.Log.Lines, l => l.Contains(secret));
    }
}
