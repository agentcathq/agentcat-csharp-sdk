using System.Text;
using AgentCat.Internal.Errors;

namespace AgentCat.Tests.Errors;

public class AppLibrariesTests
{
    internal const string DepsJson = """
        {
          "runtimeTarget": { "name": ".NETCoreApp,Version=v8.0", "signature": "" },
          "compilationOptions": {},
          "targets": {
            ".NETCoreApp,Version=v8.0": {
              "Contoso.Server/1.0.0": {
                "dependencies": { "Microsoft.Contoso.Tools": "1.0.0", "Newtonsoft.Json": "13.0.3" },
                "runtime": { "Contoso.Server.dll": {} }
              },
              "Microsoft.Contoso.Tools/1.0.0": { "runtime": { "Microsoft.Contoso.Tools.dll": {} } },
              "Contoso.Renamed/1.0.0": { "runtime": { "Contoso.RenamedAssembly.dll": {} } },
              "Newtonsoft.Json/13.0.3": {
                "runtime": { "lib/net6.0/Newtonsoft.Json.dll": { "assemblyVersion": "13.0.0.0", "fileVersion": "13.0.3.27908" } }
              },
              "SQLitePCLRaw.bundle_e_sqlite3/2.1.6": {
                "runtime": { "lib/netstandard2.0/SQLitePCLRaw.batteries_v2.dll": {} }
              },
              "Contoso.Windows/2.0.0": {
                "runtimeTargets": { "runtimes/win/lib/net8.0/Contoso.Windows.Interop.dll": { "rid": "win", "assetType": "runtime" } }
              },
              "AgentCat/0.1.0": { "runtime": { "AgentCat.dll": {} } },
              "AgentCat.Api/0.0.3": { "runtime": { "lib/net8.0/AgentCat.Api.dll": {} } },
              "Legacy/1.0.0.0": { "runtime": { "Legacy.dll": {} } }
            }
          },
          "libraries": {
            "Contoso.Server/1.0.0": { "type": "project", "serviceable": false, "sha512": "" },
            "Microsoft.Contoso.Tools/1.0.0": { "type": "project", "serviceable": false, "sha512": "" },
            "Contoso.Renamed/1.0.0": { "type": "project", "serviceable": false, "sha512": "" },
            "Newtonsoft.Json/13.0.3": {
              "type": "package",
              "serviceable": true,
              "sha512": "sha512-HrC5BXdl00IP9zeV+0Z848QWPAoCr9P3bDEZguI+gkLcBKAOxix/tLEAAHC+UvDNPv4a2d18lOReHMOagPa+zQ==",
              "path": "newtonsoft.json/13.0.3",
              "hashPath": "newtonsoft.json.13.0.3.nupkg.sha512"
            },
            "SQLitePCLRaw.bundle_e_sqlite3/2.1.6": { "type": "package", "serviceable": true, "sha512": "" },
            "Contoso.Windows/2.0.0": { "type": "package", "serviceable": true, "sha512": "" },
            "AgentCat/0.1.0": { "type": "project", "serviceable": false, "sha512": "" },
            "AgentCat.Api/0.0.3": { "type": "package", "serviceable": true, "sha512": "" },
            "Legacy/1.0.0.0": { "type": "reference", "serviceable": false, "sha512": "" }
          }
        }
        """;

    [Theory]
    [InlineData("Contoso.Server", "Project")]
    [InlineData("Microsoft.Contoso.Tools", "Project")]
    [InlineData("Contoso.RenamedAssembly", "Project")]
    [InlineData("Contoso.Renamed", "Project")]
    [InlineData("contoso.server", "Project")]
    [InlineData("Newtonsoft.Json", "Package")]
    [InlineData("SQLitePCLRaw.batteries_v2", "Package")]
    [InlineData("Contoso.Windows.Interop", "Package")]
    [InlineData("AgentCat", "Project")]
    [InlineData("AgentCat.Api", "Package")]
    [InlineData("Legacy", "Unlisted")]
    [InlineData("System.Private.CoreLib", "Unlisted")]
    [InlineData(null, "Unlisted")]
    public void Assemblies_are_classified_by_their_deps_json_library_type(string? assemblyName, string expected)
    {
        Assert.Equal(expected, AppLibraries.Parse(DepsJson).KindOf(assemblyName).ToString());
    }

    [Fact]
    public void A_project_keeps_its_assembly_name_when_a_package_lists_the_same_name()
    {
        const string json = """
            {
              "targets": { "t": {
                "Shared.Package/1.0.0": { "runtime": { "lib/net8.0/Contoso.Shared.dll": {} } },
                "Contoso.Shared/1.0.0": { "runtime": { "Contoso.Shared.dll": {} } }
              } },
              "libraries": {
                "Shared.Package/1.0.0": { "type": "package" },
                "Contoso.Shared/1.0.0": { "type": "project" }
              }
            }
            """;

        Assert.Equal(LibraryKind.Project, AppLibraries.Parse(json).KindOf("Contoso.Shared"));
    }

    [Fact]
    public void Reads_the_app_deps_file_listed_first()
    {
        var path = Path.GetTempFileName();
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes(DepsJson));
        try
        {
            var libraries = AppLibraries.FromDepsFiles(path + ";/usr/share/dotnet/shared/Microsoft.NETCore.App/8.0.0/Microsoft.NETCore.App.deps.json;");

            Assert.Equal(LibraryKind.Package, libraries.KindOf("Newtonsoft.Json"));
            Assert.Equal(LibraryKind.Project, libraries.KindOf("Contoso.Server"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(";")]
    [InlineData("/no/such/app.deps.json")]
    public void Missing_deps_files_list_nothing(string? depsFiles)
    {
        Assert.Equal(LibraryKind.Unlisted, AppLibraries.FromDepsFiles(depsFiles).KindOf("Newtonsoft.Json"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{\"libraries\":[]}")]
    [InlineData("{\"libraries\":{\"Newtonsoft.Json/13.0.3\":{\"type\":5}},\"targets\":7}")]
    [InlineData("{\"libraries\":{\"Newtonsoft.Json/13.0.3\":{\"type\":\"package\"}")]
    public void Malformed_deps_json_lists_nothing_and_does_not_throw(string json)
    {
        Assert.Equal(LibraryKind.Unlisted, AppLibraries.Parse(json).KindOf("Newtonsoft.Json"));
    }

    [Fact]
    public void Unreadable_deps_file_lists_nothing()
    {
        var path = Path.GetTempFileName();
        File.WriteAllBytes(path, new byte[] { 0xFF, 0xFE, 0x00 });
        try
        {
            Assert.Equal(LibraryKind.Unlisted, AppLibraries.FromDepsFiles(path).KindOf("Newtonsoft.Json"));
        }
        finally
        {
            File.Delete(path);
        }
    }

#if NET
    [Fact]
    public void The_test_host_deps_json_lists_the_test_project_and_its_packages()
    {
        var libraries = AppLibraries.Current;

        Assert.Same(libraries, AppLibraries.Current);
        Assert.Equal(LibraryKind.Project, libraries.KindOf("AgentCat.Tests"));
        Assert.Equal(LibraryKind.Project, libraries.KindOf("AgentCat"));
        Assert.Equal(LibraryKind.Package, libraries.KindOf("xunit.v3.assert"));
        Assert.Equal(LibraryKind.Unlisted, libraries.KindOf("System.Private.CoreLib"));
    }
#else
    [Fact]
    public void NET_Framework_hosts_have_no_deps_json_and_list_nothing()
    {
        Assert.Same(AppLibraries.Current, AppLibraries.Current);
        Assert.Equal(LibraryKind.Unlisted, AppLibraries.Current.KindOf("xunit.v3.assert"));
    }
#endif
}
