using System.Threading;
using System.Threading.Tasks;

namespace Sumo.Online
{
    public interface IMatchmakingService
    {
        Task<MatchTicket> FindMatchAsync(CancellationToken token);
        Task CancelFindMatchAsync(MatchTicket ticket);
        Task<ServerConnectionInfo> WaitForServerAsync(MatchTicket ticket, CancellationToken token);
    }
}
