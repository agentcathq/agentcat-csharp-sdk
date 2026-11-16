using System.Threading;
using System.Threading.Tasks;

namespace AgentCat.Internal.ServerInfo;

internal interface IServerInfoSender
{
    /// <summary>Sends one server info body and returns the HTTP status of the answer.</summary>
    Task<int> PostServerInfoAsync(byte[] body, CancellationToken cancellationToken);
}
