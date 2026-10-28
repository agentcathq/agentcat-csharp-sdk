using System.ComponentModel;
using ModelContextProtocol.Server;

namespace AotStdioServer;

internal sealed class EchoTools
{
    [McpServerTool(Name = "echo"), Description("Echoes the text back.")]
    public static string Echo([Description("The text to echo.")] string text) => "echo: " + text;
}

// A nested tool type whose async tool throws from another nested type. Where the runtime gives method metadata
// (net10.0), the event's frames must name both as the JIT does: ThrowAsync in AotStdioServer.Helper+Deep, then Boom in
// AotStdioServer.Outer+Inner. On net8.0 Native AOT the event has no frames.
internal static class Outer
{
    internal sealed class Inner
    {
        [McpServerTool(Name = "boom"), Description("Always fails.")]
        public async Task<string> Boom()
        {
            await Helper.Deep.ThrowAsync();
            return "unreachable";
        }
    }
}

internal static class Helper
{
    internal static class Deep
    {
        internal static async Task ThrowAsync()
        {
            await Task.Yield();
            throw new InvalidOperationException("boom from Helper.Deep.ThrowAsync");
        }
    }
}
