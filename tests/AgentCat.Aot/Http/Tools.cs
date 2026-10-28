using System.ComponentModel;
using ModelContextProtocol.Server;

namespace AotHttpServer;

internal sealed class EchoTools
{
    [McpServerTool(Name = "echo"), Description("Echoes the text back.")]
    public static string Echo([Description("The text to echo.")] string text) => "echo: " + text;
}
