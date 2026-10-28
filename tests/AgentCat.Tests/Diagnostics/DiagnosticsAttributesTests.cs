#pragma warning disable CA1861, CA1305, CA1872, CA1850, CA1837, CA2016

using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using AgentCat.Internal;
using AgentCat.Internal.Diagnostics;
using AgentCat.Internal.Logging;
using ModelContextProtocol.Server;

namespace AgentCat.Tests.Diagnostics;

public class DiagnosticsAttributesTests
{
    private static Dictionary<string, string> Map(IEnumerable<KeyValuePair<string, string>> attributes)
        => attributes.ToDictionary(a => a.Key, a => a.Value);

    private static Dictionary<string, string> Started(string? projectId)
        => Map(new DiagnosticsHarness().ForceEnabled().Started(projectId).Diagnostics.Resource);

    // TS: uses projectId verbatim when present
    [Fact]
    public void Uses_projectId_verbatim_when_present()
    {
        var m = Started("proj_ABC");

        Assert.Equal("proj_ABC", m["agentcat.project_id"]);
        Assert.False(m.ContainsKey("agentcat.install_id"));
    }

    // TS: falls back to a stable install_id when projectId is absent
    [Fact]
    public void Falls_back_to_a_stable_install_id_when_projectId_is_absent()
    {
        var a = Started(null);
        var b = Started(null);
        var empty = Started("");

        Assert.False(a.ContainsKey("agentcat.project_id"));
        Assert.Matches("^[0-9a-f]{16}$", a["agentcat.install_id"]);
        Assert.Equal(a["agentcat.install_id"], b["agentcat.install_id"]);
        Assert.Equal(a["agentcat.install_id"], empty["agentcat.install_id"]);
        Assert.Equal(DiagnosticsAttributes.InstallId(Environment.MachineName, AppContext.BaseDirectory), a["agentcat.install_id"]);
    }

    [Fact]
    public void Install_id_is_stable_and_16_hex()
    {
        var id = DiagnosticsAttributes.InstallId("host-1", "/srv/app/");
        using var sha = SHA256.Create();
        var expected = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes("host-1|file:///srv/app/"))).Replace("-", string.Empty).ToLowerInvariant().Substring(0, 16);

        Assert.Matches("^[0-9a-f]{16}$", id);
        Assert.Equal(expected, id);
        Assert.Equal(id, DiagnosticsAttributes.InstallId("host-1", "/srv/app/"));
        Assert.NotEqual(id, DiagnosticsAttributes.InstallId("host-2", "/srv/app/"));
        Assert.NotEqual(id, DiagnosticsAttributes.InstallId("host-1", "/srv/other/"));
    }

    // TS: includes sdk language, sdk version, and os/runtime metadata
    [Fact]
    public void Includes_sdk_language_sdk_version_and_os_runtime_metadata()
    {
        var m = Started("proj_1");

        Assert.Equal("csharp", m["agentcat.sdk.language"]);
        Assert.Equal(SdkInfo.Version, m["agentcat.sdk.version"]);
        Assert.Equal("dotnet", m["process.runtime.name"]);
        Assert.Equal(SdkInfo.RuntimeVersion, m["process.runtime.version"]);
        Assert.Equal(System.Diagnostics.Process.GetCurrentProcess().Id.ToString(), m["process.pid"]);
        Assert.Equal(Environment.OSVersion.Version.ToString(), m["os.version"]);
        Assert.Equal(Environment.ProcessorCount.ToString(), m["host.cpu.count"]);
        Assert.Contains(m["os.type"], new[] { "linux", "darwin", "win32" });
        Assert.Contains(m["host.arch"], new[] { "x64", "arm64" });
    }

    [Fact]
    public void Os_type_and_host_arch_use_the_node_words()
    {
        var m = Started("proj_1");

        var os = RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? "linux"
            : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "darwin"
            : "win32";
        var arch = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "x64";
        Assert.Equal(os, m["os.type"]);
        Assert.Equal(arch, m["host.arch"]);
    }

    // C#: the MCP SDK version replaces the two npm-resolution tests.
    [Fact]
    public void Reports_the_installed_MCP_SDK_version()
    {
        var m = Started("proj_1");

        Assert.Equal(LogLine.McpVersion, m["agentcat.mcp_sdk.version"]);
        Assert.Matches("^[0-9]+\\.[0-9]+\\.[0-9]+", m["agentcat.mcp_sdk.version"]);
        Assert.Equal(typeof(McpServerOptions).Assembly.GetName().Version!.Major, int.Parse(Regex.Match(m["agentcat.mcp_sdk.version"], "^[0-9]+").Value));
    }

    [Fact]
    public void Resource_attributes_come_in_the_documented_order()
    {
        var keys = DiagnosticsAttributes.Resource("proj_1", _ => "Production").Select(a => a.Key).ToArray();

        Assert.Equal(
            new[]
            {
                "agentcat.project_id", "agentcat.sdk.language", "agentcat.sdk.version", "agentcat.mcp_sdk.version",
                "process.runtime.name", "process.runtime.version", "process.pid", "os.type", "os.version", "host.arch",
                "host.cpu.count", "deployment.environment",
            },
            keys);
    }

    [Theory]
    [InlineData("Staging", "Production", "Staging")]
    [InlineData(null, "Production", "Production")]
    [InlineData("", "Production", "Production")]
    public void Deployment_environment_reads_DOTNET_ENVIRONMENT_then_ASPNETCORE_ENVIRONMENT(string? dotnet, string aspnet, string expected)
    {
        var m = Map(DiagnosticsAttributes.Resource("p", name => name == "DOTNET_ENVIRONMENT" ? dotnet : name == "ASPNETCORE_ENVIRONMENT" ? aspnet : null));

        Assert.Equal(expected, m["deployment.environment"]);
    }

    [Fact]
    public void Empty_values_are_left_out()
    {
        var m = Map(DiagnosticsAttributes.Resource("p", _ => null));

        Assert.False(m.ContainsKey("deployment.environment"));
        Assert.All(m.Values, v => Assert.NotEmpty(v));
    }

    [Fact]
    public void Record_attributes_carry_the_three_versions()
    {
        var attributes = DiagnosticsAttributes.Record();

        Assert.Equal(
            new[] { "agentcat.sdk.version", "process.runtime.version", "agentcat.mcp_sdk.version" },
            attributes.Select(a => a.Key).ToArray());
        Assert.Equal(new[] { SdkInfo.Version, SdkInfo.RuntimeVersion, LogLine.McpVersion }, attributes.Select(a => a.Value).ToArray());
    }
}
