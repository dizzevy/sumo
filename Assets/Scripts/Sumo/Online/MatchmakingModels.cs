using System;

namespace Sumo.Online
{
    public enum MatchTicketStatus
    {
        Unknown = 0,
        Searching = 1,
        WaitingForPlayers = 2,
        MatchFound = 3,
        StartingServer = 4,
        ServerReady = 5,
        Cancelled = 6,
        Failed = 7
    }

    [Serializable]
    public sealed class MatchTicket
    {
        public string TicketId;
        public string PlayerId;
        public MatchTicketStatus Status;
        public DateTime CreatedAtUtc;

        public bool IsTerminal => Status == MatchTicketStatus.Cancelled || Status == MatchTicketStatus.Failed;
    }

    [Serializable]
    public sealed class ServerConnectionInfo
    {
        public string MatchId;
        public string SessionName;
        public string Region;
        public string SceneName;
        public int MaxPlayers;
        public string Address;
        public int? Port;
        public string AuthToken;

        public bool HasAddress => string.IsNullOrWhiteSpace(Address) == false;
        public bool HasPort => Port.HasValue && Port.Value > 0;
    }
}
