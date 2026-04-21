using System;
using System.Collections.Generic;
using Fusion;
using Fusion.Sockets;
using Sumo.Gameplay;
using UnityEngine;

namespace Sumo.Online
{
    [DisallowMultipleComponent]
    [RequireComponent(typeof(NetworkRunner))]
    public sealed class DedicatedServerMatchController : MonoBehaviour, INetworkRunnerCallbacks
    {
        [SerializeField] private NetworkRunner runner;
        [SerializeField] private NetworkPrefabRef playerPrefab;
        [SerializeField] private MatchRoundManager roundManager;

        [Header("Match")]
        [SerializeField] private int minimumPlayersToStart = 2;
        [SerializeField] private bool spawnOnlyWhenMinimumPlayersReached;
        [SerializeField] private bool shutdownWhenPlayersDropBelowMinimum;

        [Header("Spawn")]
        [SerializeField] private Vector3 fallbackSpawnCenter = Vector3.zero;
        [SerializeField] private float fallbackSpawnRadius = 8f;

        private readonly Dictionary<PlayerRef, NetworkObject> _spawnedPlayers = new Dictionary<PlayerRef, NetworkObject>();
        private readonly HashSet<PlayerRef> _pendingPlayers = new HashSet<PlayerRef>();
        private readonly List<SpawnPoint> _spawnPoints = new List<SpawnPoint>();

        private const float FallbackGroundProbeHeight = 20f;
        private const float FallbackSpawnYOffset = 0.6f;

        private bool _callbacksRegistered;
        private bool _sceneReady;
        private bool _matchStarted;
        private int _spawnCursor;
        private bool _loggedFallbackSpawnWarning;

        public void Initialize(NetworkRunner runnerInstance, NetworkPrefabRef prefab, int minimumPlayers, bool shutdownOnLowPlayerCount)
        {
            runner = runnerInstance;
            playerPrefab = prefab;
            minimumPlayersToStart = Mathf.Max(2, minimumPlayers);
            shutdownWhenPlayersDropBelowMinimum = shutdownOnLowPlayerCount;
            RegisterCallbacks();
        }

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

            if (!CanAcceptNewPlayers(runnerInstance))
            {
                runnerInstance.Disconnect(player, null);
                return;
            }

            if (!_sceneReady)
            {
                _pendingPlayers.Add(player);
                return;
            }

            if (spawnOnlyWhenMinimumPlayersReached && !HasMinimumPlayers(runnerInstance))
            {
                _pendingPlayers.Add(player);
                return;
            }

            SpawnPlayerIfNeeded(runnerInstance, player);
            TryStartMatch(runnerInstance);
            SpawnPendingPlayersIfPossible(runnerInstance);
        }

        public void OnPlayerLeft(NetworkRunner runnerInstance, PlayerRef player)
        {
            if (!runnerInstance.IsServer)
            {
                return;
            }

            _pendingPlayers.Remove(player);

            if (_spawnedPlayers.TryGetValue(player, out NetworkObject trackedObject))
            {
                _spawnedPlayers.Remove(player);
                if (trackedObject != null)
                {
                    runnerInstance.Despawn(trackedObject);
                }
            }
            else if (runnerInstance.TryGetPlayerObject(player, out NetworkObject playerObject) && playerObject != null)
            {
                runnerInstance.Despawn(playerObject);
            }

            runnerInstance.SetPlayerObject(player, null);

            if (_matchStarted && CountActivePlayers(runnerInstance) < minimumPlayersToStart)
            {
                if (shutdownWhenPlayersDropBelowMinimum)
                {
                    Debug.Log("DedicatedServerMatchController: player count dropped below minimum. Shutting down match.");
                    runnerInstance.Shutdown(false, ShutdownReason.Ok, false);
                }
                else
                {
                    Debug.Log("DedicatedServerMatchController: player count below minimum, waiting for new players.");
                }
            }
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
            _pendingPlayers.Clear();
            _spawnPoints.Clear();
            _sceneReady = false;
            _matchStarted = false;
            _spawnCursor = 0;
            _loggedFallbackSpawnWarning = false;

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
                if (CanAcceptNewPlayers(runnerInstance))
                {
                    request.Accept();
                }
                else
                {
                    request.Refuse();
                }
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
            if (!runnerInstance.IsServer)
            {
                return;
            }

            _sceneReady = true;
            RefreshSpawnPoints();
            ResolveRoundManagerIfNeeded();
            Debug.Log($"DedicatedServerMatchController: scene loaded. SpawnPoints={_spawnPoints.Count}.");
            TryStartMatch(runnerInstance);
            SpawnPendingPlayersIfPossible(runnerInstance);
        }

        public void OnSceneLoadStart(NetworkRunner runnerInstance)
        {
            if (!runnerInstance.IsServer)
            {
                return;
            }

            _sceneReady = false;
        }

        public void OnObjectEnterAOI(NetworkRunner runnerInstance, NetworkObject obj, PlayerRef player)
        {
        }

        public void OnObjectExitAOI(NetworkRunner runnerInstance, NetworkObject obj, PlayerRef player)
        {
        }

        private void SpawnPendingPlayersIfPossible(NetworkRunner runnerInstance)
        {
            if (!_sceneReady)
            {
                return;
            }

            if (!CanAcceptNewPlayers(runnerInstance))
            {
                if (_pendingPlayers.Count > 0)
                {
                    List<PlayerRef> pending = new List<PlayerRef>(_pendingPlayers);
                    _pendingPlayers.Clear();
                    for (int i = 0; i < pending.Count; i++)
                    {
                        runnerInstance.Disconnect(pending[i], null);
                    }
                }

                return;
            }

            if (spawnOnlyWhenMinimumPlayersReached && !HasMinimumPlayers(runnerInstance))
            {
                return;
            }

            if (_pendingPlayers.Count == 0)
            {
                return;
            }

            List<PlayerRef> toSpawn = new List<PlayerRef>(_pendingPlayers);

            for (int i = 0; i < toSpawn.Count; i++)
            {
                SpawnPlayerIfNeeded(runnerInstance, toSpawn[i]);
                _pendingPlayers.Remove(toSpawn[i]);
            }
        }

        private void SpawnPlayerIfNeeded(NetworkRunner runnerInstance, PlayerRef player)
        {
            if (!playerPrefab.IsValid)
            {
                Debug.LogError("DedicatedServerMatchController: player prefab is not assigned in Network Prefab Table.");
                return;
            }

            if (_spawnedPlayers.ContainsKey(player))
            {
                return;
            }

            if (runnerInstance.TryGetPlayerObject(player, out NetworkObject existing) && existing != null)
            {
                _spawnedPlayers[player] = existing;
                return;
            }

            GetSpawnPose(out Vector3 position, out Quaternion rotation);

            NetworkObject spawned = runnerInstance.Spawn(playerPrefab, position, rotation, player);
            if (spawned == null)
            {
                Debug.LogError("DedicatedServerMatchController: Runner.Spawn returned null.");
                return;
            }

            _spawnedPlayers[player] = spawned;
            runnerInstance.SetPlayerObject(player, spawned);
            Debug.Log($"DedicatedServerMatchController: spawned player object for {player} at {position}.");
        }

        private void GetSpawnPose(out Vector3 position, out Quaternion rotation)
        {
            if (_spawnPoints.Count == 0)
            {
                RefreshSpawnPoints();
            }

            if (_spawnPoints.Count > 0)
            {
                int checkedCount = 0;
                while (checkedCount < _spawnPoints.Count)
                {
                    SpawnPoint point = _spawnPoints[_spawnCursor % _spawnPoints.Count];
                    _spawnCursor++;
                    checkedCount++;

                    if (point != null)
                    {
                        position = point.Position;
                        rotation = point.Rotation;
                        return;
                    }
                }
            }

            if (!_loggedFallbackSpawnWarning)
            {
                Debug.LogWarning("DedicatedServerMatchController: no SpawnPoint components found in Location1. Using fallback spawn ring.");
                _loggedFallbackSpawnWarning = true;
            }

            int index = _spawnCursor;
            _spawnCursor++;

            float angle = index * 2.39996323f;
            float baseRadius = Mathf.Max(3f, fallbackSpawnRadius);
            float ringScale = 1f - (index % 3) * 0.18f;
            float radius = baseRadius * ringScale;

            Vector3 horizontal = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * radius;
            position = fallbackSpawnCenter + horizontal;

            if (TryProjectToGround(position, out Vector3 projected))
            {
                position = projected;
            }
            else
            {
                position = new Vector3(position.x, fallbackSpawnCenter.y + FallbackSpawnYOffset, position.z);
            }

            rotation = Quaternion.identity;
        }

        private static bool TryProjectToGround(Vector3 horizontalPosition, out Vector3 projectedPosition)
        {
            Vector3 rayOrigin = new Vector3(horizontalPosition.x, horizontalPosition.y + FallbackGroundProbeHeight, horizontalPosition.z);
            if (Physics.Raycast(
                    rayOrigin,
                    Vector3.down,
                    out RaycastHit hit,
                    FallbackGroundProbeHeight * 2f,
                    ~0,
                    QueryTriggerInteraction.Ignore))
            {
                projectedPosition = hit.point + Vector3.up * FallbackSpawnYOffset;
                return true;
            }

            projectedPosition = default;
            return false;
        }

        private void TryStartMatch(NetworkRunner runnerInstance)
        {
            if (_matchStarted)
            {
                return;
            }

            if (!HasMinimumPlayers(runnerInstance))
            {
                return;
            }

            _matchStarted = true;
            Debug.Log("DedicatedServerMatchController: minimum players reached, match started.");
        }

        private bool HasMinimumPlayers(NetworkRunner runnerInstance)
        {
            return CountActivePlayers(runnerInstance) >= minimumPlayersToStart;
        }

        private static int CountActivePlayers(NetworkRunner runnerInstance)
        {
            int count = 0;
            foreach (PlayerRef _ in runnerInstance.ActivePlayers)
            {
                count++;
            }

            return count;
        }

        private void RefreshSpawnPoints()
        {
            _spawnPoints.Clear();

            SpawnPoint[] points = FindObjectsOfType<SpawnPoint>(true);
            if (points == null || points.Length == 0)
            {
                return;
            }

            Array.Sort(points, CompareSpawnPoints);

            for (int i = 0; i < points.Length; i++)
            {
                if (points[i] != null)
                {
                    _spawnPoints.Add(points[i]);
                }
            }
        }

        private static int CompareSpawnPoints(SpawnPoint a, SpawnPoint b)
        {
            if (ReferenceEquals(a, b))
            {
                return 0;
            }

            if (a == null)
            {
                return 1;
            }

            if (b == null)
            {
                return -1;
            }

            int orderCompare = a.Order.CompareTo(b.Order);
            if (orderCompare != 0)
            {
                return orderCompare;
            }

            return string.Compare(a.name, b.name, StringComparison.Ordinal);
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
            minimumPlayersToStart = Mathf.Max(2, minimumPlayersToStart);
            fallbackSpawnRadius = Mathf.Max(0f, fallbackSpawnRadius);
        }

        private bool CanAcceptNewPlayers(NetworkRunner runnerInstance)
        {
            if (runnerInstance == null || !runnerInstance.IsServer)
            {
                return false;
            }

            ResolveRoundManagerIfNeeded();

            if (roundManager == null)
            {
                return true;
            }

            return roundManager.IsAcceptingPlayers;
        }

        private void ResolveRoundManagerIfNeeded()
        {
            if (roundManager == null)
            {
                roundManager = FindObjectOfType<MatchRoundManager>(true);
            }
        }
    }
}
