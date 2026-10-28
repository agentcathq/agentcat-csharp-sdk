using AgentCat.Internal.Errors;

namespace AgentCat.Tests.Errors;

public class StackPathsTests
{
    [Theory]
    [InlineData("/Users/john/project/src/index.ts", "src/index.ts")]
    [InlineData("/home/ubuntu/app/src/server.ts", "src/server.ts")]
    [InlineData("C:\\Users\\Jane\\projects\\myapp\\src\\index.ts", "src/index.ts")]
    [InlineData("/Users/john/project/node_modules/express/lib/router.js", "node_modules/express/lib/router.js")]
    [InlineData("/app/node_modules/@scope/package/dist/index.js", "node_modules/@scope/package/dist/index.js")]
    [InlineData("/app/node_modules/pkg1/node_modules/pkg2/index.js", "node_modules/pkg2/index.js")]
    [InlineData("C:\\projects\\app\\node_modules\\lodash\\index.js", "node_modules/lodash/index.js")]
    [InlineData("/var/www/myapp/src/api/users.ts", "src/api/users.ts")]
    [InlineData("/app/dist/server.js", "dist/server.js")]
    [InlineData("/var/task/src/handler.ts", "src/handler.ts")]
    [InlineData("/usr/src/app/src/index.ts", "src/index.ts")]
    [InlineData("/opt/myservice/lib/main.ts", "lib/main.ts")]
    [InlineData("/srv/webapp/src/routes.ts", "src/routes.ts")]
    [InlineData("/some/long/path/to/project/src/components/Button.tsx", "src/components/Button.tsx")]
    [InlineData("/random/path/myproject/lib/utils/helper.ts", "lib/utils/helper.ts")]
    [InlineData("/deployment/path/dist/server.js", "dist/server.js")]
    [InlineData("/Users/john/src/project/src/index.ts", "src/index.ts")]
    [InlineData("node:internal/modules/cjs/loader", "node:internal")]
    [InlineData("node:fs/promises", "node:fs")]
    [InlineData("node:process", "node:process")]
    [InlineData("C:\\projects\\myapp\\src\\utils\\helper.ts", "src/utils/helper.ts")]
    [InlineData("C:\\projects/myapp\\src/index.ts", "src/index.ts")]
    [InlineData("/Users/dev/project/src/api/handler.ts", "src/api/handler.ts")]
    [InlineData("/app/dist/api/users.ts", "dist/api/users.ts")]
    [InlineData("/opt/nodejs/node_modules/aws-sdk/lib/service.js", "node_modules/aws-sdk/lib/service.js")]
    [InlineData("file:///Users/john/project/src/index.ts", "src/index.ts")]
    [InlineData("file:///C:/Users/Jane/project/src/index.ts", "src/index.ts")]
    [InlineData("file:///Users/john/project/node_modules/express/lib/router.js", "node_modules/express/lib/router.js")]
    [InlineData("src/index.ts", "src/index.ts")]
    [InlineData("native", "native")]
    [InlineData("<unknown>", "<unknown>")]
    [InlineData("/random/path/without/markers/file.ts", "random/path/without/markers/file.ts")]
    [InlineData("/index.ts", "index.ts")]
    [InlineData("/Users/john/long/path/to/project/src/index.ts", "src/index.ts")]
    [InlineData("C:\\Users\\Bob\\proj\\x.js", "~/proj/x.js")]
    [InlineData("file:///C:/Users/Bob/proj/x.js", "C:/Users/Bob/proj/x.js")]
    [InlineData("/home/bob/proj/x.js", "~/proj/x.js")]
    [InlineData("/home/bob/my proj/src/x.js", "src/x.js")]
    [InlineData("https://deno.land/x/mod.ts", "https://deno.land/x/mod.ts")]
    [InlineData("/_/src/MyServer/Tools.cs", "src/MyServer/Tools.cs")]
    [InlineData("D:\\a\\repo\\src\\Tools.cs", "src/Tools.cs")]
    [InlineData("/home/u/.nuget/packages/foo/1.0.0/lib/Foo.cs", ".nuget/packages/foo/1.0.0/lib/Foo.cs")]
    [InlineData("C:\\Users\\u\\.nuget\\packages\\foo\\1.0.0\\lib\\Foo.cs", ".nuget/packages/foo/1.0.0/lib/Foo.cs")]
    public void Relative_path_matches_golden_table(string raw, string expected)
    {
        Assert.Equal(expected, StackPaths.MakeRelativePath(raw, null));
    }

    [Fact]
    public void Current_directory_prefix_is_stripped()
    {
        Assert.Equal("Tools.cs", StackPaths.MakeRelativePath("/work/proj/Tools.cs", "/work/proj"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Missing_current_directory_is_ignored(string? cwd)
    {
        Assert.Equal("work/proj/Tools.cs", StackPaths.MakeRelativePath("/work/proj/Tools.cs", cwd));
    }

    [Fact]
    public void Current_directory_longer_than_the_path_does_not_throw()
    {
        Assert.Equal(string.Empty, StackPaths.MakeRelativePath("/work", "/work"));
    }

    [Fact]
    public void Bare_tilde_does_not_throw()
    {
        Assert.Equal("~", StackPaths.MakeRelativePath("~", null));
    }

    [Theory]
    [InlineData("/Users/john/project/src/index.ts", true)]
    [InlineData("C:\\projects\\app\\src\\Tools.cs", true)]
    [InlineData("src/index.ts", true)]
    [InlineData("https://deno.land/x/mod.ts", true)]
    [InlineData("/_/src/MyServer/Tools.cs", true)]
    [InlineData("/Users/john/project/node_modules/express/lib/router.js", false)]
    [InlineData("C:\\projects\\app\\node_modules\\lodash\\index.js", false)]
    [InlineData("file:///Users/john/project/node_modules/express/lib/router.js", false)]
    [InlineData("node:internal/modules/cjs/loader", false)]
    [InlineData("node:fs", false)]
    [InlineData("native", false)]
    [InlineData("<unknown>", false)]
    [InlineData("/home/u/.nuget/packages/foo/1.0.0/lib/Foo.cs", false)]
    [InlineData("C:\\Users\\u\\.nuget\\packages\\foo\\1.0.0\\lib\\Foo.cs", false)]
    [InlineData("/usr/share/dotnet/shared/Microsoft.NETCore.App/10.0.0/x.cs", false)]
    [InlineData("C:\\Program Files\\dotnet\\shared\\Microsoft.NETCore.App\\10.0.0\\x.cs", false)]
    [InlineData("dotnet/shared/Microsoft.NETCore.App/10.0.0/x.cs", false)]
    public void In_app_rules(string path, bool expected)
    {
        Assert.Equal(expected, StackPaths.IsInAppPath(path));
    }

    [Fact]
    public void Missing_path_is_not_in_app()
    {
        Assert.False(StackPaths.IsInAppPath(null));
    }
}
