using AgentCat.Internal.Runtime;

namespace AgentCat.Tests.Runtime;

public sealed class TestEnvironmentTests
{
    private static readonly string[] NoAssemblies = Array.Empty<string>();

    private static readonly string[] RuntimeOnly = { "System.Runtime" };

    private static readonly string[] NonSignals = { "Microsoft.Testing.Platform", "Microsoft.AspNetCore.TestHost" };

    private static readonly bool[] FalseFalseTrueFalse = { false, false, true, false };

    private static readonly string Shared = Path.Combine(Path.GetTempPath(), "dotnet", "shared", "Microsoft.NETCore.App", "10.0.13");

    private static readonly string AppDirectory = Path.Combine(Path.GetTempPath(), "app", "bin");

    public static TheoryData<string> Frameworks => new()
    {
        "xunit.core",
        "xunit.v3.core",
        "nunit.framework",
        "MSTest.TestFramework",
        "Microsoft.VisualStudio.TestPlatform.TestFramework",
        "TUnit.Core",
    };

    public static TheoryData<string> NotSignals => new()
    {
        "Microsoft.Testing.Platform",
        "Microsoft.Testing.Extensions.VSTestBridge",
        "Microsoft.AspNetCore.TestHost",
        "Microsoft.VisualStudio.TestPlatform.ObjectModel",
        "Microsoft.TestPlatform.CoreUtilities",
        "xunit.abstractions",
        "xunit.assert",
        "xunit.v3.assert",
        "XUnit.Core",
        "xunit.core.extra",
        "NUnit.Framework",
        "TUnit.Core.SourceGenerator",
    };

    public static TheoryData<string> ProductionSets => new() { "console", "aspnetcore", "mcp-server" };

    // A TRUSTED_PLATFORM_ASSEMBLIES value: absolute .dll paths joined with the platform's path separator.
    private static string Tpa(params string[] names)
        => string.Join(Path.PathSeparator.ToString(), names.Select(n => Path.Combine(n.StartsWith("System.", StringComparison.Ordinal) ? Shared : AppDirectory, n + ".dll")));

    [Theory]
    [MemberData(nameof(Frameworks))]
    public void A_loaded_test_framework_is_a_test_run(string framework)
        => Assert.True(TestEnvironment.Detect("MyServer.Tests", new[] { "System.Runtime", framework, "MyServer" }, Tpa("System.Runtime")));

    [Theory]
    [InlineData("testhost")]
    [InlineData("testhost.x86")]
    [InlineData("testhost.arm64")]
    public void A_testhost_entry_assembly_is_a_test_run(string entry)
        => Assert.True(TestEnvironment.Detect(entry, NoAssemblies, null));

    [Theory]
    [MemberData(nameof(Frameworks))]
    public void A_test_framework_in_the_trusted_platform_assemblies_is_a_test_run(string framework)
        => Assert.True(TestEnvironment.Detect("MyServer.Tests", RuntimeOnly, Tpa("System.Runtime", framework, "MyServer")));

    [Theory]
    [InlineData("testhosting")]
    [InlineData("TestHost")]
    [InlineData("mytesthost")]
    [InlineData("")]
    [InlineData(null)]
    public void Other_entry_assembly_names_are_not_signals(string? entry)
        => Assert.False(TestEnvironment.Detect(entry, NoAssemblies, null));

    [Theory]
    [MemberData(nameof(NotSignals))]
    public void Names_outside_the_six_framework_names_are_not_signals(string name)
        => Assert.False(TestEnvironment.Detect("MyServer", new[] { "System.Runtime", name }, Tpa("System.Runtime", name)));

    [Fact]
    public void Only_the_file_name_of_a_trusted_platform_assembly_counts()
    {
        var inDirectory = Path.Combine(AppDirectory, "xunit.core", "lib", "other.dll");
        var otherExtension = Path.Combine(AppDirectory, "xunit.core.exe");
        var coreAsExtension = Path.Combine(AppDirectory, "xunit.core");
        var longerName = Path.Combine(AppDirectory, "xunit.core.resources.dll");

        Assert.False(TestEnvironment.Detect("MyServer", NoAssemblies, inDirectory));
        Assert.True(TestEnvironment.Detect("MyServer", NoAssemblies, otherExtension));
        Assert.False(TestEnvironment.Detect("MyServer", NoAssemblies, coreAsExtension));
        Assert.False(TestEnvironment.Detect("MyServer", NoAssemblies, longerName));
        Assert.False(TestEnvironment.Detect("MyServer", NoAssemblies, ""));
        Assert.False(TestEnvironment.Detect("MyServer", NoAssemblies, Path.PathSeparator.ToString()));
        Assert.True(TestEnvironment.Detect("MyServer", NoAssemblies, Tpa("System.Runtime") + Path.PathSeparator + Tpa("TUnit.Core") + Path.PathSeparator));
    }

    [Fact]
    public void Null_loaded_names_are_skipped()
        => Assert.True(TestEnvironment.Detect(null, new string?[] { null, "nunit.framework" }, null));

    // A production process that AgentCat took for a test run would send nothing, and nobody would notice.
    [Theory]
    [MemberData(nameof(ProductionSets))]
    public void Production_assembly_sets_are_not_test_environments(string set)
    {
        var (entry, loaded, onDisk) = ProductionAssemblies.Of(set);
        var withNonSignals = loaded.Concat(NonSignals).ToArray();

        Assert.False(TestEnvironment.Detect(entry, withNonSignals, Tpa(withNonSignals.Concat(onDisk).ToArray())));
        Assert.False(TestEnvironment.Detect(entry, loaded, null));
    }

    [Fact]
    public void This_test_run_is_detected()
        => Assert.True(TestEnvironment.IsTestRun());

    [Fact]
    public void A_true_result_is_cached_and_a_false_one_is_not()
    {
        var answers = new Queue<bool>(FalseFalseTrueFalse);
        var calls = 0;
        var isTestRun = TestEnvironment.CacheTrue(() =>
        {
            calls++;
            return answers.Dequeue();
        });

        Assert.False(isTestRun());
        Assert.False(isTestRun());
        Assert.Equal(2, calls);
        Assert.True(isTestRun());
        Assert.True(isTestRun());
        Assert.True(isTestRun());
        Assert.Equal(3, calls);
    }

    // Loaded assemblies that a probe recorded in production MCP servers (the probe's own library removed), and
    // packages of the MCP server that were on disk but not loaded at the dump.
    private static class ProductionAssemblies
    {
        internal static (string Entry, string[] Loaded, string[] OnDisk) Of(string set) => set switch
        {
            "console" => ("Con", ConsoleApp, Array.Empty<string>()),
            "aspnetcore" => ("Web", AspNetCoreApp, Array.Empty<string>()),
            _ => ("WebMcp", McpServerApp, McpServerPackages),
        };

        private static readonly string[] ConsoleApp =
        {
            "Con", "System.Collections", "System.ComponentModel.Primitives", "System.Diagnostics.Process", "System.Linq",
            "System.Memory", "System.Numerics.Vectors", "System.Private.CoreLib", "System.Runtime",
        };

        private static readonly string[] AspNetCoreApp =
        {
            "Anonymously Hosted DynamicMethods Assembly", "Microsoft.AspNetCore",
            "Microsoft.AspNetCore.Authentication.Abstractions", "Microsoft.AspNetCore.Authorization",
            "Microsoft.AspNetCore.Connections.Abstractions", "Microsoft.AspNetCore.HostFiltering",
            "Microsoft.AspNetCore.Hosting", "Microsoft.AspNetCore.Hosting.Abstractions",
            "Microsoft.AspNetCore.Hosting.Server.Abstractions", "Microsoft.AspNetCore.Http",
            "Microsoft.AspNetCore.Http.Abstractions", "Microsoft.AspNetCore.Http.Extensions",
            "Microsoft.AspNetCore.Http.Features", "Microsoft.AspNetCore.HttpOverrides", "Microsoft.AspNetCore.Metadata",
            "Microsoft.AspNetCore.Routing", "Microsoft.AspNetCore.Routing.Abstractions",
            "Microsoft.AspNetCore.Server.IIS", "Microsoft.AspNetCore.Server.IISIntegration",
            "Microsoft.AspNetCore.Server.Kestrel", "Microsoft.AspNetCore.Server.Kestrel.Core",
            "Microsoft.AspNetCore.Server.Kestrel.Transport.Quic",
            "Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets", "Microsoft.Extensions.Configuration",
            "Microsoft.Extensions.Configuration.Abstractions", "Microsoft.Extensions.Configuration.Binder",
            "Microsoft.Extensions.Configuration.CommandLine", "Microsoft.Extensions.Configuration.EnvironmentVariables",
            "Microsoft.Extensions.Configuration.FileExtensions", "Microsoft.Extensions.Configuration.Json",
            "Microsoft.Extensions.DependencyInjection", "Microsoft.Extensions.DependencyInjection.Abstractions",
            "Microsoft.Extensions.Diagnostics", "Microsoft.Extensions.Diagnostics.Abstractions",
            "Microsoft.Extensions.Features", "Microsoft.Extensions.FileProviders.Abstractions",
            "Microsoft.Extensions.FileProviders.Physical", "Microsoft.Extensions.Hosting",
            "Microsoft.Extensions.Hosting.Abstractions", "Microsoft.Extensions.Logging",
            "Microsoft.Extensions.Logging.Abstractions", "Microsoft.Extensions.Logging.Configuration",
            "Microsoft.Extensions.Logging.Console", "Microsoft.Extensions.Logging.Debug",
            "Microsoft.Extensions.Logging.EventSource", "Microsoft.Extensions.ObjectPool",
            "Microsoft.Extensions.Options", "Microsoft.Extensions.Options.ConfigurationExtensions",
            "Microsoft.Extensions.Primitives", "Microsoft.Extensions.Validation", "Microsoft.Net.Http.Headers",
            "Microsoft.Win32.Primitives", "System.Collections", "System.Collections.Concurrent",
            "System.Collections.Immutable", "System.Collections.NonGeneric", "System.ComponentModel",
            "System.ComponentModel.Primitives", "System.Console", "System.Diagnostics.DiagnosticSource",
            "System.Diagnostics.Process", "System.Diagnostics.Tracing", "System.IO.FileSystem.Watcher",
            "System.IO.Pipelines", "System.Linq", "System.Linq.Expressions", "System.Memory", "System.Net.Http",
            "System.Net.NameResolution", "System.Net.Primitives", "System.Net.Quic", "System.Net.Security",
            "System.Net.Sockets", "System.Net.WebSockets", "System.Numerics.Vectors", "System.ObjectModel",
            "System.Private.CoreLib", "System.Private.Uri", "System.Reflection.Emit.ILGeneration",
            "System.Reflection.Emit.Lightweight", "System.Reflection.Primitives", "System.Runtime",
            "System.Runtime.InteropServices", "System.Runtime.Intrinsics", "System.Security.Claims",
            "System.Security.Cryptography", "System.Text.Encoding.Extensions", "System.Text.Encodings.Web",
            "System.Text.Json", "System.Threading", "System.Threading.Thread", "System.Threading.ThreadPool", "Web",
        };

        private static readonly string[] McpServerApp =
        {
            "Microsoft.AspNetCore", "Microsoft.AspNetCore.Authentication.Abstractions",
            "Microsoft.AspNetCore.Authorization", "Microsoft.AspNetCore.Connections.Abstractions",
            "Microsoft.AspNetCore.HostFiltering", "Microsoft.AspNetCore.Hosting",
            "Microsoft.AspNetCore.Hosting.Abstractions", "Microsoft.AspNetCore.Hosting.Server.Abstractions",
            "Microsoft.AspNetCore.Http", "Microsoft.AspNetCore.Http.Abstractions",
            "Microsoft.AspNetCore.Http.Extensions", "Microsoft.AspNetCore.Http.Features",
            "Microsoft.AspNetCore.Http.Results", "Microsoft.AspNetCore.HttpOverrides", "Microsoft.AspNetCore.Metadata",
            "Microsoft.AspNetCore.Routing", "Microsoft.AspNetCore.Routing.Abstractions",
            "Microsoft.AspNetCore.Server.IIS", "Microsoft.AspNetCore.Server.IISIntegration",
            "Microsoft.AspNetCore.Server.Kestrel", "Microsoft.AspNetCore.Server.Kestrel.Core",
            "Microsoft.AspNetCore.Server.Kestrel.Transport.Quic",
            "Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets", "Microsoft.AspNetCore.WebUtilities",
            "Microsoft.EntityFrameworkCore", "Microsoft.Extensions.AI.Abstractions",
            "Microsoft.Extensions.Configuration", "Microsoft.Extensions.Configuration.Abstractions",
            "Microsoft.Extensions.Configuration.Binder", "Microsoft.Extensions.Configuration.EnvironmentVariables",
            "Microsoft.Extensions.Configuration.FileExtensions", "Microsoft.Extensions.Configuration.Json",
            "Microsoft.Extensions.DependencyInjection", "Microsoft.Extensions.DependencyInjection.Abstractions",
            "Microsoft.Extensions.Diagnostics", "Microsoft.Extensions.Diagnostics.Abstractions",
            "Microsoft.Extensions.Features", "Microsoft.Extensions.FileProviders.Abstractions",
            "Microsoft.Extensions.FileProviders.Physical", "Microsoft.Extensions.Hosting",
            "Microsoft.Extensions.Hosting.Abstractions", "Microsoft.Extensions.Logging",
            "Microsoft.Extensions.Logging.Abstractions", "Microsoft.Extensions.Logging.Configuration",
            "Microsoft.Extensions.Logging.Console", "Microsoft.Extensions.Logging.Debug",
            "Microsoft.Extensions.Logging.EventSource", "Microsoft.Extensions.ObjectPool",
            "Microsoft.Extensions.Options", "Microsoft.Extensions.Options.ConfigurationExtensions",
            "Microsoft.Extensions.Primitives", "Microsoft.Extensions.Validation", "Microsoft.Net.Http.Headers",
            "Microsoft.Win32.Primitives", "ModelContextProtocol", "ModelContextProtocol.AspNetCore",
            "ModelContextProtocol.Core", "System.Collections", "System.Collections.Concurrent",
            "System.Collections.Immutable", "System.Collections.NonGeneric", "System.ComponentModel",
            "System.ComponentModel.Primitives", "System.Console", "System.Diagnostics.DiagnosticSource",
            "System.Diagnostics.Process", "System.Diagnostics.Tracing", "System.IO.FileSystem.Watcher",
            "System.IO.Pipelines", "System.Linq", "System.Memory", "System.Net.Http", "System.Net.NameResolution",
            "System.Net.Primitives", "System.Net.Quic", "System.Net.Security", "System.Net.Sockets",
            "System.Net.WebSockets", "System.Numerics.Vectors", "System.ObjectModel", "System.Private.CoreLib",
            "System.Private.Uri", "System.Reflection.Emit.ILGeneration", "System.Reflection.Emit.Lightweight",
            "System.Reflection.Primitives", "System.Runtime", "System.Runtime.InteropServices",
            "System.Runtime.Intrinsics", "System.Security.Claims", "System.Security.Cryptography",
            "System.Text.Encoding.Extensions", "System.Text.Encodings.Web", "System.Text.Json", "System.Threading",
            "System.Threading.Channels", "System.Threading.Thread", "System.Threading.ThreadPool", "WebMcp",
        };

        private static readonly string[] McpServerPackages =
        {
            "Microsoft.EntityFrameworkCore.Sqlite", "Microsoft.Data.Sqlite", "Serilog", "Serilog.AspNetCore",
            "OpenTelemetry", "OpenTelemetry.Extensions.Hosting", "Polly.Core", "Newtonsoft.Json",
        };
    }
}
