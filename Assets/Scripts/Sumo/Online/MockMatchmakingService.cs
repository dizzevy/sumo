using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Sumo.Online
{
    public sealed class MockMatchmakingService : IMatchmakingService
    {
        public sealed class Settings
        {
            public string PlayerId = "player_local";
            public string SessionName = "sumo_match_001";
            public string MatchId = "match_001";
            public string Region = "local";
            public string SceneName = "location_test";
            public int MaxPlayers = BootstrapConfig.TargetMaxPlayers;
            public float SearchDelaySeconds = 0.8f;
            public float WaitForPlayersDelaySeconds = 2.0f;
            public float ServerBootDelaySeconds = 1.5f;
        }

        private readonly Settings _settings;
        private readonly HashSet<string> _cancelledTickets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly object _lock = new object();
        private int _ticketCounter;

        public MockMatchmakingService(Settings settings)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            Debug.LogWarning("MockMatchmakingService is active. This simulates queue/server states and does not launch real dedicated servers.");
        }

        public Task<MatchTicket> FindMatchAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();

            int number = Interlocked.Increment(ref _ticketCounter);
            MatchTicket ticket = new MatchTicket
            {
                TicketId = $"mock_ticket_{number:0000}",
                PlayerId = _settings.PlayerId,
                Status = MatchTicketStatus.Searching,
                CreatedAtUtc = DateTime.UtcNow
            };

            return Task.FromResult(ticket);
        }

        public Task CancelFindMatchAsync(MatchTicket ticket)
        {
            if (ticket == null || string.IsNullOrWhiteSpace(ticket.TicketId))
            {
                return Task.CompletedTask;
            }

            lock (_lock)
            {
                _cancelledTickets.Add(ticket.TicketId);
            }

            ticket.Status = MatchTicketStatus.Cancelled;
            return Task.CompletedTask;
        }

        public async Task<ServerConnectionInfo> WaitForServerAsync(MatchTicket ticket, CancellationToken token)
        {
            if (ticket == null)
            {
                throw new ArgumentNullException(nameof(ticket));
            }

            if (string.IsNullOrWhiteSpace(ticket.TicketId))
            {
                throw new ArgumentException("TicketId is required.", nameof(ticket));
            }

            ticket.Status = MatchTicketStatus.WaitingForPlayers;
            await DelayWithCancellationAsync(_settings.WaitForPlayersDelaySeconds, ticket, token);

            ticket.Status = MatchTicketStatus.MatchFound;
            await DelayWithCancellationAsync(_settings.SearchDelaySeconds, ticket, token);

            ticket.Status = MatchTicketStatus.StartingServer;
            await DelayWithCancellationAsync(_settings.ServerBootDelaySeconds, ticket, token);

            ticket.Status = MatchTicketStatus.ServerReady;

            return new ServerConnectionInfo
            {
                MatchId = _settings.MatchId,
                SessionName = _settings.SessionName,
                Region = _settings.Region,
                SceneName = _settings.SceneName,
                MaxPlayers = Mathf.Clamp(_settings.MaxPlayers, 2, BootstrapConfig.TargetMaxPlayers),
                Address = null,
                Port = null,
                AuthToken = null
            };
        }

        private async Task DelayWithCancellationAsync(float seconds, MatchTicket ticket, CancellationToken token)
        {
            int totalMs = Mathf.Max(0, Mathf.RoundToInt(seconds * 1000f));
            int elapsedMs = 0;

            while (elapsedMs < totalMs)
            {
                token.ThrowIfCancellationRequested();

                if (IsCancelled(ticket.TicketId))
                {
                    ticket.Status = MatchTicketStatus.Cancelled;
                    throw new OperationCanceledException("Matchmaking was cancelled.", token);
                }

                int step = Math.Min(200, totalMs - elapsedMs);
                await Task.Delay(step, token);
                elapsedMs += step;
            }
        }

        private bool IsCancelled(string ticketId)
        {
            lock (_lock)
            {
                return _cancelledTickets.Contains(ticketId);
            }
        }
    }
}
