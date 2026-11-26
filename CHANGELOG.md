# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [1.1.1] - 2026-11-26

### Security

- The SDK now masks sensitive values in every event before the event leaves your server. It tests each key in `parameters`, `response` and `error`, at any depth. When the key names a secret, the SDK keeps the key and replaces the value with `[redacted]`. A key names a secret when it is a known credential header (`authorization`, `cookie`, `x-api-key` and others) or has a word such as `token`, `secret`, `password`, `signature` or `api-key`, in any spelling (`X-Api-Key`, `HTTP_X_API_KEY`, `xApiKey`). Before, the SDK sent the HTTP headers of each MCP request as received, in `parameters.extra.requestInfo.headers`. Tool arguments and results under such key names lose their values too, for example `pageToken` and `auth_type`. `null`, `true` and `false` stay. No option turns the mask off (#10).

## [1.1.0] - 2026-11-17

### Added

- The server info heartbeat: about once an hour, the SDK sends AgentCat the server's name, version, title and instructions, and its full lists of tools, resources, resource templates and prompts. `RedactSensitiveInformation` runs on the text. The package now depends on `AgentCat.Api` 0.0.6.

## [1.0.0] - 2026-11-03

First release of the AgentCat SDK for C#.

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

[Unreleased]: https://github.com/agentcathq/agentcat-csharp-sdk/compare/v1.1.1...HEAD
[1.1.1]: https://github.com/agentcathq/agentcat-csharp-sdk/compare/v1.1.0...v1.1.1
[1.1.0]: https://github.com/agentcathq/agentcat-csharp-sdk/compare/v1.0.0...v1.1.0
[1.0.0]: https://github.com/agentcathq/agentcat-csharp-sdk/releases/tag/v1.0.0
