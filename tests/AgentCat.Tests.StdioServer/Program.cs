using AgentCat;
using AgentCat.Internal.Logging;
using AgentCat.Tests.E2E;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

var logPath = Environment.GetEnvironmentVariable(E2EToolkit.LogPathVariable);
if (!string.IsNullOrEmpty(logPath))
{
    FileLog.PathOverride = logPath;
}

// Writes lines through the process log from several threads and exits, without a host or an MCP server.
if (Environment.GetEnvironmentVariable(E2EToolkit.ModeVariable) == "log-writer")
{
    var lines = int.Parse(Environment.GetEnvironmentVariable("AGENTCAT_E2E_LINES")!, System.Globalization.CultureInfo.InvariantCulture);
    var threadCount = int.Parse(Environment.GetEnvironmentVariable("AGENTCAT_E2E_THREADS")!, System.Globalization.CultureInfo.InvariantCulture);
    var log = new FileLog(FileLog.DefaultPath());
    var pid = Environment.ProcessId;
    var threads = Enumerable.Range(0, threadCount).Select(t => new Thread(() =>
    {
        for (var n = 0; n < lines; n++)
        {
            log.Write(pid + ":" + t + ":" + n + ":" + new string('x', 80));
        }
    })).ToList();
    threads.ForEach(t => t.Start());
    threads.ForEach(t => t.Join());
    System.Console.Out.WriteLine("dropped=" + FileLog.DroppedLines);
    return 0;
}

var scenario = Environment.GetEnvironmentVariable(E2EToolkit.ScenarioVariable) ?? Scenarios.BaselineDefaults;
var handlerTools = Environment.GetEnvironmentVariable(E2EToolkit.ToolsVariable) == E2EToolkit.HandlerToolsValue;
var noShutdownHandlers = Environment.GetEnvironmentVariable(E2EToolkit.ShutdownHandlersVariable) == E2EToolkit.OffValue;
var projectVariable = Environment.GetEnvironmentVariable(E2EToolkit.ProjectVariable);
var projectId = string.IsNullOrEmpty(projectVariable) ? E2EToolkit.ProjectId
    : projectVariable == E2EToolkit.TelemetryOnlyValue ? ""
    : projectVariable;
var exporterHost = Environment.GetEnvironmentVariable(E2EExporters.HostVariable);

// stdout carries the MCP frames only, so the lines for the test go to stderr.
System.Console.Error.WriteLine(E2EToolkit.EnabledLine(Environment.GetEnvironmentVariable("AGENTCAT_ENABLED")));
E2EToolkit.SlowStarted = static () => System.Console.Error.WriteLine(E2EToolkit.SlowStartedLine);

// No host: AgentCatSdk.Track on the options, McpServer.Create over stdio, and no logging, until stdin closes.
if (Environment.GetEnvironmentVariable(E2EToolkit.HostVariable) == E2EToolkit.NoHostValue)
{
    // Attached before Track attaches AgentCat's handler, so it runs first.
    AppDomain.CurrentDomain.ProcessExit += (_, _) => System.Console.Error.WriteLine(E2EToolkit.ProcessExitLine);
    var options = new McpServerOptions { ServerInfo = E2EToolkit.ServerInfo() };
    E2EToolkit.AddTo(options, handlerTools);
    var agentCatOptions = new AgentCatOptions();
    Configure(agentCatOptions);
    AgentCatSdk.Track(options, projectId, agentCatOptions);
    await using var server = McpServer.Create(new StdioServerTransport(options), options);
    await server.RunAsync();
    return 0;
}

var builder = Host.CreateApplicationBuilder(args);

// stdout carries the MCP frames only.
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

builder.Services.AddMcpServer(o => o.ServerInfo = E2EToolkit.ServerInfo())
    .WithStdioServerTransport()
    .WithE2ETools(handlerTools)
    .WithAgentCat(projectId, Configure);

var app = builder.Build();
app.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.Register(
    static () => System.Console.Error.WriteLine(E2EToolkit.HostStoppingLine));
await app.RunAsync();

return 0;

// The scenario's options and the shutdown handlers; with an exporter host, every exporter that reaches it by its URL
// (Datadog's URLs are always https on the Datadog site).
void Configure(AgentCatOptions agentCat)
{
    Scenarios.Apply(scenario, agentCat);
    agentCat.DisableShutdownHandlers = noShutdownHandlers;
    if (!string.IsNullOrEmpty(exporterHost))
    {
        E2EExporters.Add(agentCat, exporterHost, withDatadog: false, tracing: false);
    }
}
