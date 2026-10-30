# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [1.0.0-beta.1] - 2026-10-30

First release of the AgentCat SDK for C#, as a beta.

### Added

- `WithAgentCat()` for servers that `AddMcpServer()` builds (stateless HTTP, stateful HTTP, stdio) and `AgentCatSdk.Track()` for servers that you create with `McpServer.Create`.
- The `session_id`, `agent_id` and `context` parameters that AgentCat injects into each tool, the `get_more_tools` tool, and the session text and `mcp_session` result field, with the same strings as the TypeScript SDK.
- Support for `ModelContextProtocol` 1.0.0 and later (the 1.x and 2.x lines), on .NET 8, .NET 10 and .NET Framework 4.7.2 or later (`netstandard2.0`).
- The hooks `Identify`, `ResolveSessionId`, `EventTags`, `EventProperties`, `RedactSensitiveInformation` and `RedactEvent`.
- Custom events, with `AgentCatSdk.PublishCustomEventAsync()` and `IAgentCat`.
- The four exporters: `OtlpExporter`, `DatadogExporter`, `SentryExporter` and `PostHogExporter`.
- Capture of the HTTP request headers and URL of each tool call on ASP.NET Core servers, in `parameters.extra.requestInfo` and `AgentCatToolCall.Headers`. On legacy SSE this is the session's `GET /sse` request; the [ASP.NET Core guide](https://docs.agentcat.com/sdk/csharp/aspnet-core) lists when there is none. Headers are sent as received, `Authorization` and `Cookie` included; remove them with `RedactEvent`.
- A log file (`~/agentcat.log`) and the `LogToHost` option for the host logger.
- Diagnostics, which send AgentCat the SDK's own log lines to find broken installs, and their opt-out (`DisableDiagnostics`, `AGENTCAT_DISABLE_DIAGNOSTICS`).
- Delivery of queued events at shutdown: after the host stops, at process exit, and (for `Track`) on SIGTERM and SIGINT, for about 6 seconds at most. `DisableShutdownHandlers`, `FlushAsync` and `ShutdownAsync` control it.
- The `AGENTCAT_ENABLED` kill switch. AgentCat stays off in test runs, and `AgentCatSdk.Fake()` records events for your own tests.
- Trim-safe and Native AOT compatible builds.

[Unreleased]: https://github.com/agentcathq/agentcat-csharp-sdk/compare/v1.0.0-beta.1...HEAD
[1.0.0-beta.1]: https://github.com/agentcathq/agentcat-csharp-sdk/releases/tag/v1.0.0-beta.1
