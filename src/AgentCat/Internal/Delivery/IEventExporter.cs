using System.Threading;
using System.Threading.Tasks;

namespace AgentCat.Internal.Delivery;

internal interface IEventExporter
{
    Task ExportAsync(AgentCatEvent e, CancellationToken cancellationToken);
}
