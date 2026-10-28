using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCat.Internal.Errors;

namespace AgentCat.Tests.Errors;

public class ExceptionCaptureTests
{
    private const string TestsModule = "AgentCat.Tests.Errors.ExceptionCaptureTests";
    private const string ThrowersModule = TestsModule + "+Throwers";

    private static readonly string[] ErrorKeys = { "message", "type", "platform", "stack", "frames" };
    private static readonly string[] ChainedErrorKeys = { "message", "type", "platform", "stack", "frames", "chained_errors" };
    private static readonly string[] ChainEntryKeys = { "message", "type", "stack", "frames" };
    private static readonly string[] ResultKeys = { "message", "platform" };

    private static readonly string[] FrameKeyOrder =
    {
        "function", "module", "filename", "abs_path", "lineno", "colno", "in_app", "context_line",
    };

    [Fact]
    public void Captures_message_type_stack_and_platform()
    {
        var e = Catch(() => Throwers.Throw("Test error message"));

        var error = ExceptionCapture.Capture(e);

        Assert.Equal("Test error message", Text(error["message"]));
        Assert.Equal("System.InvalidOperationException", Text(error["type"]));
        Assert.Equal(e.ToString(), Text(error["stack"]));
        Assert.Equal("csharp", Text(error["platform"]));
        Assert.NotEmpty(Frames(error));
        Assert.Equal(ErrorKeys, error.Select(p => p.Key));
    }

    [Fact]
    public void Nested_and_generic_exception_types_use_their_full_names()
    {
        var nested = ExceptionCapture.Capture(Catch(Throwers.ThrowNested));
        var generic = ExceptionCapture.Capture(Catch(Throwers.ThrowGeneric));

        Assert.Equal(TestsModule + "+NestedTestException", Text(nested["type"]));
        Assert.Equal(typeof(NestedTestException).FullName, Text(nested["type"]));
        Assert.Equal(TestsModule + "+GenericTestException`1[System.Int32]", Text(generic["type"]));
    }

    [Fact]
    public void Empty_message_is_kept_and_type_is_still_set()
    {
        var error = ExceptionCapture.Capture(Catch(() => Throwers.Throw(string.Empty)));

        Assert.Equal(string.Empty, Text(error["message"]));
        Assert.Equal("System.InvalidOperationException", Text(error["type"]));
    }

    [Fact]
    public void Frames_are_innermost_first_with_the_TS_frame_fields()
    {
        var e = Catch(() => Throwers.Throw("x"));
        var expected = new StackTrace(e, true).GetFrame(0)!;

        var first = Frames(ExceptionCapture.Capture(e))[0]!.AsObject();

        Assert.Equal("Throw", Text(first["function"]));
        Assert.Equal(ThrowersModule, Text(first["module"]));
        Assert.True(first["in_app"]!.GetValue<bool>());
        var absPath = expected.GetFileName();
        Assert.False(string.IsNullOrEmpty(absPath));
        Assert.Equal(absPath, Text(first["abs_path"]));
        Assert.Equal(StackPaths.MakeRelativePath(absPath!, CurrentDirectory()), Text(first["filename"]));
        AssertOptionalNumber(first, "lineno", expected.GetFileLineNumber());
        AssertOptionalNumber(first, "colno", expected.GetFileColumnNumber());
    }

    [Fact]
    public void Every_frame_has_string_filename_and_function_and_TS_key_order()
    {
        var e = Catch(Throwers.ReadMissingFile);

        var frames = Frames(ExceptionCapture.Capture(e));

        Assert.NotEmpty(frames);
        foreach (var node in frames)
        {
            var frame = node!.AsObject();
            Assert.False(string.IsNullOrEmpty(Text(frame["function"])));
            Assert.False(string.IsNullOrEmpty(Text(frame["filename"])));
            Assert.Contains(frame["in_app"]!.GetValueKind(), new[] { JsonValueKind.True, JsonValueKind.False });
            var keys = frame.Select(p => p.Key).ToList();
            Assert.Equal(FrameKeyOrder.Where(keys.Contains), keys);
            foreach (var key in new[] { "module", "abs_path", "context_line" })
            {
                if (frame.ContainsKey(key))
                {
                    Assert.Equal(JsonValueKind.String, frame[key]!.GetValueKind());
                }
            }

            foreach (var key in new[] { "lineno", "colno" })
            {
                if (frame.ContainsKey(key))
                {
                    Assert.True(frame[key]!.GetValue<int>() > 0);
                }
            }
        }
    }

    [Fact]
    public void Framework_and_SDK_frames_are_not_in_app()
    {
        var framework = Frames(ExceptionCapture.Capture(Catch(Throwers.ReadMissingFile)));
        var sdk = Frames(ExceptionCapture.Capture(Catch(Throwers.FailInsideTheSdk)));

        var frameworkFrames = framework.Where(f => ModuleOf(f).StartsWith("System.", StringComparison.Ordinal)).ToList();
        var sdkFrames = sdk.Where(f => ModuleOf(f).StartsWith("AgentCat.Internal.", StringComparison.Ordinal)).ToList();
        var ownFrames = framework.Concat(sdk).Where(f => ModuleOf(f).StartsWith(TestsModule, StringComparison.Ordinal)).ToList();
        var otherFrames = framework.Concat(sdk).Where(f => !ModuleOf(f).StartsWith(TestsModule, StringComparison.Ordinal)).ToList();

        Assert.NotEmpty(frameworkFrames);
        Assert.NotEmpty(sdkFrames);
        Assert.NotEmpty(ownFrames);
        Assert.All(otherFrames, f =>
        {
            Assert.False(f!["in_app"]!.GetValue<bool>());
            Assert.False(f.AsObject().ContainsKey("context_line"));
        });
        Assert.All(ownFrames, f => Assert.True(f!["in_app"]!.GetValue<bool>()));
    }

    [Fact]
    public void Package_frames_are_not_in_app_and_project_frames_are()
    {
        var frames = Frames(ExceptionCapture.Capture(Catch(() => Assert.Equal(1, 2))));

        var xunitFrames = frames.Where(f => ModuleOf(f).StartsWith("Xunit.", StringComparison.Ordinal)).ToList();
        var ownFrames = frames.Where(f => ModuleOf(f).StartsWith(TestsModule, StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(xunitFrames);
        Assert.NotEmpty(ownFrames);
        Assert.All(ownFrames, f => Assert.True(f!["in_app"]!.GetValue<bool>()));
#if NET
        Assert.All(xunitFrames, f => Assert.False(f!["in_app"]!.GetValue<bool>()));
#endif
    }

    [Theory]
    [InlineData("Contoso.Server.Tools", "Contoso.Server", true)]
    [InlineData("Microsoft.Contoso.Tools.Weather", "Microsoft.Contoso.Tools", true)]
    [InlineData("System.Text.RegularExpressions.Generated.Regex1", "Contoso.Server", true)]
    [InlineData("Contoso.Renamed.Jobs", "Contoso.RenamedAssembly", true)]
    [InlineData("Newtonsoft.Json.JsonConvert", "Newtonsoft.Json", false)]
    [InlineData("SQLitePCL.Batteries_V2", "SQLitePCLRaw.batteries_v2", false)]
    [InlineData("Contoso.Windows.Interop.Native", "Contoso.Windows.Interop", false)]
    [InlineData("AgentCat.Internal.Errors.StackPaths", "AgentCat", false)]
    [InlineData("AgentCat.Api.Client.EventsApi", "AgentCat.Api", false)]
    [InlineData("AgentCat.Internal.Events.EventTruncator", null, false)]
    [InlineData("Contoso.Server.Tools", null, true)]
    [InlineData("Legacy.Jobs", "Legacy", true)]
    [InlineData("Microsoft.Legacy.Jobs", "Microsoft.Legacy", false)]
    [InlineData("System.IO.File", "System.Private.CoreLib", false)]
    [InlineData("Contoso.Plugins.Loader", "System.Private.CoreLib", false)]
    [InlineData("Contoso.Plugins.Loader", "netstandard", false)]
    [InlineData(null, "Contoso.Server", false)]
    public void In_app_follows_the_deps_json_library_type_then_the_framework_prefixes(string? module, string? assembly, bool expected)
    {
        var libraries = AppLibraries.Parse(AppLibrariesTests.DepsJson);

        var frame = SingleFrame(new ExceptionCapture.FrameInfo(module, "Run", assembly, "/app/src/Tools.cs", 10, 5), libraries);

        Assert.Equal(expected, frame["in_app"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData("Contoso.Server.Tools", "Contoso.Server", true)]
    [InlineData("Microsoft.Contoso.Tools.Weather", "Microsoft.Contoso.Tools", false)]
    [InlineData("Newtonsoft.Json.JsonConvert", "Newtonsoft.Json", true)]
    [InlineData("System.IO.File", "System.Private.CoreLib", false)]
    [InlineData("ModelContextProtocol.Server.McpServer", "ModelContextProtocol.Core", false)]
    [InlineData("AgentCat.Internal.Errors.StackPaths", "AgentCat", false)]
    [InlineData("AgentCat.Internal.Errors.StackPaths", null, false)]
    public void Without_a_deps_json_in_app_falls_back_to_the_framework_prefixes(string module, string? assembly, bool expected)
    {
        var frame = SingleFrame(new ExceptionCapture.FrameInfo(module, "Run", assembly, "/app/src/Tools.cs", 10, 5), AppLibraries.Empty);

        Assert.Equal(expected, frame["in_app"]!.GetValue<bool>());
    }

    [Fact]
    public void Project_frames_under_a_package_folder_are_not_in_app()
    {
        var libraries = AppLibraries.Parse(AppLibrariesTests.DepsJson);
        var info = new ExceptionCapture.FrameInfo(
            "Contoso.Server.Tools", "Run", "Contoso.Server", "/home/u/.nuget/packages/contoso.server/1.0.0/Tools.cs", 10, 5);

        Assert.False(SingleFrame(info, libraries)["in_app"]!.GetValue<bool>());
    }

    [Fact]
    public void Context_line_is_the_source_line_when_the_source_file_exists()
    {
        var first = Frames(ExceptionCapture.Capture(Catch(() => Throwers.Throw("context"))))[0]!.AsObject();
        var absPath = Text(first["abs_path"]);

        // Deterministic CI builds record /_/ source paths, which do not exist at run time.
        if (File.Exists(absPath))
        {
            var line = File.ReadAllText(absPath).Split('\n')[first["lineno"]!.GetValue<int>() - 1];
            Assert.Equal(line, Text(first["context_line"]));
            Assert.Contains("throw new InvalidOperationException(message)", line);
        }
        else
        {
            Assert.False(first.ContainsKey("context_line"));
        }
    }

    [Fact]
    public void Source_files_are_looked_up_once_per_capture()
    {
        var root = Catch(() => Throwers.Throw("root"));
        var top = Catch(() => Throwers.ThrowWrapped("top", root));
        var sources = new ExceptionCapture.SourceLines();

        var error = ExceptionCapture.Capture(top, sources);

        var inAppFrames = Frames(error).Concat(Frames(error["chained_errors"]![0]!.AsObject()))
            .Where(f => f!["in_app"]!.GetValue<bool>())
            .ToList();
        Assert.True(inAppFrames.Count >= 4);
        Assert.Single(inAppFrames.Select(f => Text(f!["abs_path"])).Distinct());
        Assert.Equal(1, sources.FileReads);
    }

    [Fact]
    public void Context_line_keeps_trailing_spaces_and_carriage_return()
    {
        var path = TempFile("line one\r\n    throw new Error(\"x\");   \r\nlast line");
        try
        {
            var sources = new ExceptionCapture.SourceLines();

            Assert.True(sources.TryGetLine(path, 2, out var line));
            Assert.Equal("    throw new Error(\"x\");   \r", line);
            Assert.True(sources.TryGetLine(path, 3, out var last));
            Assert.Equal("last line", last);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Missing_file_or_line_gives_no_context_line()
    {
        var path = TempFile("only\n");
        try
        {
            var sources = new ExceptionCapture.SourceLines();

            Assert.False(sources.TryGetLine(path + ".missing", 1, out _));
            Assert.False(sources.TryGetLine(path, 0, out _));
            Assert.False(sources.TryGetLine(path, 3, out _));
            Assert.True(sources.TryGetLine(path, 2, out var empty));
            Assert.Equal(string.Empty, empty);
            Assert.Equal(2, sources.FileReads);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Source_files_over_the_size_cap_are_skipped()
    {
        var atCap = TempFile("x" + new string('y', ExceptionCapture.SourceLines.MaxFileBytes - 1));
        var overCap = TempFile(new string('y', ExceptionCapture.SourceLines.MaxFileBytes + 1));
        try
        {
            var sources = new ExceptionCapture.SourceLines();

            Assert.True(sources.TryGetLine(atCap, 1, out var line));
            Assert.Equal(ExceptionCapture.SourceLines.MaxFileBytes, line.Length);
            Assert.False(sources.TryGetLine(overCap, 1, out _));
        }
        finally
        {
            File.Delete(atCap);
            File.Delete(overCap);
        }
    }

    [Fact]
    public async Task Compiler_generated_names_are_resolved()
    {
        // Rule: async and iterator methods and local functions show their own names. A lambda has no name of
        // its own, so it shows the name of the method that contains it (here ThrowFromLambda). The module is
        // the declaring type with compiler-generated nested types (<>c, <>c__DisplayClass, <X>d__N) removed.
        var asyncError = ExceptionCapture.Capture(await CatchAsync(Throwers.ThrowAfterYieldAsync));
        var lambdaError = ExceptionCapture.Capture(Catch(Throwers.ThrowFromLambda));
        var localError = ExceptionCapture.Capture(Catch(Throwers.ThrowFromLocalFunction));

        AssertFirstFrame(asyncError, ThrowersModule, "ThrowAfterYieldAsync");
        AssertFirstFrame(lambdaError, ThrowersModule, "ThrowFromLambda");
        AssertFirstFrame(localError, ThrowersModule, "Fail");
        foreach (var frame in Frames(asyncError).Concat(Frames(lambdaError)).Concat(Frames(localError)))
        {
            if (ModuleOf(frame).StartsWith(TestsModule, StringComparison.Ordinal))
            {
                Assert.DoesNotContain("<", Text(frame!["function"]), StringComparison.Ordinal);
                Assert.DoesNotContain("<", ModuleOf(frame), StringComparison.Ordinal);
                Assert.NotEqual("MoveNext", Text(frame!["function"]));
            }
        }
    }

    [Fact]
    public async Task Other_compiler_generated_shapes_are_resolved()
    {
        AssertFirstFrame(ExceptionCapture.Capture(Catch(Throwers.ThrowFromIterator)), ThrowersModule, "Iterate");
        AssertFirstFrame(ExceptionCapture.Capture(Catch(() => Throwers.ThrowFromCapturingLambda(3))), ThrowersModule, "ThrowFromCapturingLambda");
        AssertFirstFrame(ExceptionCapture.Capture(await CatchAsync(Throwers.ThrowFromAsyncLambda)), ThrowersModule, "ThrowFromAsyncLambda");
        AssertFirstFrame(ExceptionCapture.Capture(await CatchAsync(Throwers.ThrowFromAsyncLocalFunction)), ThrowersModule, "FailAsync");
        AssertFirstFrame(ExceptionCapture.Capture(Catch(() => GC.KeepAlive(new LambdaInConstructor()))), TestsModule + "+LambdaInConstructor", ".ctor");
        AssertFirstFrame(ExceptionCapture.Capture(Catch(() => new GenericThrower<int>().Throw())), TestsModule + "+GenericThrower`1", "Throw");
        AssertFirstFrame(ExceptionCapture.Capture(Catch(() => Throwers.ThrowGenericMethod<string>())), ThrowersModule, "ThrowGenericMethod");
        AssertFirstFrame(ExceptionCapture.Capture(Catch(FileLocalThrower.Throw)), "AgentCat.Tests.Errors.FileLocalThrower", "Throw");
    }

    [Theory]
    [InlineData("Ns.Tools", "GetWeather", "GetWeather")]
    [InlineData("Ns.Tools+<GetWeather>d__3", "MoveNext", "GetWeather")]
    [InlineData("Ns.Tools+<Iterate>d__8", "<>m__Finally1", "Iterate")]
    [InlineData("Ns.Tools+<Run>d__2`1", "MoveNext", "Run")]
    [InlineData("Ns.Tools+<>c", "<Run>b__3_0", "Run")]
    [InlineData("Ns.Tools+<>c__DisplayClass3_0", "<Run>b__0", "Run")]
    [InlineData("Ns.Tools+<>c__DisplayClass3_0`1", "<Run>b__1", "Run")]
    [InlineData("Ns.Tools", "<Run>g__Local|3_0", "Local")]
    [InlineData("Ns.Tools+<>c__DisplayClass3_0", "<Run>g__Local|0", "Local")]
    [InlineData("Ns.Tools+<<Run>g__LocalAsync|3_0>d", "MoveNext", "LocalAsync")]
    [InlineData("Ns.Tools+<>c+<<Run>b__3_0>d", "MoveNext", "Run")]
    [InlineData("Ns.Tools+<>c", "<<Run>g__Local|3_0>b__1", "Local")]
    [InlineData("Ns.Tools+<>c", "<.ctor>b__0_0", ".ctor")]
    [InlineData("Program", "<Main>$", "<Main>$")]
    [InlineData("Program+<>c", "<<Main>$>b__0_0", "<Main>$")]
    [InlineData("Program+<<Main>$>d__0", "MoveNext", "<Main>$")]
    [InlineData("Ns.Tools", ".ctor", ".ctor")]
    [InlineData("Ns.Tools", "get_Name", "get_Name")]
    [InlineData("Ns.Tools", "MoveNext", "MoveNext")]
    [InlineData(null, "lambda_method12", "lambda_method")]
    [InlineData(null, "lambda_method", "lambda_method")]
    [InlineData("Ns.Tools", "Method2", "Method2")]
    [InlineData("Ns.Tools", "Handler10", "Handler10")]
    [InlineData("Ns.Tools", "mylambda_method12", "mylambda_method12")]
    [InlineData("Ns.Tools", "lambda_methodX2", "lambda_methodX2")]
    [InlineData("Ns.Tools", null, "<unknown>")]
    [InlineData(null, "", "<unknown>")]
    public void Function_names_follow_the_source(string? declaringType, string? method, string expected)
    {
        Assert.Equal(expected, ExceptionCapture.FunctionOf(declaringType, method));
    }

    [Fact]
    public void Lambda_method_digits_are_stripped_from_function_and_stack()
    {
        var compiled = System.Linq.Expressions.Expression
            .Lambda<Func<int>>(
                System.Linq.Expressions.Expression.Throw(
                    System.Linq.Expressions.Expression.Constant(new InvalidOperationException("boom")),
                    typeof(int)))
            .Compile();

        var error = ExceptionCapture.Capture(Catch(() => compiled()));

        var stack = error["stack"]!.GetValue<string>();
        Assert.Contains("lambda_method(", stack, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"lambda_method\d", stack);
        var functions = error["frames"]!.AsArray().Select(f => f!["function"]!.GetValue<string>()).ToList();
        Assert.DoesNotContain(functions, f => f.StartsWith("lambda_method", StringComparison.Ordinal) && f != "lambda_method");
        Assert.Contains("lambda_method", functions);
    }

    [Fact]
    public void Other_names_ending_in_digits_are_kept_in_the_stack()
    {
        var error = ExceptionCapture.Capture(Catch(() => Throwers.Method2()));

        Assert.Contains("Method2(", error["stack"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal("Method2", error["frames"]!.AsArray()[0]!["function"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("Ns.Tools", "Ns.Tools")]
    [InlineData("Ns.Outer+Inner", "Ns.Outer+Inner")]
    [InlineData("Ns.Tools+<>c", "Ns.Tools")]
    [InlineData("Ns.Tools+<>c__DisplayClass3_0", "Ns.Tools")]
    [InlineData("Ns.Outer+Inner+<GetWeather>d__3", "Ns.Outer+Inner")]
    [InlineData("Ns.Tools+<>c+<<Run>b__3_0>d", "Ns.Tools")]
    [InlineData("Ns.Box`1", "Ns.Box`1")]
    [InlineData("Ns.Box`1+<Run>d__2`1", "Ns.Box`1")]
    [InlineData("Ns.Box`1[[System.Int32, System.Private.CoreLib, Version=10.0.0.0, Culture=neutral, PublicKeyToken=7cec85d7bea7798e]]", "Ns.Box`1")]
    [InlineData("Ns.Box`1+Inner[[Ns.Outer+Inner, App, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null]]", "Ns.Box`1+Inner")]
    [InlineData("Ns.Box`1<Int32>", "Ns.Box`1")]
    [InlineData("Ns.<Tools>F0A1B2C3D4E5F60718293A4B5C6D7E8F90A1B2C3D4E5F60718293A4B5C6D7E8F9__Helper", "Ns.Helper")]
    [InlineData("<Tools>F0A1B2__Helper+<>c", "Helper")]
    [InlineData("<PrivateImplementationDetails>", "<PrivateImplementationDetails>")]
    [InlineData("Interop+Sys", "Interop+Sys")]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Modules_drop_compiler_generated_nesting_and_generic_arguments(string? declaringType, string? expected)
    {
        Assert.Equal(expected, ExceptionCapture.ModuleOf(declaringType));
    }

    [Fact]
    public void Same_throw_site_gives_identical_grouping_fields()
    {
        var first = Frames(ExceptionCapture.Capture(Catch(() => Throwers.Throw("first"))))[0]!;
        var second = Frames(ExceptionCapture.Capture(Catch(() => Throwers.Throw("second"))))[0]!;

        foreach (var key in new[] { "function", "module", "filename" })
        {
            Assert.Equal(Text(first[key]), Text(second[key]));
        }

        Assert.Equal(first["lineno"]?.ToJsonString(), second["lineno"]?.ToJsonString());
    }

    [Fact]
    public void Frames_are_capped_at_50()
    {
        var error = ExceptionCapture.Capture(Catch(() => Throwers.Recurse(80)));

        var frames = Frames(error);
        Assert.Equal(ExceptionCapture.MaxFrames, frames.Count);
        Assert.All(frames, f => Assert.Equal("Recurse", Text(f!["function"])));
    }

    [Fact]
    public void Frames_with_neither_a_method_nor_a_file_are_dropped()
    {
        var frames = ExceptionCapture.FramesFrom(
            new ExceptionCapture.FrameInfo?[]
            {
                new(null, null, null, null, 0, 0),
                new("Contoso.Server.Tools", "Run", "Contoso.Server", null, 0, 0),
                new("Contoso.Server.Tools", string.Empty, "Contoso.Server", string.Empty, 7, 3),
                new(null, null, null, "/app/src/Tools.cs", 12, 0),
                null,
            },
            new ExceptionCapture.SourceLines(),
            AppLibraries.Empty,
            null);

        Assert.Equal(2, frames!.Count);
        Assert.Equal("Run", Text(frames[0]!["function"]));
        Assert.Equal("<unknown>", Text(frames[1]!["function"]));
        Assert.Equal("/app/src/Tools.cs", Text(frames[1]!["abs_path"]));
    }

    [Fact]
    public void Frames_are_left_out_when_none_has_a_method_or_a_file()
    {
        var unknown = Enumerable.Repeat(new ExceptionCapture.FrameInfo(null, null, "Contoso.Server", null, 0, 0), 60);

        Assert.Null(ExceptionCapture.FramesFrom(unknown, new ExceptionCapture.SourceLines(), AppLibraries.Empty, null));
        Assert.Null(ExceptionCapture.FramesFrom(Array.Empty<ExceptionCapture.FrameInfo?>(), new ExceptionCapture.SourceLines(), AppLibraries.Empty, null));
    }

    [Fact]
    public void Dropped_frames_do_not_count_toward_the_frame_cap()
    {
        var unknown = Enumerable.Repeat(new ExceptionCapture.FrameInfo(null, null, null, null, 0, 0), 60);
        var known = Enumerable.Range(0, 60).Select(i => new ExceptionCapture.FrameInfo("Contoso.Server.Tools", "Run" + i, "Contoso.Server", null, 0, 0));

        var frames = ExceptionCapture.FramesFrom(unknown.Concat(known), new ExceptionCapture.SourceLines(), AppLibraries.Empty, null);

        Assert.Equal(ExceptionCapture.MaxFrames, frames!.Count);
        Assert.Equal("Run0", Text(frames[0]!["function"]));
        Assert.Equal("Run49", Text(frames[49]!["function"]));
    }

    private static readonly string[] ExpectedFunctions = { "Run", "Start", "Main" };

    [Fact]
    public void Reflection_invoker_frames_are_left_out()
    {
        var frames = ExceptionCapture.FramesFrom(
            new ExceptionCapture.FrameInfo?[]
            {
                new("Contoso.Server.Tools", "Run", "Contoso.Server", null, 0, 0),
                new("System.Reflection.MethodBaseInvoker", "InterpretedInvoke_Method", "System.Private.CoreLib", null, 0, 0),
                new("System.Reflection.MethodBaseInvoker", "InvokeWithNoArgs", "System.Private.CoreLib", null, 0, 0),
                new("System.RuntimeMethodHandle", "InvokeMethod", "System.Private.CoreLib", null, 0, 0),
                new("System.Reflection.MethodBase", "Invoke", "System.Private.CoreLib", null, 0, 0),
                new("Contoso.Server.Dispatch", "InvokeStub_Tools.Run", "Contoso.Server", null, 0, 0),
                new("System.Runtime.CompilerServices.AsyncTaskMethodBuilder", "Start", "System.Private.CoreLib", null, 0, 0),
                new("Contoso.Server.Host", "Main", "Contoso.Server", null, 0, 0),
            },
            new ExceptionCapture.SourceLines(),
            AppLibraries.Empty,
            null);

        Assert.Equal(ExpectedFunctions, frames!.Select(f => Text(f!["function"])).ToArray());
    }

    [Fact]
    public void The_first_and_later_calls_of_a_reflection_invoked_method_give_the_same_frames()
    {
        var method = typeof(Throwers).GetMethod(nameof(Throwers.Throw))!;
        var traces = new List<string[]>();
        for (var i = 0; i < 4; i++)
        {
            var thrown = Assert.Throws<System.Reflection.TargetInvocationException>(() => method.Invoke(null, new object[] { "reflected" }));
            var error = ExceptionCapture.Capture(thrown.InnerException!);
            var frames = Frames(error);
            Assert.DoesNotContain(frames, f => ModuleOf(f).StartsWith("System.Reflection.", StringComparison.Ordinal)
                || ModuleOf(f) == "System.RuntimeMethodHandle"
                || Text(f!["function"]).StartsWith("InvokeStub_", StringComparison.Ordinal));
            traces.Add(frames.Select(f => ModuleOf(f) + "." + Text(f!["function"])).ToArray());
        }

        Assert.All(traces, t => Assert.Equal(traces[0], t));
        Assert.Equal(nameof(Throwers.Throw), traces[0][0].Split('.').Last());
    }

    [Fact]
    public void Inner_exceptions_become_chained_errors_nearest_first()
    {
        var root = Catch(() => Throwers.Throw("Root cause"));
        var middle = Catch(() => Throwers.ThrowWrapped("Middle cause", root));
        var top = Catch(() => Throwers.ThrowWrapped("Top error", middle));

        var error = ExceptionCapture.Capture(top);

        var chain = error["chained_errors"]!.AsArray();
        Assert.Equal(2, chain.Count);
        Assert.Equal("Middle cause", Text(chain[0]!["message"]));
        Assert.Equal("Root cause", Text(chain[1]!["message"]));
        Assert.Equal(ChainedErrorKeys, error.Select(p => p.Key));
        foreach (var entry in chain)
        {
            Assert.Equal(ChainEntryKeys, entry!.AsObject().Select(p => p.Key));
            Assert.Equal("System.InvalidOperationException", Text(entry["type"]));
            Assert.NotEmpty(Frames(entry.AsObject()));
        }

        Assert.Equal(root.ToString(), Text(chain[1]!["stack"]));
        Assert.Equal("Throw", Text(Frames(chain[1]!.AsObject())[0]!["function"]));
    }

    [Fact]
    public void Chain_keeps_the_10_nearest_inner_exceptions()
    {
        Exception error = new InvalidOperationException("Root");
        for (var i = 0; i < 20; i++)
        {
            error = new InvalidOperationException("Level " + i, error);
        }

        var chain = ExceptionCapture.Capture(error)["chained_errors"]!.AsArray();

        Assert.Equal(ExceptionCapture.MaxChainDepth, chain.Count);
        Assert.Equal("Level 18", Text(chain[0]!["message"]));
        Assert.Equal("Level 9", Text(chain[9]!["message"]));
    }

    [Fact]
    public void Aggregate_exception_follows_its_first_inner_exception()
    {
        var aggregate = new AggregateException(new InvalidOperationException("first"), new ArgumentException("second"));

        var chain = ExceptionCapture.Capture(aggregate)["chained_errors"]!.AsArray();

        var entry = Assert.Single(chain);
        Assert.Equal("first", Text(entry!["message"]));
        Assert.Equal("System.InvalidOperationException", Text(entry["type"]));
    }

    [Fact]
    public void Cyclic_inner_exception_chain_terminates_without_a_stack()
    {
        var root = new InvalidOperationException("root");
        var middle = new InvalidOperationException("middle", root);
        var top = new InvalidOperationException("top", middle);
        var field = typeof(Exception).GetField("_innerException", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field!.SetValue(root, top);

        var error = ExceptionCapture.Capture(top);

        Assert.Equal("top", Text(error["message"]));
        Assert.False(error.ContainsKey("stack"));
        var chain = error["chained_errors"]!.AsArray();
        Assert.Equal("middle,root", string.Join(",", chain.Select(c => Text(c!["message"]))));
    }

    [Fact]
    public void Never_thrown_exception_has_a_stack_but_no_frames()
    {
        var error = ExceptionCapture.Capture(new InvalidOperationException("never thrown"));

        Assert.Equal("never thrown", Text(error["message"]));
        Assert.Equal("System.InvalidOperationException: never thrown", Text(error["stack"]));
        Assert.False(error.ContainsKey("frames"));
        Assert.Equal("csharp", Text(error["platform"]));
    }

    [Fact]
    public void Aggregate_exception_with_100_inner_exceptions_is_captured()
    {
        var e = Catch(() => Throwers.ThrowAggregate(100));

        var error = ExceptionCapture.Capture(e);

        Assert.Equal("System.AggregateException", Text(error["type"]));
        Assert.Contains("inner 99", Text(error["stack"]), StringComparison.Ordinal);
        Assert.NotEmpty(Frames(error));
        var entry = Assert.Single(error["chained_errors"]!.AsArray());
        Assert.Equal("inner 0", Text(entry!["message"]));
    }

    [Fact]
    public void Throwing_message_getter_falls_back_to_an_empty_message()
    {
        var broken = Catch(Throwers.ThrowBrokenMessage);
        var wrapper = Catch(() => Throwers.ThrowWrapped("wrapper", broken));

        var error = ExceptionCapture.Capture(broken);
        var wrapped = ExceptionCapture.Capture(wrapper);

        Assert.Equal(string.Empty, Text(error["message"]));
        Assert.Equal(TestsModule + "+BrokenMessageException", Text(error["type"]));
        Assert.Equal("csharp", Text(error["platform"]));
        Assert.NotEmpty(Frames(error));
        Assert.Equal("wrapper", Text(wrapped["message"]));
        Assert.Equal(string.Empty, Text(wrapped["chained_errors"]![0]!["message"]));
    }

    [Fact]
    public void Null_exception_is_captured_without_throwing()
    {
        Assert.Equal("{\"message\":\"null\",\"platform\":\"csharp\"}", ExceptionCapture.Capture(null!).ToJsonString());
    }

    [Theory]
    [InlineData("{\"isError\":true,\"content\":[{\"type\":\"text\",\"text\":\" a \"},{\"type\":\"image\",\"data\":\"AAAA\",\"mimeType\":\"image/png\"},{\"type\":\"text\",\"text\":\"b\"}]}", "a  b")]
    [InlineData("{\"isError\":true,\"content\":[]}", "Unknown error")]
    [InlineData("{\"isError\":true,\"content\":[null,5,{\"type\":\"text\",\"text\":\"z\"}]}", "z")]
    [InlineData("{\"isError\":true,\"content\":[{\"type\":\"text\",\"text\":\"  \\n \"}]}", "Unknown error")]
    [InlineData("{\"isError\":false,\"content\":[{\"type\":\"text\",\"text\":\"Tool failed\"}]}", "Tool failed")]
    [InlineData("{\"isError\":null,\"content\":[[\"text\"],{\"type\":\"Text\",\"text\":\"no\"},{\"text\":\"no\"},{\"type\":\"text\",\"text\":\"yes\"}]}", "yes")]
    [InlineData("{\"isError\":true,\"content\":[{\"type\":\"text\"},{\"type\":\"text\",\"text\":5},{\"type\":\"text\",\"text\":true},{\"type\":\"text\",\"text\":{\"a\":1}},{\"type\":\"text\",\"text\":[1,[2,null]]},{\"type\":\"text\",\"text\":null}]}", "5 true [object Object] 1,2,")]
    public void Tool_result_text_blocks_become_the_message(string result, string expected)
    {
        var error = ExceptionCapture.CaptureFromResult(JsonNode.Parse(result));

        Assert.Equal(new JsonObject { ["message"] = expected, ["platform"] = "csharp" }.ToJsonString(), error.ToJsonString());
    }

    [Theory]
    [InlineData("{\"isError\":true}", "{\"isError\":true}")]
    [InlineData("{\"isError\":true,\"content\":\"oops\"}", "{\"isError\":true,\"content\":\"oops\"}")]
    [InlineData("{\"content\":[{\"type\":\"text\",\"text\":\"é <b>\"}]}", "{\"content\":[{\"type\":\"text\",\"text\":\"é <b>\"}]}")]
    [InlineData("{\"isError\":true,\"s\":\"\ud83d\ude00\u2028\"}", "{\"isError\":true,\"s\":\"\ud83d\ude00\u2028\"}")]
    [InlineData("{\"isError\":true,\"s\":\"\\ud800\"}", "{\"isError\":true,\"s\":\"\\ud800\"}")]
    [InlineData("\"plain text\"", "plain text")]
    [InlineData("42", "42")]
    [InlineData("false", "false")]
    [InlineData("null", "null")]
    public void Results_that_are_not_tool_results_are_stringified(string result, string expected)
    {
        var error = ExceptionCapture.CaptureFromResult(JsonNode.Parse(result));

        Assert.Equal(expected, Text(error["message"]));
        Assert.Equal(ResultKeys, error.Select(p => p.Key));
    }

    private static void AssertFirstFrame(JsonObject error, string module, string function)
    {
        var frame = Frames(error)[0]!;
        Assert.Equal(function, Text(frame["function"]));
        Assert.Equal(module, Text(frame["module"]));
    }

    private static void AssertOptionalNumber(JsonObject frame, string key, int expected)
    {
        if (expected > 0)
        {
            Assert.Equal(expected, frame[key]!.GetValue<int>());
        }
        else
        {
            Assert.False(frame.ContainsKey(key));
        }
    }

    private static JsonObject SingleFrame(ExceptionCapture.FrameInfo info, AppLibraries libraries)
    {
        var frames = ExceptionCapture.FramesFrom(new[] { info }, new ExceptionCapture.SourceLines(), libraries, null);

        return Assert.Single(frames!)!.AsObject();
    }

    private static JsonArray Frames(JsonObject error) => error["frames"]!.AsArray();

    private static string Text(JsonNode? node) => node!.GetValue<string>();

    private static string ModuleOf(JsonNode? frame) => frame!["module"]?.GetValue<string>() ?? string.Empty;

    private static string? CurrentDirectory() => Environment.CurrentDirectory.Replace('\\', '/');

    private static string TempFile(string content)
    {
        var path = Path.GetTempFileName();
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes(content));
        return path;
    }

    private static Exception Catch(Action action)
    {
        try
        {
            action();
        }
        catch (Exception e)
        {
            return e;
        }

        throw new InvalidOperationException("The action did not throw.");
    }

    private static async Task<Exception> CatchAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception e)
        {
            return e;
        }

        throw new InvalidOperationException("The action did not throw.");
    }

    public sealed class NestedTestException : Exception
    {
        public NestedTestException(string message)
            : base(message)
        {
        }
    }

    public sealed class GenericTestException<T> : Exception
    {
        public GenericTestException(string message)
            : base(message)
        {
        }
    }

    public sealed class BrokenMessageException : Exception
    {
        public override string Message => throw new InvalidOperationException("The message is unavailable.");
    }

    private sealed class LambdaInConstructor
    {
        public LambdaInConstructor()
        {
            Action fail = () => throw new InvalidOperationException("constructor");
            fail();
        }
    }

    private sealed class GenericThrower<T>
    {
        private readonly string _name = typeof(T).Name;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void Throw() => throw new InvalidOperationException(_name);
    }

    private static class Throwers
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Throw(string message) => throw new InvalidOperationException(message);

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Method2() => throw new InvalidOperationException("two");

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void ThrowWrapped(string message, Exception inner) => throw new InvalidOperationException(message, inner);

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void ThrowNested() => throw new NestedTestException("nested");

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void ThrowGeneric() => throw new GenericTestException<int>("generic");

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void ThrowBrokenMessage() => throw new BrokenMessageException();

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void ThrowAggregate(int count)
        {
            var inner = new List<Exception>();
            for (var i = 0; i < count; i++)
            {
                inner.Add(new InvalidOperationException("inner " + i));
            }

            throw new AggregateException(inner);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void ReadMissingFile()
        {
            var text = File.ReadAllText(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "missing.txt"));
            GC.KeepAlive(text);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void FailInsideTheSdk()
        {
            var path = StackPaths.MakeRelativePath(null!, null);
            GC.KeepAlive(path);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static int Recurse(int depth)
        {
            if (depth == 0)
            {
                throw new InvalidOperationException("deep");
            }

            return Recurse(depth - 1) + 1;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static async Task ThrowAfterYieldAsync()
        {
            await Task.Yield();
            throw new InvalidOperationException("async");
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void ThrowFromLambda()
        {
            Action fail = () => throw new InvalidOperationException("lambda");
            fail();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void ThrowFromLocalFunction()
        {
            Fail();

            [MethodImpl(MethodImplOptions.NoInlining)]
            static void Fail() => throw new InvalidOperationException("local function");
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void ThrowFromCapturingLambda(int value)
        {
            Action fail = () => throw new InvalidOperationException("captured " + value);
            fail();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static async Task ThrowFromAsyncLambda()
        {
            Func<Task> fail = async () =>
            {
                await Task.Yield();
                throw new InvalidOperationException("async lambda");
            };
            await fail();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static async Task ThrowFromAsyncLocalFunction()
        {
            await FailAsync();

            static async Task FailAsync()
            {
                await Task.Yield();
                throw new InvalidOperationException("async local function");
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void ThrowFromIterator()
        {
            foreach (var item in Iterate())
            {
                GC.KeepAlive(item);
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void ThrowGenericMethod<T>() => throw new InvalidOperationException(typeof(T).Name);

        private static IEnumerable<int> Iterate()
        {
            yield return 1;
            throw new InvalidOperationException("iterator");
        }
    }
}

file static class FileLocalThrower
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Throw() => throw new InvalidOperationException("file-local");
}
