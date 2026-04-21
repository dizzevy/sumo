using System;
using System.Collections.Generic;
using Fusion;
using UnityEngine;
using UnityEngine.Serialization;

namespace Sumo.Gameplay
{
    [DisallowMultipleComponent]
    [RequireComponent(typeof(NetworkObject))]
    public sealed class MatchRoundManager : NetworkBehaviour
    {
        [Header("References")]
        [SerializeField] private PreRoundBoxSpawner preRoundBoxSpawner;
        [SerializeField] private DropFloorController[] dropFloorControllers;
        [SerializeField, HideInInspector, FormerlySerializedAs("dropFloorController")] private DropFloorController legacyDropFloorController;
        [SerializeField] private SafeZoneManager safeZoneManager;

        [Header("Match Rules")]
        [SerializeField] private int minimumPlayersToStart = 2;
        [SerializeField] private int maximumPlayersPerMatch = 10;
        [SerializeField] private float waitingForMorePlayersSeconds = 30f;
        [SerializeField] private bool closeSessionForLateJoinAfterRoundLock = true;

        [Header("Phase Durations (seconds)")]
        [SerializeField] private float preRoundBoxDelay = 0.3f;
        [SerializeField] private float countdownSeconds = 5f;
        [SerializeField] private float dropDelaySeconds = 1f;
        [SerializeField] private float safeZoneDurationSeconds = 30f;
        [SerializeField] private float eliminationResolveDelay = 0.35f;
        [SerializeField] private float nextZoneTransitionDelay = 0.25f;
        [SerializeField] private float roundFinishedDelay = 3.5f;
        [SerializeField] private float resetRoundDelay = 1f;

        [Networked] public MatchState State { get; private set; }
        [Networked] public TickTimer StateTimer { get; private set; }
        [Networked] public int RoundIndex { get; private set; }
        [Networked] public int AlivePlayersInRound { get; private set; }
        [Networked] public int CurrentZoneStep { get; private set; }
        [Networked] public float CurrentZoneRadius { get; private set; }
        [Networked] public int WinnerPlayerRawEncoded { get; private set; }
        [Networked] public NetworkBool AcceptingPlayers { get; private set; }
        [Networked] public TickTimer WaitingForMorePlayersTimer { get; private set; }

        public bool IsAcceptingPlayers => AcceptingPlayers;
        public int MinimumPlayersToStart => minimumPlayersToStart;
        public int MaximumPlayersPerMatch => Mathf.Max(minimumPlayersToStart, maximumPlayersPerMatch);
        public float RemainingPhaseTime => Runner == null ? 0f : (StateTimer.RemainingTime(Runner) ?? 0f);
        public float WaitingForMorePlayersRemainingTime => Runner == null ? 0f : (WaitingForMorePlayersTimer.RemainingTime(Runner) ?? 0f);
        public bool IsWaitingForMorePlayersCountdownActive => Runner != null && !WaitingForMorePlayersTimer.ExpiredOrNotRunning(Runner);

        public int LocalEliminationNotificationSequence => _localEliminationNotificationSequence;
        public int LastNotifiedEliminatedPlayerRawEncoded => _lastNotifiedEliminatedPlayerRawEncoded;
        public int LastNotifiedRemainingAlive => _lastNotifiedRemainingAlive;
        public int LocalWinnerNotificationSequence => _localWinnerNotificationSequence;
        public int LastNotifiedWinnerRawEncoded => _lastNotifiedWinnerRawEncoded;

        private readonly Dictionary<PlayerRef, PlayerRoundState> _playerStates = new Dictionary<PlayerRef, PlayerRoundState>();
        private readonly HashSet<PlayerRef> _missingPlayerStateLogged = new HashSet<PlayerRef>();
        private readonly List<PlayerRef> _activePlayersScratch = new List<PlayerRef>(16);
        private readonly List<PlayerRef> _roundRoster = new List<PlayerRef>(16);
        private readonly List<PlayerRef> _removeScratch = new List<PlayerRef>(16);

        private int _eliminationOrderCounter;
        private int _localEliminationNotificationSequence;
        private int _lastNotifiedEliminatedPlayerRawEncoded = PlayerRef.None.RawEncoded;
        private int _lastNotifiedRemainingAlive;
        private int _localWinnerNotificationSequence;
        private int _lastNotifiedWinnerRawEncoded = PlayerRef.None.RawEncoded;
        private bool _waitingForMorePlayersCountdownArmed;

        public override void Spawned()
        {
            EnsureReferences();

            if (!HasStateAuthority)
            {
                return;
            }

            minimumPlayersToStart = Mathf.Max(2, minimumPlayersToStart);
            maximumPlayersPerMatch = Mathf.Max(minimumPlayersToStart, maximumPlayersPerMatch);
            waitingForMorePlayersSeconds = Mathf.Max(0f, waitingForMorePlayersSeconds);
            WinnerPlayerRawEncoded = PlayerRef.None.RawEncoded;
            AlivePlayersInRound = 0;
            CurrentZoneStep = 0;
            CurrentZoneRadius = 0f;
            RoundIndex = Mathf.Max(0, RoundIndex);
            ResetWaitingForMorePlayersCountdown();
            EnterWaitingForPlayers();
        }

        public override void FixedUpdateNetwork()
        {
            if (!HasStateAuthority)
            {
                return;
            }

            EnsureReferences();
            RefreshPlayerStates();

            bool activeRoundState = State != MatchState.WaitingForPlayers
                                    && State != MatchState.RoundFinished
                                    && State != MatchState.ResetRound;

            if (activeRoundState && _roundRoster.Count > 0 && AlivePlayersInRound <= 1)
            {
                EnterRoundFinished();
                return;
            }

            if (activeRoundState && _roundRoster.Count == 0)
            {
                EnterWaitingForPlayers();
                return;
            }

            switch (State)
            {
                case MatchState.WaitingForPlayers:
                    TickWaitingForPlayers();
                    break;

                case MatchState.PreRoundBox:
                    if (IsCurrentTimerExpired())
                    {
                        EnterState(MatchState.Countdown, countdownSeconds);
                    }
                    break;

                case MatchState.Countdown:
                    if (IsCurrentTimerExpired())
                    {
                        SetAllDropFloorsClosed(false);

                        EnterState(MatchState.DropPlayers, dropDelaySeconds);
                    }
                    break;

                case MatchState.DropPlayers:
                    if (IsCurrentTimerExpired())
                    {
                        StartSafeZonePhase(isFirstZone: true);
                    }
                    break;

                case MatchState.SafeZonePhase:
                    if (IsCurrentTimerExpired())
                    {
                        EnterEliminateOutsideZone();
                    }
                    break;

                case MatchState.EliminateOutsideZone:
                    if (IsCurrentTimerExpired())
                    {
                        if (AlivePlayersInRound <= 1)
                        {
                            EnterRoundFinished();
                        }
                        else
                        {
                            EnterNextZone();
                        }
                    }
                    break;

                case MatchState.NextZone:
                    if (IsCurrentTimerExpired())
                    {
                        EnterState(MatchState.SafeZonePhase, safeZoneDurationSeconds);
                    }
                    break;

                case MatchState.RoundFinished:
                    if (IsCurrentTimerExpired())
                    {
                        EnterState(MatchState.ResetRound, resetRoundDelay);
                    }
                    break;

                case MatchState.ResetRound:
                    if (IsCurrentTimerExpired())
                    {
                        if (CanStartRoundNow())
                        {
                            StartNewRound();
                        }
                        else
                        {
                            EnterWaitingForPlayers();
                        }
                    }
                    break;

                default:
                    EnterWaitingForPlayers();
                    break;
            }
        }

        private void TickWaitingForPlayers()
        {
            SetAdmissionOpen(true);

            SetAllDropFloorsClosed(true);

            if (safeZoneManager != null)
            {
                safeZoneManager.ServerHideCurrentZone();
            }

            AlivePlayersInRound = 0;
            CurrentZoneStep = 0;
            CurrentZoneRadius = 0f;
            WinnerPlayerRawEncoded = PlayerRef.None.RawEncoded;

            if (!CanStartRoundNow())
            {
                ResetWaitingForMorePlayersCountdown();
                return;
            }

            int activeCount = _activePlayersScratch.Count;
            int maxPlayers = Mathf.Max(minimumPlayersToStart, maximumPlayersPerMatch);

            if (activeCount >= maxPlayers)
            {
                ResetWaitingForMorePlayersCountdown();
                StartNewRound();
                return;
            }

            if (waitingForMorePlayersSeconds <= 0.001f)
            {
                ResetWaitingForMorePlayersCountdown();
                StartNewRound();
                return;
            }

            if (!_waitingForMorePlayersCountdownArmed)
            {
                WaitingForMorePlayersTimer = TickTimer.CreateFromSeconds(Runner, waitingForMorePlayersSeconds);
                _waitingForMorePlayersCountdownArmed = true;
                return;
            }

            if (WaitingForMorePlayersTimer.ExpiredOrNotRunning(Runner))
            {
                ResetWaitingForMorePlayersCountdown();
                StartNewRound();
            }
        }

        private void StartNewRound()
        {
            ResetWaitingForMorePlayersCountdown();
            BuildRosterFromCurrentPlayers();
            if (_roundRoster.Count < minimumPlayersToStart)
            {
                EnterWaitingForPlayers();
                return;
            }

            RoundIndex = Mathf.Max(1, RoundIndex + 1);
            WinnerPlayerRawEncoded = PlayerRef.None.RawEncoded;
            _eliminationOrderCounter = 0;

            SetAdmissionOpen(false);

            if (safeZoneManager != null)
            {
                safeZoneManager.ServerResetForRound(RoundIndex);
            }

            SetAllDropFloorsClosed(true);

            for (int i = 0; i < _roundRoster.Count; i++)
            {
                PlayerRef player = _roundRoster[i];
                if (!_playerStates.TryGetValue(player, out PlayerRoundState state) || state == null)
                {
                    continue;
                }

                Vector3 spawnPos;
                Quaternion spawnRot;
                if (preRoundBoxSpawner != null)
                {
                    preRoundBoxSpawner.TryGetSpawnPose(i, out spawnPos, out spawnRot);
                }
                else
                {
                    spawnPos = Vector3.up * 5f;
                    spawnRot = Quaternion.identity;
                }

                state.ServerPrepareForRound(RoundIndex, spawnPos, spawnRot);
            }

            AlivePlayersInRound = CountAlivePlayersInRoster();
            EnterState(MatchState.PreRoundBox, preRoundBoxDelay);
        }

        private void StartSafeZonePhase(bool isFirstZone)
        {
            if (safeZoneManager == null)
            {
                EnterRoundFinished();
                return;
            }

            bool success = isFirstZone
                ? safeZoneManager.ServerSpawnFirstZone()
                : safeZoneManager.ServerSpawnNextZone();

            if (!success)
            {
                EnterRoundFinished();
                return;
            }

            CurrentZoneStep = safeZoneManager.CurrentZoneStep;
            CurrentZoneRadius = safeZoneManager.GetCurrentRadiusOrDefault();
            EnterState(MatchState.SafeZonePhase, safeZoneDurationSeconds);
        }

        private void EnterEliminateOutsideZone()
        {
            EnterState(MatchState.EliminateOutsideZone, eliminationResolveDelay);

            if (safeZoneManager == null)
            {
                AlivePlayersInRound = CountAlivePlayersInRoster();
                return;
            }

            int aliveRemaining = CountAlivePlayersInRoster();

            for (int i = 0; i < _roundRoster.Count; i++)
            {
                PlayerRef player = _roundRoster[i];
                if (!_playerStates.TryGetValue(player, out PlayerRoundState state) || state == null)
                {
                    continue;
                }

                if (!state.IsAliveInRound)
                {
                    continue;
                }

                if (safeZoneManager.IsInsideCurrentZone(state.ZoneCheckPosition))
                {
                    continue;
                }

                Vector3 spectatorPos;
                Quaternion spectatorRot;
                if (preRoundBoxSpawner != null)
                {
                    preRoundBoxSpawner.GetSpectatorHoldPose(_eliminationOrderCounter, out spectatorPos, out spectatorRot);
                }
                else
                {
                    spectatorPos = new Vector3(0f, 14f, 0f);
                    spectatorRot = Quaternion.identity;
                }

                _eliminationOrderCounter++;
                state.ServerEliminateToSpectator(_eliminationOrderCounter, spectatorPos, spectatorRot);
                aliveRemaining = Mathf.Max(0, aliveRemaining - 1);
                RPC_NotifyPlayerEliminated(player.RawEncoded, aliveRemaining);
            }

            AlivePlayersInRound = CountAlivePlayersInRoster();
        }

        private void EnterNextZone()
        {
            if (safeZoneManager == null)
            {
                EnterRoundFinished();
                return;
            }

            bool success = safeZoneManager.ServerSpawnNextZone();
            if (!success)
            {
                EnterRoundFinished();
                return;
            }

            CurrentZoneStep = safeZoneManager.CurrentZoneStep;
            CurrentZoneRadius = safeZoneManager.GetCurrentRadiusOrDefault();
            EnterState(MatchState.NextZone, nextZoneTransitionDelay);
        }

        private void EnterRoundFinished()
        {
            if (State == MatchState.RoundFinished)
            {
                return;
            }

            if (safeZoneManager != null)
            {
                safeZoneManager.ServerHideCurrentZone();
            }

            SetAllDropFloorsClosed(true);

            AlivePlayersInRound = CountAlivePlayersInRoster();
            WinnerPlayerRawEncoded = ResolveWinnerRawEncoded();
            if (WinnerPlayerRawEncoded != PlayerRef.None.RawEncoded)
            {
                RPC_NotifyRoundWinner(WinnerPlayerRawEncoded);
            }

            EnterState(MatchState.RoundFinished, roundFinishedDelay);
        }

        private void EnterWaitingForPlayers()
        {
            _roundRoster.Clear();
            _eliminationOrderCounter = 0;
            ResetWaitingForMorePlayersCountdown();

            if (safeZoneManager != null)
            {
                safeZoneManager.ServerHideCurrentZone();
            }

            SetAllDropFloorsClosed(true);

            AlivePlayersInRound = 0;
            CurrentZoneStep = 0;
            CurrentZoneRadius = 0f;
            WinnerPlayerRawEncoded = PlayerRef.None.RawEncoded;

            SetAdmissionOpen(true);
            EnterState(MatchState.WaitingForPlayers, 0f);
        }

        private void EnterState(MatchState newState, float durationSeconds)
        {
            if (newState != MatchState.WaitingForPlayers)
            {
                ResetWaitingForMorePlayersCountdown();
            }

            State = newState;
            StateTimer = durationSeconds > 0.001f
                ? TickTimer.CreateFromSeconds(Runner, durationSeconds)
                : default;

            if (closeSessionForLateJoinAfterRoundLock)
            {
                bool shouldAccept = newState == MatchState.WaitingForPlayers;
                SetAdmissionOpen(shouldAccept);
            }

            UpdateSessionProperties();
        }

        private bool IsCurrentTimerExpired()
        {
            return StateTimer.ExpiredOrNotRunning(Runner);
        }

        private void RefreshPlayerStates()
        {
            _activePlayersScratch.Clear();
            foreach (PlayerRef player in Runner.ActivePlayers)
            {
                _activePlayersScratch.Add(player);
            }

            _removeScratch.Clear();
            foreach (KeyValuePair<PlayerRef, PlayerRoundState> pair in _playerStates)
            {
                if (!_activePlayersScratch.Contains(pair.Key))
                {
                    _removeScratch.Add(pair.Key);
                }
            }

            for (int i = 0; i < _removeScratch.Count; i++)
            {
                _playerStates.Remove(_removeScratch[i]);
                _missingPlayerStateLogged.Remove(_removeScratch[i]);
            }

            for (int i = _roundRoster.Count - 1; i >= 0; i--)
            {
                if (!_activePlayersScratch.Contains(_roundRoster[i]))
                {
                    _roundRoster.RemoveAt(i);
                }
            }

            for (int i = 0; i < _activePlayersScratch.Count; i++)
            {
                PlayerRef player = _activePlayersScratch[i];
                if (!Runner.TryGetPlayerObject(player, out NetworkObject playerObject) || playerObject == null)
                {
                    continue;
                }

                PlayerRoundState state = playerObject.GetComponent<PlayerRoundState>();
                if (state == null)
                {
                    if (_missingPlayerStateLogged.Add(player))
                    {
                        Debug.LogError($"MatchRoundManager: Player object for {player} is missing PlayerRoundState component.");
                    }

                    continue;
                }

                _playerStates[player] = state;
            }

            AlivePlayersInRound = CountAlivePlayersInRoster();
        }

        private bool CanStartRoundNow()
        {
            if (_activePlayersScratch.Count < minimumPlayersToStart)
            {
                return false;
            }

            for (int i = 0; i < _activePlayersScratch.Count; i++)
            {
                PlayerRef player = _activePlayersScratch[i];
                if (!_playerStates.TryGetValue(player, out PlayerRoundState state) || state == null)
                {
                    return false;
                }

                if (!state.IsClientReady)
                {
                    return false;
                }
            }

            return true;
        }

        private void BuildRosterFromCurrentPlayers()
        {
            _roundRoster.Clear();
            for (int i = 0; i < _activePlayersScratch.Count; i++)
            {
                PlayerRef player = _activePlayersScratch[i];
                if (!_playerStates.TryGetValue(player, out PlayerRoundState state) || state == null)
                {
                    continue;
                }

                if (!state.IsClientReady)
                {
                    continue;
                }

                _roundRoster.Add(player);
            }
        }

        private int CountAlivePlayersInRoster()
        {
            int alive = 0;
            for (int i = 0; i < _roundRoster.Count; i++)
            {
                PlayerRef player = _roundRoster[i];
                if (_playerStates.TryGetValue(player, out PlayerRoundState state)
                    && state != null
                    && state.IsAliveInRound)
                {
                    alive++;
                }
            }

            return alive;
        }

        private int ResolveWinnerRawEncoded()
        {
            PlayerRef winner = PlayerRef.None;
            int aliveCount = 0;

            for (int i = 0; i < _roundRoster.Count; i++)
            {
                PlayerRef player = _roundRoster[i];
                if (!_playerStates.TryGetValue(player, out PlayerRoundState state) || state == null)
                {
                    continue;
                }

                if (!state.IsAliveInRound)
                {
                    continue;
                }

                winner = player;
                aliveCount++;
                if (aliveCount > 1)
                {
                    return PlayerRef.None.RawEncoded;
                }
            }

            return aliveCount == 1 ? winner.RawEncoded : PlayerRef.None.RawEncoded;
        }

        private void SetAdmissionOpen(bool isOpen)
        {
            AcceptingPlayers = isOpen;

            if (!closeSessionForLateJoinAfterRoundLock || Runner == null || !Runner.IsServer)
            {
                return;
            }

            SessionInfo sessionInfo = Runner.SessionInfo;
            if (!sessionInfo.IsValid)
            {
                return;
            }

            if (sessionInfo.IsOpen != isOpen)
            {
                sessionInfo.IsOpen = isOpen;
            }
        }

        private void UpdateSessionProperties()
        {
            if (Runner == null || !Runner.IsServer)
            {
                return;
            }

            SessionInfo sessionInfo = Runner.SessionInfo;
            if (!sessionInfo.IsValid)
            {
                return;
            }

            Dictionary<string, SessionProperty> props = new Dictionary<string, SessionProperty>(6)
            {
                ["matchState"] = (int)State,
                ["round"] = RoundIndex,
                ["zoneStep"] = CurrentZoneStep,
                ["accepting"] = AcceptingPlayers ? 1 : 0,
                ["minPlayers"] = minimumPlayersToStart,
                ["maxPlayers"] = Mathf.Max(minimumPlayersToStart, maximumPlayersPerMatch)
            };

            sessionInfo.UpdateCustomProperties(props);
        }

        private void EnsureReferences()
        {
            if (preRoundBoxSpawner == null)
            {
                preRoundBoxSpawner = FindObjectOfType<PreRoundBoxSpawner>(true);
            }

            if ((dropFloorControllers == null || dropFloorControllers.Length == 0) && legacyDropFloorController != null)
            {
                dropFloorControllers = new[] { legacyDropFloorController };
            }

            if (dropFloorControllers == null || dropFloorControllers.Length == 0)
            {
                dropFloorControllers = FindObjectsOfType<DropFloorController>(true);
            }
            else if (legacyDropFloorController != null)
            {
                bool containsLegacy = false;
                for (int i = 0; i < dropFloorControllers.Length; i++)
                {
                    if (dropFloorControllers[i] == legacyDropFloorController)
                    {
                        containsLegacy = true;
                        break;
                    }
                }

                if (!containsLegacy)
                {
                    DropFloorController[] merged = new DropFloorController[dropFloorControllers.Length + 1];
                    for (int i = 0; i < dropFloorControllers.Length; i++)
                    {
                        merged[i] = dropFloorControllers[i];
                    }

                    merged[merged.Length - 1] = legacyDropFloorController;
                    dropFloorControllers = merged;
                }
            }

            if (safeZoneManager == null)
            {
                safeZoneManager = FindObjectOfType<SafeZoneManager>(true);
            }
        }

        [Rpc(RpcSources.StateAuthority, RpcTargets.All, Channel = RpcChannel.Reliable)]
        private void RPC_NotifyPlayerEliminated(int playerRawEncoded, int aliveRemaining)
        {
            _localEliminationNotificationSequence++;
            _lastNotifiedEliminatedPlayerRawEncoded = playerRawEncoded;
            _lastNotifiedRemainingAlive = aliveRemaining;
        }

        [Rpc(RpcSources.StateAuthority, RpcTargets.All, Channel = RpcChannel.Reliable)]
        private void RPC_NotifyRoundWinner(int winnerRawEncoded)
        {
            _localWinnerNotificationSequence++;
            _lastNotifiedWinnerRawEncoded = winnerRawEncoded;
        }

        private void OnValidate()
        {
            minimumPlayersToStart = Mathf.Max(2, minimumPlayersToStart);
            maximumPlayersPerMatch = Mathf.Max(minimumPlayersToStart, maximumPlayersPerMatch);
            waitingForMorePlayersSeconds = Mathf.Max(0f, waitingForMorePlayersSeconds);
            preRoundBoxDelay = Mathf.Max(0f, preRoundBoxDelay);
            countdownSeconds = Mathf.Max(0f, countdownSeconds);
            dropDelaySeconds = Mathf.Max(0f, dropDelaySeconds);
            safeZoneDurationSeconds = Mathf.Max(1f, safeZoneDurationSeconds);
            eliminationResolveDelay = Mathf.Max(0f, eliminationResolveDelay);
            nextZoneTransitionDelay = Mathf.Max(0f, nextZoneTransitionDelay);
            roundFinishedDelay = Mathf.Max(0f, roundFinishedDelay);
            resetRoundDelay = Mathf.Max(0f, resetRoundDelay);
        }

        private void ResetWaitingForMorePlayersCountdown()
        {
            _waitingForMorePlayersCountdownArmed = false;
            WaitingForMorePlayersTimer = default;
        }

        private void SetAllDropFloorsClosed(bool closed)
        {
            if (dropFloorControllers == null || dropFloorControllers.Length == 0)
            {
                return;
            }

            for (int i = 0; i < dropFloorControllers.Length; i++)
            {
                DropFloorController floor = dropFloorControllers[i];
                if (floor != null)
                {
                    floor.SetClosedState(closed);
                }
            }
        }
    }
}
