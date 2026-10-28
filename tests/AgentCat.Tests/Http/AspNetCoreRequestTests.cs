#if NET
using System.Runtime.CompilerServices;
using AgentCat.Internal.Http;
using AgentCat.Internal.Logging;
using AgentCat.Tests.TestSupport;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCat.Tests.Http;

public class AspNetCoreRequestTests
{
    private const string Off = "Warning: AgentCat HTTP request capture is off: ";

    public static TheoryData<string, string> Members => new()
    {
        { "HttpContext", "Microsoft.AspNetCore.Http.IHttpContextAccessor.HttpContextRenamed" },
        { "Request", "Microsoft.AspNetCore.Http.HttpContext.RequestRenamed" },
        { "Headers", "Microsoft.AspNetCore.Http.HttpRequest.HeadersRenamed" },
        { "Scheme", "Microsoft.AspNetCore.Http.HttpRequest.SchemeRenamed" },
        { "Host", "Microsoft.AspNetCore.Http.HttpRequest.HostRenamed" },
        { "PathBase", "Microsoft.AspNetCore.Http.HttpRequest.PathBaseRenamed" },
        { "Path", "Microsoft.AspNetCore.Http.HttpRequest.PathRenamed" },
        { "QueryString", "Microsoft.AspNetCore.Http.HttpRequest.QueryStringRenamed" },
    };

    public static TheoryData<string, string> Types => new()
    {
        { "HttpContext", "Microsoft.AspNetCore.Http.HttpContextRenamed" },
        { "HttpRequest", "Microsoft.AspNetCore.Http.HttpRequestRenamed" },
    };

    public static TheoryData<string, string, string, string, string> UrlParts => new()
    {
        { "http", "localhost", "", "", "" },
        { "https", "Example.TEST:8443", "/base", "/mcp/café x", "?q=a%20b&r=%2F&u=%E2%9C%93" },
        { "http", "h", "/base/", "/x", "" },
        { "http", "h", "/base", "", "?a=1" },
        { "http", "[::1]:5000", "", "/", "" },
        { "http", "bücher.example", "", "/p", "" },
    };

    // Each type and member by the reader's own names, so a rename in ASP.NET Core fails here by name.
    [Fact]
    public void Reader_names_resolve_on_aspnetcore()
    {
        var names = AspNetCoreRequest.Names.Default;
        var missing = new List<string>();

        var accessor = FindType(AspNetCoreRequest.IHttpContextAccessorType, typeof(IHttpContextAccessor), missing);
        FindType(AspNetCoreRequest.HttpContextAccessorType, typeof(HttpContextAccessor), missing);
        var context = FindType(AspNetCoreRequest.HttpContextType, typeof(HttpContext), missing);
        var request = FindType(AspNetCoreRequest.HttpRequestType, typeof(HttpRequest), missing);
        var uriHelper = FindType(AspNetCoreRequest.UriHelperType, typeof(UriHelper), missing);
        FindProperty(accessor, names.HttpContext, missing);
        FindProperty(context, names.Request, missing);
        foreach (var name in new[] { names.Headers, names.Scheme, names.Host, names.PathBase, names.Path, names.QueryString })
        {
            FindProperty(request, name, missing);
        }

        if (uriHelper?.GetMethod(names.GetEncodedUrl, new[] { typeof(HttpRequest) }) is null)
        {
            missing.Add(AspNetCoreRequest.UriHelperType + " " + names.GetEncodedUrl);
        }

        Assert.Empty(missing);
        var reader = new AspNetCoreRequest.Reader(names);
        Assert.Null(reader.MissingMember);
        Assert.True(reader.IsAvailable);
        Assert.True(AspNetCoreRequest.IsAvailable);
    }

    [Theory]
    [MemberData(nameof(Members))]
    public void A_missing_member_logs_one_line_and_turns_capture_off(string member, string missing)
    {
        var reader = new AspNetCoreRequest.Reader(Rename(member));
        var log = new RecordingLog();
        using var services = ServicesFor(Context(r => r.Headers["X-A"] = "1"));
        var registered = new ServiceCollection();

        var first = reader.Capture(services, log);
        var second = reader.Capture(services, log);
        reader.TryRegisterAccessor(registered);

        Assert.Null(first);
        Assert.Null(second);
        Assert.False(reader.IsAvailable);
        Assert.Equal(missing, reader.MissingMember);
        Assert.Equal(new[] { Off + missing + " was not found" }, log.Lines);
        Assert.Empty(registered);
    }

    // IHttpContextAccessor is the probe: once it is found, the app has ASP.NET Core, and a type missing after it is a
    // rename that the line reports.
    [Theory]
    [MemberData(nameof(Types))]
    public void A_missing_type_after_the_probe_logs_one_line_and_turns_capture_off(string type, string missing)
    {
        var reader = new AspNetCoreRequest.Reader(RenameType(type));
        var log = new RecordingLog();
        using var services = ServicesFor(Context(r => r.Headers["X-A"] = "1"));
        var registered = new ServiceCollection();

        var first = reader.Capture(services, log);
        var second = reader.Capture(services, log);
        reader.TryRegisterAccessor(registered);

        Assert.Null(first);
        Assert.Null(second);
        Assert.False(reader.IsAvailable);
        Assert.Equal(missing, reader.MissingMember);
        Assert.Equal(new[] { Off + missing + " was not found" }, log.Lines);
        Assert.Empty(registered);
    }

    // Without IHttpContextAccessor the app has no ASP.NET Core: capture is off without a line, and the reader looks
    // for no other type, so a missing one after the probe goes unreported.
    [Fact]
    public void No_IHttpContextAccessor_turns_capture_off_without_a_line()
    {
        var names = AspNetCoreRequest.Names.Default with
        {
            AccessorType = Renamed(AspNetCoreRequest.Names.Default.AccessorType),
            ContextType = Renamed(AspNetCoreRequest.Names.Default.ContextType),
        };
        var reader = new AspNetCoreRequest.Reader(names);
        var log = new RecordingLog();
        using var services = ServicesFor(Context(r => r.Headers["X-A"] = "1"));
        var registered = new ServiceCollection();

        var first = reader.Capture(services, log);
        var second = reader.Capture(services, log);
        reader.TryRegisterAccessor(registered);

        Assert.Null(first);
        Assert.Null(second);
        Assert.False(reader.IsAvailable);
        Assert.Null(reader.MissingMember);
        Assert.Empty(log.Lines);
        Assert.Empty(registered);
    }

    // A stdio app can have Microsoft.AspNetCore.Http.Abstractions without Microsoft.AspNetCore.Http: that is not
    // ASP.NET Core, so capture is off without a line.
    [Fact]
    public void No_HttpContextAccessor_class_turns_capture_off_without_a_line()
    {
        var names = AspNetCoreRequest.Names.Default with
        {
            AccessorClassType = Renamed(AspNetCoreRequest.Names.Default.AccessorClassType),
        };
        var reader = new AspNetCoreRequest.Reader(names);
        var log = new RecordingLog();
        using var services = ServicesFor(Context(r => r.Headers["X-A"] = "1"));
        var registered = new ServiceCollection();

        var first = reader.Capture(services, log);
        reader.TryRegisterAccessor(registered);

        Assert.Null(first);
        Assert.False(reader.IsAvailable);
        Assert.Null(reader.MissingMember);
        Assert.Empty(log.Lines);
        Assert.Empty(registered);
    }

    [Fact]
    public void Capture_reads_every_header_and_the_url()
    {
        var log = new RecordingLog();
        var context = Context(r =>
        {
            r.Headers.Append("X-Multi", "a");
            r.Headers.Append("X-Multi", "b");
            r.Headers.Append("Cookie", "a=1");
            r.Headers.Append("Cookie", "b=2");
            r.Headers.Authorization = "Bearer secret";
            r.Headers["X-Empty"] = "";
            r.Headers.Append("Accept", "application/json");
            r.Headers.Append("Accept", "text/event-stream");
        });
        using var services = ServicesFor(context);

        var capture = AspNetCoreRequest.Capture(services, log);

        Assert.NotNull(capture);
        Assert.Equal(
            new[]
            {
                Pair("accept", "application/json, text/event-stream"),
                Pair("authorization", "Bearer secret"),
                Pair("cookie", "a=1; b=2"),
                Pair("host", "localhost"),
                Pair("x-empty", ""),
                Pair("x-multi", "a, b"),
            },
            capture.Headers);
        Assert.Equal("http://localhost/mcp", capture.Url);
        Assert.Empty(log.Lines);
    }

    [Fact]
    public void Url_has_scheme_host_path_base_path_and_query()
    {
        var context = Context(r =>
        {
            r.Scheme = "https";
            r.Host = new HostString("example.test:8443");
            r.PathBase = new PathString("/base");
            r.Path = new PathString("/mcp/café x");
            r.QueryString = new QueryString("?q=a%20b&r=%2F&u=%E2%9C%93");
        });
        using var services = ServicesFor(context);

        var capture = AspNetCoreRequest.Capture(services, new RecordingLog());

        Assert.Equal("https://example.test:8443/base/mcp/caf%C3%A9%20x?q=a%20b&r=%2F&u=%E2%9C%93", capture?.Url);
    }

    [Fact]
    public void No_host_leaves_the_url_out()
    {
        using var noHost = ServicesFor(Context(r =>
        {
            r.Headers.Remove("Host");
            r.Headers["X-A"] = "1";
        }));
        using var emptyHost = ServicesFor(Context(r =>
        {
            r.Host = new HostString(string.Empty);
            r.Headers["X-A"] = "1";
        }));

        var first = AspNetCoreRequest.Capture(noHost, new RecordingLog());
        var second = AspNetCoreRequest.Capture(emptyHost, new RecordingLog());

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Null(first.Url);
        Assert.Null(second.Url);
        Assert.Equal(new[] { Pair("x-a", "1") }, first.Headers);
        Assert.Equal(new[] { Pair("host", ""), Pair("x-a", "1") }, second.Headers);
    }

    [Fact]
    public void The_host_keeps_its_case()
    {
        using var services = ServicesFor(Context(r => r.Host = new HostString("Example.TEST:8443")));

        var capture = AspNetCoreRequest.Capture(services, new RecordingLog());

        Assert.Equal("http://Example.TEST:8443/mcp", capture?.Url);
    }

    // Without UriHelper.GetEncodedUrl the reader builds the same string from the request parts, and writes no line.
    [Theory]
    [MemberData(nameof(UrlParts))]
    public void Url_from_the_parts_matches_GetEncodedUrl(string scheme, string host, string pathBase, string path, string query)
    {
        var context = Context(r =>
        {
            r.Scheme = scheme;
            r.Host = new HostString(host);
            r.PathBase = new PathString(pathBase);
            r.Path = new PathString(path);
            r.QueryString = new QueryString(query);
        });
        using var services = ServicesFor(context);
        var log = new RecordingLog();
        var withoutUriHelper = new AspNetCoreRequest.Reader(AspNetCoreRequest.Names.Default with { GetEncodedUrl = "GetEncodedUrlRenamed" });

        var expected = context.Request.GetEncodedUrl();

        Assert.True(withoutUriHelper.IsAvailable);
        Assert.Equal(expected, withoutUriHelper.Capture(services, log)?.Url);
        Assert.Equal(expected, AspNetCoreRequest.Capture(services, log)?.Url);
        Assert.Empty(log.Lines);
    }

    [Fact]
    public void No_context_gives_no_capture()
    {
        var log = new RecordingLog();
        using var noContext = ServicesFor(null);
        using var noAccessor = new ServiceCollection().BuildServiceProvider();

        Assert.Null(AspNetCoreRequest.Capture(noContext, log));
        Assert.Null(AspNetCoreRequest.Capture(noAccessor, log));
        Assert.Null(AspNetCoreRequest.Capture(null, log));
        Assert.Empty(log.Lines);
    }

    // ASP.NET Core sets every HttpContextAccessor through one static slot, so a cached instance sees the request
    // when the services that the call gives have no accessor.
    [Fact]
    public void A_cached_accessor_serves_when_the_services_have_none()
    {
        var log = new RecordingLog();
        var current = new HttpContextAccessor { HttpContext = Context(r => r.Headers["X-A"] = "1") };
        RequestCapture? withoutServices;
        RequestCapture? withoutAccessor;
        try
        {
            using var noAccessor = new ServiceCollection().BuildServiceProvider();
            withoutServices = AspNetCoreRequest.Capture(null, log);
            withoutAccessor = AspNetCoreRequest.Capture(noAccessor, log);
        }
        finally
        {
            current.HttpContext = null;
        }

        Assert.Equal(new[] { Pair("host", "localhost"), Pair("x-a", "1") }, withoutServices?.Headers);
        Assert.Equal(new[] { Pair("host", "localhost"), Pair("x-a", "1") }, withoutAccessor?.Headers);
        Assert.Null(AspNetCoreRequest.Capture(null, log));
        Assert.Empty(log.Lines);
    }

    [Fact]
    public void A_disposed_context_gives_no_capture()
    {
        var log = new RecordingLog();
        var uninitialized = Context(r => r.Headers["X-A"] = "1");
        uninitialized.Uninitialize();
        using var keptContext = ServicesFor(uninitialized);
        var disposedProvider = ServicesFor(Context());
        disposedProvider.Dispose();
        using var scopeOwner = ServicesFor(Context());
        var scope = scopeOwner.CreateScope();
        scope.Dispose();

        Assert.Null(AspNetCoreRequest.Capture(keptContext, log));
        Assert.Null(AspNetCoreRequest.Capture(disposedProvider, log));
        Assert.Null(AspNetCoreRequest.Capture(scope.ServiceProvider, log));
        Assert.Empty(log.Lines);
    }

    // ASP.NET Core clears the accessor before it reuses a context for the next request on the connection, so a
    // different context after the copy means that the copy can mix two requests.
    [Fact]
    public void A_context_replaced_during_the_copy_gives_no_capture()
    {
        var log = new RecordingLog();
        var first = Context(r => r.Headers["X-A"] = "1");
        using var replaced = ServicesWith(new SwappingAccessor(first, Context(r => r.Headers["X-B"] = "2")));
        using var cleared = ServicesWith(new SwappingAccessor(first, null));
        using var kept = ServicesWith(new SwappingAccessor(first, first));

        Assert.Null(AspNetCoreRequest.Capture(replaced, log));
        Assert.Null(AspNetCoreRequest.Capture(cleared, log));
        Assert.Equal(new[] { Pair("host", "localhost"), Pair("x-a", "1") }, AspNetCoreRequest.Capture(kept, log)?.Headers);
        Assert.Empty(log.Lines);
    }

    [Fact]
    public async Task Capture_keeps_no_request_object()
    {
        var (context, capture) = CaptureThenEndTheRequest();

        Assert.Empty(await Retention.LiveAfterCollectingAsync(new[] { ("HttpContext", context) }));
        Assert.NotNull(capture);
        Assert.Equal(new[] { Pair("host", "localhost"), Pair("x-sentinel", "1") }, capture.Headers);
        Assert.Equal("http://localhost/mcp", capture.Url);
        GC.KeepAlive(capture);
    }

    // The registered accessor holds the request only while it runs, as ASP.NET Core does.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference Context, RequestCapture? Capture) CaptureThenEndTheRequest()
    {
        var services = new ServiceCollection();
        services.AddHttpContextAccessor();
        using var provider = services.BuildServiceProvider();
        var accessor = provider.GetRequiredService<IHttpContextAccessor>();
        var context = Context(r => r.Headers["X-Sentinel"] = "1");
        accessor.HttpContext = context;
        try
        {
            return (new WeakReference(context), AspNetCoreRequest.Capture(provider, NullAgentCatLog.Instance));
        }
        finally
        {
            accessor.HttpContext = null;
        }
    }

    private static Type? FindType(string name, Type expected, List<string> missing)
    {
        var found = Type.GetType(name);
        if (found != expected)
        {
            missing.Add(name);
            return null;
        }

        return found;
    }

    private static void FindProperty(Type? type, string name, List<string> missing)
    {
        if (type?.GetProperty(name) is null)
        {
            missing.Add((type?.FullName ?? "(type not found)") + "." + name);
        }
    }

    private static AspNetCoreRequest.Names Rename(string member)
    {
        var names = AspNetCoreRequest.Names.Default;
        return member switch
        {
            "HttpContext" => names with { HttpContext = names.HttpContext + "Renamed" },
            "Request" => names with { Request = names.Request + "Renamed" },
            "Headers" => names with { Headers = names.Headers + "Renamed" },
            "Scheme" => names with { Scheme = names.Scheme + "Renamed" },
            "Host" => names with { Host = names.Host + "Renamed" },
            "PathBase" => names with { PathBase = names.PathBase + "Renamed" },
            "Path" => names with { Path = names.Path + "Renamed" },
            "QueryString" => names with { QueryString = names.QueryString + "Renamed" },
            _ => throw new ArgumentOutOfRangeException(nameof(member), member, null),
        };
    }

    private static AspNetCoreRequest.Names RenameType(string type)
    {
        var names = AspNetCoreRequest.Names.Default;
        return type switch
        {
            "HttpContextAccessor" => names with { AccessorClassType = Renamed(names.AccessorClassType) },
            "HttpContext" => names with { ContextType = Renamed(names.ContextType) },
            "HttpRequest" => names with { RequestType = Renamed(names.RequestType) },
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
        };
    }

    // "Namespace.Type, Assembly" becomes "Namespace.TypeRenamed, Assembly".
    private static string Renamed(string assemblyQualifiedName) => assemblyQualifiedName.Replace(",", "Renamed,", StringComparison.Ordinal);

    private static DefaultHttpContext Context(Action<HttpRequest>? configure = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("localhost");
        context.Request.Path = new PathString("/mcp");
        configure?.Invoke(context.Request);
        return context;
    }

    private static ServiceProvider ServicesFor(HttpContext? context) => ServicesWith(new TestAccessor { HttpContext = context });

    private static ServiceProvider ServicesWith(IHttpContextAccessor accessor)
    {
        var services = new ServiceCollection();
        services.AddSingleton(accessor);
        return services.BuildServiceProvider();
    }

    private static KeyValuePair<string, string> Pair(string name, string value) => new(name, value);

    private sealed class TestAccessor : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; }
    }

    // The first read gives the first context, every later read the second.
    private sealed class SwappingAccessor(HttpContext first, HttpContext? second) : IHttpContextAccessor
    {
        private int _reads;

        public HttpContext? HttpContext
        {
            get => _reads++ == 0 ? first : second;
            set { }
        }
    }
}
#endif
