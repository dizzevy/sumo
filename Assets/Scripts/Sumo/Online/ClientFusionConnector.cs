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

        public NetworkRunner Runner => _runner;

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

            _runner = CreateRunner();

            INetworkSceneManager sceneManager = _runner.GetComponent<NetworkSceneManagerDefault>();

            StartGameArgs args = new StartGameArgs
            {
                GameMode = GameMode.Client,
                SessionName = connectionInfo.SessionName,
                PlayerCount = Mathf.Max(2, connectionInfo.MaxPlayers),
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

            return Task.CompletedTask;
        }

        private void OnDestroy()
        {
            _ = DisconnectAsync();
        }

        private NetworkRunner CreateRunner()
        {
            GameObject runnerObject = new GameObject(runnerObjectName);
            if (dontDestroyOnLoad)
            {
                DontDestroyOnLoad(runnerObject);
            }

            NetworkRunner runner = runnerObject.AddComponent<NetworkRunner>();
            runner.ProvideInput = true;

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
            return runner;
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
            }
        }

        public void OnConnectedToServer(NetworkRunner runner)
        {
        }

        public void OnDisconnectedFromServer(NetworkRunner runner, NetDisconnectReason reason)
        {
            Debug.LogWarning($"Client disconnected from dedicated server: {reason}");
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
    }
}
