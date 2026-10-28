using AotHttpServer;

var builder = WebApplication.CreateBuilder(args);

// The listening URL comes from ASPNETCORE_URLS; the events API URL and the diagnostics switch from AGENTCAT_API_URL
// and AGENTCAT_DISABLE_DIAGNOSTICS.
builder.Services
    .AddMcpServer()
    .WithHttpTransport(o => o.Stateless = true)
    .WithTools<EchoTools>()
    .WithAgentCat("proj_aot_http");

var app = builder.Build();
app.MapMcp();
app.Run();
