using System.Reflection;
using AgentCat.Internal;
using ModelContextProtocol.Protocol;

namespace AgentCat.Tests;

public class BuildGateTests
{
    [Fact]
    public void Assembly_is_strong_named()
        => Assert.NotEmpty(typeof(AgentCatAssemblyMarker).Assembly.GetName().GetPublicKeyToken()!);

    private static string Metadata(string key)
        => typeof(BuildGateTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(a => a.Key == key).Value ?? string.Empty;

    private static string ExpectedPrefix => Metadata("ExpectedVersionPrefix");

    private static string ExpectedPackageVersion
        => Metadata("ExpectedVersionSuffix") is { Length: > 0 } suffix ? ExpectedPrefix + "-" + suffix : ExpectedPrefix;

    [Fact]
    public void Assembly_version_is_major_only()
        => Assert.Equal(new Version(Version.Parse(ExpectedPrefix).Major, 0, 0, 0),
            typeof(AgentCatAssemblyMarker).Assembly.GetName().Version);

    [Fact]
    public void Informational_version_has_the_package_version()
        => Assert.StartsWith(ExpectedPackageVersion, typeof(AgentCatAssemblyMarker).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion, StringComparison.Ordinal);

    [Fact]
    public void Packed_version_is_the_props_version()
        => Assert.Equal(ExpectedPackageVersion, SdkInfo.Version);

    [Fact]
    public void File_version_is_the_version_prefix()
        => Assert.Equal(ExpectedPrefix + ".0", typeof(AgentCatAssemblyMarker).Assembly
            .GetCustomAttribute<AssemblyFileVersionAttribute>()!.Version);

    [Fact]
    public void Referenced_assemblies_are_framework_or_mcp_only()
        => Assert.All(typeof(AgentCatAssemblyMarker).Assembly.GetReferencedAssemblies().Select(a => a.Name!),
            n => Assert.True(IsAllowedReference(n), n));

    [Theory]
    [InlineData("netstandard", true)]
    [InlineData("System.Text.Json", true)]
    [InlineData("Microsoft.Extensions.Logging.Abstractions", true)]
    [InlineData("ModelContextProtocol.Core", true)]
    [InlineData("AgentCat.Api", true)]
    [InlineData("AgentCat.ApiExtensions", false)]
    [InlineData("Microsoft.AspNetCore", false)]
    [InlineData("Microsoft.AspNetCore.Http.Abstractions", false)]
    [InlineData("ModelContextProtocol.AspNetCore", false)]
    [InlineData("Newtonsoft.Json", false)]
    public void Reference_gate_rejects_ASP_NET_Core(string name, bool allowed)
        => Assert.Equal(allowed, IsAllowedReference(name));

    [Fact]
    public void Loaded_MCP_assembly_is_the_requested_test_version()
    {
        var requested = typeof(BuildGateTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "McpTestVersion")?.Value;
        if (string.IsNullOrEmpty(requested))
        {
            return;
        }

        var mcp = typeof(CallToolResult).Assembly;
        Assert.Equal("ModelContextProtocol.Core", mcp.GetName().Name);
        Assert.StartsWith(requested, mcp.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion, StringComparison.Ordinal);
    }

    private static bool IsAllowedReference(string name)
        => (name is "netstandard" or "mscorlib" or "AgentCat.Api"
                || name.StartsWith("System", StringComparison.Ordinal)
                || name.StartsWith("Microsoft.", StringComparison.Ordinal)
                || name.StartsWith("ModelContextProtocol", StringComparison.Ordinal))
            && !name.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal)
            && !name.StartsWith("ModelContextProtocol.AspNetCore", StringComparison.Ordinal);
}
