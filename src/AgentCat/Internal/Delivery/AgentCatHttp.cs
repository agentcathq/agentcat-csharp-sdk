using System.Net.Http;
using System.Threading;

namespace AgentCat.Internal.Delivery;

/// <summary>The one <see cref="HttpClient"/> of the SDK for events and diagnostics. It lives as long as the process.</summary>
internal static class AgentCatHttp
{
    internal static HttpClient Client { get; } = new(HttpEventSender.CreateDefaultHandler())
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };
}
