using System;

namespace AgentCat.Internal.Errors;

internal static class StackPaths
{
    private const string NodeModulesMarker = "/node_modules/";
    private const string NuGetMarker = "/.nuget/packages/";
    private const string DotnetSharedMarker = "dotnet/shared/";

    private static readonly string[] PrimaryMarkers = { "/src/", "/lib/", "/dist/", "/build/" };

    private static readonly string[] SecondaryMarkers =
    {
        "/app/", "/components/", "/pages/", "/api/", "/utils/", "/services/", "/modules/",
    };

    internal static string MakeRelativePath(string path, string? currentDirectory)
    {
        var result = NormalizeUrl(path);

        if (!Starts(result, '/') && !StartsWithDriveBackslash(result))
        {
            return result.StartsWith("node:", StringComparison.Ordinal) ? NormalizeNodeInternals(result) : result;
        }

        result = result.Replace('\\', '/');

        var nodeModulesIndex = result.LastIndexOf(NodeModulesMarker, StringComparison.Ordinal);
        if (nodeModulesIndex != -1)
        {
            return result.Substring(nodeModulesIndex + 1);
        }

        var nuGetIndex = result.LastIndexOf(NuGetMarker, StringComparison.Ordinal);
        if (nuGetIndex != -1)
        {
            return result.Substring(nuGetIndex + 1);
        }

        result = StripSystemPrefixes(result);
        result = StripDeploymentPaths(result);

        if (currentDirectory is { Length: > 0 } cwd && result.StartsWith(cwd, StringComparison.Ordinal))
        {
            result = SafeSubstring(result, cwd.Length + 1);
        }

        if (Starts(result, '/') || StartsWithDriveSlash(result))
        {
            result = FindProjectPath(result);
        }
        else if (Starts(result, '~'))
        {
            var withoutTilde = "/" + SafeSubstring(result, 2);
            var projectPath = FindProjectPath(withoutTilde);
            if (!string.Equals(projectPath, withoutTilde, StringComparison.Ordinal))
            {
                result = projectPath;
            }
        }

        if (Starts(result, '/'))
        {
            result = result.Substring(1);
        }

        return result;
    }

    internal static bool IsInAppPath(string? path)
    {
        if (path is null)
        {
            return false;
        }

        if (path.Contains(NodeModulesMarker) || path.Contains("\\node_modules\\"))
        {
            return false;
        }

        if (path.Contains(NuGetMarker) || path.Contains("\\.nuget\\packages\\"))
        {
            return false;
        }

        if (ContainsSharedRuntimeFolder(path))
        {
            return false;
        }

        if (path.StartsWith("node:", StringComparison.Ordinal))
        {
            return false;
        }

        return !string.Equals(path, "native", StringComparison.Ordinal)
            && !string.Equals(path, "<unknown>", StringComparison.Ordinal);
    }

    private static bool ContainsSharedRuntimeFolder(string path)
    {
        var normalized = path.Replace('\\', '/');
        var index = normalized.IndexOf(DotnetSharedMarker, StringComparison.OrdinalIgnoreCase);
        return index != -1 && (index == 0 || normalized[index - 1] == '/');
    }

    private static string NormalizeUrl(string path)
    {
        if (!path.StartsWith("file://", StringComparison.Ordinal))
        {
            return path;
        }

        var result = path.Substring(7);
        if (!Starts(result, '/') && !StartsWithDriveLetter(result))
        {
            result = "/" + result;
        }

        return result;
    }

    private static string NormalizeNodeInternals(string path)
    {
        if (path.StartsWith("node:internal", StringComparison.Ordinal))
        {
            return "node:internal";
        }

        var slash = path.IndexOf('/');
        return slash == -1 ? path : path.Substring(0, slash);
    }

    private static string StripSystemPrefixes(string path)
    {
        path = StripHomePrefix(path, "/Users/");
        path = StripHomePrefix(path, "/home/");

        if (path.Length > 3 && IsAsciiLetter(path[0]) && path[1] == ':' && IsSeparator(path[2])
            && string.CompareOrdinal(path, 3, "Users", 0, 5) == 0
            && path.Length > 8 && IsSeparator(path[8]))
        {
            var end = IndexOfSeparator(path, 9);
            if (end > 9)
            {
                return "~/" + path.Remove(0, end + 1);
            }
        }

        return path;
    }

    private static string StripHomePrefix(string path, string prefix)
    {
        if (path.StartsWith(prefix, StringComparison.Ordinal))
        {
            var end = path.IndexOf('/', prefix.Length);
            if (end > prefix.Length)
            {
                return "~/" + path.Remove(0, end + 1);
            }
        }

        return path;
    }

    private static string StripDeploymentPaths(string path)
    {
        path = StripPrefix(path, "/var/www/", true);
        path = StripPrefix(path, "/var/task/", false);
        path = StripPrefix(path, "/usr/src/app/", false);
        path = StripPrefix(path, "/app/", false);
        path = StripPrefix(path, "/opt/", true);
        path = StripPrefix(path, "/srv/", true);
        path = StripPrefix(path, "/_/", false);
        return path;
    }

    private static string StripPrefix(string path, string prefix, bool withSegment)
    {
        if (!path.StartsWith(prefix, StringComparison.Ordinal))
        {
            return path;
        }

        if (!withSegment)
        {
            return path.Substring(prefix.Length);
        }

        var end = path.IndexOf('/', prefix.Length);
        return end > prefix.Length ? path.Substring(end + 1) : path;
    }

    private static string FindProjectPath(string path)
    {
        foreach (var marker in PrimaryMarkers)
        {
            var index = path.LastIndexOf(marker, StringComparison.Ordinal);
            if (index != -1)
            {
                return path.Substring(index + 1);
            }
        }

        foreach (var marker in SecondaryMarkers)
        {
            var index = path.LastIndexOf(marker, StringComparison.Ordinal);
            if (index != -1)
            {
                return path.Substring(index + 1);
            }
        }

        return path;
    }

    private static string SafeSubstring(string value, int start) =>
        start >= value.Length ? string.Empty : value.Substring(start);

    private static bool Starts(string value, char c) => value.Length > 0 && value[0] == c;

    private static bool IsAsciiLetter(char c) => (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z');

    private static bool IsSeparator(char c) => c == '/' || c == '\\';

    private static int IndexOfSeparator(string value, int start)
    {
        for (var i = start; i < value.Length; i++)
        {
            if (IsSeparator(value[i]))
            {
                return i;
            }
        }

        return -1;
    }

    private static bool StartsWithDriveLetter(string value) =>
        value.Length >= 2 && IsAsciiLetter(value[0]) && value[1] == ':';

    private static bool StartsWithDriveBackslash(string value) =>
        value.Length >= 3 && StartsWithDriveLetter(value) && value[2] == '\\';

    private static bool StartsWithDriveSlash(string value) =>
        value.Length >= 3 && StartsWithDriveLetter(value) && value[2] == '/';
}
