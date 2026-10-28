using AgentCat.Internal.Http;
using Microsoft.Extensions.Primitives;
#if !NET
using AgentCat.Tests.TestSupport;
using Microsoft.Extensions.DependencyInjection;
#endif

namespace AgentCat.Tests.Http;

public class RequestCaptureTests
{
    [Fact]
    public void Repeated_values_join_with_comma_space()
    {
        var capture = RequestCapture.Create(new[] { Header("X-Multi", "a", "b", "c"), Header("X-Comma", "c, d") }, null);

        Assert.Equal(new[] { Pair("x-comma", "c, d"), Pair("x-multi", "a, b, c") }, capture.Headers);
    }

    [Theory]
    [InlineData("Cookie")]
    [InlineData("cookie")]
    [InlineData("COOKIE")]
    public void Repeated_cookies_join_with_semicolon_space(string name)
    {
        var capture = RequestCapture.Create(new[] { Header(name, "a=1", "b=2"), Header("X-Cookie", "c=3", "d=4") }, null);

        Assert.Equal(new[] { Pair("cookie", "a=1; b=2"), Pair("x-cookie", "c=3, d=4") }, capture.Headers);
    }

    [Fact]
    public void An_empty_value_is_an_empty_string()
    {
        var capture = RequestCapture.Create(
            new[]
            {
                new KeyValuePair<string, StringValues>("X-None", StringValues.Empty),
                Header("X-Blank", ""),
                new KeyValuePair<string, StringValues>("X-Null", new StringValues(new string?[] { null })),
                new KeyValuePair<string, StringValues>("X-Null-String", new StringValues((string?)null)),
            },
            null);

        Assert.Equal(new[] { Pair("x-blank", ""), Pair("x-none", ""), Pair("x-null", ""), Pair("x-null-string", "") }, capture.Headers);
    }

    // Ordinal, not culture order: a culture comparison would put "xa" before "x-b".
    [Fact]
    public void Names_are_lower_cased_and_sorted()
    {
        var capture = RequestCapture.Create(
            new[]
            {
                Header("xa", "5"),
                Header("X-B", "2"),
                Header("Accept", "*/*"),
                Header("x-a", "1"),
                Header("Content-Type", "application/json"),
                Header("Mcp-Session-Id", "s1"),
            },
            null);

        Assert.Equal(
            new[]
            {
                Pair("accept", "*/*"),
                Pair("content-type", "application/json"),
                Pair("mcp-session-id", "s1"),
                Pair("x-a", "1"),
                Pair("x-b", "2"),
                Pair("xa", "5"),
            },
            capture.Headers);
    }

    [Fact]
    public void A_lower_cased_collision_keeps_the_later_value()
    {
        var forward = RequestCapture.Create(
            new[] { Header("X-Dup", "first"), Header("Accept", "*/*"), Header("x-dup", "second"), Header("X-DUP", "third") },
            null);
        var backward = RequestCapture.Create(new[] { Header("X-DUP", "third"), Header("x-dup", "second"), Header("X-Dup", "first") }, null);

        Assert.Equal(new[] { Pair("accept", "*/*"), Pair("x-dup", "third") }, forward.Headers);
        Assert.Equal(new[] { Pair("x-dup", "first") }, backward.Headers);
    }

    [Fact]
    public void Authorization_and_cookie_are_kept_verbatim()
    {
        var capture = RequestCapture.Create(
            new[]
            {
                Header("Authorization", "Bearer sk-live-Secret_123"),
                Header("Cookie", "session=abc; theme=dark"),
                Header("Proxy-Authorization", "Basic dXNlcjpwYXNz"),
                Header("X-Api-Key", "  Spaced Value  "),
            },
            "http://localhost/mcp");

        Assert.Equal(
            new[]
            {
                Pair("authorization", "Bearer sk-live-Secret_123"),
                Pair("cookie", "session=abc; theme=dark"),
                Pair("proxy-authorization", "Basic dXNlcjpwYXNz"),
                Pair("x-api-key", "  Spaced Value  "),
            },
            capture.Headers);
        Assert.Equal("http://localhost/mcp", capture.Url);
    }

    [Fact]
    public void The_capture_is_a_copy_that_cannot_change()
    {
        var source = new List<KeyValuePair<string, StringValues>> { Header("X-A", "1") };
        var capture = RequestCapture.Create(source, null);

        source[0] = Header("X-A", "changed");
        source.Add(Header("X-B", "2"));

        Assert.Equal(new[] { Pair("x-a", "1") }, capture.Headers);
        Assert.Throws<NotSupportedException>(() => ((IList<KeyValuePair<string, string>>)capture.Headers)[0] = Pair("x-a", "2"));
        Assert.Null(capture.Url);
    }

    [Fact]
    public void No_headers_give_an_empty_list()
    {
        var capture = RequestCapture.Create(null, "http://localhost/");

        Assert.Empty(capture.Headers);
        Assert.Equal("http://localhost/", capture.Url);
    }

#if !NET
    [Fact]
    public void Net_framework_hosts_have_no_capture()
    {
        var log = new RecordingLog();
        var services = new ServiceCollection();

        AspNetCoreRequest.TryRegisterAccessor(services);
        using var provider = services.BuildServiceProvider();

        Assert.False(AspNetCoreRequest.IsAvailable);
        Assert.Empty(services);
        Assert.Null(AspNetCoreRequest.Capture(provider, log));
        Assert.Null(AspNetCoreRequest.Capture(null, log));
        Assert.Empty(log.Lines);
    }
#endif

    private static KeyValuePair<string, StringValues> Header(string name, params string[] values)
        => new(name, new StringValues(values));

    private static KeyValuePair<string, string> Pair(string name, string value) => new(name, value);
}
