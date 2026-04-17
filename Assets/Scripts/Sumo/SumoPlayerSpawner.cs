using System;
using System.Collections.Generic;
using Fusion;
using Fusion.Sockets;
using UnityEngine;

namespace Sumo
{
    [DisallowMultipleComponent]
    [RequireComponent(typeof(NetworkRunner))]
    public sealed class SumoPlayerSpawner : MonoBehaviour, INetworkRunnerCallbacks
    {
        [SerializeField] private NetworkRunner runner;
        [SerializeField] private NetworkPrefabRef playerPrefab;

        [Header("Spawn")]
        [SerializeField] private Transform[] spawnPoints;
        [SerializeField] private Vector3 fallbackSpawnCenter = Vector3.zero;
        [SerializeField] private float fallbackSpawnRadius = 8f;

        private readonly Dictionary<PlayerRef, NetworkObject> _spawnedPlayers = new Dictionary<PlayerRef, NetworkObject>();
        private bool _callbacksRegistered;
        private int _spawnCursor;

        private void Reset()
        {
            runner = GetComponent<NetworkRunner>();
        }

        private void Awake()
        {
            if (runner == null)
            {
                runner = GetComponent<NetworkRunner>();
            }
        }

        private void OnEnable()
        {
            RegisterCallbacks();
        }

        private void OnDisable()
        {
            UnregisterCallbacks();
        }

        public void OnPlayerJoined(NetworkRunner runnerInstance, PlayerRef player)
        {
            if (!runnerInstance.IsServer)
            {
                return;
            }

            if (_spawnedPlayers.ContainsKey(player))
            {
                return;
            }

            if (runnerInstance.TryGetPlayerObject(player, out NetworkObject existingObject) && existingObject != null)
            {
                _spawnedPlayers[player] = existingObject;
                return;
            }

            if (!playerPrefab.IsValid)
            {
                Debug.LogError("SumoPlayerSpawner: Player Prefab is not assigned in Network Prefab Table.");
                return;
            }

            Vector3 spawnPosition = GetSpawnPosition();
            NetworkObject playerObject = runnerInstance.Spawn(playerPrefab, spawnPosition, Quaternion.identity, player);

            if (playerObject == null)
            {
                Debug.LogError("SumoPlayerSpawner: Runner.Spawn returned null for player prefab.");
                return;
            }

            _spawnedPlayers[player] = playerObject;
            runnerInstance.SetPlayerObject(player, playerObject);
        }

        public void OnPlayerLeft(NetworkRunner runnerInstance, PlayerRef player)
        {
            if (!runnerInstance.IsServer)
            {
                return;
            }

            NetworkObject playerObject = null;
            if (_spawnedPlayers.TryGetValue(player, out NetworkObject trackedObject))
            {
                playerObject = trackedObject;
                _spawnedPlayers.Remove(player);
            }
            else
            {
                runnerInstance.TryGetPlayerObject(player, out playerObject);
            }

            if (playerObject != null)
            {
                runnerInstance.Despawn(playerObject);
            }

            runnerInstance.SetPlayerObject(player, null);
        }

        public void OnInput(NetworkRunner runnerInstance, NetworkInput input)
        {
        }

        public void OnInputMissing(NetworkRunner runnerInstance, PlayerRef player, NetworkInput input)
        {
        }

        public void OnShutdown(NetworkRunner runnerInstance, ShutdownReason shutdownReason)
        {
            _spawnedPlayers.Clear();
            _spawnCursor = 0;
            UnregisterCallbacks();
        }

        public void OnConnectedToServer(NetworkRunner runnerInstance)
        {
        }

        public void OnDisconnectedFromServer(NetworkRunner runnerInstance, NetDisconnectReason reason)
        {
        }

        public void OnConnectRequest(NetworkRunner runnerInstance, NetworkRunnerCallbackArgs.ConnectRequest request, byte[] token)
        {
            if (runnerInstance.IsServer)
            {
                request.Accept();
            }
        }

        public void OnConnectFailed(NetworkRunner runnerInstance, NetAddress remoteAddress, NetConnectFailedReason reason)
        {
        }

        public void OnUserSimulationMessage(NetworkRunner runnerInstance, SimulationMessagePtr message)
        {
        }

        public void OnSessionListUpdated(NetworkRunner runnerInstance, List<SessionInfo> sessionList)
        {
        }

        public void OnCustomAuthenticationResponse(NetworkRunner runnerInstance, Dictionary<string, object> data)
        {
        }

        public void OnHostMigration(NetworkRunner runnerInstance, HostMigrationToken hostMigrationToken)
        {
        }

        public void OnReliableDataReceived(NetworkRunner runnerInstance, PlayerRef player, ReliableKey key, ArraySegment<byte> data)
        {
        }

        public void OnReliableDataProgress(NetworkRunner runnerInstance, PlayerRef player, ReliableKey key, float progress)
        {
        }

        public void OnSceneLoadDone(NetworkRunner runnerInstance)
        {
        }

        public void OnSceneLoadStart(NetworkRunner runnerInstance)
        {
        }

        public void OnObjectEnterAOI(NetworkRunner runnerInstance, NetworkObject obj, PlayerRef player)
        {
        }

        public void OnObjectExitAOI(NetworkRunner runnerInstance, NetworkObject obj, PlayerRef player)
        {
        }

        private Vector3 GetSpawnPosition()
        {
            if (spawnPoints != null && spawnPoints.Length > 0)
            {
                int checkedCount = 0;

                while (checkedCount < spawnPoints.Length)
                {
                    Transform point = spawnPoints[_spawnCursor % spawnPoints.Length];
                    _spawnCursor++;
                    checkedCount++;

                    if (point != null)
                    {
                        return point.position;
                    }
                }
            }

            float angle = _spawnCursor * 2.39996323f;
            float radius = Mathf.Min(fallbackSpawnRadius, 1.5f + _spawnCursor * 0.75f);
            _spawnCursor++;

            return fallbackSpawnCenter + new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * radius;
        }

        private void RegisterCallbacks()
        {
            if (_callbacksRegistered || runner == null)
            {
                return;
            }

            runner.AddCallbacks(this);
            _callbacksRegistered = true;
        }

        private void UnregisterCallbacks()
        {
            if (!_callbacksRegistered || runner == null)
            {
                return;
            }

            runner.RemoveCallbacks(this);
            _callbacksRegistered = false;
        }

        private void OnValidate()
        {
            fallbackSpawnRadius = Mathf.Max(0f, fallbackSpawnRadius);
        }
    }
}
