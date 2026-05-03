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
        public const int ScoreCapacity = 20;

        [Header("References")]
        [SerializeField] private PreRoundBoxSpawner preRoundBoxSpawner;
        [SerializeField] private DropFloorController[] dropFloorControllers;
        [SerializeField, HideInInspector, FormerlySerializedAs("dropFloorController")] private DropFloorController legacyDropFloorController;
        [SerializeField] private SafeZoneManager safeZoneManager;
        [SerializeField] private NetworkPrefabRef npcPrefab;

        [Header("Match Rules")]
        [SerializeField] private int minimumPlayersToStart = 2;
        [SerializeField] private int maximumPlayersPerMatch = 8;
        [SerializeField] private float waitingForMorePlayersSeconds = 30f;
        [SerializeField] private bool closeSessionForLateJoinAfterRoundLock = true;
        [SerializeField] private bool startFirstRoundImmediatelyAfterMatchmaking = true;
        [SerializeField] private int winsToFinishMatch = 3;
        [SerializeField] private float scoreboardDisplaySeconds = 4f;
        [SerializeField] private float finalScoreDisplaySeconds = 5f;
        [SerializeField] private bool shutdownServerAfterFinalScore = true;

        [Header("NPC Fill")]
        [SerializeField] private bool spawnNpcsAtRoundStart = true;
        [SerializeField] private int minimumNpcsPerRound = 1;
        [SerializeField] private KeyCode npcStopStartBind = KeyCode.T;
        [SerializeField] private bool resetNpcMovementOnRoundStart = true;

        [Header("Phase Durations (seconds)")]
        [SerializeField] private float preRoundBoxDelay = 0.3f;
        [SerializeField] private float classSelectionSeconds = 20f;
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
        [Networked] public NetworkBool ClassAbilitiesUnlocked { get; private set; }
        [Networked] public int WinnerPlayerRawEncoded { get; private set; }
        [Networked] public NetworkBool AcceptingPlayers { get; private set; }
        [Networked] public TickTimer WaitingForMorePlayersTimer { get; private set; }
        [Networked] public NetworkBool MatchmakingStartConfirmed { get; private set; }
        [Networked] public int ConfirmedHumanPlayersAtStart { get; private set; }
        [Networked] public int MatchWinTarget { get; private set; }
        [Networked] public int ScoreParticipantCount { get; private set; }
        [Networked] public int MatchWinnerRawEncoded { get; private set; }
        [Networked] public int LockedNpcCount { get; private set; }
        [Networked] public NetworkBool MatchScoreInitialized { get; private set; }
        [Networked, Capacity(ScoreCapacity)] public NetworkArray<int> ScoreParticipantRawEncoded => default;
        [Networked, Capacity(ScoreCapacity)] public NetworkArray<int> ScoreParticipantWins => default;

        public bool IsNetworkSpawned { get; private set; }
        public bool IsAcceptingPlayers => IsNetworkSpawned && AcceptingPlayers;
        public int MinimumPlayersToStart => minimumPlayersToStart;
        public int MaximumPlayersPerMatch => Mathf.Max(minimumPlayersToStart, maximumPlayersPerMatch);
        public bool ShouldReturnToMainMenuAfterMatch => shutdownServerAfterFinalScore;
        public float RemainingPhaseTime => IsNetworkSpawned && Runner != null ? (StateTimer.RemainingTime(Runner) ?? 0f) : 0f;
        public float WaitingForMorePlayersRemainingTime => IsNetworkSpawned && Runner != null ? (WaitingForMorePlayersTimer.RemainingTime(Runner) ?? 0f) : 0f;
        public bool IsWaitingForMorePlayersCountdownActive => IsNetworkSpawned && Runner != null && !WaitingForMorePlayersTimer.ExpiredOrNotRunning(Runner);

        public int LocalEliminationNotificationSequence => _localEliminationNotificationSequence;
        public int LastNotifiedEliminatedPlayerRawEncoded => _lastNotifiedEliminatedPlayerRawEncoded;
        public int LastNotifiedRemainingAlive => _lastNotifiedRemainingAlive;
        public int LocalWinnerNotificationSequence => _localWinnerNotificationSequence;
        public int LastNotifiedWinnerRawEncoded => _lastNotifiedWinnerRawEncoded;

        private readonly Dictionary<PlayerRef, PlayerRoundState> _playerStates = new Dictionary<PlayerRef, PlayerRoundState>();
        private readonly HashSet<PlayerRef> _missingPlayerStateLogged = new HashSet<PlayerRef>();
        private readonly List<PlayerRef> _activePlayersScratch = new List<PlayerRef>(16);
        private readonly List<PlayerRef> _roundRoster = new List<PlayerRef>(16);
        private readonly List<PlayerRoundState> _roundNpcStates = new List<PlayerRoundState>(16);
        private readonly List<PlayerRef> _removeScratch = new List<PlayerRef>(16);
        private readonly List<NetworkObject> _spawnedNpcs = new List<NetworkObject>(16);

        private int _eliminationOrderCounter;
        private int _localEliminationNotificationSequence;
        private int _lastNotifiedEliminatedPlayerRawEncoded = PlayerRef.None.RawEncoded;
        private int _lastNotifiedRemainingAlive;
        private int _localWinnerNotificationSequence;
        private int _lastNotifiedWinnerRawEncoded = PlayerRef.None.RawEncoded;
        private bool _waitingForMorePlayersCountdownArmed;
        private bool _runtimeNpcsMoving = true;

        public bool ServerEliminatePlayerFromBoundary(PlayerRoundState state)
        {
            if (!HasStateAuthority || state == null || !state.IsAliveInRound || !IsRoundRunningForBoundaryElimination())
            {
                return false;
            }

            if (!TryResolveRoundParticipantId(state, out int participantRawEncoded))
            {
                return false;
            }

            if (!TryEliminatePlayerState(state, participantRawEncoded))
            {
                return false;
            }

            if (AlivePlayersInRound <= 1)
            {
                EnterRoundFinished();
            }

            return true;
        }

        public override void Spawned()
        {
            IsNetworkSpawned = true;
            EnsureReferences();

            if (!HasStateAuthority)
            {
                return;
            }

            minimumPlayersToStart = Mathf.Max(2, minimumPlayersToStart);
            maximumPlayersPerMatch = Mathf.Max(minimumPlayersToStart, maximumPlayersPerMatch);
            waitingForMorePlayersSeconds = Mathf.Max(0f, waitingForMorePlayersSeconds);
            WinnerPlayerRawEncoded = PlayerRef.None.RawEncoded;
            MatchWinnerRawEncoded = PlayerRef.None.RawEncoded;
            MatchWinTarget = Mathf.Max(1, winsToFinishMatch);
            ScoreParticipantCount = 0;
            LockedNpcCount = -1;
            MatchScoreInitialized = false;
            MatchmakingStartConfirmed = false;
            ConfirmedHumanPlayersAtStart = 0;
            AlivePlayersInRound = 0;
            CurrentZoneStep = 0;
            CurrentZoneRadius = 0f;
            ClassAbilitiesUnlocked = false;
            RoundIndex = Mathf.Max(0, RoundIndex);
            ClearScoreArrays();
            ResetWaitingForMorePlayersCountdown();
            EnterWaitingForPlayers();
        }

        public override void Despawned(NetworkRunner runner, bool hasState)
        {
            IsNetworkSpawned = false;
        }

        public bool TryGetState(out MatchState state)
        {
            if (!IsNetworkSpawned)
            {
                state = MatchState.WaitingForPlayers;
                return false;
            }

            state = State;
            return true;
        }

        private void Update()
        {
            if (Runner == null || Object == null || !Object.IsInSimulation)
            {
                return;
            }

            if (!Sumo.SumoNpcBallDriver.WasKeyPressedThisFrame(npcStopStartBind))
            {
                return;
            }

            if (HasStateAuthority)
            {
                ToggleRuntimeNpcsMoving();
                return;
            }

            RPC_RequestToggleRuntimeNpcsMoving();
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
                                    && State != MatchState.ClassSelection
                                    && State != MatchState.RoundFinished
                                    && State != MatchState.Scoreboard
                                    && State != MatchState.ResetRound
                                    && State != MatchState.MatchFinished;

            if (activeRoundState && CountRoundParticipants() > 0 && AlivePlayersInRound <= 1)
            {
                EnterRoundFinished();
                return;
            }

            if (activeRoundState && CountRoundParticipants() == 0)
            {
                EnterWaitingForPlayers();
                return;
            }

            switch (State)
            {
                case MatchState.WaitingForPlayers:
                    TickWaitingForPlayers();
                    break;

                case MatchState.ClassSelection:
                    TickClassSelection();
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
                        EnterState(MatchState.Scoreboard, scoreboardDisplaySeconds);
                    }
                    break;

                case MatchState.Scoreboard:
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

                case MatchState.MatchFinished:
                    if (IsCurrentTimerExpired())
                    {
                        ShutdownFinishedMatchIfNeeded();
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

            if (!CanEnterClassSelection())
            {
                ResetWaitingForMorePlayersCountdown();
                return;
            }

            int activeCount = _activePlayersScratch.Count;
            int maxPlayers = Mathf.Max(minimumPlayersToStart, maximumPlayersPerMatch);

            if (activeCount >= maxPlayers)
            {
                ResetWaitingForMorePlayersCountdown();
                EnterClassSelection();
                return;
            }

            if (waitingForMorePlayersSeconds <= 0.001f)
            {
                ResetWaitingForMorePlayersCountdown();
                EnterClassSelection();
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
                EnterClassSelection();
            }
        }

        private void TickClassSelection()
        {
            SetAdmissionOpen(false);
            SetAllDropFloorsClosed(true);

            if (safeZoneManager != null)
            {
                safeZoneManager.ServerHideCurrentZone();
            }

            if (!CanEnterClassSelection())
            {
                EnterWaitingForPlayers();
                return;
            }

            if (!AreAllActivePlayersClassReady() && !IsCurrentTimerExpired())
            {
                return;
            }

            AutoConfirmPendingClassSelections();
            StartNewRound();
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

            EnsureMatchScoreInitialized(_roundRoster.Count);
            DespawnRuntimeNpcs();

            if (resetNpcMovementOnRoundStart)
            {
                SetRuntimeNpcsMoving(true);
            }

            RoundIndex = Mathf.Max(1, RoundIndex + 1);
            WinnerPlayerRawEncoded = PlayerRef.None.RawEncoded;
            MatchWinnerRawEncoded = PlayerRef.None.RawEncoded;
            ClassAbilitiesUnlocked = false;
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
                GetRoundSpawnPose(i, out spawnPos, out spawnRot);

                state.ServerPrepareForRound(RoundIndex, spawnPos, spawnRot, player.RawEncoded);
            }

            SpawnRuntimeNpcsForRound(_roundRoster.Count);

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
            if (isFirstZone)
            {
                ClassAbilitiesUnlocked = true;
            }

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

                TryEliminatePlayerState(state, player.RawEncoded);
            }

            for (int i = 0; i < _roundNpcStates.Count; i++)
            {
                PlayerRoundState state = _roundNpcStates[i];
                if (state == null || !state.IsAliveInRound)
                {
                    continue;
                }

                if (safeZoneManager.IsInsideCurrentZone(state.ZoneCheckPosition))
                {
                    continue;
                }

                TryEliminatePlayerState(state, EncodeNpcParticipantRawEncoded(i));
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
            ClassAbilitiesUnlocked = false;

            AlivePlayersInRound = CountAlivePlayersInRoster();
            WinnerPlayerRawEncoded = ResolveWinnerRawEncoded();
            if (WinnerPlayerRawEncoded != PlayerRef.None.RawEncoded)
            {
                AddMatchWin(WinnerPlayerRawEncoded);
                RPC_NotifyRoundWinner(WinnerPlayerRawEncoded);
            }

            if (HasMatchWinner())
            {
                EnterState(MatchState.MatchFinished, finalScoreDisplaySeconds);
            }
            else
            {
                EnterState(MatchState.Scoreboard, scoreboardDisplaySeconds);
            }
        }

        private void EnterWaitingForPlayers()
        {
            DespawnRuntimeNpcs();
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
            ClassAbilitiesUnlocked = false;
            WinnerPlayerRawEncoded = PlayerRef.None.RawEncoded;

            SetAdmissionOpen(true);
            EnterState(MatchState.WaitingForPlayers, 0f);
        }

        private void EnterClassSelection()
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
            ClassAbilitiesUnlocked = false;
            WinnerPlayerRawEncoded = PlayerRef.None.RawEncoded;

            for (int i = 0; i < _activePlayersScratch.Count; i++)
            {
                PlayerRef player = _activePlayersScratch[i];
                if (_playerStates.TryGetValue(player, out PlayerRoundState state) && state != null)
                {
                    state.ServerBeginClassSelection();
                }
            }

            SetAdmissionOpen(false);
            EnterState(MatchState.ClassSelection, classSelectionSeconds);
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

        private void ShutdownFinishedMatchIfNeeded()
        {
            if (!shutdownServerAfterFinalScore || Runner == null)
            {
                return;
            }

            SetAdmissionOpen(false);
            Runner.Shutdown(false, ShutdownReason.Ok, false);
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

            for (int i = _roundNpcStates.Count - 1; i >= 0; i--)
            {
                PlayerRoundState npcState = _roundNpcStates[i];
                if (npcState == null || npcState.Object == null || !npcState.Object.IsInSimulation)
                {
                    _roundNpcStates.RemoveAt(i);
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

        private bool IsRoundRunningForBoundaryElimination()
        {
            return State == MatchState.DropPlayers
                   || State == MatchState.SafeZonePhase
                   || State == MatchState.EliminateOutsideZone
                   || State == MatchState.NextZone;
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

            for (int i = 0; i < _roundNpcStates.Count; i++)
            {
                PlayerRoundState state = _roundNpcStates[i];
                if (state != null && state.IsAliveInRound)
                {
                    alive++;
                }
            }

            return alive;
        }

        private int CountRoundParticipants()
        {
            return _roundRoster.Count + _roundNpcStates.Count;
        }

        public void ServerConfirmMatchmakingStart(int confirmedHumanPlayers)
        {
            if (!HasStateAuthority)
            {
                return;
            }

            MatchmakingStartConfirmed = true;
            ConfirmedHumanPlayersAtStart = Mathf.Max(0, confirmedHumanPlayers);
        }

        public int GetScoreParticipantRawEncoded(int slot)
        {
            if (slot < 0 || slot >= ScoreParticipantCount || slot >= ScoreCapacity)
            {
                return PlayerRef.None.RawEncoded;
            }

            return ScoreParticipantRawEncoded.Get(slot);
        }

        public int GetScoreParticipantWins(int slot)
        {
            if (slot < 0 || slot >= ScoreParticipantCount || slot >= ScoreCapacity)
            {
                return 0;
            }

            return ScoreParticipantWins.Get(slot);
        }

        public string FormatParticipantLabel(PlayerRoundState state)
        {
            if (state == null)
            {
                return "Player";
            }

            int rawEncoded = state.ParticipantRawEncoded;
            if (rawEncoded == PlayerRef.None.RawEncoded && state.Object != null && state.Object.InputAuthority != PlayerRef.None)
            {
                rawEncoded = state.Object.InputAuthority.RawEncoded;
            }

            return FormatParticipantLabel(rawEncoded);
        }

        public string FormatParticipantLabel(int rawEncoded)
        {
            for (int i = 0; i < ScoreParticipantCount && i < ScoreCapacity; i++)
            {
                int participant = ScoreParticipantRawEncoded.Get(i);
                if (participant == rawEncoded)
                {
                    return FormatScoreSlotLabel(i, participant);
                }
            }

            if (rawEncoded < 0)
            {
                return $"Bot {Mathf.Abs(rawEncoded)}";
            }

            if (rawEncoded == PlayerRef.None.RawEncoded)
            {
                return "Player";
            }

            return $"Player {rawEncoded}";
        }

        private void EnsureMatchScoreInitialized(int humanPlayerCount)
        {
            MatchWinTarget = Mathf.Max(1, winsToFinishMatch);

            if (MatchScoreInitialized)
            {
                return;
            }

            LockedNpcCount = spawnNpcsAtRoundStart ? ResolveNpcCountForRound(humanPlayerCount) : 0;
            ScoreParticipantCount = 0;
            MatchWinnerRawEncoded = PlayerRef.None.RawEncoded;
            ClearScoreArrays();

            for (int i = 0; i < _roundRoster.Count && ScoreParticipantCount < ScoreCapacity; i++)
            {
                ScoreParticipantRawEncoded.Set(ScoreParticipantCount, _roundRoster[i].RawEncoded);
                ScoreParticipantWins.Set(ScoreParticipantCount, 0);
                ScoreParticipantCount++;
            }

            int npcCount = Mathf.Clamp(LockedNpcCount, 0, ScoreCapacity - ScoreParticipantCount);
            LockedNpcCount = npcCount;
            for (int i = 0; i < npcCount && ScoreParticipantCount < ScoreCapacity; i++)
            {
                ScoreParticipantRawEncoded.Set(ScoreParticipantCount, EncodeNpcParticipantRawEncoded(i));
                ScoreParticipantWins.Set(ScoreParticipantCount, 0);
                ScoreParticipantCount++;
            }

            MatchScoreInitialized = true;
        }

        private void ClearScoreArrays()
        {
            for (int i = 0; i < ScoreCapacity; i++)
            {
                ScoreParticipantRawEncoded.Set(i, PlayerRef.None.RawEncoded);
                ScoreParticipantWins.Set(i, 0);
            }
        }

        private void AddMatchWin(int winnerRawEncoded)
        {
            for (int i = 0; i < ScoreParticipantCount && i < ScoreCapacity; i++)
            {
                if (ScoreParticipantRawEncoded.Get(i) != winnerRawEncoded)
                {
                    continue;
                }

                int wins = Mathf.Max(0, ScoreParticipantWins.Get(i)) + 1;
                ScoreParticipantWins.Set(i, wins);
                return;
            }
        }

        private bool HasMatchWinner()
        {
            int target = Mathf.Max(1, MatchWinTarget);
            for (int i = 0; i < ScoreParticipantCount && i < ScoreCapacity; i++)
            {
                if (ScoreParticipantWins.Get(i) >= target)
                {
                    MatchWinnerRawEncoded = ScoreParticipantRawEncoded.Get(i);
                    return true;
                }
            }

            return false;
        }

        private string FormatScoreSlotLabel(int slot, int rawEncoded)
        {
            if (rawEncoded < 0)
            {
                return $"Bot {Mathf.Abs(rawEncoded)}";
            }

            int humanOrdinal = 0;
            for (int i = 0; i <= slot && i < ScoreParticipantCount && i < ScoreCapacity; i++)
            {
                if (ScoreParticipantRawEncoded.Get(i) > 0)
                {
                    humanOrdinal++;
                }
            }

            return $"Player {Mathf.Max(1, humanOrdinal)}";
        }

        private void SpawnRuntimeNpcsForRound(int humanPlayerCount)
        {
            if (!spawnNpcsAtRoundStart)
            {
                return;
            }

            int npcCount = ResolveNpcCountForRound(humanPlayerCount);
            if (npcCount <= 0)
            {
                return;
            }

            if (!npcPrefab.IsValid)
            {
                Debug.LogWarning("MatchRoundManager: NPC prefab is not assigned. Runtime NPC fill is enabled but no NPCs were spawned.");
                return;
            }

            for (int i = 0; i < npcCount; i++)
            {
                int spawnIndex = humanPlayerCount + i;
                GetRoundSpawnPose(spawnIndex, out Vector3 spawnPos, out Quaternion spawnRot);

                NetworkObject npcObject = Runner.Spawn(npcPrefab, spawnPos, spawnRot, PlayerRef.None);
                if (npcObject == null)
                {
                    Debug.LogError($"MatchRoundManager: Runner.Spawn returned null for NPC #{i + 1}.");
                    continue;
                }

                if (ConfigureRuntimeNpc(npcObject, i, spawnPos, spawnRot))
                {
                    _spawnedNpcs.Add(npcObject);
                }
                else
                {
                    Runner.Despawn(npcObject);
                }
            }

            Debug.Log($"MatchRoundManager: spawned {_spawnedNpcs.Count} runtime NPCs for round {RoundIndex}.");
        }

        private int ResolveNpcCountForRound(int humanPlayerCount)
        {
            if (MatchScoreInitialized && LockedNpcCount >= 0)
            {
                return Mathf.Max(0, LockedNpcCount);
            }

            int maxBalls = Mathf.Max(minimumPlayersToStart, maximumPlayersPerMatch);
            int freeSlots = Mathf.Max(0, maxBalls - Mathf.Max(0, humanPlayerCount));
            if (freeSlots <= 0)
            {
                return 0;
            }

            int minNpcs = Mathf.Clamp(Mathf.Max(1, minimumNpcsPerRound), 1, freeSlots);
            return UnityEngine.Random.Range(minNpcs, freeSlots + 1);
        }

        private bool ConfigureRuntimeNpc(NetworkObject npcObject, int localIndex, Vector3 spawnPos, Quaternion spawnRot)
        {
            if (npcObject == null)
            {
                return false;
            }

            npcObject.name = $"RuntimeNpc_R{RoundIndex}_{localIndex + 1}";

            Sumo.SumoNpcBallDriver npcDriver = npcObject.GetComponent<Sumo.SumoNpcBallDriver>();
            if (npcDriver != null)
            {
                npcDriver.ConfigureRuntimeSpawnedNpc();
                npcDriver.SetMoving(_runtimeNpcsMoving);
            }
            else
            {
                Debug.LogWarning($"MatchRoundManager: spawned NPC prefab '{npcObject.name}' is missing SumoNpcBallDriver.");
            }

            PlayerRoundState npcState = npcObject.GetComponent<PlayerRoundState>();
            if (npcState == null)
            {
                Debug.LogError($"MatchRoundManager: spawned NPC prefab '{npcObject.name}' is missing PlayerRoundState.");
                return false;
            }

            npcState.ServerForceClientReady(true);
            npcState.ServerPrepareForRound(RoundIndex, spawnPos, spawnRot, EncodeNpcParticipantRawEncoded(localIndex));
            _roundNpcStates.Add(npcState);
            return true;
        }

        private void DespawnRuntimeNpcs()
        {
            if (_spawnedNpcs.Count == 0)
            {
                _roundNpcStates.Clear();
                return;
            }

            for (int i = 0; i < _spawnedNpcs.Count; i++)
            {
                NetworkObject npcObject = _spawnedNpcs[i];
                if (npcObject != null && Runner != null)
                {
                    Runner.Despawn(npcObject);
                }
            }

            _spawnedNpcs.Clear();
            _roundNpcStates.Clear();
        }

        private void ToggleRuntimeNpcsMoving()
        {
            SetRuntimeNpcsMoving(!_runtimeNpcsMoving);
        }

        private void SetRuntimeNpcsMoving(bool moving)
        {
            _runtimeNpcsMoving = moving;

            for (int i = 0; i < _spawnedNpcs.Count; i++)
            {
                NetworkObject npcObject = _spawnedNpcs[i];
                if (npcObject == null)
                {
                    continue;
                }

                Sumo.SumoNpcBallDriver npcDriver = npcObject.GetComponent<Sumo.SumoNpcBallDriver>();
                if (npcDriver != null)
                {
                    npcDriver.SetMoving(moving);
                }
            }

            Debug.Log($"MatchRoundManager: runtime NPC movement {(moving ? "started" : "stopped")}.");
        }

        private void GetRoundSpawnPose(int index, out Vector3 position, out Quaternion rotation)
        {
            if (preRoundBoxSpawner != null)
            {
                preRoundBoxSpawner.TryGetSpawnPose(index, out position, out rotation);
                return;
            }

            position = Vector3.up * 5f;
            rotation = Quaternion.identity;
        }

        private bool TryEliminatePlayerState(PlayerRoundState state, int participantRawEncoded)
        {
            if (state == null || !state.IsAliveInRound)
            {
                return false;
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

            AlivePlayersInRound = CountAlivePlayersInRoster();
            if (participantRawEncoded != PlayerRef.None.RawEncoded)
            {
                RPC_NotifyPlayerEliminated(participantRawEncoded, AlivePlayersInRound);
            }

            return true;
        }

        private bool CanEnterClassSelection()
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
            }

            return true;
        }

        private bool AreAllActivePlayersClassReady()
        {
            if (_activePlayersScratch.Count < minimumPlayersToStart)
            {
                return false;
            }

            for (int i = 0; i < _activePlayersScratch.Count; i++)
            {
                PlayerRef player = _activePlayersScratch[i];
                if (!_playerStates.TryGetValue(player, out PlayerRoundState state) || state == null || !state.IsClientReady)
                {
                    return false;
                }
            }

            return true;
        }

        private void AutoConfirmPendingClassSelections()
        {
            for (int i = 0; i < _activePlayersScratch.Count; i++)
            {
                PlayerRef player = _activePlayersScratch[i];
                if (!_playerStates.TryGetValue(player, out PlayerRoundState state) || state == null || state.IsClientReady)
                {
                    continue;
                }

                state.ServerAutoConfirmClassSelection();
            }
        }

        private int ResolveWinnerRawEncoded()
        {
            int winnerRawEncoded = PlayerRef.None.RawEncoded;
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

                winnerRawEncoded = player.RawEncoded;
                aliveCount++;
                if (aliveCount > 1)
                {
                    return PlayerRef.None.RawEncoded;
                }
            }

            for (int i = 0; i < _roundNpcStates.Count; i++)
            {
                PlayerRoundState state = _roundNpcStates[i];
                if (state == null || !state.IsAliveInRound)
                {
                    continue;
                }

                winnerRawEncoded = EncodeNpcParticipantRawEncoded(i);
                aliveCount++;
                if (aliveCount > 1)
                {
                    return PlayerRef.None.RawEncoded;
                }
            }

            return aliveCount == 1 ? winnerRawEncoded : PlayerRef.None.RawEncoded;
        }

        private bool TryResolveRoundParticipantId(PlayerRoundState state, out int participantRawEncoded)
        {
            participantRawEncoded = PlayerRef.None.RawEncoded;
            if (state == null)
            {
                return false;
            }

            PlayerRef player = ResolvePlayerRef(state);
            if (player != PlayerRef.None && _roundRoster.Contains(player))
            {
                participantRawEncoded = player.RawEncoded;
                return true;
            }

            int npcIndex = _roundNpcStates.IndexOf(state);
            if (npcIndex >= 0)
            {
                participantRawEncoded = EncodeNpcParticipantRawEncoded(npcIndex);
                return true;
            }

            return false;
        }

        private static int EncodeNpcParticipantRawEncoded(int npcIndex)
        {
            return -(Mathf.Max(0, npcIndex) + 1);
        }

        private static PlayerRef ResolvePlayerRef(PlayerRoundState state)
        {
            if (state == null || state.Object == null)
            {
                return PlayerRef.None;
            }

            return state.Object.InputAuthority;
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

            if (sessionInfo.IsVisible != isOpen)
            {
                sessionInfo.IsVisible = isOpen;
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

            Dictionary<string, SessionProperty> props = new Dictionary<string, SessionProperty>(10)
            {
                ["matchState"] = (int)State,
                ["round"] = RoundIndex,
                ["zoneStep"] = CurrentZoneStep,
                ["accepting"] = AcceptingPlayers ? 1 : 0,
                ["minPlayers"] = minimumPlayersToStart,
                ["maxPlayers"] = Mathf.Max(minimumPlayersToStart, maximumPlayersPerMatch),
                ["winTarget"] = Mathf.Max(1, MatchWinTarget),
                ["lockedNpcCount"] = Mathf.Max(0, LockedNpcCount),
                ["matchWinner"] = MatchWinnerRawEncoded
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

        [Rpc(RpcSources.All, RpcTargets.StateAuthority, Channel = RpcChannel.Reliable)]
        private void RPC_RequestToggleRuntimeNpcsMoving()
        {
            ToggleRuntimeNpcsMoving();
        }

        private void OnValidate()
        {
            minimumPlayersToStart = Mathf.Max(2, minimumPlayersToStart);
            maximumPlayersPerMatch = Mathf.Max(minimumPlayersToStart, maximumPlayersPerMatch);
            minimumNpcsPerRound = Mathf.Max(1, minimumNpcsPerRound);
            winsToFinishMatch = Mathf.Max(1, winsToFinishMatch);
            waitingForMorePlayersSeconds = Mathf.Max(0f, waitingForMorePlayersSeconds);
            preRoundBoxDelay = Mathf.Max(0f, preRoundBoxDelay);
            classSelectionSeconds = Mathf.Max(1f, classSelectionSeconds);
            countdownSeconds = Mathf.Max(0f, countdownSeconds);
            dropDelaySeconds = Mathf.Max(0f, dropDelaySeconds);
            safeZoneDurationSeconds = Mathf.Max(1f, safeZoneDurationSeconds);
            eliminationResolveDelay = Mathf.Max(0f, eliminationResolveDelay);
            nextZoneTransitionDelay = Mathf.Max(0f, nextZoneTransitionDelay);
            roundFinishedDelay = Mathf.Max(0f, roundFinishedDelay);
            scoreboardDisplaySeconds = Mathf.Max(0.5f, scoreboardDisplaySeconds);
            finalScoreDisplaySeconds = Mathf.Max(0.5f, finalScoreDisplaySeconds);
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
