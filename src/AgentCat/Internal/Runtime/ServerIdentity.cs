using System;
using System.Reflection;
using System.Threading;
using ModelContextProtocol.Server;

namespace AgentCat.Internal.Runtime;

internal static class ServerIdentity
{
    private static readonly Lazy<(string Name, string Version)> Fallback = new(ReadEntryAssembly, LazyThreadSafetyMode.PublicationOnly);

    // Runs on the request path for every tool call, so the fallback is read once per process.
    internal static (string Name, string Version) Of(McpServerOptions? options)
    {
        var info = options?.ServerInfo;
        return info is null ? Fallback.Value : (info.Name, info.Version);
    }

    private static (string Name, string Version) ReadEntryAssembly()
    {
        var assembly = (Assembly.GetEntryAssembly() ?? typeof(McpServer).Assembly).GetName();
        return (assembly.Name ?? "McpServer", assembly.Version?.ToString() ?? "1.0.0");
    }
}
