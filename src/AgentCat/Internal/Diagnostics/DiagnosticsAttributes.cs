using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using AgentCat.Internal.Logging;
using AgentCat.Internal.Text;

namespace AgentCat.Internal.Diagnostics;

/// <summary>The OTLP resource attributes and per-record attributes of the SDK diagnostics. Empty values are left out.</summary>
internal static class DiagnosticsAttributes
{
    private const string Unknown = "unknown";

    internal static IReadOnlyList<KeyValuePair<string, string>> Resource(string? projectId)
        => Resource(projectId, Environment.GetEnvironmentVariable);

    internal static IReadOnlyList<KeyValuePair<string, string>> Resource(string? projectId, Func<string, string?> environment)
    {
        var list = new List<KeyValuePair<string, string>>();
        if (!string.IsNullOrEmpty(projectId))
        {
            Add(list, "agentcat.project_id", projectId);
        }
        else
        {
            Add(list, "agentcat.install_id", Safe(ComputeInstallId));
        }

        Add(list, "agentcat.sdk.language", "csharp");
        Add(list, "agentcat.sdk.version", Known(SdkInfo.Version));
        Add(list, "agentcat.mcp_sdk.version", Known(LogLine.McpVersion));
        Add(list, "process.runtime.name", "dotnet");
        Add(list, "process.runtime.version", Known(SdkInfo.RuntimeVersion));
        Add(list, "process.pid", Safe(ProcessId));
        Add(list, "os.type", Safe(OsType));
        Add(list, "os.version", Safe(static () => Environment.OSVersion.Version.ToString()));
        Add(list, "host.arch", Safe(HostArch));
        Add(list, "host.cpu.count", Safe(static () => Environment.ProcessorCount.ToString(CultureInfo.InvariantCulture)));
        Add(list, "deployment.environment", Safe(() => DeploymentEnvironment(environment)));
        return list;
    }

    internal static IReadOnlyList<KeyValuePair<string, string>> Record()
    {
        var list = new List<KeyValuePair<string, string>>();
        Add(list, "agentcat.sdk.version", Known(SdkInfo.Version));
        Add(list, "process.runtime.version", Known(SdkInfo.RuntimeVersion));
        Add(list, "agentcat.mcp_sdk.version", Known(LogLine.McpVersion));
        return list;
    }

    /// <summary>The first 16 hex characters of SHA-256 of <c>machine|file://baseDirectory</c>.</summary>
    internal static string InstallId(string machineName, string baseDirectory)
        => Hex.Lower(Crypto.Sha256Utf8(machineName + "|file://" + baseDirectory)).Substring(0, 16);

    private static string? ComputeInstallId() => InstallId(Environment.MachineName, AppContext.BaseDirectory);

    private static string? Known(string value) => value == Unknown ? null : value;

    private static string? ProcessId()
    {
#if NET
        return Environment.ProcessId.ToString(CultureInfo.InvariantCulture);
#else
        using var process = Process.GetCurrentProcess();
        return process.Id.ToString(CultureInfo.InvariantCulture);
#endif
    }

    private static string? OsType()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return "linux";
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return "darwin";
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return "win32";
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Create("FREEBSD")))
        {
            return "freebsd";
        }

        var description = RuntimeInformation.OSDescription.Trim();
        var space = description.IndexOf(' ');
        return (space < 0 ? description : description.Substring(0, space)).ToLowerInvariant();
    }

    private static string? HostArch() => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X64 => "x64",
        Architecture.Arm64 => "arm64",
        Architecture.X86 => "ia32",
        Architecture.Arm => "arm",
        var other => other.ToString().ToLowerInvariant(),
    };

    private static string? DeploymentEnvironment(Func<string, string?> environment)
    {
        var value = environment("DOTNET_ENVIRONMENT");
        return string.IsNullOrEmpty(value) ? environment("ASPNETCORE_ENVIRONMENT") : value;
    }

    private static string? Safe(Func<string?> read)
    {
        try
        {
            return read();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void Add(List<KeyValuePair<string, string>> list, string key, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            list.Add(new KeyValuePair<string, string>(key, value!));
        }
    }
}
