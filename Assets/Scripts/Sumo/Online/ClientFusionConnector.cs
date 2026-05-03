using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Fusion;
using Fusion.Addons.Physics;
using Fusion.Sockets;
using UnityEngine;

namespace Sumo.Online
{
    [DisallowMultipleComponent]
    public sealed class ClientFusionConnector : MonoBehaviour, INetworkRunnerCallbacks
    {
        [SerializeField] private string runnerObjectName = "ClientNetworkRunner";
        [SerializeField] private bool dontDestroyOnLoad = true;

        private NetworkRunner _runner;
        private bool _connectionClosedNotified;
        private readonly List<SessionInfo> _latestSessionList = new List<SessionInfo>();
        private TaskCompletionSource<List<SessionInfo>> _sessionListWaiter;

        public NetworkRunner Runner => _runner;
        public event Action ConnectionClosed;

        public async Task<ServerConnectionInfo> FindAvailableServerAsync(ServerSearchOptions options, CancellationToken token)
        {
            options = options ?? new ServerSearchOptions();

            await DisconnectAsync();

            _runner = CreateRunner(false);
            _latestSessionList.Clear();
            _sessionListWaiter = CreateSessionListWaiter();

            StartGameResult lobbyResult = await _runner.JoinSessionLobby(
                SessionLobby.ClientServer,
                null,
                null,
                null,
                null,
                token,
                true);

            if (!lobbyResult.Ok)
            {
                string error = string.IsNullOrWhiteSpace(lobbyResult.ErrorMessage)
                    ? lobbyResult.ShutdownReason.ToString()
                    : lobbyResult.ErrorMessage;

                await DisconnectAsync();
                throw new InvalidOperationException($"Failed to join server lobby: {error}");
            }

            int pollDelayMs = Mathf.Max(100, Mathf.RoundToInt(Mathf.Max(0.1f, options.PollIntervalSeconds) * 1000f));

            while (true)
            {
                token.ThrowIfCancellationRequested();

                if (TrySelectServer(_latestSessionList, options, out ServerConnectionInfo connectionInfo))
                {
                    await DisconnectAsync();
                    return connectionInfo;
                }

                Task<List<SessionInfo>> updateTask = _sessionListWaiter.Task;
                Task delayTask = Task.Delay(pollDelayMs, token);
                Task completedTask = await Task.WhenAny(updateTask, delayTask);

                if (completedTask == updateTask)
                {
                    _sessionListWaiter = CreateSessionListWaiter();
                }
            }
        }

        public async Task<StartGameResult> ConnectAsync(ServerConnectionInfo connectionInfo, CancellationToken token)
        {
            if (connectionInfo == null)
            {
                throw new ArgumentNullException(nameof(connectionInfo));
            }

            if (string.IsNullOrWhiteSpace(connectionInfo.SessionName))
            {
                throw new InvalidOperationException("ServerConnectionInfo.SessionName is required.");
            }

            await DisconnectAsync();

            _runner = CreateRunner(true);

            INetworkSceneManager sceneManager = _runner.GetComponent<NetworkSceneManagerDefault>();

            StartGameArgs args = new StartGameArgs
            {
                GameMode = GameMode.Client,
                SessionName = connectionInfo.SessionName,
                PlayerCount = Mathf.Clamp(
                    connectionInfo.MaxPlayers > 0 ? connectionInfo.MaxPlayers : BootstrapConfig.TargetMaxPlayers,
                    2,
                    BootstrapConfig.TargetMaxPlayers),
                SceneManager = sceneManager,
                EnableClientSessionCreation = false,
                StartGameCancellationToken = token
            };

            if (!string.IsNullOrWhiteSpace(connectionInfo.AuthToken))
            {
                args.ConnectionToken = Encoding.UTF8.GetBytes(connectionInfo.AuthToken);
            }

            if (TryCreateAddress(connectionInfo, out NetAddress netAddress))
            {
                args.Address = netAddress;
            }

            StartGameResult result = await _runner.StartGame(args);

            if (!result.Ok)
            {
                await DisconnectAsync();
            }

            return result;
        }

        public Task DisconnectAsync()
        {
            if (_runner == null)
            {
                return Task.CompletedTask;
            }

            _runner.RemoveCallbacks(this);
            _runner.Shutdown(true, ShutdownReason.Ok, false);
            _runner = null;
            NotifyConnectionClosed();

            return Task.CompletedTask;
        }

        private void OnDestroy()
        {
            _ = DisconnectAsync();
        }

        private NetworkRunner CreateRunner(bool provideInput)
        {
            GameObject runnerObject = new GameObject(runnerObjectName);
            if (dontDestroyOnLoad)
            {
                DontDestroyOnLoad(runnerObject);
            }

            NetworkRunner runner = runnerObject.AddComponent<NetworkRunner>();
            runner.ProvideInput = provideInput;

            if (runnerObject.GetComponent<NetworkSceneManagerDefault>() == null)
            {
                runnerObject.AddComponent<NetworkSceneManagerDefault>();
            }

            RunnerSimulatePhysics3D physicsSimulator = runnerObject.GetComponent<RunnerSimulatePhysics3D>();
            if (physicsSimulator == null)
            {
                physicsSimulator = runnerObject.AddComponent<RunnerSimulatePhysics3D>();
            }

            physicsSimulator.ClientPhysicsSimulation = ClientPhysicsSimulation.SimulateAlways;

            runner.AddCallbacks(this);
            _connectionClosedNotified = false;
            return runner;
        }

        private static TaskCompletionSource<List<SessionInfo>> CreateSessionListWaiter()
        {
            return new TaskCompletionSource<List<SessionInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        private static bool TrySelectServer(List<SessionInfo> sessions, ServerSearchOptions options, out ServerConnectionInfo connectionInfo)
        {
            connectionInfo = null;

            if (sessions == null || sessions.Count == 0)
            {
                return false;
            }

            SessionInfo bestSession = null;
            int bestPlayerCount = int.MinValue;
            int bestOpenSlots = int.MinValue;

            for (int i = 0; i < sessions.Count; i++)
            {
                SessionInfo session = sessions[i];
                if (!IsJoinableSumoServer(session, options))
                {
                    continue;
                }

                int maxPeers = session.MaxPlayers > 0 ? session.MaxPlayers : Mathf.Max(2, options.MaxPlayers);
                int openSlots = maxPeers - session.PlayerCount;
                if (bestSession == null
                    || session.PlayerCount > bestPlayerCount
                    || (session.PlayerCount == bestPlayerCount && openSlots > bestOpenSlots))
                {
                    bestSession = session;
                    bestPlayerCount = session.PlayerCount;
                    bestOpenSlots = openSlots;
                }
            }

            if (bestSession == null)
            {
                return false;
            }

            connectionInfo = BuildConnectionInfo(bestSession, options);
            return true;
        }

        private static bool IsJoinableSumoServer(SessionInfo session, ServerSearchOptions options)
        {
            if (session == null || !session.IsValid || !session.IsOpen || !session.IsVisible)
            {
                return false;
            }

            int maxPeers = session.MaxPlayers > 0 ? session.MaxPlayers : Mathf.Max(2, options.MaxPlayers);
            if (maxPeers > 0 && session.PlayerCount >= maxPeers)
            {
                return false;
            }

            if (TryGetSessionString(session, "mode", out string mode)
                && !string.Equals(mode, options.GameMode, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (TryGetSessionInt(session, "accepting", out int accepting) && accepting <= 0)
            {
                return false;
            }

            return true;
        }

        private static ServerConnectionInfo BuildConnectionInfo(SessionInfo session, ServerSearchOptions options)
        {
            TryGetSessionString(session, "matchId", out string matchId);
            TryGetSessionString(session, "map", out string sceneName);
            TryGetSessionString(session, "region", out string region);
            TryGetSessionInt(session, "maxPlayers", out int maxPlayers);

            if (maxPlayers <= 0)
            {
                maxPlayers = session.MaxPlayers > 0 ? session.MaxPlayers : options.MaxPlayers;
            }

            return new ServerConnectionInfo
            {
                MatchId = string.IsNullOrWhiteSpace(matchId) ? session.Name : matchId,
                SessionName = session.Name,
                Region = string.IsNullOrWhiteSpace(region)
                    ? (string.IsNullOrWhiteSpace(session.Region) ? options.Region : session.Region)
                    : region,
                SceneName = string.IsNullOrWhiteSpace(sceneName) ? options.SceneName : sceneName,
                MaxPlayers = Mathf.Clamp(maxPlayers, 2, BootstrapConfig.TargetMaxPlayers),
                Address = null,
                Port = null,
                AuthToken = null
            };
        }

        private static bool TryGetSessionString(SessionInfo session, string key, out string value)
        {
            value = null;

            if (session == null || session.Properties == null || !session.Properties.TryGetValue(key, out SessionProperty property))
            {
                return false;
            }

            object rawValue = property.PropertyValue;
            if (rawValue == null)
            {
                return false;
            }

            value = rawValue.ToString();
            return !string.IsNullOrWhiteSpace(value);
        }

        private static bool TryGetSessionInt(SessionInfo session, string key, out int value)
        {
            value = 0;

            if (!TryGetSessionString(session, key, out string rawValue))
            {
                return false;
            }

            return int.TryParse(rawValue, out value);
        }

        private static bool TryCreateAddress(ServerConnectionInfo connectionInfo, out NetAddress address)
        {
            address = default;

            if (!connectionInfo.HasAddress && !connectionInfo.HasPort)
            {
                return false;
            }

            ushort port = (ushort)Mathf.Clamp(connectionInfo.Port ?? 0, 0, ushort.MaxValue);

            try
            {
                if (connectionInfo.HasAddress)
                {
                    address = NetAddress.CreateFromIpPort(connectionInfo.Address, port);
                }
                else
                {
                    address = NetAddress.Any(port);
                }

                return address.IsValid;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"Failed to parse dedicated server address '{connectionInfo.Address}:{port}'. {ex.Message}");
                return false;
            }
        }

        public void OnPlayerJoined(NetworkRunner runner, PlayerRef player)
        {
        }

        public void OnPlayerLeft(NetworkRunner runner, PlayerRef player)
        {
        }

        public void OnInput(NetworkRunner runner, NetworkInput input)
        {
        }

        public void OnInputMissing(NetworkRunner runner, PlayerRef player, NetworkInput input)
        {
        }

        public void OnShutdown(NetworkRunner runner, ShutdownReason shutdownReason)
        {
            if (runner == _runner)
            {
                _runner = null;
                NotifyConnectionClosed();
            }
        }

        public void OnConnectedToServer(NetworkRunner runner)
        {
        }

        public void OnDisconnectedFromServer(NetworkRunner runner, NetDisconnectReason reason)
        {
            Debug.LogWarning($"Client disconnected from dedicated server: {reason}");
            if (runner == _runner)
            {
                NotifyConnectionClosed();
            }
        }

        public void OnConnectRequest(NetworkRunner runner, NetworkRunnerCallbackArgs.ConnectRequest request, byte[] token)
        {
        }

        public void OnConnectFailed(NetworkRunner runner, NetAddress remoteAddress, NetConnectFailedReason reason)
        {
            Debug.LogWarning($"Client connect failed: {reason}");
        }

        public void OnUserSimulationMessage(NetworkRunner runner, SimulationMessagePtr message)
        {
        }

        public void OnSessionListUpdated(NetworkRunner runner, List<SessionInfo> sessionList)
        {
            if (runner != _runner || sessionList == null)
            {
                return;
            }

            _latestSessionList.Clear();
            _latestSessionList.AddRange(sessionList);
            _sessionListWaiter?.TrySetResult(new List<SessionInfo>(_latestSessionList));
        }

        public void OnCustomAuthenticationResponse(NetworkRunner runner, Dictionary<string, object> data)
        {
        }

        public void OnHostMigration(NetworkRunner runner, HostMigrationToken hostMigrationToken)
        {
        }

        public void OnReliableDataReceived(NetworkRunner runner, PlayerRef player, ReliableKey key, ArraySegment<byte> data)
        {
        }

        public void OnReliableDataProgress(NetworkRunner runner, PlayerRef player, ReliableKey key, float progress)
        {
        }

        public void OnSceneLoadDone(NetworkRunner runner)
        {
        }

        public void OnSceneLoadStart(NetworkRunner runner)
        {
        }

        public void OnObjectEnterAOI(NetworkRunner runner, NetworkObject obj, PlayerRef player)
        {
        }

        public void OnObjectExitAOI(NetworkRunner runner, NetworkObject obj, PlayerRef player)
        {
        }

        private void NotifyConnectionClosed()
        {
            if (_connectionClosedNotified)
            {
                return;
            }

            _connectionClosedNotified = true;
            ConnectionClosed?.Invoke();
        }
    }
}
