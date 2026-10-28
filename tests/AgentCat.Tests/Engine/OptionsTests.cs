using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCat.Internal.Engine;

namespace AgentCat.Tests.Engine;

public class OptionsTests
{
    [Fact]
    public void Defaults_match_TS()
    {
        var options = new AgentCatOptions();

        Assert.True(options.EnableReportMissing);
        Assert.True(options.EnableTracing);
        Assert.True(options.EnableToolCallContext);
        Assert.Null(options.CustomContextDescription);
        Assert.False(options.EnableAgentTracking);
        Assert.Null(options.ResolveSessionId);
        Assert.Null(options.Identify);
        Assert.Null(options.EventTags);
        Assert.Null(options.EventProperties);
        Assert.Null(options.RedactSensitiveInformation);
        Assert.Null(options.RedactEvent);
        Assert.Null(options.ApiBaseUrl);

        var settings = TrackerSettings.From(null);
        Assert.True(settings.EnableReportMissing);
        Assert.True(settings.EnableTracing);
        Assert.True(settings.EnableToolCallContext);
        Assert.False(settings.EnableAgentTracking);
        Assert.Equal(TimeSpan.FromSeconds(30), settings.PendingHookBudget);
    }

    [Fact]
    public void Settings_are_read_once()
    {
        Func<AgentCatToolCall, CancellationToken, ValueTask<string?>> resolve = (_, _) => new ValueTask<string?>("a");
        Func<AgentCatToolCall, CancellationToken, ValueTask<UserIdentity?>> identify = (_, _) => new ValueTask<UserIdentity?>(new UserIdentity("u"));
        Func<AgentCatToolCall, CancellationToken, ValueTask<IReadOnlyDictionary<string, string>?>> tags = (_, _) => new ValueTask<IReadOnlyDictionary<string, string>?>((IReadOnlyDictionary<string, string>?)null);
        Func<AgentCatToolCall, CancellationToken, ValueTask<JsonObject?>> props = (_, _) => new ValueTask<JsonObject?>((JsonObject?)null);
        Func<string, CancellationToken, ValueTask<string>> redact = (s, _) => new ValueTask<string>(s);
        Func<AgentCatEvent, CancellationToken, ValueTask<AgentCatEvent?>> redactEvent = (e, _) => new ValueTask<AgentCatEvent?>(e);

        var options = new AgentCatOptions
        {
            EnableReportMissing = false,
            EnableTracing = true,
            EnableToolCallContext = false,
            CustomContextDescription = "why",
            EnableAgentTracking = true,
            ResolveSessionId = resolve,
            Identify = identify,
            EventTags = tags,
            EventProperties = props,
            RedactSensitiveInformation = redact,
            RedactEvent = redactEvent,
            ApiBaseUrl = "https://one.example.com",
        };

        var settings = TrackerSettings.From(options);

        options.EnableReportMissing = true;
        options.EnableTracing = false;
        options.EnableToolCallContext = true;
        options.CustomContextDescription = "other";
        options.EnableAgentTracking = false;
        options.ResolveSessionId = null;
        options.Identify = null;
        options.EventTags = null;
        options.EventProperties = null;
        options.RedactSensitiveInformation = null;
        options.RedactEvent = null;
        options.ApiBaseUrl = "https://two.example.com";

        Assert.False(settings.EnableReportMissing);
        Assert.True(settings.EnableTracing);
        Assert.False(settings.EnableToolCallContext);
        Assert.Equal("why", settings.CustomContextDescription);
        Assert.True(settings.EnableAgentTracking);
        Assert.Same(resolve, settings.ResolveSessionId);
        Assert.Same(identify, settings.Identify);
        Assert.Same(tags, settings.EventTags);
        Assert.Same(props, settings.EventProperties);
        Assert.Same(redact, settings.RedactSensitiveInformation);
        Assert.Same(redactEvent, settings.RedactEvent);
        Assert.Equal("https://one.example.com", settings.ApiBaseUrl);
    }

    [Fact]
    public void Injection_settings_follow_tracing_and_hook_mode()
    {
        var def = TrackerSettings.From(new AgentCatOptions()).Injection;
        Assert.True(def.InjectSessionId);
        Assert.False(def.InjectAgentId);
        Assert.True(def.EnableReportMissing);
        Assert.True(def.EnableToolCallContext);
        Assert.Null(def.CustomContextDescription);

        var off = TrackerSettings.From(new AgentCatOptions { EnableTracing = false, EnableAgentTracking = true }).Injection;
        Assert.False(off.InjectSessionId);
        Assert.False(off.InjectAgentId);

        var hook = TrackerSettings.From(new AgentCatOptions { ResolveSessionId = (_, _) => new ValueTask<string?>("s") }).Injection;
        Assert.False(hook.InjectSessionId);

        var agent = TrackerSettings.From(new AgentCatOptions { EnableAgentTracking = true, CustomContextDescription = "d" }).Injection;
        Assert.True(agent.InjectAgentId);
        Assert.Equal("d", agent.CustomContextDescription);
    }

    [Fact]
    public void Tool_call_snapshot_is_immutable()
    {
        var args = new Dictionary<string, JsonElement> { ["a"] = JsonDocument.Parse("1").RootElement.Clone() };
        var meta = new JsonObject { ["k"] = "v" };

        var call = new AgentCatToolCall("t", args, meta, null, null, null, null, null, null!);

        args["b"] = JsonDocument.Parse("2").RootElement.Clone();
        args.Remove("a");
        meta["k"] = "changed";
        meta["extra"] = 1;

        Assert.Equal("a", Assert.Single(call.Arguments.Keys));
        Assert.Equal(1, call.Arguments["a"].GetInt32());
        Assert.Equal("v", (string?)call.Meta!["k"]);
        Assert.False(call.Meta.ContainsKey("extra"));
        Assert.NotSame(meta, call.Meta);
        Assert.Empty(call.Headers);
        Assert.IsNotType<Dictionary<string, JsonElement>>(call.Arguments);
        Assert.True(((ICollection<KeyValuePair<string, JsonElement>>)call.Arguments).IsReadOnly);
        Assert.True(((ICollection<KeyValuePair<string, string>>)call.Headers).IsReadOnly);
    }

    [Fact]
    public void Tool_call_snapshot_without_arguments_or_meta_is_empty()
    {
        var call = new AgentCatToolCall("t", null, null, "c", "1", "p", "s", null, null!);

        Assert.Empty(call.Arguments);
        Assert.Null(call.Meta);
        Assert.Equal("c", call.ClientName);
        Assert.Equal("s", call.TransportSessionId);
    }
}
