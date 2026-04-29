using System;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Sumo.Online
{
    public sealed class ProductionMatchmakingService : IMatchmakingService, IDisposable
    {
        public sealed class Settings
        {
            public string BaseUrl;
            public string PlayerId;
            public string GameMode = "sumo";
            public string DefaultSceneName = "location_test";
            public string DefaultRegion = "auto";
            public int DefaultMaxPlayers = BootstrapConfig.TargetMaxPlayers;
            public float PollIntervalSeconds = 1f;
            public string AuthToken;
        }

        private readonly Settings _settings;
        private readonly HttpClient _httpClient;
        private readonly bool _ownsClient;
        private readonly TimeSpan _pollInterval;

        public ProductionMatchmakingService(Settings settings, HttpClient httpClient = null)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));

            if (string.IsNullOrWhiteSpace(_settings.BaseUrl))
            {
                throw new ArgumentException("Production matchmaking requires a backend BaseUrl.", nameof(settings));
            }

            _settings.DefaultMaxPlayers = Mathf.Clamp(_settings.DefaultMaxPlayers, 2, BootstrapConfig.TargetMaxPlayers);

            _pollInterval = TimeSpan.FromSeconds(Mathf.Max(0.2f, _settings.PollIntervalSeconds));

            if (httpClient == null)
            {
                _httpClient = new HttpClient();
                _ownsClient = true;
            }
            else
            {
                _httpClient = httpClient;
                _ownsClient = false;
            }
        }

        public async Task<MatchTicket> FindMatchAsync(CancellationToken token)
        {
            QueueRequestDto request = new QueueRequestDto
            {
                playerId = _settings.PlayerId,
                gameMode = _settings.GameMode
            };

            string payload = JsonUtility.ToJson(request);
            string responseText = await SendAsync(HttpMethod.Post, "/queue", payload, token);
            QueueTicketResponseDto response = Deserialize<QueueTicketResponseDto>(responseText);

            if (response == null || string.IsNullOrWhiteSpace(response.ticketId))
            {
                throw new InvalidOperationException("Matchmaking backend did not return a valid ticketId.");
            }

            return new MatchTicket
            {
                TicketId = response.ticketId,
                PlayerId = string.IsNullOrWhiteSpace(response.playerId) ? _settings.PlayerId : response.playerId,
                Status = ParseStatus(response.status, MatchTicketStatus.Searching),
                CreatedAtUtc = ParseDate(response.createdAt)
            };
        }

        public async Task CancelFindMatchAsync(MatchTicket ticket)
        {
            if (ticket == null || string.IsNullOrWhiteSpace(ticket.TicketId))
            {
                return;
            }

            try
            {
                await SendAsync(HttpMethod.Delete, $"/queue/{Uri.EscapeDataString(ticket.TicketId)}", null, CancellationToken.None);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"CancelFindMatchAsync failed for ticket {ticket.TicketId}: {ex.Message}");
            }

            ticket.Status = MatchTicketStatus.Cancelled;
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

            while (true)
            {
                token.ThrowIfCancellationRequested();

                string queueResponseText = await SendAsync(
                    HttpMethod.Get,
                    $"/queue/{Uri.EscapeDataString(ticket.TicketId)}",
                    null,
                    token);

                QueueStatusResponseDto queueResponse = Deserialize<QueueStatusResponseDto>(queueResponseText);

                if (queueResponse == null)
                {
                    throw new InvalidOperationException("Queue status response is empty.");
                }

                ticket.Status = ParseStatus(queueResponse.status, ticket.Status);

                if (ticket.Status == MatchTicketStatus.Cancelled)
                {
                    throw new OperationCanceledException("Ticket has been cancelled on backend.", token);
                }

                if (ticket.Status == MatchTicketStatus.Failed)
                {
                    string message = string.IsNullOrWhiteSpace(queueResponse.message)
                        ? "Matchmaking failed on backend."
                        : queueResponse.message;
                    throw new InvalidOperationException(message);
                }

                string matchId = string.IsNullOrWhiteSpace(queueResponse.matchId) ? null : queueResponse.matchId;
                if (string.IsNullOrWhiteSpace(matchId))
                {
                    await Task.Delay(_pollInterval, token);
                    continue;
                }

                if (ticket.Status == MatchTicketStatus.WaitingForPlayers || ticket.Status == MatchTicketStatus.Searching || ticket.Status == MatchTicketStatus.Unknown)
                {
                    ticket.Status = MatchTicketStatus.MatchFound;
                }

                string serverResponseText = await SendAsync(
                    HttpMethod.Get,
                    $"/matches/{Uri.EscapeDataString(matchId)}/server",
                    null,
                    token);

                ServerInfoResponseDto serverResponse = Deserialize<ServerInfoResponseDto>(serverResponseText);

                if (serverResponse == null)
                {
                    ticket.Status = MatchTicketStatus.StartingServer;
                    await Task.Delay(_pollInterval, token);
                    continue;
                }

                bool isReady = serverResponse.ready || string.IsNullOrWhiteSpace(serverResponse.sessionName) == false;
                if (!isReady)
                {
                    ticket.Status = MatchTicketStatus.StartingServer;
                    await Task.Delay(_pollInterval, token);
                    continue;
                }

                ticket.Status = MatchTicketStatus.ServerReady;

                return new ServerConnectionInfo
                {
                    MatchId = string.IsNullOrWhiteSpace(serverResponse.matchId) ? matchId : serverResponse.matchId,
                    SessionName = serverResponse.sessionName,
                    Region = string.IsNullOrWhiteSpace(serverResponse.region) ? _settings.DefaultRegion : serverResponse.region,
                    SceneName = string.IsNullOrWhiteSpace(serverResponse.sceneName) ? _settings.DefaultSceneName : serverResponse.sceneName,
                    MaxPlayers = Mathf.Clamp(
                        serverResponse.maxPlayers > 0 ? serverResponse.maxPlayers : _settings.DefaultMaxPlayers,
                        2,
                        BootstrapConfig.TargetMaxPlayers),
                    Address = string.IsNullOrWhiteSpace(serverResponse.address) ? null : serverResponse.address,
                    Port = serverResponse.port > 0 ? serverResponse.port : null,
                    AuthToken = string.IsNullOrWhiteSpace(serverResponse.authToken) ? null : serverResponse.authToken
                };
            }
        }

        public void Dispose()
        {
            if (_ownsClient)
            {
                _httpClient.Dispose();
            }
        }

        private async Task<string> SendAsync(HttpMethod method, string relativePath, string payload, CancellationToken token)
        {
            Uri endpoint = BuildUri(relativePath);
            using (HttpRequestMessage request = new HttpRequestMessage(method, endpoint))
            {
                if (string.IsNullOrEmpty(payload) == false)
                {
                    request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
                }

                if (string.IsNullOrWhiteSpace(_settings.AuthToken) == false)
                {
                    request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _settings.AuthToken);
                }

                using (HttpResponseMessage response = await _httpClient.SendAsync(request, token))
                {
                    string responseText = response.Content == null ? string.Empty : await response.Content.ReadAsStringAsync();

                    if (response.IsSuccessStatusCode == false)
                    {
                        string message = $"HTTP {(int)response.StatusCode} on {endpoint}: {responseText}";
                        throw new HttpRequestException(message);
                    }

                    return responseText;
                }
            }
        }

        private Uri BuildUri(string relativePath)
        {
            string trimmedBase = _settings.BaseUrl.TrimEnd('/');
            string trimmedPath = relativePath.StartsWith("/", StringComparison.Ordinal) ? relativePath : "/" + relativePath;
            return new Uri(trimmedBase + trimmedPath, UriKind.Absolute);
        }

        private static MatchTicketStatus ParseStatus(string rawStatus, MatchTicketStatus fallback)
        {
            if (string.IsNullOrWhiteSpace(rawStatus))
            {
                return fallback;
            }

            switch (rawStatus.Trim().ToLowerInvariant())
            {
                case "queued":
                case "searching":
                    return MatchTicketStatus.Searching;
                case "waiting":
                case "waiting_for_players":
                case "waitingforplayers":
                    return MatchTicketStatus.WaitingForPlayers;
                case "matched":
                case "match_found":
                case "matchfound":
                    return MatchTicketStatus.MatchFound;
                case "starting":
                case "starting_server":
                case "startingserver":
                    return MatchTicketStatus.StartingServer;
                case "ready":
                case "server_ready":
                case "serverready":
                    return MatchTicketStatus.ServerReady;
                case "cancelled":
                case "canceled":
                    return MatchTicketStatus.Cancelled;
                case "failed":
                case "error":
                    return MatchTicketStatus.Failed;
                default:
                    return fallback;
            }
        }

        private static DateTime ParseDate(string rawDate)
        {
            if (string.IsNullOrWhiteSpace(rawDate))
            {
                return DateTime.UtcNow;
            }

            if (DateTime.TryParse(rawDate, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out DateTime value))
            {
                return value.ToUniversalTime();
            }

            return DateTime.UtcNow;
        }

        private static T Deserialize<T>(string json) where T : class
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            try
            {
                return JsonUtility.FromJson<T>(json);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"Failed to parse JSON into {typeof(T).Name}: {ex.Message}");
                return null;
            }
        }

        [Serializable]
        private sealed class QueueRequestDto
        {
            public string playerId;
            public string gameMode;
        }

        [Serializable]
        private sealed class QueueTicketResponseDto
        {
            public string ticketId;
            public string playerId;
            public string status;
            public string createdAt;
        }

        [Serializable]
        private sealed class QueueStatusResponseDto
        {
            public string ticketId;
            public string status;
            public string matchId;
            public string message;
        }

        [Serializable]
        private sealed class ServerInfoResponseDto
        {
            public string matchId;
            public string sessionName;
            public string region;
            public string sceneName;
            public int maxPlayers;
            public string address;
            public int port;
            public string authToken;
            public bool ready;
        }
    }
}
