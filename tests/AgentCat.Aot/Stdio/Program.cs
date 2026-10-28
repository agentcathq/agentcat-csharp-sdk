using System.Text.Json.Nodes;
using AgentCat;
using AotStdioServer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);

// stdout carries the MCP frames, so logs go to stderr.
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

// The events API URL and the diagnostics switch come from AGENTCAT_API_URL and AGENTCAT_DISABLE_DIAGNOSTICS.
// The hooks fill identify_data, tags and properties, so every object field of the wire body is written.
builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithTools<EchoTools>()
    .WithTools<Outer.Inner>()
    .WithAgentCat("proj_aot_stdio", o =>
    {
        o.Identify = static (_, _) => new ValueTask<UserIdentity?>(new UserIdentity("aot-user")
        {
            UserName = "AOT User",
            UserData = new JsonObject { ["plan"] = "aot" },
        });
        o.EventTags = static (_, _) => new ValueTask<IReadOnlyDictionary<string, string>?>(
            new Dictionary<string, string> { ["lane"] = "aot" });
        o.EventProperties = static (call, _) => new ValueTask<JsonObject?>(new JsonObject { ["tool"] = call.ToolName });
    });

await builder.Build().RunAsync();
