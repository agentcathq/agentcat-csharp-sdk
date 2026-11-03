<div align="center">
  <img alt="AgentCat — see exactly how agents experience your product" src="https://raw.githubusercontent.com/agentcathq/agentcat-csharp-sdk/main/docs/static/og-image.png" width="80%">
</div>
<h3 align="center">
    <a href="#getting-started">Getting Started</a>
    <span> · </span>
    <a href="#why-use-agentcat-">Features</a>
    <span> · </span>
    <a href="https://docs.agentcat.com">Docs</a>
    <span> · </span>
    <a href="https://agentcat.com">Website</a>
    <span> · </span>
    <a href="#free-for-open-source">Open Source</a>
    <span> · </span>
    <a href="https://book.agentcat.com/meet">Schedule a Demo</a>
</h3>
<p align="center">
  <a href="https://www.nuget.org/packages/AgentCat"><img src="https://img.shields.io/nuget/vpre/AgentCat.svg" alt="NuGet version"></a>
  <a href="https://www.nuget.org/packages/AgentCat"><img src="https://img.shields.io/nuget/dt/AgentCat.svg" alt="NuGet downloads"></a>
  <a href="https://opensource.org/licenses/MIT"><img src="https://img.shields.io/badge/License-MIT-blue.svg" alt="License: MIT"></a>
  <a href="https://dotnet.microsoft.com/"><img src="https://img.shields.io/badge/.NET-8.0%20%7C%2010.0%20%7C%20Standard%202.0-512BD4.svg" alt=".NET"></a>
  <a href="https://github.com/agentcathq/agentcat-csharp-sdk/issues"><img src="https://img.shields.io/github/issues/agentcathq/agentcat-csharp-sdk.svg" alt="GitHub issues"></a>
  <a href="https://github.com/agentcathq/agentcat-csharp-sdk/actions/workflows/ci.yml"><img src="https://github.com/agentcathq/agentcat-csharp-sdk/actions/workflows/ci.yml/badge.svg" alt="CI"></a>
</p>

> [!NOTE]
> AgentCat for C# is compatible with the [MCP Protocol "Stateless" 2026-07-28 Update](https://blog.modelcontextprotocol.io/posts/2026-07-28/) as the official C# MCP SDK puts it into effect: [`ModelContextProtocol`](https://github.com/modelcontextprotocol/csharp-sdk) 2.x, with the 1.x line supported too. The stateless transition has a massive impact on analytics, as sessions were a built-in concept tying related tool calls together. AgentCat has now migrated its session tracking under guidance of the MCP core team's recommendations of using [explicit handles (SEP-2567)](https://github.com/modelcontextprotocol/modelcontextprotocol/pull/2567).
>
> As a result AgentCat now injects a `session_id` on every MCP tool call to associate them under the same task umbrella. Our evals show much higher tool correlation accuracy at the cost of < 1% additional context pollution.

AgentCat is an analytics platform for MCP server owners 🐱. It captures user intentions and behavior patterns to help you understand what AI users actually need from your tools — eliminating guesswork and accelerating product development all with one-line of code.

This SDK also provides a free and simple way to forward telemetry like logs, traces, and errors to any Open Telemetry collector or popular tools like Datadog, Sentry, and PostHog.

```bash
dotnet add package AgentCat
```

To learn more about us, check us out [here](https://agentcat.com). For detailed guides visit our [documentation](https://docs.agentcat.com).

## Why use AgentCat? 🤔

AgentCat helps builders of MCP servers, Claude Connectors, and ChatGPT Plugins learn how to improve them by capturing any agents goals and detecting when they get stuck.

Use AgentCat for:

- **Agent session replay** 🎬. Follow alongside your users and their agents to understand why they're using your MCP servers, what functionality you're missing, and what clients they're coming from.
- **Trace debugging** 🔍. See where your users are getting stuck, track and find when LLMs get confused by your API, and debug sessions across all deployments of your MCP server.
- **Existing platform support** 📊. Get logging and tracing out of the box for your existing observability platforms (OpenTelemetry, Datadog, Sentry) — eliminating the tedious work of implementing telemetry yourself.

<img alt="AgentCat architecture — the AgentCat SDK inside your MCP server sends analytics to your observability vendors and session replay to the AgentCat dashboard" src="https://raw.githubusercontent.com/agentcathq/agentcat-csharp-sdk/main/docs/static/architecture.png" />

## How it works

AgentCat works as a lightweight middleware inside your MCP server. When you add `WithAgentCat()` or call `AgentCatSdk.Track()`, it seamlessly modifies your tool schemas as your server lists them, following the MCP core team's [explicit handles (SEP-2567)](https://github.com/modelcontextprotocol/modelcontextprotocol/pull/2567) guidelines. Concretely, AgentCat adds the following to your server:

- **`session_id`** — a parameter injected into each tool's input schema. Agents echo it back on every call, letting AgentCat group related tool calls into one task even over stateless transports. Values are validated: anything that is not a well-formed AgentCat session ID is rejected rather than adopted, and the agent is told to re-send the ID it was given.
- **`agent_id`** _(off by default)_ — enabled with `EnableAgentTracking = true`. Each agent self-generates its own ID, keeping parallel agents working the same task individually attributable.
- **`context`** — a parameter asking the agent to explain, in one sentence, why it is making this call. This is where intent data comes from.
- **`get_more_tools`** — an additional tool, prompt-engineered so that agents readily report the features and tools they looked for but couldn't find — surfacing your missing functionality directly from real usage.

Injected parameters are stripped from arguments before your tool handler runs, so your code never sees them. For tools that declare an output schema, issued IDs are also mirrored into `structuredContent` (as `mcp_session`), so clients that only read structured results still receive them.

## Getting Started

To get started with AgentCat, first create an account and obtain your project ID by signing up at [agentcat.com](https://agentcat.com). For detailed setup instructions visit our [documentation](https://docs.agentcat.com).

Once you have your project ID, set up AgentCat for your MCP server below; the [quickstart](https://docs.agentcat.com/quickstart) walks through each setup step by step. AgentCat stays off while your tests run, and [`AgentCatSdk.Fake()`](https://docs.agentcat.com/sdk/csharp/operations#testing) records the events of a test for your assertions.

### Stateless HTTP on ASP.NET Core

Install AgentCat next to `ModelContextProtocol.AspNetCore` (`dotnet add package ModelContextProtocol.AspNetCore`), then add `WithAgentCat()` to the server builder, anywhere in the chain:

```csharp
using System.ComponentModel;
using ModelContextProtocol.Server;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddMcpServer()
    .WithHttpTransport(o => o.Stateless = true)
    .WithTools<EchoTools>()
    // Track the server with AgentCat
    .WithAgentCat("proj_0000000");

var app = builder.Build();
app.MapMcp();
app.Run();

[McpServerToolType]
public class EchoTools
{
    [McpServerTool, Description("Echo a message")]
    public static string Echo(string msg) => msg;
}
```

With `null` instead of the ID, AgentCat reads it from `AGENTCAT_PROJECT_ID`. Stateless HTTP builds a fresh server for every request, and `WithAgentCat()` tracks each of them. Tracking each instance is cheap — the event queue, telemetry exporters, and diagnostics are initialized once and shared across instances. The [ASP.NET Core guide](https://docs.agentcat.com/sdk/csharp/aspnet-core) has the details, stateful HTTP included.

### stdio on the generic host

A stdio server on the generic host (`ModelContextProtocol` and `Microsoft.Extensions.Hosting`) adds `WithAgentCat()` the same way:

```csharp
using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

var builder = Host.CreateApplicationBuilder(args);

// stdout carries the MCP messages: write the console logs to stderr
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

builder.Services.AddMcpServer()
    .WithStdioServerTransport()
    .WithTools<EchoTools>()
    // Track the server with AgentCat
    .WithAgentCat("proj_0000000");

await builder.Build().RunAsync();

[McpServerToolType]
public class EchoTools
{
    [McpServerTool, Description("Echo a message")]
    public static string Echo(string msg) => msg;
}
```

When the client closes the input, the host stops, and AgentCat sends the queued events before the process ends.

### `McpServer.Create`

When you create the server yourself with `McpServer.Create`, without dependency injection, pass its options to `AgentCatSdk.Track()` before `McpServer.Create` reads them:

```csharp
using AgentCat;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

var options = new McpServerOptions
{
    ServerInfo = new Implementation { Name = "echo-mcp", Version = "0.1.0" },
    ToolCollection = [McpServerTool.Create((string msg) => msg, new() { Name = "echo", Description = "Echo a message" })],
};

// Track the server with AgentCat
AgentCatSdk.Track(options, "proj_0000000");

await using var server = McpServer.Create(new StdioServerTransport("echo-mcp"), options);
await server.RunAsync();
```

Without a host, AgentCat sends the queued events at process exit and, on .NET 8 and later, on SIGTERM and SIGINT. The [`McpServer.Create` guide](https://docs.agentcat.com/sdk/csharp/mcp-server-create) has the details.

### Identifying users

We strongly encourage identifying every actor. If you can't resolve a real user, return a stable anonymized ID instead — for example, a hash of the auth token or API key — so that all events from the same end user still roll up to one actor in your dashboard rather than scattering into anonymous one-off sessions.

`Identify` (like every AgentCat hook) runs concurrently with your tool handler: after its first `await`, a slow lookup — or a hook that fails outright — costs analytics data for that event, never your tool's response time. The part before the first `await` runs on the request thread, before your tool, so keep it short.

The callback receives an `AgentCatToolCall` and a `CancellationToken`. The `AgentCatToolCall` holds the tool name, the raw arguments and `_meta`, the HTTP headers, the client's name and version, the authenticated `User`, and the request context of the MCP SDK. Most identity signals live in its `Headers`, with lower-case names (empty on stdio):

```csharp fragment
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using AgentCat;

builder.Services.AddMcpServer()
    // ... your transport and tools
    .WithAgentCat("proj_0000000", options =>
    {
        options.Identify = (call, cancellationToken) =>
        {
            if (!call.Headers.TryGetValue("authorization", out var token))
            {
                return ValueTask.FromResult<UserIdentity?>(null);
            }

            // A stable anonymized ID, or your own user ID
            var userId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
            return ValueTask.FromResult<UserIdentity?>(new UserIdentity(userId)
            {
                UserData = new JsonObject { ["orgId"] = call.Headers.GetValueOrDefault("x-org-id") },
            });
        };
    });
```

With `AgentCatSdk.Track()`, set the same hooks on the `AgentCatOptions` that you pass as its third argument.

### Redacting sensitive data

AgentCat redacts all data sent to its servers and encrypts at rest, but for additional security, it offers a hook to do your own redaction on all text data returned back to our servers.

```csharp fragment
using System.Text.RegularExpressions;

builder.Services.AddMcpServer()
    // ... your transport and tools
    .WithAgentCat("proj_0000000", options =>
    {
        options.RedactSensitiveInformation = (text, _) =>
            ValueTask.FromResult(Regex.Replace(text, @"\bsk_live_\w+", "[REDACTED]"));
    });
```

For redaction decisions that need more context than a single string — such as which tool was called or what type of event is being published — use the event-level `RedactEvent` hook. It receives the full event object and returns a modified event, or `null` to drop the event entirely. It can be combined with `RedactSensitiveInformation`.

```csharp fragment
using AgentCat;

builder.Services.AddMcpServer()
    // ... your transport and tools
    .WithAgentCat("proj_0000000", options =>
    {
        options.RedactEvent = (e, _) =>
        {
            // Drop events from tools that handle secrets entirely
            if (e.ResourceName == "get_credentials")
            {
                return ValueTask.FromResult<AgentCatEvent?>(null);
            }
            // Strip response payloads from a specific tool
            if (e.ResourceName == "export_report")
            {
                e.Response = null;
            }

            return ValueTask.FromResult<AgentCatEvent?>(e);
        };
    });
```

On ASP.NET Core, a tool-call event also carries the HTTP request of the call in `parameters.extra.requestInfo` (on legacy SSE, the session's `GET /sse` request; the [ASP.NET Core guide](https://docs.agentcat.com/sdk/csharp/aspnet-core) lists when there is none): each header as received, `authorization` and `cookie` included, and the URL with its query string, which can carry an API key. This `RedactEvent` hook removes the `authorization` and `cookie` headers and the query string; remove any other header that carries a secret, such as `x-api-key`, the same way. `RedactEvent` holds one hook, so merge this code with your other `RedactEvent` logic:

```csharp fragment
using System.Text.Json.Nodes;
using AgentCat;

builder.Services.AddMcpServer()
    // ... your transport and tools
    .WithAgentCat("proj_0000000", options =>
    {
        options.RedactEvent = (e, _) =>
        {
            if (e.Parameters is JsonObject p && p["extra"] is JsonObject x && x["requestInfo"] is JsonObject info)
            {
                if (info["headers"] is JsonObject headers)
                {
                    headers.Remove("authorization");
                    headers.Remove("cookie");
                }

                if (info["url"] is JsonValue url && url.TryGetValue(out string? text) && text.IndexOf('?') is var query and >= 0)
                {
                    info["url"] = text.Substring(0, query);
                }
            }

            return new ValueTask<AgentCatEvent?>(e);
        };
    });
```

### Vendor Support

AgentCat seamlessly integrates with your existing observability stack, providing automatic logging and tracing without the tedious setup typically required. Export telemetry data to multiple platforms simultaneously:

```csharp fragment
using AgentCat;

// The project ID can be "" (or null without AGENTCAT_PROJECT_ID) if you just want to forward telemetry
builder.Services.AddMcpServer()
    // ... your transport and tools
    .WithAgentCat("proj_0000000", options =>
    {
        options.Exporters["otlp"] = new OtlpExporter("http://localhost:4318/v1/traces");
        options.Exporters["datadog"] = new DatadogExporter(
            apiKey: Environment.GetEnvironmentVariable("DD_API_KEY") ?? "",
            site: "datadoghq.com",
            service: "my-mcp-server");
        options.Exporters["sentry"] = new SentryExporter(Environment.GetEnvironmentVariable("SENTRY_DSN") ?? "")
        {
            Environment = "production",
        };
        options.Exporters["posthog"] = new PostHogExporter(Environment.GetEnvironmentVariable("POSTHOG_API_KEY") ?? "")
        {
            Host = "https://us.i.posthog.com", // Optional: defaults to US region
        };
    });
```

Learn more about our free and open source [telemetry integrations](https://docs.agentcat.com/telemetry/integrations).

### Internal diagnostics

To help us catch and fix broken installs, the SDK sends AgentCat every line it logs, with its time and level. The lines can contain session IDs, actor IDs, tool names, error messages and the answers of exporters that failed. Records also carry operational metadata, such as your project ID (or, when none is set, an install ID that hashes the machine name and the install path), the OS, and the versions of .NET, the SDK and `ModelContextProtocol`. Your local log is unchanged: `~/agentcat.log`, or the logger of your host with `LogToHost = true`.

Diagnostics are on by default and can be turned off completely with either:

- `.WithAgentCat(projectId, options => options.DisableDiagnostics = true)`, or
- the `AGENTCAT_DISABLE_DIAGNOSTICS` (or `DISABLE_DIAGNOSTICS`) environment variable.

## .NET runtimes

- **ASP.NET Core and the generic host**: AgentCat sends the queued events once the host has stopped (Kestrel and the MCP stdio session included), for at most 5 s, then its last diagnostics batch for at most 1 s; it ignores the host's shutdown token, so a stop can take up to 6 s longer than `HostOptions.ShutdownTimeout`. Hosts before `Microsoft.Extensions.Hosting` 8 and the legacy `WebHost` rely on the process-exit handler instead.
- **No host (`McpServer.Create`)**: `AgentCatSdk.Track()` sends the queued events at process exit and, on .NET 8 and later, on SIGTERM and SIGINT, without cancelling the signal.
- **stdio**: AgentCat writes nothing to stdout or stderr, so stdout stays JSON-RPC. The official C# MCP client kills a stdio server when it is disposed, so the events that AgentCat has not sent by then are lost.
- **Containers**: set the grace period of your container (`terminationGracePeriodSeconds`, `docker stop --time`) at least 6 s above the time your server needs to stop, so that AgentCat can deliver the queued events.
- **.NET Framework 4.7.2 and later**: through .NET Standard 2.0, for stdio servers; there is no ASP.NET Core, so events carry no request headers. AgentCat sends through `HttpClientHandler`, with at most five connections to a server and no DNS refresh for an open connection, and the OpenTelemetry `HttpClient` instrumentation for .NET Framework can record AgentCat's requests. All `ProcessExit` handlers of the process share 2 s. The samples in this README use .NET 8 APIs; on .NET Framework, return `new ValueTask<T>(value)` instead of `ValueTask.FromResult(value)`, and `default` instead of `ValueTask.FromResult<T?>(null)`.
- **Native AOT and trimming**: on .NET 8 and .NET 10 the package is trim and Native AOT compatible: a stdio server and an ASP.NET Core server published with Native AOT get no warnings from AgentCat, and request capture works. On .NET 8 an ASP.NET Core app still gets two warnings from ASP.NET Core's own `JsonOptions`. Under Native AOT, error events have no file names or line numbers, and on .NET 8 they carry the stack text but no frames.

AgentCat supports .NET 8 and later and .NET Framework 4.7.2 or later, and `ModelContextProtocol` 1.0.0 or later, on the 1.x and 2.x lines. On every pull request and every day, CI reads NuGet and tests the newest patch of each minor release of `ModelContextProtocol`, from 1.0.0 up. If your server creates tools concurrently, for example on every stateless request, reference `Microsoft.Extensions.AI.Abstractions` 10.9.0 or later: earlier versions (`ModelContextProtocol` 1.0.0 brings 10.3.0, and 2.2.0 brings 10.8.3) have a race ([dotnet/extensions#7677](https://github.com/dotnet/extensions/pull/7677)) that can stop AgentCat from adding its `context` parameter to a tool. The [operations guide](https://docs.agentcat.com/sdk/csharp/operations) covers the log file, diagnostics, `AGENTCAT_ENABLED` and test runs, testing and shutdown, and the [limits page](https://docs.agentcat.com/sdk/csharp/limits) lists the differences from the TypeScript SDK and the known limits.

## Free for open source

AgentCat is free for qualified open source projects. We believe in supporting the ecosystem that makes MCP possible. If you maintain an open source MCP server, you can access our full analytics platform at no cost.

**How to apply**: Email [hi@agentcat.com](mailto:hi@agentcat.com) with your repository link

_Already using AgentCat? We'll upgrade your account immediately._

## Community Cats 🐱

Meet the cats behind AgentCat! Add your cat to our community by submitting a PR with your cat's photo in the `docs/cats/` directory of the [TypeScript SDK](https://github.com/agentcathq/agentcat-typescript-sdk/tree/main/docs/cats), whose cats this README shows too.

<div align="left">
  <img src="https://raw.githubusercontent.com/agentcathq/agentcat-csharp-sdk/main/docs/cats/bibi.png" alt="bibi" width="80" height="80">
  <img src="https://raw.githubusercontent.com/agentcathq/agentcat-csharp-sdk/main/docs/cats/zelda.jpg" alt="zelda" width="80" height="80">
  <img src="https://raw.githubusercontent.com/agentcathq/agentcat-csharp-sdk/main/docs/cats/void.jpg" alt="void" width="80" height="80">
</div>

_Want to add your cat? Create a PR adding your cat's photo to_ [`docs/cats/` of the TypeScript SDK](https://github.com/agentcathq/agentcat-typescript-sdk/tree/main/docs/cats) _and update its README!_
