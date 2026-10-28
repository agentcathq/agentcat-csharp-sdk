using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;

namespace AgentCat.Internal.Runtime;

/// <summary>Whether this process runs automated tests: a test framework is loaded or ships with the process.</summary>
internal static class TestEnvironment
{
    // xUnit v2, xUnit v3, NUnit, MSTest 4, MSTest 3 and earlier, TUnit. Platform and host assemblies such as
    // Microsoft.Testing.Platform or Microsoft.AspNetCore.TestHost are no signal: production hosts load them too.
    private static readonly string[] Frameworks =
    {
        "xunit.core",
        "xunit.v3.core",
        "nunit.framework",
        "MSTest.TestFramework",
        "Microsoft.VisualStudio.TestPlatform.TestFramework",
        "TUnit.Core",
    };

    private static readonly Func<bool> Live = CacheTrue(DetectLive);

    /// <summary>Detects with the live process inputs. A true result is kept for the process; a false one is not.</summary>
    internal static bool IsTestRun() => Live();

    // A false result can come before the test framework loads, so only a true result is kept.
    internal static Func<bool> CacheTrue(Func<bool> detect)
    {
        var found = 0;
        return () =>
        {
            if (Volatile.Read(ref found) != 0)
            {
                return true;
            }

            var hit = detect();
            if (hit)
            {
                Volatile.Write(ref found, 1);
            }

            return hit;
        };
    }

    /// <summary>
    /// True when the entry assembly is <c>testhost</c> or <c>testhost.*</c>, when a loaded assembly is a test
    /// framework, or when a file of the trusted platform assemblies list is one (ordinal names).
    /// </summary>
    internal static bool Detect(string? entryAssemblyName, IEnumerable<string?> loadedAssemblyNames, string? trustedPlatformAssemblies)
    {
        if (entryAssemblyName is not null
            && (entryAssemblyName == "testhost" || entryAssemblyName.StartsWith("testhost.", StringComparison.Ordinal)))
        {
            return true;
        }

        foreach (var name in loadedAssemblyNames)
        {
            if (name is not null && Array.IndexOf(Frameworks, name) >= 0)
            {
                return true;
            }
        }

        return trustedPlatformAssemblies is not null && ListsFramework(trustedPlatformAssemblies);
    }

    private static bool DetectLive()
    {
        try
        {
            return Detect(NameOf(Assembly.GetEntryAssembly()), LoadedNames(), AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static IEnumerable<string?> LoadedNames()
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            yield return NameOf(assembly);
        }
    }

    // The simple name from the display name, which the runtime keeps; GetName() builds a new AssemblyName per call.
    // The six names have no characters that a display name escapes, so the comparison gives the same answer.
    private static string? NameOf(Assembly? assembly)
    {
        try
        {
            var fullName = assembly?.FullName;
            if (fullName is null)
            {
                return null;
            }

            var comma = fullName.IndexOf(',');
            return comma < 0 ? fullName : fullName.Substring(0, comma);
        }
        catch (Exception)
        {
            return null;
        }
    }

    // The file name without its extension of each path, as Path.GetFileNameWithoutExtension reads it.
    private static bool ListsFramework(string paths)
    {
        var start = 0;
        while (start <= paths.Length)
        {
            var end = paths.IndexOf(Path.PathSeparator, start);
            if (end < 0)
            {
                end = paths.Length;
            }

            var nameStart = start;
            for (var i = end - 1; i >= start; i--)
            {
                if (paths[i] == Path.DirectorySeparatorChar || paths[i] == Path.AltDirectorySeparatorChar)
                {
                    nameStart = i + 1;
                    break;
                }
            }

            var nameEnd = end;
            for (var i = end - 1; i >= nameStart; i--)
            {
                if (paths[i] == '.')
                {
                    nameEnd = i;
                    break;
                }
            }

            var length = nameEnd - nameStart;
            foreach (var framework in Frameworks)
            {
                if (framework.Length == length && string.CompareOrdinal(paths, nameStart, framework, 0, length) == 0)
                {
                    return true;
                }
            }

            start = end + 1;
        }

        return false;
    }
}
