using System.Reflection;
using System.Runtime.InteropServices;

namespace AgentCat.Internal;

internal static class SdkInfo
{
    internal const string Language = "C#";

    internal static string Version { get; } = ReadVersion();

    internal static string RuntimeVersion { get; } = ReadRuntimeVersion();

    internal static string UserAgent { get; } = "agentcat-csharp/" + Version + " dotnet/" + RuntimeVersion;

    private static string ReadVersion()
    {
        var attribute = typeof(AgentCatAssemblyMarker).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
        var version = attribute?.InformationalVersion;
        if (string.IsNullOrEmpty(version))
        {
            return "unknown";
        }

        var plus = version!.IndexOf('+');
        return plus >= 0 ? version.Substring(0, plus) : version;
    }

    private static string ReadRuntimeVersion()
    {
        var description = RuntimeInformation.FrameworkDescription;
        var space = description.LastIndexOf(' ');
        return space >= 0 ? description.Substring(space + 1) : description;
    }
}
