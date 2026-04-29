using System;
using System.Threading;
using System.Threading.Tasks;
using Fusion;
using UnityEngine;

namespace Sumo.Online
{
    public enum MatchmakingClientState
    {
        Idle = 0,
        Searching = 1,
        WaitingForPlayers = 2,
        MatchFound = 3,
        StartingServer = 4,
        Connecting = 5,
        Connected = 6,
        Failed = 7
    }

    [DisallowMultipleComponent]
    public sealed class MatchmakingClient : MonoBehaviour
    {
        [Header("References")]
        [SerializeField] private BootstrapConfig bootstrapConfig;
        [SerializeField] private ClientFusionConnector fusionConnector;

        [Header("Service Selection")]
        [SerializeField] private bool forceMockMatchmaking;

        [Header("Runtime")]
        [SerializeField] private float statusPollIntervalSeconds = 0.2f;
        [SerializeField] private float connectRetryWindowSeconds = 10.0f;
        [SerializeField] private float connectRetryDelaySeconds = 0.5f;

        private const string PlayerIdKey = "sumo.player.id";

        private IMatchmakingService _service;
        private CancellationTokenSource _searchCts;
        private MatchTicket _activeTicket;
        private Task _currentFlowTask;

        public event Action<MatchmakingClientState, string> StateChanged;

        public MatchmakingClientState CurrentState { get; private set; } = MatchmakingClientState.Idle;
        public string CurrentStateText { get; private set; } = "Idle";
        public bool IsSearching => _currentFlowTask != null && _currentFlowTask.IsCompleted == false;

        public void Configure(BootstrapConfig config, ClientFusionConnector connector, bool forceMock)
        {
            bootstrapConfig = config;
            fusionConnector = connector;
            forceMockMatchmaking = forceMock;
        }

        private void Awake()
        {
            if (fusionConnector == null)
            {
                fusionConnector = GetComponent<ClientFusionConnector>();
            }

            SetState(MatchmakingClientState.Idle, "Idle", true);
        }

        private void OnDestroy()
        {
            if (_searchCts != null)
            {
                _searchCts.Cancel();
            }

            if (fusionConnector != null)
            {
                _ = fusionConnector.DisconnectAsync();
            }

            if (_service is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }

        public void FindGame()
        {
            if (IsSearching)
            {
                return;
            }

            _currentFlowTask = RunFindGameFlowAsync();
        }

        public void CancelSearch()
        {
            _ = CancelSearchAsync();
        }

        private async Task RunFindGameFlowAsync()
        {
            _searchCts = new CancellationTokenSource();
            CancellationToken token = _searchCts.Token;

            try
            {
                if (_service == null)
                {
                    _service = CreateService();
                }

                SetState(MatchmakingClientState.Searching, "Searching...");
                _activeTicket = await _service.FindMatchAsync(token);

                if (_activeTicket != null)
                {
                    ApplyStateFromTicket(_activeTicket.Status);
                }
                else
                {
                    SetState(MatchmakingClientState.WaitingForPlayers, "Waiting for players...");
                }

                ServerConnectionInfo connection = await WaitForServerWithProgressAsync(_activeTicket, token);

                SetState(MatchmakingClientState.MatchFound, "Match found");
                SetState(MatchmakingClientState.Connecting, "Connecting...");

                if (fusionConnector == null)
                {
                    throw new InvalidOperationException("ClientFusionConnector reference is missing.");
                }

                StartGameResult result = await ConnectWithRetryAsync(connection, token);

                if (result.Ok)
                {
                    SetState(MatchmakingClientState.Connected, "Connected");
                    return;
                }

                string error = BuildConnectionError(result);

                SetState(MatchmakingClientState.Failed, $"Failed: {error}");
                await fusionConnector.DisconnectAsync();
            }
            catch (OperationCanceledException)
            {
                SetState(MatchmakingClientState.Idle, "Idle");
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                SetState(MatchmakingClientState.Failed, $"Failed: {ex.Message}");

                if (fusionConnector != null)
                {
                    await fusionConnector.DisconnectAsync();
                }
            }
            finally
            {
                _activeTicket = null;
                DisposeSearchToken();
                _currentFlowTask = null;
            }
        }

        private async Task<ServerConnectionInfo> WaitForServerWithProgressAsync(MatchTicket ticket, CancellationToken token)
        {
            if (ticket == null)
            {
                throw new ArgumentNullException(nameof(ticket));
            }

            Task<ServerConnectionInfo> waitTask = _service.WaitForServerAsync(ticket, token);
            MatchTicketStatus lastTicketStatus = ticket.Status;
            ApplyStateFromTicket(lastTicketStatus);

            int pollDelayMs = Mathf.Max(50, Mathf.RoundToInt(statusPollIntervalSeconds * 1000f));

            while (waitTask.IsCompleted == false)
            {
                token.ThrowIfCancellationRequested();

                if (ticket.Status != lastTicketStatus)
                {
                    lastTicketStatus = ticket.Status;
                    ApplyStateFromTicket(lastTicketStatus);
                }

                await Task.Delay(pollDelayMs, token);
            }

            return await waitTask;
        }

        private async Task<StartGameResult> ConnectWithRetryAsync(ServerConnectionInfo connection, CancellationToken token)
        {
            float retryWindow = Mathf.Max(0f, connectRetryWindowSeconds);
            int retryDelayMs = Mathf.Max(100, Mathf.RoundToInt(connectRetryDelaySeconds * 1000f));
            DateTime retryDeadlineUtc = DateTime.UtcNow.AddSeconds(retryWindow);

            while (true)
            {
                token.ThrowIfCancellationRequested();

                StartGameResult result = await fusionConnector.ConnectAsync(connection, token);
                if (result.Ok || !ShouldRetryConnection(result) || DateTime.UtcNow >= retryDeadlineUtc)
                {
                    return result;
                }

                SetState(MatchmakingClientState.Connecting, "Connecting...");

                int remainingMs = Mathf.Max(0, Mathf.RoundToInt((float)(retryDeadlineUtc - DateTime.UtcNow).TotalMilliseconds));
                await Task.Delay(Mathf.Min(retryDelayMs, remainingMs), token);
            }
        }

        private async Task CancelSearchAsync()
        {
            if (_searchCts == null)
            {
                SetState(MatchmakingClientState.Idle, "Idle");
                return;
            }

            _searchCts.Cancel();

            if (_service != null && _activeTicket != null)
            {
                await _service.CancelFindMatchAsync(_activeTicket);
            }

            if (fusionConnector != null)
            {
                await fusionConnector.DisconnectAsync();
            }

            SetState(MatchmakingClientState.Idle, "Idle");
        }

        private IMatchmakingService CreateService()
        {
            bool useMock = forceMockMatchmaking;

            if (!useMock && bootstrapConfig == null)
            {
                useMock = true;
            }

            if (!useMock && bootstrapConfig != null && bootstrapConfig.UseMockMatchmakingInEditor && Application.isEditor)
            {
                useMock = true;
            }

            if (!useMock && bootstrapConfig != null && string.IsNullOrWhiteSpace(bootstrapConfig.ProductionBackendBaseUrl))
            {
                useMock = true;
            }

            string playerId = GetOrCreatePlayerId();

            if (useMock)
            {
                MockMatchmakingService.Settings settings = new MockMatchmakingService.Settings
                {
                    PlayerId = playerId,
                    SessionName = bootstrapConfig != null ? bootstrapConfig.MockSessionName : "sumo_match_001",
                    MatchId = bootstrapConfig != null ? bootstrapConfig.MockMatchId : "match_001",
                    Region = bootstrapConfig != null ? bootstrapConfig.MockRegion : "local",
                    SceneName = bootstrapConfig != null ? bootstrapConfig.DefaultSceneName : "location_test",
                    MaxPlayers = bootstrapConfig != null ? bootstrapConfig.DefaultMaxPlayers : BootstrapConfig.TargetMaxPlayers,
                    SearchDelaySeconds = bootstrapConfig != null ? bootstrapConfig.MockSearchDelaySeconds : 0.8f,
                    WaitForPlayersDelaySeconds = bootstrapConfig != null ? bootstrapConfig.MockWaitForPlayersDelaySeconds : 2f,
                    ServerBootDelaySeconds = bootstrapConfig != null ? bootstrapConfig.MockServerBootDelaySeconds : 1.5f
                };

                return new MockMatchmakingService(settings);
            }

            ProductionMatchmakingService.Settings productionSettings = new ProductionMatchmakingService.Settings
            {
                BaseUrl = bootstrapConfig != null ? bootstrapConfig.ProductionBackendBaseUrl : string.Empty,
                PlayerId = playerId,
                GameMode = "sumo",
                DefaultSceneName = bootstrapConfig != null ? bootstrapConfig.DefaultSceneName : "location_test",
                DefaultRegion = "auto",
                DefaultMaxPlayers = bootstrapConfig != null ? bootstrapConfig.DefaultMaxPlayers : BootstrapConfig.TargetMaxPlayers,
                PollIntervalSeconds = bootstrapConfig != null ? bootstrapConfig.ProductionPollIntervalSeconds : 1f,
                AuthToken = null
            };

            return new ProductionMatchmakingService(productionSettings);
        }

        private static bool ShouldRetryConnection(StartGameResult result)
        {
            if (result.Ok)
            {
                return false;
            }

            return IsGameDoesNotExist(result);
        }

        private static string BuildConnectionError(StartGameResult result)
        {
            if (IsGameDoesNotExist(result))
            {
                return "Dedicated server session is not ready. Start the server first or wait until it finishes booting.";
            }

            return string.IsNullOrWhiteSpace(result.ErrorMessage)
                ? result.ShutdownReason.ToString()
                : result.ErrorMessage;
        }

        private static bool IsGameDoesNotExist(StartGameResult result)
        {
            string error = result.ErrorMessage ?? string.Empty;
            string reason = result.ShutdownReason.ToString();

            return error.IndexOf("Game does not exist", StringComparison.OrdinalIgnoreCase) >= 0
                   || error.IndexOf("32758", StringComparison.OrdinalIgnoreCase) >= 0
                   || reason.IndexOf("NotFound", StringComparison.OrdinalIgnoreCase) >= 0
                   || (reason.IndexOf("Game", StringComparison.OrdinalIgnoreCase) >= 0
                       && reason.IndexOf("Exist", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private string GetOrCreatePlayerId()
        {
            if (PlayerPrefs.HasKey(PlayerIdKey))
            {
                string existing = PlayerPrefs.GetString(PlayerIdKey);
                if (string.IsNullOrWhiteSpace(existing) == false)
                {
                    return existing;
                }
            }

            string prefix = bootstrapConfig != null ? bootstrapConfig.PlayerIdPrefix : "player";
            string playerId = $"{prefix}_{Guid.NewGuid():N}";
            PlayerPrefs.SetString(PlayerIdKey, playerId);
            PlayerPrefs.Save();
            return playerId;
        }

        private void ApplyStateFromTicket(MatchTicketStatus status)
        {
            switch (status)
            {
                case MatchTicketStatus.Searching:
                    SetState(MatchmakingClientState.Searching, "Searching...");
                    break;
                case MatchTicketStatus.WaitingForPlayers:
                    SetState(MatchmakingClientState.WaitingForPlayers, "Waiting for players...");
                    break;
                case MatchTicketStatus.MatchFound:
                    SetState(MatchmakingClientState.MatchFound, "Match found");
                    break;
                case MatchTicketStatus.StartingServer:
                    SetState(MatchmakingClientState.StartingServer, "Starting server...");
                    break;
                case MatchTicketStatus.ServerReady:
                    SetState(MatchmakingClientState.Connecting, "Connecting...");
                    break;
                case MatchTicketStatus.Cancelled:
                    SetState(MatchmakingClientState.Idle, "Idle");
                    break;
                case MatchTicketStatus.Failed:
                    SetState(MatchmakingClientState.Failed, "Failed");
                    break;
                default:
                    break;
            }
        }

        private void SetState(MatchmakingClientState state, string text, bool forceNotify = false)
        {
            bool changed = forceNotify || CurrentState != state || string.Equals(CurrentStateText, text, StringComparison.Ordinal) == false;
            CurrentState = state;
            CurrentStateText = text;

            if (!changed)
            {
                return;
            }

            StateChanged?.Invoke(CurrentState, CurrentStateText);
        }

        private void DisposeSearchToken()
        {
            if (_searchCts == null)
            {
                return;
            }

            _searchCts.Dispose();
            _searchCts = null;
        }
    }
}
