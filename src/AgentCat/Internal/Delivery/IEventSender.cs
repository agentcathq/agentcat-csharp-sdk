using System.Threading;
using System.Threading.Tasks;

namespace AgentCat.Internal.Delivery;

internal interface IEventSender
{
    Task SendAsync(AgentCatEvent e, CancellationToken cancellationToken);
}
