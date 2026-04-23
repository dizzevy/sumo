using System;
using System.Collections.Generic;
using Fusion;
using UnityEngine;

namespace Sumo
{
    [DisallowMultipleComponent]
    [RequireComponent(typeof(SumoBallController))]
    [RequireComponent(typeof(SumoBallPhysicsConfig))]
    [RequireComponent(typeof(Rigidbody))]
    [RequireComponent(typeof(SphereCollider))]
    public sealed class SumoCollisionController : NetworkBehaviour
    {
        [Header("References")]
        [SerializeField] private SumoBallController ballController;
        [SerializeField] private SumoBallPhysicsConfig physicsConfig;

        [Header("Collision Filtering")]
        [SerializeField] private LayerMask playerMask = ~0;
        [SerializeField] private bool disableNativePlayerCollision = true;

        [Header("Prediction")]
        [SerializeField] private bool enableClientPredictedImpact = true;
        [SerializeField] private bool enableClientPredictedRam = true;
        [SerializeField] private bool applyPredictedForcesToRemoteProxies = true;

        [Header("Victim Local Catchup")]
        [SerializeField] private bool enableVictimLocalCatchup = true;

        [Header("Debug")]
        [SerializeField] private bool logImpacts;
        [SerializeField] private bool logStateMachine;

        public event Action<SumoImpactData> ImpactApplied;

        private const byte PushPhaseNone = 0;
        private const byte PushPhaseImpact = 1;
        private const byte PushPhaseRam = 2;
        private const int LocalVictimCatchupHoldTicks = 6;
        private const int LocalVictimCatchupReleaseTicks = 10;
        private const int LocalVictimImpactMinimumTicks = 4;
        private const int LocalVictimPeakHoldTicks = 8;
        private const float LocalVictimImpactDirectionBlend = 0.4f;
        private const float LocalVictimRamDirectionBlend = 0.14f;
        private const float LocalVictimImpactTargetRiseScale = 1.05f;
        private const float LocalVictimRamTargetRiseScale = 0.82f;
        private const float LocalVictimTargetFallScale = 0.08f;
        private const float LocalVictimRamPeakSpeedFloor = 0.9f;
        private const float LocalVictimReleaseAccelerationDecayPerSecond = 16f;
        private const float LocalVictimReleaseAssistDecayPerSecond = 6f;
        private const float AuthorityVictimPushDirectionBlend = 0.18f;
        private const float AuthorityVictimPushTargetDecayFloor = 0.92f;
        private const float AuthorityVictimPushAccelerationDecayFloor = 0.9f;
        private const float AuthorityVictimPushEnergyDecayFloor = 0.94f;
        private const float FallbackVictimAnticipationMinClosingSpeed = 0.95f;
        private const float FallbackVictimAnticipationMinDirectionDot = 0.2f;
        private const int FallbackVictimAnticipationImpactTicks = 2;
        private const int FallbackVictimAnticipationTtlTicks = 8;
        private const int FallbackVictimAnticipationContactLossTicks = 2;
        private const int FallbackVictimAnticipationHandoffTicks = 3;
        private const float FallbackVictimAnticipationTargetSpeedScale = 0.98f;
        private const float FallbackVictimAnticipationImpactMaxDeltaVPerTick = 0.14f;
        private const float FallbackVictimAnticipationRamMaxDeltaVPerTick = 0.1f;
        private const float VictimAnticipationNearContactPadding = 0.03f;
        private const float ReimpactHardBreakDistanceMultiplier = 1.4f;

        [Networked] private int VictimPresentationSequence { get; set; }
        [Networked] private float VictimPresentationStrength { get; set; }
        [Networked] private byte NetworkVictimPushPhase { get; set; }
        [Networked] private int NetworkVictimPushSequence { get; set; }
        [Networked] private int NetworkVictimPushStartTick { get; set; }
        [Networked] private Vector3 NetworkVictimPushDirection { get; set; }
        [Networked] private float NetworkVictimPushTargetSpeed { get; set; }
        [Networked] private float NetworkVictimPushAcceleration { get; set; }
        [Networked] private float NetworkVictimPushEnergy01 { get; set; }
        [Networked] private float NetworkVictimPushAssist01 { get; set; }
        [Networked] private float NetworkRamDriveAssist01 { get; set; }

        public int VictimPresentationSignalSequence
        {
            get
            {
                if (TryReadVictimPresentationSignal(out int sequence, out _))
                {
                    return sequence;
                }

                return _victimPresentationSequenceFallback;
            }
        }

        public float VictimPresentationSignalStrength
        {
            get
            {
                if (TryReadVictimPresentationSignal(out _, out float strength))
                {
                    return strength;
                }

                return _victimPresentationStrengthFallback;
            }
        }
        public Vector3 CurrentVelocity => _rigidbody != null ? _rigidbody.linearVelocity : Vector3.zero;

        public bool ShouldPredictRemoteProxyForces()
        {
            return applyPredictedForcesToRemoteProxies;
        }

        private Rigidbody _rigidbody;
        private SphereCollider _sphereCollider;
        private Vector3 _preSimVelocity;
        private int _preSimVelocityTick = int.MinValue;
        private int _victimPresentationSequenceFallback;
        private float _victimPresentationStrengthFallback;

        private Vector3 _localVictimCatchupDirection;
        private float _localVictimCatchupTargetSpeed;
        private float _localVictimCatchupAcceleration;
        private float _localVictimCatchupAssist01;
        private float _localVictimCatchupPeakTargetSpeed;
        private byte _localVictimCatchupPhase = PushPhaseNone;
        private int _localVictimCatchupSequence = int.MinValue;
        private int _localVictimCatchupStartTick = int.MinValue;
        private int _localVictimCatchupLastSeenTick = int.MinValue;
        private int _localVictimCatchupPeakTick = int.MinValue;
        private int _localVictimImpactLockUntilTick = int.MinValue;
        private bool _hasLocalVictimCatchupEnvelope;
        private bool _localVictimCatchupFromAnticipation;
        private int _localVictimCatchupHandoffUntilTick = int.MinValue;
        private bool _localVictimCatchupHasDeltaVOverride;
        private float _localVictimCatchupMaxDeltaVOverride;

        private int _localVictimAnticipationAttackerKey;
        private int _localVictimAnticipationStartTick = int.MinValue;
        private int _localVictimAnticipationLastSeenTick = int.MinValue;
        private Vector3 _localVictimAnticipationDirection;
        private float _localVictimAnticipationTargetSpeed;
        private float _localVictimAnticipationAssist01;

        private Vector3 _authorityVictimPushPreviousDirection;
        private float _authorityVictimPushPreviousTargetSpeed;
        private float _authorityVictimPushPreviousAcceleration;
        private float _authorityVictimPushPreviousEnergy01;
        private byte _authorityVictimPushPreviousPhase = PushPhaseNone;
        private int _authorityVictimPushPreviousStartTick = int.MinValue;

        private struct VictimPushEnvelopeSample
        {
            public bool HasValue;
            public bool IsAnticipation;
            public byte Phase;
            public int Sequence;
            public int StartTick;
            public Vector3 Direction;
            public float TargetSpeed;
            public float Acceleration;
            public float Assist01;
            public bool HasMaxDeltaVOverride;
            public float MaxDeltaVPerTick;
        }

        private struct ContactSnapshot
        {
            public SumoCollisionController Other;
            public int SelfKey;
            public int OtherKey;
            public Vector3 ContactPoint;
            public Vector3 ContactNormal;
            public Vector3 SelfVelocity;
            public Vector3 OtherVelocity;
            public float RelativeClosingSpeed;
        }

        private struct AnalyticContact
        {
            public int FirstKey;
            public int SecondKey;
            public Vector3 FirstVelocity;
            public Vector3 SecondVelocity;
            public Vector3 ContactDirection;
            public Vector3 ContactPoint;
            public float Penetration;
            public float Separation;
            public float RelativeClosingSpeed;
            public bool IsContact;
            public bool IsEnter;
        }

        private struct BlockNormalSample
        {
            public int Tick;
            public Vector3 Sum;
        }

        private readonly struct PredictedFrameSnapshot
        {
            public readonly Dictionary<long, SumoRamState> PairStates;
            public readonly Dictionary<long, int> LastImpactTicks;
            public readonly Dictionary<long, int> LastContactTicks;

            public PredictedFrameSnapshot(
                Dictionary<long, SumoRamState> pairStates,
                Dictionary<long, int> lastImpactTicks,
                Dictionary<long, int> lastContactTicks)
            {
                PairStates = pairStates;
                LastImpactTicks = lastImpactTicks;
                LastContactTicks = lastContactTicks;
            }
        }

        private enum SimulationMode : byte
        {
            Authoritative = 0,
            Predicted = 1
        }

        private static readonly Dictionary<long, SumoRamState> PairStates = new Dictionary<long, SumoRamState>(128);
        private static readonly Dictionary<long, SumoRamState> PredictedPairStates = new Dictionary<long, SumoRamState>(128);
        private static readonly List<long> PairKeysBuffer = new List<long>(64);
        private static readonly List<long> PairPruneBuffer = new List<long>(64);
        private static readonly Dictionary<long, int> PredictedPairLastImpactTick = new Dictionary<long, int>(128);
        private static readonly Dictionary<long, int> PredictedPairLastContactTick = new Dictionary<long, int>(128);
        private static readonly Dictionary<int, PredictedFrameSnapshot> PredictedHistory = new Dictionary<int, PredictedFrameSnapshot>(192);
        private static readonly Dictionary<int, SumoCollisionController> ActiveControllers = new Dictionary<int, SumoCollisionController>(128);
        private static readonly Dictionary<int, BlockNormalSample> AuthorityBlockNormals = new Dictionary<int, BlockNormalSample>(128);
        private static readonly Dictionary<int, BlockNormalSample> PredictedBlockNormals = new Dictionary<int, BlockNormalSample>(128);
        private static readonly HashSet<long> IgnoredNativeCollisionPairs = new HashSet<long>();
        private static readonly List<int> PredictedHistoryTickBuffer = new List<int>(128);

        private static int _pairCacheRunnerId = int.MinValue;
        private static int _pairCacheLastTick = int.MinValue;
        private static int _predictedCacheRunnerId = int.MinValue;
        private static int _predictedCacheLastTick = int.MinValue;
        private static int _preSimVelocityCacheRunnerId = int.MinValue;
        private static int _preSimVelocityCacheTick = int.MinValue;
        private static int _authorityEnvelopeResetRunnerId = int.MinValue;
        private static int _authorityEnvelopeResetTick = int.MinValue;

        private const int FallbackContactBreakGraceTicks = 6;
        private const int FallbackReengageBreakTicks = 6;
        private const float FallbackReengageDistance = 0.22f;
        private const float FallbackReengageSpeed = 4.8f;
        private const float FallbackTieSpeedEpsilon = 0.15f;
        private const float FallbackMinRamPressureSpeed = 1.6f;
        private const float FallbackRamStopEnergyThreshold = 0.08f;
        private const float FallbackImpactVerticalLift = 0.02f;
        private const float FallbackImpactBurstDuration = 0.08f;
        private const float FallbackFirstImpactBurstFrontload = 0.62f;
        private const float FallbackFirstImpactKickShare = 0.48f;
        private const float FallbackAttackerReferenceTopSpeed = 10f;
        private const int FallbackMaxRamDurationTicks = 32;
        private const int FallbackMinReimpactTicks = 12;
        private const float RamContactBlendRisePerSecond = 13f;
        private const float RamContactBlendFallPerSecond = 7f;
        private const float RamContactStartBlend = 0.24f;
        private const float ActivePhaseContactSeparationEpsilon = 0.008f;
        private const float ActivePhaseContactExtraTolerance = 0.012f;
        private const float PairDirectionBlend = 0.2f;
        private const int SoftStalePairTicks = 420;
        private const int HardStalePairTicks = 1200;
        private const float FallbackPlayerContactEnterPadding = 0.003f;
        private const float FallbackPlayerContactExitPadding = 0.024f;
        private const float FallbackPlayerContactPenetrationSlop = 0.01f;
        private const float FallbackPlayerContactPositionCorrection = 0.85f;
        private const float FallbackPlayerContactVelocityDamping = 1f;
        private const float PredictedPenetrationResolveRate = 22f;
        private const float PredictedPenetrationResolveMaxSpeed = 6f;
        private const float AuthorityMaxPositionCorrectionPerTick = 0.05f;
        private const float ActiveContactVelocityDampingScale = 0.32f;
        private const float ActiveContactClosingDeadZone = 0.25f;
        private const float ActiveContactPenetrationResolveRate = 12f;
        private const float ActiveContactPenetrationResolveMaxSpeed = 2.6f;
        private const float PredictedActiveCombatPenetrationResolveRate = 7.5f;
        private const float PredictedActiveCombatPenetrationResolveMaxSpeed = 1.35f;
        private const float PredictedActiveCombatPenetrationDeadZone = 0.012f;
        private const int ActiveCombatVictimImpactResponseTicks = 2;
        private const float ActiveCombatVictimImpactAuthorityResponseShare = 0.22f;
        private const float ActiveCombatVictimImpactPredictedResponseShare = 0.08f;
        private const float ActiveCombatVictimRamAuthorityResponseShare = 0.08f;
        private const float ActiveCombatVictimRamPredictedResponseShare = 0.02f;
        private const int ReimpactSuppressionContactResetTicks = 12;
        private const float ActiveCombatPenetrationSlopBonus = 0.025f;
        private const int PredictedRollbackHistoryTicks = 192;

        private void Awake()
        {
            CacheComponents();
        }

        private void OnEnable()
        {
            CacheComponents();
            RegisterActiveController();
        }

        private void OnDisable()
        {
            UnregisterActiveController();
        }

        public override void Spawned()
        {
            CacheComponents();
            EnsurePairCacheContext();
            RegisterActiveController();
        }

        public override void Despawned(NetworkRunner runner, bool hasState)
        {
            int controllerKey = GetControllerKey(this);
            if (controllerKey != 0)
            {
                RemoveStatesForController(controllerKey, PairStates);
                RemoveStatesForController(controllerKey, PredictedPairStates);
                AuthorityBlockNormals.Remove(controllerKey);
                PredictedBlockNormals.Remove(controllerKey);
            }

            UnregisterActiveController();
        }

        public override void FixedUpdateNetwork()
        {
            CacheComponents();
            RegisterActiveController();

            int currentTick = Runner != null ? Runner.Tick.Raw : Time.frameCount;
            EnsurePreSimVelocitySamples(currentTick);
            CachePreSimVelocity(currentTick);

            if (CanProcessAuthorityTick())
            {
                EnsureAuthorityCombatFrame(currentTick);
                CaptureAnalyticContactsForOwnedPairs(currentTick, PairStates, SimulationMode.Authoritative);
                ProcessOwnedPairStates(currentTick, PairStates, SimulationMode.Authoritative);
                PrunePairStates(currentTick, PairStates);
            }

            RefreshLocalVictimCatchupEnvelope(currentTick);
            bool localVictimPresentationActive = IsLocalVictimPresentationActive();

            if (localVictimPresentationActive)
            {
                ClearPredictedStateForLocalVictim();
            }
            else if (CanProcessPredictedTick())
            {
                CaptureAnalyticContactsForOwnedPairs(currentTick, PredictedPairStates, SimulationMode.Predicted);
                ProcessOwnedPairStates(currentTick, PredictedPairStates, SimulationMode.Predicted);
                PrunePairStates(currentTick, PredictedPairStates);
                StorePredictedFrame(currentTick);
            }

            if (localVictimPresentationActive)
            {
                ApplyVictimLocalCatchup();
            }
        }


        private void EnsureAuthorityCombatFrame(int currentTick)
        {
            if (Runner == null)
            {
                return;
            }

            int runnerId = Runner.GetInstanceID();
            if (_authorityEnvelopeResetRunnerId == runnerId && _authorityEnvelopeResetTick == currentTick)
            {
                return;
            }

            foreach (KeyValuePair<int, SumoCollisionController> entry in ActiveControllers)
            {
                SumoCollisionController controller = entry.Value;
                if (controller == null || controller.Runner != Runner || !controller.HasStateAuthority)
                {
                    continue;
                }

                controller.BeginAuthorityCombatFrame();
            }

            _authorityEnvelopeResetRunnerId = runnerId;
            _authorityEnvelopeResetTick = currentTick;
        }

        private void BeginAuthorityCombatFrame()
        {
            _authorityVictimPushPreviousPhase = NetworkVictimPushPhase;
            _authorityVictimPushPreviousStartTick = NetworkVictimPushStartTick;
            _authorityVictimPushPreviousDirection = NetworkVictimPushDirection;
            _authorityVictimPushPreviousTargetSpeed = NetworkVictimPushTargetSpeed;
            _authorityVictimPushPreviousAcceleration = NetworkVictimPushAcceleration;
            _authorityVictimPushPreviousEnergy01 = NetworkVictimPushEnergy01;

            NetworkRamDriveAssist01 = 0f;
            NetworkVictimPushAssist01 = 0f;
            NetworkVictimPushPhase = PushPhaseNone;
            NetworkVictimPushDirection = Vector3.zero;
            NetworkVictimPushTargetSpeed = 0f;
            NetworkVictimPushAcceleration = 0f;
            NetworkVictimPushEnergy01 = 0f;
        }

        private bool IsLocalVictimPresentationActive()
        {
            if (Runner == null
                || !Runner.IsClient
                || !HasInputAuthority
                || HasStateAuthority
                || !enableVictimLocalCatchup)
            {
                return false;
            }

            if (!_hasLocalVictimCatchupEnvelope)
            {
                return false;
            }

            int currentTick = Runner.Tick.Raw;
            if (HasActiveLocalAttackerRole(currentTick))
            {
                return false;
            }

            return currentTick - _localVictimCatchupLastSeenTick <= LocalVictimCatchupReleaseTicks;
        }

        private bool IsLocalVictimCatchupEnvelopeActiveForPresentation()
        {
            if (Runner == null
                || !Runner.IsClient
                || !HasInputAuthority
                || !enableVictimLocalCatchup
                || !_hasLocalVictimCatchupEnvelope)
            {
                return false;
            }

            int currentTick = Runner.Tick.Raw;
            if (currentTick - _localVictimCatchupLastSeenTick > LocalVictimCatchupReleaseTicks)
            {
                return false;
            }

            return _localVictimCatchupPhase != PushPhaseNone
                && (_localVictimCatchupTargetSpeed > 0.01f || _localVictimCatchupAssist01 > 0.01f);
        }

        private bool HasActiveLocalAttackerRole(int currentTick)
        {
            if (Runner == null
                || !Runner.IsClient
                || !HasInputAuthority
                || HasStateAuthority)
            {
                return false;
            }

            int selfKey = GetControllerKey(this);
            if (selfKey == 0)
            {
                return false;
            }

            foreach (KeyValuePair<long, SumoRamState> pair in PredictedPairStates)
            {
                SumoRamState state = pair.Value;
                if (state.AttackerRef != selfKey)
                {
                    continue;
                }

                if (state.State != SumoPairState.InitialImpact && state.State != SumoPairState.Ramming)
                {
                    continue;
                }

                int contactBreakTicks = GetPairContactBreakGraceTicks(state.FirstController, state.SecondController);
                if (!IsContactActive(state, currentTick, contactBreakTicks))
                {
                    continue;
                }

                if (RequiresPhysicalContact(state.State)
                    && ComputeEdgeSeparation(state.FirstController, state.SecondController)
                        > GetActiveContactSeparationTolerance(state.FirstController, state.SecondController) + 0.03f)
                {
                    continue;
                }

                return true;
            }

            return Mathf.Clamp01(NetworkRamDriveAssist01) >= 0.35f
                && NetworkVictimPushPhase == PushPhaseNone;
        }

        private void RefreshLocalVictimCatchupEnvelope(int currentTick)
        {
            if (Runner == null
                || !Runner.IsClient
                || !HasInputAuthority
                || HasStateAuthority
                || !enableVictimLocalCatchup)
            {
                ClearLocalVictimCatchupEnvelope();
                return;
            }

            float dt = Runner != null ? Runner.DeltaTime : Time.fixedDeltaTime;
            if (dt <= 0f)
            {
                dt = Time.fixedDeltaTime > 0f ? Time.fixedDeltaTime : (1f / 60f);
            }

            bool hasNetworkPush = TryBuildAuthoritativeVictimPushSample(out VictimPushEnvelopeSample networkSample);
            bool localAttackerActive = HasActiveLocalAttackerRole(currentTick);
            if (localAttackerActive && !hasNetworkPush)
            {
                if (_localVictimCatchupFromAnticipation)
                {
                    ClearLocalVictimCatchupEnvelope();
                    return;
                }

                ClearLocalVictimAnticipationState();
            }

            VictimPushEnvelopeSample anticipatedSample = default;
            bool hasAnticipationPush = !localAttackerActive
                && TryBuildAnticipatedVictimPushSample(currentTick, out anticipatedSample);

            VictimPushEnvelopeSample sample = default;
            bool hasSample = false;

            if (hasNetworkPush)
            {
                sample = networkSample;
                hasSample = true;
            }
            else if (hasAnticipationPush)
            {
                sample = anticipatedSample;
                hasSample = true;
            }

            if (hasSample)
            {
                int handoffTicks = physicsConfig != null
                    ? physicsConfig.VictimAnticipationHandoffTicks
                    : FallbackVictimAnticipationHandoffTicks;

                bool transitioningFromAnticipationToNetwork = !sample.IsAnticipation
                    && _hasLocalVictimCatchupEnvelope
                    && _localVictimCatchupFromAnticipation;

                if (transitioningFromAnticipationToNetwork)
                {
                    _localVictimCatchupHandoffUntilTick = currentTick + Mathf.Max(1, handoffTicks);
                }

                bool allowSoftHandoff = !sample.IsAnticipation
                    && currentTick <= _localVictimCatchupHandoffUntilTick;

                bool resetEnvelope = !_hasLocalVictimCatchupEnvelope
                    || currentTick - _localVictimCatchupLastSeenTick > LocalVictimCatchupReleaseTicks
                    || (sample.Sequence != _localVictimCatchupSequence && !allowSoftHandoff)
                    || (sample.StartTick != _localVictimCatchupStartTick && !allowSoftHandoff)
                    || (_localVictimCatchupPhase != PushPhaseImpact && sample.Phase == PushPhaseImpact && !allowSoftHandoff && !_localVictimCatchupFromAnticipation);

                if (resetEnvelope)
                {
                    _localVictimCatchupDirection = sample.Direction;
                    _localVictimCatchupTargetSpeed = sample.TargetSpeed;
                    _localVictimCatchupAcceleration = sample.Acceleration;
                    _localVictimCatchupAssist01 = sample.Assist01;
                    _localVictimCatchupPeakTargetSpeed = sample.TargetSpeed;
                    _localVictimCatchupPeakTick = currentTick;
                    _localVictimImpactLockUntilTick = sample.Phase == PushPhaseImpact
                        ? currentTick + LocalVictimImpactMinimumTicks
                        : currentTick;
                }
                else
                {
                    float directionBlend = sample.Phase == PushPhaseImpact
                        ? LocalVictimImpactDirectionBlend
                        : LocalVictimRamDirectionBlend;

                    if (allowSoftHandoff)
                    {
                        directionBlend = Mathf.Max(directionBlend, 0.26f);
                    }

                    Vector3 previousDirection = _localVictimCatchupDirection.sqrMagnitude > 0.0001f
                        ? _localVictimCatchupDirection.normalized
                        : sample.Direction;
                    _localVictimCatchupDirection = Vector3.Slerp(previousDirection, sample.Direction, Mathf.Clamp01(directionBlend));
                    if (_localVictimCatchupDirection.sqrMagnitude > 0.0001f)
                    {
                        _localVictimCatchupDirection.Normalize();
                    }
                    else
                    {
                        _localVictimCatchupDirection = sample.Direction;
                    }

                    float riseScale = sample.Phase == PushPhaseImpact
                        ? LocalVictimImpactTargetRiseScale
                        : LocalVictimRamTargetRiseScale;
                    float riseRate = Mathf.Max(0.01f, Mathf.Max(_localVictimCatchupAcceleration, sample.Acceleration) * riseScale);
                    float fallRate = Mathf.Max(0.01f, riseRate * LocalVictimTargetFallScale);
                    float preservedTargetFloor = 0f;

                    bool preservingImpactLaunch = currentTick <= _localVictimImpactLockUntilTick;
                    if (sample.Phase == PushPhaseRam)
                    {
                        if (preservingImpactLaunch)
                        {
                            preservedTargetFloor = _localVictimCatchupPeakTargetSpeed;
                        }
                        else if (currentTick - _localVictimCatchupPeakTick <= LocalVictimPeakHoldTicks)
                        {
                            preservedTargetFloor = _localVictimCatchupPeakTargetSpeed * LocalVictimRamPeakSpeedFloor;
                        }
                    }

                    float rawTargetSpeed = Mathf.Max(sample.TargetSpeed, preservedTargetFloor);
                    float targetRate = rawTargetSpeed >= _localVictimCatchupTargetSpeed ? riseRate : fallRate;
                    _localVictimCatchupTargetSpeed = Mathf.MoveTowards(_localVictimCatchupTargetSpeed, rawTargetSpeed, targetRate * dt);
                    _localVictimCatchupAcceleration = Mathf.MoveTowards(
                        _localVictimCatchupAcceleration,
                        sample.Acceleration,
                        32f * dt);
                    _localVictimCatchupAssist01 = Mathf.MoveTowards(
                        _localVictimCatchupAssist01,
                        sample.Assist01,
                        8f * dt);

                    if (_localVictimCatchupTargetSpeed > _localVictimCatchupPeakTargetSpeed)
                    {
                        _localVictimCatchupPeakTargetSpeed = _localVictimCatchupTargetSpeed;
                        _localVictimCatchupPeakTick = currentTick;
                    }

                    if (sample.Phase == PushPhaseImpact)
                    {
                        _localVictimImpactLockUntilTick = currentTick + LocalVictimImpactMinimumTicks;
                    }
                }

                _localVictimCatchupFromAnticipation = sample.IsAnticipation;
                _localVictimCatchupHasDeltaVOverride = sample.HasMaxDeltaVOverride;
                _localVictimCatchupMaxDeltaVOverride = sample.MaxDeltaVPerTick;
                _localVictimCatchupPhase = sample.Phase;
                _localVictimCatchupSequence = sample.Sequence;
                _localVictimCatchupStartTick = sample.StartTick;
                _localVictimCatchupLastSeenTick = currentTick;
                _hasLocalVictimCatchupEnvelope = true;
                return;
            }

            if (_hasLocalVictimCatchupEnvelope)
            {
                int ticksSinceLastSeen = currentTick - _localVictimCatchupLastSeenTick;
                if (ticksSinceLastSeen <= LocalVictimCatchupReleaseTicks)
                {
                    _localVictimCatchupTargetSpeed = Mathf.MoveTowards(
                        _localVictimCatchupTargetSpeed,
                        0f,
                        Mathf.Max(1f, _localVictimCatchupAcceleration * LocalVictimTargetFallScale) * dt);
                    _localVictimCatchupAcceleration = Mathf.MoveTowards(
                        _localVictimCatchupAcceleration,
                        0f,
                        LocalVictimReleaseAccelerationDecayPerSecond * dt);
                    _localVictimCatchupAssist01 = Mathf.MoveTowards(
                        _localVictimCatchupAssist01,
                        0f,
                        LocalVictimReleaseAssistDecayPerSecond * dt);
                    return;
                }
            }

            ClearLocalVictimCatchupEnvelope();
        }

        private void ClearLocalVictimCatchupEnvelope()
        {
            _hasLocalVictimCatchupEnvelope = false;
            _localVictimCatchupDirection = Vector3.zero;
            _localVictimCatchupTargetSpeed = 0f;
            _localVictimCatchupAcceleration = 0f;
            _localVictimCatchupAssist01 = 0f;
            _localVictimCatchupPeakTargetSpeed = 0f;
            _localVictimCatchupPhase = PushPhaseNone;
            _localVictimCatchupSequence = int.MinValue;
            _localVictimCatchupStartTick = int.MinValue;
            _localVictimCatchupLastSeenTick = int.MinValue;
            _localVictimCatchupPeakTick = int.MinValue;
            _localVictimImpactLockUntilTick = int.MinValue;
            _localVictimCatchupFromAnticipation = false;
            _localVictimCatchupHandoffUntilTick = int.MinValue;
            _localVictimCatchupHasDeltaVOverride = false;
            _localVictimCatchupMaxDeltaVOverride = 0f;
            ClearLocalVictimAnticipationState();
        }

        private void ClearLocalVictimAnticipationState()
        {
            _localVictimAnticipationAttackerKey = 0;
            _localVictimAnticipationStartTick = int.MinValue;
            _localVictimAnticipationLastSeenTick = int.MinValue;
            _localVictimAnticipationDirection = Vector3.zero;
            _localVictimAnticipationTargetSpeed = 0f;
            _localVictimAnticipationAssist01 = 0f;
        }

        private bool TryBuildAuthoritativeVictimPushSample(out VictimPushEnvelopeSample sample)
        {
            sample = default;
            if (NetworkVictimPushPhase == PushPhaseNone)
            {
                return false;
            }

            Vector3 direction = GetHorizontalDirection(NetworkVictimPushDirection);
            if (direction.sqrMagnitude < 0.0001f)
            {
                direction = NetworkVictimPushDirection;
            }

            if (direction.sqrMagnitude < 0.0001f)
            {
                return false;
            }

            direction.Normalize();
            sample = new VictimPushEnvelopeSample
            {
                HasValue = true,
                IsAnticipation = false,
                Phase = NetworkVictimPushPhase,
                Sequence = NetworkVictimPushSequence,
                StartTick = NetworkVictimPushStartTick,
                Direction = direction,
                TargetSpeed = Mathf.Max(0f, NetworkVictimPushTargetSpeed),
                Acceleration = Mathf.Max(0f, NetworkVictimPushAcceleration),
                Assist01 = Mathf.Clamp01(NetworkVictimPushAssist01),
                HasMaxDeltaVOverride = false,
                MaxDeltaVPerTick = 0f
            };
            return true;
        }

        private bool TryBuildAnticipatedVictimPushSample(int currentTick, out VictimPushEnvelopeSample sample)
        {
            sample = default;
            if (_rigidbody == null || physicsConfig == null)
            {
                CacheComponents();
            }

            if (_rigidbody == null || physicsConfig == null)
            {
                return false;
            }

            int selfKey = GetControllerKey(this);
            if (selfKey == 0)
            {
                return false;
            }

            float minClosingSpeed = physicsConfig != null
                ? physicsConfig.VictimAnticipationMinClosingSpeed
                : FallbackVictimAnticipationMinClosingSpeed;
            float minDirectionDot = physicsConfig != null
                ? physicsConfig.VictimAnticipationMinDirectionDot
                : FallbackVictimAnticipationMinDirectionDot;
            int impactTicks = physicsConfig != null
                ? physicsConfig.VictimAnticipationImpactTicks
                : FallbackVictimAnticipationImpactTicks;
            int ttlTicks = physicsConfig != null
                ? physicsConfig.VictimAnticipationTtlTicks
                : FallbackVictimAnticipationTtlTicks;
            int contactLossTicks = physicsConfig != null
                ? physicsConfig.VictimAnticipationContactLossTicks
                : FallbackVictimAnticipationContactLossTicks;
            float targetSpeedScale = physicsConfig != null
                ? physicsConfig.VictimAnticipationTargetSpeedScale
                : FallbackVictimAnticipationTargetSpeedScale;

            bool foundFresh = false;
            int bestAttackerKey = 0;
            Vector3 bestDirection = Vector3.zero;
            float bestTargetSpeed = 0f;
            float bestRelativeClosing = 0f;
            float bestScore = float.NegativeInfinity;

            Vector3 selfVelocity = _rigidbody.linearVelocity;
            Vector3 selfCenter = _rigidbody.worldCenterOfMass;

            foreach (KeyValuePair<int, SumoCollisionController> entry in ActiveControllers)
            {
                SumoCollisionController other = entry.Value;
                int otherKey = entry.Key;
                if (other == null
                    || other == this
                    || otherKey == 0
                    || otherKey == selfKey
                    || other.Runner != Runner
                    || other.HasInputAuthority)
                {
                    continue;
                }

                if (other._rigidbody == null || other._sphereCollider == null || other.ballController == null)
                {
                    other.CacheComponents();
                }

                if (other._rigidbody == null
                    || other._rigidbody.isKinematic
                    || other.Object == null
                    || !other.Object.IsInSimulation)
                {
                    continue;
                }

                int otherLayerMask = 1 << other.gameObject.layer;
                int selfLayerMask = 1 << gameObject.layer;
                bool layerAllowed = (playerMask.value & otherLayerMask) != 0
                    || (other.playerMask.value & selfLayerMask) != 0;
                if (!layerAllowed)
                {
                    continue;
                }

                Vector3 otherCenter = other._rigidbody.worldCenterOfMass;
                Vector3 otherToSelf = ResolveDirection(otherCenter, selfCenter, _localVictimAnticipationDirection);
                float centerDistance = Vector3.Distance(otherCenter, selfCenter);
                float combinedRadius = GetScaledRadius(other) + GetScaledRadius(this);
                float edgeSeparation = Mathf.Max(0f, centerDistance - combinedRadius);
                float nearContactDistance = Mathf.Max(
                    VictimAnticipationNearContactPadding,
                    GetPairContactEnterPadding(this, other) + 0.015f);
                if (edgeSeparation > nearContactDistance)
                {
                    continue;
                }

                Vector3 otherVelocity = other.GetVelocitySampleForTick(currentTick);
                float relativeClosingSpeed = Mathf.Max(0f, Vector3.Dot(otherVelocity - selfVelocity, otherToSelf));
                if (relativeClosingSpeed < minClosingSpeed)
                {
                    continue;
                }

                float directionDot = ComputeDirectionDot(other, otherVelocity, otherToSelf);
                if (directionDot < minDirectionDot)
                {
                    continue;
                }

                float attackerForwardSpeed = Mathf.Max(0f, Vector3.Dot(otherVelocity, otherToSelf));
                attackerForwardSpeed = Mathf.Max(attackerForwardSpeed, GetIntentApproachSpeed(other, otherToSelf, otherVelocity));
                if (attackerForwardSpeed <= 0.0001f)
                {
                    continue;
                }

                float selfApproachTowardOther = GetApproachSpeed(this, selfVelocity, -otherToSelf);
                float dominanceEpsilon = GetPairTieSpeedEpsilon(this, other);
                if (attackerForwardSpeed <= selfApproachTowardOther + dominanceEpsilon)
                {
                    continue;
                }

                float currentForwardSpeed = Mathf.Max(0f, Vector3.Dot(selfVelocity, otherToSelf));
                float targetSpeed = Mathf.Max(currentForwardSpeed, attackerForwardSpeed * targetSpeedScale);
                float score = relativeClosingSpeed * 2f + attackerForwardSpeed + directionDot * 0.5f - edgeSeparation * 3f;
                if (score <= bestScore)
                {
                    continue;
                }

                bestScore = score;
                bestAttackerKey = otherKey;
                bestDirection = otherToSelf;
                bestTargetSpeed = targetSpeed;
                bestRelativeClosing = relativeClosingSpeed;
                foundFresh = true;
            }

            if (foundFresh)
            {
                bool sameAttacker = _localVictimAnticipationAttackerKey == bestAttackerKey
                    && currentTick - _localVictimAnticipationLastSeenTick <= contactLossTicks;
                if (!sameAttacker)
                {
                    _localVictimAnticipationAttackerKey = bestAttackerKey;
                    _localVictimAnticipationStartTick = currentTick;
                }

                _localVictimAnticipationLastSeenTick = currentTick;
                _localVictimAnticipationDirection = bestDirection.sqrMagnitude > 0.0001f
                    ? bestDirection.normalized
                    : _localVictimAnticipationDirection;
                _localVictimAnticipationTargetSpeed = bestTargetSpeed;
                _localVictimAnticipationAssist01 = Mathf.Clamp01(Mathf.InverseLerp(
                    minClosingSpeed,
                    minClosingSpeed + 3.5f,
                    bestRelativeClosing));
            }
            else
            {
                if (_localVictimAnticipationAttackerKey == 0 || _localVictimAnticipationStartTick == int.MinValue)
                {
                    return false;
                }

                int ticksSinceSeen = currentTick - _localVictimAnticipationLastSeenTick;
                int lifetimeTicks = currentTick - _localVictimAnticipationStartTick;
                if (ticksSinceSeen > contactLossTicks || lifetimeTicks > ttlTicks)
                {
                    ClearLocalVictimAnticipationState();
                    return false;
                }

                _localVictimAnticipationTargetSpeed = Mathf.Max(0f, _localVictimAnticipationTargetSpeed * 0.95f);
                _localVictimAnticipationAssist01 = Mathf.Max(0f, _localVictimAnticipationAssist01 * 0.9f);
            }

            if (_localVictimAnticipationDirection.sqrMagnitude < 0.0001f)
            {
                return false;
            }

            int elapsedTicks = Mathf.Max(0, currentTick - _localVictimAnticipationStartTick);
            byte phase = elapsedTicks < Mathf.Max(1, impactTicks) ? PushPhaseImpact : PushPhaseRam;
            float maxDeltaVPerTick = phase == PushPhaseImpact
                ? (physicsConfig != null
                    ? physicsConfig.VictimAnticipationImpactMaxDeltaVPerTick
                    : FallbackVictimAnticipationImpactMaxDeltaVPerTick)
                : (physicsConfig != null
                    ? physicsConfig.VictimAnticipationRamMaxDeltaVPerTick
                    : FallbackVictimAnticipationRamMaxDeltaVPerTick);

            float acceleration = phase == PushPhaseImpact
                ? physicsConfig.VictimLocalImpactCatchupAcceleration
                : physicsConfig.VictimLocalRamCatchupAcceleration;

            float currentForward = Mathf.Max(0f, Vector3.Dot(selfVelocity, _localVictimAnticipationDirection));
            float targetSpeedFloor = phase == PushPhaseImpact
                ? currentForward
                : currentForward * 0.98f;

            sample = new VictimPushEnvelopeSample
            {
                HasValue = true,
                IsAnticipation = true,
                Phase = phase,
                Sequence = _localVictimAnticipationAttackerKey,
                StartTick = _localVictimAnticipationStartTick,
                Direction = _localVictimAnticipationDirection,
                TargetSpeed = Mathf.Max(targetSpeedFloor, _localVictimAnticipationTargetSpeed),
                Acceleration = Mathf.Max(0f, acceleration),
                Assist01 = Mathf.Clamp01(Mathf.Max(_localVictimAnticipationAssist01, phase == PushPhaseImpact ? 1f : 0.55f)),
                HasMaxDeltaVOverride = true,
                MaxDeltaVPerTick = Mathf.Max(0.01f, maxDeltaVPerTick)
            };

            return true;
        }

        private void ClearPredictedStateForLocalVictim()
        {
            int selfKey = GetControllerKey(this);
            if (selfKey == 0)
            {
                return;
            }

            PredictedBlockNormals.Remove(selfKey);
            RemoveStatesForController(selfKey, PredictedPairStates);

            PairKeysBuffer.Clear();
            foreach (KeyValuePair<long, int> pair in PredictedPairLastImpactTick)
            {
                long pairKey = pair.Key;
                int firstKey = (int)(pairKey >> 32);
                int secondKey = unchecked((int)(pairKey & 0xFFFFFFFF));
                if (firstKey == selfKey || secondKey == selfKey)
                {
                    PairKeysBuffer.Add(pairKey);
                }
            }

            for (int i = 0; i < PairKeysBuffer.Count; i++)
            {
                long pairKey = PairKeysBuffer[i];
                PredictedPairLastImpactTick.Remove(pairKey);
                PredictedPairLastContactTick.Remove(pairKey);
            }

            PairKeysBuffer.Clear();
        }

        private void ApplyVictimLocalCatchup()
        {
            if (_rigidbody == null || physicsConfig == null)
            {
                return;
            }

            if (!_hasLocalVictimCatchupEnvelope)
            {
                return;
            }

            byte phase = _localVictimCatchupPhase;
            if (phase == PushPhaseNone)
            {
                return;
            }

            Vector3 direction = _localVictimCatchupDirection;
            if (direction.sqrMagnitude < 0.0001f)
            {
                return;
            }

            direction.Normalize();

            float dt = Runner != null ? Runner.DeltaTime : Time.fixedDeltaTime;
            if (dt <= 0f)
            {
                return;
            }

            int currentTick = Runner != null ? Runner.Tick.Raw : Time.frameCount;
            float assist01 = Mathf.Clamp01(_localVictimCatchupAssist01);
            float currentForwardSpeed = Vector3.Dot(_rigidbody.linearVelocity, direction);
            float desiredForwardSpeed = Mathf.Max(
                currentForwardSpeed,
                _localVictimCatchupTargetSpeed * physicsConfig.VictimLocalPushPrediction);

            if (currentTick <= _localVictimImpactLockUntilTick)
            {
                desiredForwardSpeed = Mathf.Max(desiredForwardSpeed, _localVictimCatchupPeakTargetSpeed);
            }
            else if (currentTick - _localVictimCatchupPeakTick <= LocalVictimPeakHoldTicks)
            {
                desiredForwardSpeed = Mathf.Max(
                    desiredForwardSpeed,
                    _localVictimCatchupPeakTargetSpeed * Mathf.Lerp(LocalVictimRamPeakSpeedFloor, 1f, assist01));
            }

            float configuredCatchupAcceleration = phase == PushPhaseImpact
                ? physicsConfig.VictimLocalImpactCatchupAcceleration
                : physicsConfig.VictimLocalRamCatchupAcceleration;

            float catchupAcceleration = Mathf.Max(
                configuredCatchupAcceleration,
                _localVictimCatchupAcceleration * physicsConfig.VictimLocalPushPrediction);
            catchupAcceleration *= Mathf.Lerp(0.9f, 1.15f, assist01);

            float nextForwardSpeed = Mathf.MoveTowards(
                currentForwardSpeed,
                desiredForwardSpeed,
                catchupAcceleration * dt);

            float positiveDeltaVCap = physicsConfig.VictimLocalPushMaxDeltaVPerTick;
            if (phase == PushPhaseImpact)
            {
                positiveDeltaVCap = Mathf.Min(positiveDeltaVCap, physicsConfig.VictimLocalImpactMaxDeltaVPerTick);
            }
            else
            {
                positiveDeltaVCap *= Mathf.Lerp(0.95f, 1.2f, assist01);
            }

            if (_localVictimCatchupHasDeltaVOverride)
            {
                positiveDeltaVCap = Mathf.Min(positiveDeltaVCap, Mathf.Max(0.01f, _localVictimCatchupMaxDeltaVOverride));
            }

            float deltaV = nextForwardSpeed - currentForwardSpeed;
            deltaV = Mathf.Clamp(
                deltaV,
                -physicsConfig.VictimLocalPushBrakeDeltaVPerTick,
                positiveDeltaVCap);

            if (Mathf.Abs(deltaV) <= 0.0001f)
            {
                return;
            }

            _rigidbody.AddForce(direction * deltaV, ForceMode.VelocityChange);
        }

        private void PublishRamDrive(SumoCollisionController attacker, float assist01)
        {
            if (attacker == null || !attacker.HasStateAuthority)
            {
                return;
            }

            attacker.NetworkRamDriveAssist01 = Mathf.Max(attacker.NetworkRamDriveAssist01, Mathf.Clamp01(assist01));
        }

        private void PublishVictimPush(
            SumoCollisionController victim,
            byte phase,
            Vector3 direction,
            float targetSpeed,
            float acceleration,
            float energy01,
            int startTick)
        {
            if (victim == null || !victim.HasStateAuthority)
            {
                return;
            }

            Vector3 normalizedDirection = GetHorizontalDirection(direction);
            if (normalizedDirection.sqrMagnitude < 0.0001f)
            {
                normalizedDirection = direction;
            }

            if (normalizedDirection.sqrMagnitude > 0.0001f)
            {
                normalizedDirection.Normalize();
            }

            bool sameEnvelopeAsPrevious = victim._authorityVictimPushPreviousPhase != PushPhaseNone
                && victim._authorityVictimPushPreviousStartTick == startTick;

            if (phase == PushPhaseRam && sameEnvelopeAsPrevious)
            {
                Vector3 previousDirection = GetHorizontalDirection(victim._authorityVictimPushPreviousDirection);
                if (previousDirection.sqrMagnitude < 0.0001f)
                {
                    previousDirection = victim._authorityVictimPushPreviousDirection;
                }

                if (previousDirection.sqrMagnitude > 0.0001f && normalizedDirection.sqrMagnitude > 0.0001f)
                {
                    normalizedDirection = Vector3.Slerp(
                        previousDirection.normalized,
                        normalizedDirection,
                        AuthorityVictimPushDirectionBlend);

                    if (normalizedDirection.sqrMagnitude > 0.0001f)
                    {
                        normalizedDirection.Normalize();
                    }
                }

                targetSpeed = Mathf.Max(
                    Mathf.Max(0f, targetSpeed),
                    victim._authorityVictimPushPreviousTargetSpeed * AuthorityVictimPushTargetDecayFloor);
                acceleration = Mathf.Max(
                    Mathf.Max(0f, acceleration),
                    victim._authorityVictimPushPreviousAcceleration * AuthorityVictimPushAccelerationDecayFloor);
                energy01 = Mathf.Max(
                    Mathf.Clamp01(energy01),
                    victim._authorityVictimPushPreviousEnergy01 * AuthorityVictimPushEnergyDecayFloor);
            }

            float score = Mathf.Max(0f, targetSpeed) + Mathf.Clamp01(energy01) * 100f;
            float currentScore = Mathf.Max(0f, victim.NetworkVictimPushTargetSpeed) + Mathf.Clamp01(victim.NetworkVictimPushEnergy01) * 100f;
            if (victim.NetworkVictimPushPhase != PushPhaseNone && score + 0.001f < currentScore)
            {
                return;
            }

            if (phase == PushPhaseImpact && (victim.NetworkVictimPushPhase != PushPhaseImpact || victim.NetworkVictimPushStartTick != startTick))
            {
                victim.NetworkVictimPushSequence++;
            }

            victim.NetworkVictimPushPhase = phase;
            victim.NetworkVictimPushStartTick = startTick;
            victim.NetworkVictimPushDirection = normalizedDirection;
            victim.NetworkVictimPushTargetSpeed = Mathf.Max(0f, targetSpeed);
            victim.NetworkVictimPushAcceleration = Mathf.Max(0f, acceleration);
            victim.NetworkVictimPushEnergy01 = Mathf.Clamp01(energy01);
            victim.NetworkVictimPushAssist01 = Mathf.Max(victim.NetworkVictimPushAssist01, Mathf.Clamp01(Mathf.Max(energy01, phase == PushPhaseImpact ? 1f : 0f)));
        }
        private void EnsurePreSimVelocitySamples(int currentTick)
        {
            if (Runner == null)
            {
                return;
            }

            int runnerId = Runner.GetInstanceID();
            if (_preSimVelocityCacheRunnerId == runnerId && _preSimVelocityCacheTick == currentTick)
            {
                return;
            }

            foreach (KeyValuePair<int, SumoCollisionController> entry in ActiveControllers)
            {
                SumoCollisionController controller = entry.Value;
                if (controller == null || controller.Runner != Runner)
                {
                    continue;
                }

                if (controller._rigidbody == null || controller._sphereCollider == null || controller.ballController == null)
                {
                    controller.CacheComponents();
                }

                controller.CachePreSimVelocity(currentTick);
            }

            _preSimVelocityCacheRunnerId = runnerId;
            _preSimVelocityCacheTick = currentTick;
        }

        public bool HasActiveRamDrive()
        {
            return GetRamDriveAssist01() > 0.01f;
        }

        public bool HasActiveVictimPush()
        {
            return GetVictimPushAssist01() > 0.01f;
        }

        public float GetRamDriveAssist01()
        {
            if (Runner == null)
            {
                return 0f;
            }

            if (IsLocalVictimPresentationActive())
            {
                return 0f;
            }

            Dictionary<long, SumoRamState> states = HasStateAuthority
                ? PairStates
                : PredictedPairStates;

            if (states.Count == 0)
            {
                return 0f;
            }

            int selfKey = GetControllerKey(this);
            if (selfKey == 0)
            {
                return 0f;
            }

            int currentTick = Runner.Tick.Raw;
            float assist = Mathf.Clamp01(NetworkRamDriveAssist01);

            foreach (KeyValuePair<long, SumoRamState> pair in states)
            {
                SumoRamState state = pair.Value;
                if (state.AttackerRef != selfKey)
                {
                    continue;
                }

                if (state.State != SumoPairState.InitialImpact && state.State != SumoPairState.Ramming)
                {
                    continue;
                }

                int contactBreakTicks = GetPairContactBreakGraceTicks(state.FirstController, state.SecondController);
                if (!IsContactActive(state, currentTick, contactBreakTicks))
                {
                    continue;
                }

                if (RequiresPhysicalContact(state.State)
                    && ComputeEdgeSeparation(state.FirstController, state.SecondController)
                        > GetActiveContactSeparationTolerance(state.FirstController, state.SecondController) + 0.03f)
                {
                    continue;
                }

                if (state.State == SumoPairState.InitialImpact)
                {
                    float duration = Mathf.Max(0.01f, state.InitialImpactDuration);
                    float elapsed01 = Mathf.Clamp01(state.InitialImpactElapsed / duration);
                    float impactAssist = Mathf.Lerp(1f, 0.55f, elapsed01);
                    assist = Mathf.Max(assist, impactAssist);
                    continue;
                }

                SumoCollisionController attacker = state.AttackerController;
                float stopThreshold = GetRamStopThreshold(attacker != null ? attacker : this);
                if (state.RamEnergy > stopThreshold)
                {
                    float normalizedEnergy = state.InitialRamEnergy > 0.0001f
                        ? Mathf.Clamp01(state.RamEnergy / state.InitialRamEnergy)
                        : 0f;
                    float contactAssist = Mathf.Clamp01(state.RamContactBlend);
                    float ramAssist = Mathf.Clamp01(Mathf.Max(normalizedEnergy, contactAssist));
                    assist = Mathf.Max(assist, ramAssist);
                }
            }

            return Mathf.Clamp01(assist);
        }

        public float GetVictimPushAssist01()
        {
            if (Runner == null)
            {
                return 0f;
            }

            if (IsLocalVictimPresentationActive())
            {
                return Mathf.Clamp01(NetworkVictimPushAssist01);
            }

            Dictionary<long, SumoRamState> states = HasStateAuthority
                ? PairStates
                : PredictedPairStates;

            if (states.Count == 0)
            {
                return 0f;
            }

            int selfKey = GetControllerKey(this);
            if (selfKey == 0)
            {
                return 0f;
            }

            int currentTick = Runner.Tick.Raw;
            float assist = Mathf.Clamp01(NetworkVictimPushAssist01);

            foreach (KeyValuePair<long, SumoRamState> pair in states)
            {
                SumoRamState state = pair.Value;
                if (state.VictimRef != selfKey)
                {
                    continue;
                }

                if (state.State != SumoPairState.InitialImpact && state.State != SumoPairState.Ramming)
                {
                    continue;
                }

                int contactBreakTicks = GetPairContactBreakGraceTicks(state.FirstController, state.SecondController);
                if (!IsContactActive(state, currentTick, contactBreakTicks))
                {
                    continue;
                }

                if (RequiresPhysicalContact(state.State)
                    && ComputeEdgeSeparation(state.FirstController, state.SecondController)
                        > GetActiveContactSeparationTolerance(state.FirstController, state.SecondController) + 0.03f)
                {
                    continue;
                }

                if (state.State == SumoPairState.InitialImpact)
                {
                    float duration = Mathf.Max(0.01f, state.InitialImpactDuration);
                    float elapsed01 = Mathf.Clamp01(state.InitialImpactElapsed / duration);
                    float impactAssist = Mathf.Lerp(1f, 0.72f, elapsed01);
                    assist = Mathf.Max(assist, impactAssist);
                    continue;
                }

                SumoCollisionController attacker = state.AttackerController;
                float stopThreshold = GetRamStopThreshold(attacker != null ? attacker : this);
                if (state.RamEnergy > stopThreshold)
                {
                    float normalizedEnergy = state.InitialRamEnergy > 0.0001f
                        ? Mathf.Clamp01(state.RamEnergy / state.InitialRamEnergy)
                        : 0f;
                    float contactAssist = Mathf.Clamp01(state.RamContactBlend);
                    float ramAssist = Mathf.Clamp01(Mathf.Max(normalizedEnergy, contactAssist));
                    assist = Mathf.Max(assist, Mathf.Lerp(0.35f, 1f, ramAssist));
                }
            }

            return Mathf.Clamp01(assist);
        }

        public float GetVictimPushAssistForPresentation01()
        {
            if (Runner == null)
            {
                return 0f;
            }

            float assist = Mathf.Clamp01(NetworkVictimPushAssist01);
            if (!IsLocalVictimCatchupEnvelopeActiveForPresentation())
            {
                return assist;
            }

            float localAssist = Mathf.Clamp01(_localVictimCatchupAssist01);
            if (_localVictimCatchupFromAnticipation && _hasLocalVictimCatchupEnvelope)
            {
                localAssist = Mathf.Max(localAssist, 0.90f);
            }
            else
            {
                localAssist = Mathf.Max(localAssist, 0.78f);
            }

            return Mathf.Clamp01(Mathf.Max(assist, localAssist));
        }

        public bool IsLocalVictimCatchupActiveForPresentation()
        {
            return IsLocalVictimCatchupEnvelopeActiveForPresentation();
        }

        public bool TryGetMovementBlockNormal(out Vector3 playerBlockNormal)
        {
            playerBlockNormal = Vector3.zero;

            if (IsLocalVictimPresentationActive())
            {
                return false;
            }

            int controllerKey = GetControllerKey(this);
            if (controllerKey == 0)
            {
                return false;
            }

            int currentTick = Runner != null ? Runner.Tick.Raw : Time.frameCount;
            Dictionary<int, BlockNormalSample> source = HasStateAuthority
                ? AuthorityBlockNormals
                : PredictedBlockNormals;

            if (!source.TryGetValue(controllerKey, out BlockNormalSample sample))
            {
                return false;
            }

            long tickDelta = (long)currentTick - sample.Tick;
            if (tickDelta < 0L || tickDelta > 1L)
            {
                return false;
            }

            if (sample.Sum.sqrMagnitude < 0.0001f)
            {
                return false;
            }

            playerBlockNormal = sample.Sum.normalized;
            return true;
        }

        private bool CanProcessAuthorityTick()
        {
            if (Runner == null || Object == null || !Object.IsInSimulation || !HasStateAuthority)
            {
                return false;
            }

            EnsurePairCacheContext();

            if (_rigidbody == null || _sphereCollider == null)
            {
                CacheComponents();
            }

            return _rigidbody != null && !_rigidbody.isKinematic;
        }

        private bool CanProcessPredictedTick()
        {
            if (!IsPredictedImpactRuntimeEnabled())
            {
                return false;
            }

            if (Runner == null
                || !Runner.IsClient
                || HasStateAuthority
                || !HasInputAuthority
                || Object == null
                || !Object.IsInSimulation)
            {
                return false;
            }

            EnsurePredictedContext();

            if (_rigidbody == null || _sphereCollider == null)
            {
                CacheComponents();
            }

            return _rigidbody != null && !_rigidbody.isKinematic;
        }

        private void CaptureAnalyticContactsForOwnedPairs(
            int currentTick,
            Dictionary<long, SumoRamState> states,
            SimulationMode mode)
        {
            if (_rigidbody == null || _sphereCollider == null)
            {
                CacheComponents();
            }

            int selfKey = GetControllerKey(this);
            if (selfKey == 0)
            {
                return;
            }

            foreach (KeyValuePair<int, SumoCollisionController> entry in ActiveControllers)
            {
                SumoCollisionController other = entry.Value;
                int otherKey = entry.Key;

                if (!IsValidOtherForPair(other, otherKey, mode))
                {
                    continue;
                }

                if (!ShouldProcessPairWith(selfKey, other, otherKey, mode))
                {
                    continue;
                }

                bool selfIsFirst = selfKey <= otherKey;
                SumoCollisionController first = selfIsFirst ? this : other;
                SumoCollisionController second = selfIsFirst ? other : this;
                int firstKey = selfIsFirst ? selfKey : otherKey;
                int secondKey = selfIsFirst ? otherKey : selfKey;

                if (first == null || second == null || first == second)
                {
                    continue;
                }

                int ownerKey = mode == SimulationMode.Authoritative
                    ? Mathf.Min(firstKey, secondKey)
                    : selfKey;

                long pairKey = BuildPairKey(firstKey, secondKey);
                bool hadState = states.TryGetValue(pairKey, out SumoRamState pairState);

                if (hadState && IsPairInvalidForContact(pairState, ownerKey, firstKey, secondKey, currentTick))
                {
                    hadState = false;
                }

                if (!hadState)
                {
                    pairState = CreateFreshState(pairKey, currentTick);
                }

                pairState.PairKey = pairKey;
                pairState.OwnerKey = ownerKey;
                pairState.FirstRef = firstKey;
                pairState.SecondRef = secondKey;
                pairState.FirstController = first;
                pairState.SecondController = second;

                EnsureNativePlayerCollisionSuppressed(first, second);

                if (!TryBuildAnalyticContact(first, second, pairState, currentTick, out AnalyticContact contact))
                {
                    states[pairKey] = pairState;
                    continue;
                }

                pairState.ContactPoint = contact.ContactPoint;
                if (contact.ContactDirection.sqrMagnitude > 0.0001f)
                {
                    pairState.ContactNormal = -contact.ContactDirection.normalized;
                }

                if (contact.IsContact)
                {
                    int previousContactTick = pairState.LastContactTick;
                    pairState.LastContactTick = currentTick;

                    int contactBreakTicks = GetPairContactBreakGraceTicks(first, second);
                    bool contactRestarted = previousContactTick <= 0
                        || currentTick - previousContactTick > contactBreakTicks;

                    bool canCaptureEnter = pairState.State != SumoPairState.InitialImpact
                        && pairState.State != SumoPairState.Ramming;

                    if ((contact.IsEnter || contactRestarted) && canCaptureEnter)
                    {
                        ContactSnapshot snapshot = BuildContactSnapshotForPair(second, firstKey, secondKey, contact);
                        CaptureEnterSnapshot(
                            ref pairState,
                            first,
                            second,
                            currentTick,
                            true,
                            snapshot);
                    }

                    ResolveAnalyticContactConstraint(first, second, contact, pairState, currentTick, mode);
                    AddBlockNormalForTick(mode, firstKey, -contact.ContactDirection, currentTick);
                    AddBlockNormalForTick(mode, secondKey, contact.ContactDirection, currentTick);

                    if (mode == SimulationMode.Predicted)
                    {
                        PredictedPairLastContactTick[pairKey] = currentTick;
                    }
                }

                states[pairKey] = pairState;
            }
        }

        private static ContactSnapshot BuildContactSnapshotForPair(
            SumoCollisionController second,
            int firstKey,
            int secondKey,
            in AnalyticContact contact)
        {
            return new ContactSnapshot
            {
                Other = second,
                SelfKey = firstKey,
                OtherKey = secondKey,
                ContactPoint = contact.ContactPoint,
                ContactNormal = -contact.ContactDirection,
                SelfVelocity = contact.FirstVelocity,
                OtherVelocity = contact.SecondVelocity,
                RelativeClosingSpeed = contact.RelativeClosingSpeed
            };
        }

        private bool TryBuildAnalyticContact(
            SumoCollisionController first,
            SumoCollisionController second,
            in SumoRamState pairState,
            int currentTick,
            out AnalyticContact contact)
        {
            contact = default;

            if (first == null || second == null || first == second)
            {
                return false;
            }

            if (first._rigidbody == null || first._sphereCollider == null)
            {
                first.CacheComponents();
            }

            if (second._rigidbody == null || second._sphereCollider == null)
            {
                second.CacheComponents();
            }

            if (first._rigidbody == null
                || second._rigidbody == null
                || first._sphereCollider == null
                || second._sphereCollider == null)
            {
                return false;
            }

            if (first.Object == null
                || second.Object == null
                || !first.Object.IsInSimulation
                || !second.Object.IsInSimulation)
            {
                return false;
            }

            int firstLayerMask = 1 << second.gameObject.layer;
            int secondLayerMask = 1 << first.gameObject.layer;
            bool layerAllowed = (first.playerMask.value & firstLayerMask) != 0
                || (second.playerMask.value & secondLayerMask) != 0;

            if (!layerAllowed)
            {
                return false;
            }

            Vector3 firstCenter = first._rigidbody.worldCenterOfMass;
            Vector3 secondCenter = second._rigidbody.worldCenterOfMass;
            Vector3 firstToSecond = ResolveDirection(firstCenter, secondCenter, pairState.ContactNormal);

            float firstRadius = GetScaledRadius(first);
            float secondRadius = GetScaledRadius(second);
            float combinedRadius = firstRadius + secondRadius;

            float centerDistance = Vector3.Distance(firstCenter, secondCenter);
            float edgeSeparation = centerDistance - combinedRadius;
            float penetration = Mathf.Max(0f, -edgeSeparation);
            float separation = Mathf.Max(0f, edgeSeparation);

            int contactBreakTicks = GetPairContactBreakGraceTicks(first, second);
            bool hadRecentContact = IsContactActive(pairState, currentTick, contactBreakTicks);

            float enterPadding = GetPairContactEnterPadding(first, second);
            float exitPadding = GetPairContactExitPadding(first, second);
            float activePadding = hadRecentContact ? exitPadding : enterPadding;
            bool isContact = centerDistance <= combinedRadius + activePadding;
            bool isEnter = isContact && !hadRecentContact;

            Vector3 firstVelocity = first.GetVelocitySampleForTick(currentTick);
            Vector3 secondVelocity = second.GetVelocitySampleForTick(currentTick);
            float relativeClosingSpeed = Mathf.Max(0f, Vector3.Dot(firstVelocity - secondVelocity, firstToSecond));

            float firstSurfaceDistance = Mathf.Min(firstRadius, centerDistance * 0.5f);
            Vector3 contactPoint = firstCenter + firstToSecond * firstSurfaceDistance;
            if (contactPoint.sqrMagnitude <= 0.0001f)
            {
                contactPoint = 0.5f * (firstCenter + secondCenter);
            }

            contact = new AnalyticContact
            {
                FirstKey = GetControllerKey(first),
                SecondKey = GetControllerKey(second),
                FirstVelocity = firstVelocity,
                SecondVelocity = secondVelocity,
                ContactDirection = firstToSecond,
                ContactPoint = contactPoint,
                Penetration = penetration,
                Separation = separation,
                RelativeClosingSpeed = relativeClosingSpeed,
                IsContact = isContact,
                IsEnter = isEnter
            };

            return contact.FirstKey != 0 && contact.SecondKey != 0;
        }

        private static void ResolveAnalyticContactConstraint(
            SumoCollisionController first,
            SumoCollisionController second,
            in AnalyticContact contact,
            in SumoRamState pairState,
            int currentTick,
            SimulationMode mode)
        {
            if (!contact.IsContact)
            {
                return;
            }

            if (first == null || second == null || first._rigidbody == null || second._rigidbody == null)
            {
                return;
            }

            Vector3 contactDirection = contact.ContactDirection;
            if (contactDirection.sqrMagnitude < 0.0001f)
            {
                return;
            }

            contactDirection.Normalize();
            bool isActiveCombatContact = pairState.State == SumoPairState.InitialImpact
                || pairState.State == SumoPairState.Ramming;
            bool useLocalOnlyPredictedResponse = ShouldUseLocalOnlyPredictedContactResponse(
                mode,
                isActiveCombatContact);

            float firstResponseShare = GetActiveCombatContactResponseShare(first, pairState, currentTick, mode, isActiveCombatContact);
            float secondResponseShare = GetActiveCombatContactResponseShare(second, pairState, currentTick, mode, isActiveCombatContact);

            float firstInvMass = GetContactInverseMass(first, mode, useLocalOnlyPredictedResponse) * firstResponseShare;
            float secondInvMass = GetContactInverseMass(second, mode, useLocalOnlyPredictedResponse) * secondResponseShare;

            float invMassSum = firstInvMass + secondInvMass;
            if (invMassSum <= 0.0001f)
            {
                return;
            }

            float velocityDamping = GetPairContactVelocityDamping(first, second);
            if (isActiveCombatContact)
            {
                velocityDamping *= ActiveContactVelocityDampingScale;
            }

            Vector3 firstVelocity = first._rigidbody.linearVelocity;
            Vector3 secondVelocity = second._rigidbody.linearVelocity;
            float closingSpeed = Vector3.Dot(firstVelocity - secondVelocity, contactDirection);
            float closingDeadZone = isActiveCombatContact ? ActiveContactClosingDeadZone : 0f;

            // In predicted local-vs-proxy pairs we only stabilize the local body and let
            // authority own the two-body response. This removes proxy feedback jitter on
            // both attacker and victim screens.
            if (!useLocalOnlyPredictedResponse && closingSpeed > closingDeadZone)
            {
                float impulseMagnitude = (closingSpeed - closingDeadZone)
                    * Mathf.Clamp(velocityDamping, 0f, 1.25f)
                    / invMassSum;
                Vector3 impulse = contactDirection * impulseMagnitude;
                ApplyVelocityDelta(first, -impulse * firstInvMass, mode);
                ApplyVelocityDelta(second, impulse * secondInvMass, mode);
            }

            float penetrationSlop = GetPairContactPenetrationSlop(first, second);
            if (isActiveCombatContact)
            {
                penetrationSlop += ActiveCombatPenetrationSlopBonus;
            }

            bool predictedActiveCombat = mode == SimulationMode.Predicted && isActiveCombatContact;
            float penetration = Mathf.Max(0f, contact.Penetration - penetrationSlop);
            if (predictedActiveCombat && useLocalOnlyPredictedResponse)
            {
                penetration = Mathf.Max(0f, penetration - PredictedActiveCombatPenetrationDeadZone);
            }

            if (penetration <= 0.0001f)
            {
                return;
            }

            float correctionScale = GetPairContactPositionCorrection(first, second);
            if (correctionScale <= 0f)
            {
                return;
            }

            bool useVelocityPenetrationResolve = mode == SimulationMode.Predicted || isActiveCombatContact;
            if (useVelocityPenetrationResolve)
            {
                float resolveRate;
                float resolveMaxSpeed;

                if (predictedActiveCombat)
                {
                    resolveRate = PredictedActiveCombatPenetrationResolveRate;
                    resolveMaxSpeed = PredictedActiveCombatPenetrationResolveMaxSpeed;
                }
                else
                {
                    resolveRate = isActiveCombatContact
                        ? ActiveContactPenetrationResolveRate
                        : PredictedPenetrationResolveRate;
                    resolveMaxSpeed = isActiveCombatContact
                        ? ActiveContactPenetrationResolveMaxSpeed
                        : PredictedPenetrationResolveMaxSpeed;
                }

                float resolveSpeed = Mathf.Min(
                    resolveMaxSpeed,
                    penetration * resolveRate * correctionScale);

                if (resolveSpeed <= 0.0001f)
                {
                    return;
                }

                Vector3 separationVelocity = contactDirection * (resolveSpeed / invMassSum);
                ApplyVelocityDelta(first, -separationVelocity * firstInvMass, mode);
                ApplyVelocityDelta(second, separationVelocity * secondInvMass, mode);
                return;
            }

            float correctionDistance = Mathf.Min(
                penetration * correctionScale,
                AuthorityMaxPositionCorrectionPerTick);
            if (correctionDistance <= 0.0001f)
            {
                return;
            }

            Vector3 correction = contactDirection * (correctionDistance / invMassSum);
            ApplyPositionDelta(first, -correction * firstInvMass, mode);
            ApplyPositionDelta(second, correction * secondInvMass, mode);
        }

        private static bool ShouldUseLocalOnlyPredictedContactResponse(
            SimulationMode mode,
            bool isActiveCombatContact)
        {
            if (mode != SimulationMode.Predicted)
            {
                return false;
            }

            return isActiveCombatContact;
        }

        private static float GetActiveCombatContactResponseShare(
            SumoCollisionController controller,
            in SumoRamState pairState,
            int currentTick,
            SimulationMode mode,
            bool isActiveCombatContact)
        {
            if (!isActiveCombatContact || controller == null || !pairState.HasAttacker)
            {
                return 1f;
            }

            int controllerKey = GetControllerKey(controller);
            if (controllerKey == 0 || controllerKey == pairState.AttackerRef)
            {
                return 1f;
            }

            if (controllerKey != pairState.VictimRef)
            {
                return 1f;
            }

            bool isImpactWindow = pairState.State == SumoPairState.InitialImpact
                && pairState.StartTick > 0
                && currentTick - pairState.StartTick < ActiveCombatVictimImpactResponseTicks;

            if (isImpactWindow)
            {
                return mode == SimulationMode.Authoritative
                    ? ActiveCombatVictimImpactAuthorityResponseShare
                    : ActiveCombatVictimImpactPredictedResponseShare;
            }

            return mode == SimulationMode.Authoritative
                ? ActiveCombatVictimRamAuthorityResponseShare
                : ActiveCombatVictimRamPredictedResponseShare;
        }

        private static void ApplyVelocityDelta(SumoCollisionController controller, Vector3 velocityDelta, SimulationMode mode)
        {
            if (!CanApplyForces(controller, mode))
            {
                return;
            }

            if (velocityDelta.sqrMagnitude <= 0.0000001f)
            {
                return;
            }

            controller._rigidbody.linearVelocity += velocityDelta;
        }

        private static void ApplyPositionDelta(SumoCollisionController controller, Vector3 positionDelta, SimulationMode mode)
        {
            if (!CanApplyForces(controller, mode))
            {
                return;
            }

            if (positionDelta.sqrMagnitude <= 0.0000001f)
            {
                return;
            }

            controller._rigidbody.position += positionDelta;
        }

        private static void AddBlockNormalForTick(SimulationMode mode, int controllerKey, Vector3 normal, int currentTick)
        {
            if (controllerKey == 0 || normal.sqrMagnitude < 0.0001f)
            {
                return;
            }

            Dictionary<int, BlockNormalSample> target = mode == SimulationMode.Authoritative
                ? AuthorityBlockNormals
                : PredictedBlockNormals;

            if (target.TryGetValue(controllerKey, out BlockNormalSample sample)
                && sample.Tick == currentTick)
            {
                sample.Sum += normal;
            }
            else
            {
                sample = new BlockNormalSample
                {
                    Tick = currentTick,
                    Sum = normal
                };
            }

            target[controllerKey] = sample;
        }

        private static float GetContactInverseMass(
            SumoCollisionController controller,
            SimulationMode mode,
            bool useLocalOnlyPredictedCombatResponse)
        {
            if (!CanApplyForces(controller, mode))
            {
                return 0f;
            }

            if (useLocalOnlyPredictedCombatResponse && !controller.HasInputAuthority)
            {
                return 0f;
            }

            return 1f / Mathf.Max(0.01f, controller._rigidbody.mass);
        }

        private static bool CanApplyForces(SumoCollisionController controller, SimulationMode mode)
        {
            if (controller == null
                || controller._rigidbody == null
                || controller._rigidbody.isKinematic
                || controller.Runner == null
                || controller.Object == null
                || !controller.Object.IsInSimulation)
            {
                return false;
            }

            if (mode == SimulationMode.Authoritative)
            {
                return controller.HasStateAuthority;
            }

            return controller.Runner.IsClient
                && !controller.HasStateAuthority
                && (controller.HasInputAuthority || controller.CanApplyPredictedProxyForces());
        }

        private static bool CanApplyGameplayForces(SumoCollisionController controller, SimulationMode mode)
        {
            if (controller == null
                || controller._rigidbody == null
                || controller._rigidbody.isKinematic
                || controller.Runner == null
                || controller.Object == null
                || !controller.Object.IsInSimulation)
            {
                return false;
            }

            if (mode == SimulationMode.Authoritative)
            {
                return controller.HasStateAuthority;
            }

            return controller.Runner.IsClient
                && !controller.HasStateAuthority
                && (controller.HasInputAuthority || controller.CanApplyPredictedProxyForces());
        }

        private bool CanApplyPredictedProxyForces()
        {
            if (ballController == null)
            {
                CacheComponents();
            }

            return applyPredictedForcesToRemoteProxies
                && ballController != null
                && ballController.IsRemoteProxyPredictionActive();
        }

        private static bool IsValidOtherForPair(SumoCollisionController other, int otherKey, SimulationMode mode)
        {
            if (other == null || otherKey == 0)
            {
                return false;
            }

            if (other._rigidbody == null || other._sphereCollider == null)
            {
                other.CacheComponents();
            }
            else if (other.ballController == null)
            {
                other.CacheComponents();
            }

            if (other._rigidbody == null || other._rigidbody.isKinematic)
            {
                return false;
            }

            if (other.Runner == null
                || other.Object == null
                || !other.Object.IsInSimulation)
            {
                return false;
            }

            if (mode == SimulationMode.Authoritative)
            {
                return other.HasStateAuthority;
            }

            // In predicted mode the local attacker still needs contact data against
            // remote proxies so it can stop cleanly at the victim surface, but we do
            // not require the proxy itself to be client-simulated. The predicted force
            // application path still only affects the local player, so victim visuals
            // remain smooth.
            return !other.HasStateAuthority
                && other.ballController != null;
        }

        private bool ShouldProcessPairWith(int selfKey, SumoCollisionController other, int otherKey, SimulationMode mode)
        {
            if (other == null || other == this || selfKey == 0 || otherKey == 0 || selfKey == otherKey)
            {
                return false;
            }

            if (mode == SimulationMode.Authoritative)
            {
                return selfKey < otherKey;
            }

            if (selfKey < otherKey)
            {
                return true;
            }

            return !other.HasInputAuthority;
        }

        private static void EnsureNativePlayerCollisionSuppressed(
            SumoCollisionController first,
            SumoCollisionController second)
        {
            if (first == null || second == null || first == second)
            {
                return;
            }

            if (!first.disableNativePlayerCollision && !second.disableNativePlayerCollision)
            {
                return;
            }

            if (first._sphereCollider == null || first._sphereCollider.isTrigger)
            {
                return;
            }

            if (second._sphereCollider == null || second._sphereCollider.isTrigger)
            {
                return;
            }

            long pairKey = BuildPairKey(GetControllerKey(first), GetControllerKey(second));
            if (pairKey == 0 || !IgnoredNativeCollisionPairs.Add(pairKey))
            {
                return;
            }

            Physics.IgnoreCollision(first._sphereCollider, second._sphereCollider, true);
        }

        private void ProcessOwnedPairStates(
            int currentTick,
            Dictionary<long, SumoRamState> states,
            SimulationMode mode)
        {
            int selfKey = GetControllerKey(this);
            if (selfKey == 0 || states.Count == 0)
            {
                return;
            }

            PairKeysBuffer.Clear();

            foreach (KeyValuePair<long, SumoRamState> pair in states)
            {
                if (pair.Value.OwnerKey == selfKey)
                {
                    PairKeysBuffer.Add(pair.Key);
                }
            }

            for (int i = 0; i < PairKeysBuffer.Count; i++)
            {
                long pairKey = PairKeysBuffer[i];
                if (!states.TryGetValue(pairKey, out SumoRamState pairState))
                {
                    continue;
                }

                if (!TryResolvePairControllers(ref pairState, out SumoCollisionController first, out SumoCollisionController second))
                {
                    states.Remove(pairKey);
                    continue;
                }

                int contactBreakTicks = GetPairContactBreakGraceTicks(first, second);
                bool hasContact = IsContactActive(pairState, currentTick, contactBreakTicks);
                if (hasContact && RequiresPhysicalContact(pairState.State))
                {
                    float separation = ComputeEdgeSeparation(first, second);
                    if (separation > GetActiveContactSeparationTolerance(first, second))
                    {
                        hasContact = false;
                    }
                }

                if (!hasContact)
                {
                    UpdateSeparationSinceBreak(ref pairState, first, second, currentTick);
                }

                switch (pairState.State)
                {
                    case SumoPairState.None:
                        ProcessNoneState(ref pairState, currentTick, hasContact, first, second, mode);
                        break;

                    case SumoPairState.InitialImpact:
                        ProcessInitialImpactState(ref pairState, currentTick, hasContact, mode);
                        break;

                    case SumoPairState.Ramming:
                        ProcessRammingState(ref pairState, currentTick, hasContact, mode);
                        break;

                    case SumoPairState.RamDepleted:
                        ProcessRamDepletedState(ref pairState, currentTick, hasContact, first, second);
                        break;

                    case SumoPairState.ReengageReady:
                        ProcessReengageReadyState(ref pairState, currentTick, hasContact, first, second, mode);
                        break;
                }

                if (ShouldRemoveState(pairState, currentTick, contactBreakTicks))
                {
                    states.Remove(pairKey);
                }
                else
                {
                    states[pairKey] = pairState;
                }
            }

            PairKeysBuffer.Clear();
        }

        private void ProcessNoneState(
            ref SumoRamState pairState,
            int currentTick,
            bool hasContact,
            SumoCollisionController first,
            SumoCollisionController second,
            SimulationMode mode)
        {
            if (!hasContact)
            {
                return;
            }

            pairState.BreakStartTick = 0;
            pairState.MaxSeparationSinceBreak = 0f;

            if (!HasFreshEnter(pairState))
            {
                return;
            }

            bool startedImpact = TryStartInitialImpact(ref pairState, currentTick, first, second, false, mode);
            ConsumePendingEnter(ref pairState);

            if (!startedImpact)
            {
                pairState.State = SumoPairState.None;
                ClearAttacker(ref pairState);
            }
        }

        private void ProcessInitialImpactState(
            ref SumoRamState pairState,
            int currentTick,
            bool hasContact,
            SimulationMode mode)
        {
            if (!TryGetLiveAttackerVictim(ref pairState, out SumoCollisionController attacker, out SumoCollisionController victim))
            {
                pairState.State = SumoPairState.None;
                ClearAttacker(ref pairState);
                return;
            }

            ApplyInitialImpactBurstStep(ref pairState, attacker, victim, hasContact, mode);

            float stopThreshold = GetRamStopThreshold(attacker);
            if (pairState.RamEnergy <= stopThreshold)
            {
                SetRamDepleted(ref pairState, hasContact, currentTick);
                return;
            }

            pairState.RamContactBlend = Mathf.Max(pairState.RamContactBlend, RamContactStartBlend);
            pairState.State = SumoPairState.Ramming;
        }

        private void ProcessRammingState(
            ref SumoRamState pairState,
            int currentTick,
            bool hasContact,
            SimulationMode mode)
        {
            if (!TryGetLiveAttackerVictim(ref pairState, out SumoCollisionController attacker, out SumoCollisionController victim))
            {
                pairState.State = SumoPairState.None;
                ClearAttacker(ref pairState);
                return;
            }

            if (mode == SimulationMode.Predicted && !IsPredictedRamRuntimeEnabled())
            {
                SetRamDepleted(ref pairState, hasContact, currentTick);
                return;
            }

            if (pairState.MaxRamDurationTicks > 0 && currentTick - pairState.StartTick >= pairState.MaxRamDurationTicks)
            {
                SetRamDepleted(ref pairState, hasContact, currentTick);
                return;
            }

            if (pairState.LastRamTick == currentTick)
            {
                return;
            }

            pairState.LastRamTick = currentTick;

            float deltaTime = Runner != null ? Runner.DeltaTime : Time.fixedDeltaTime;
            ApplyInitialImpactBurstStep(ref pairState, attacker, victim, hasContact, mode);

            if (!hasContact)
            {
                pairState.RamContactBlend = Mathf.Max(0f, pairState.RamContactBlend - deltaTime * RamContactBlendFallPerSecond);

                SumoRamTickResult noContactTick = SumoImpactResolver.ComputeRamTick(
                    attacker.physicsConfig,
                    deltaTime,
                    pairState.RamEnergy,
                    Mathf.Max(0.0001f, pairState.InitialRamEnergy),
                    0f,
                    0f,
                    false,
                    0f);

                pairState.RamEnergy = Mathf.Max(0f, pairState.RamEnergy - noContactTick.EnergyDecay);
                if (noContactTick.ShouldStop || pairState.RamEnergy <= GetRamStopThreshold(attacker))
                {
                    SetRamDepleted(ref pairState, false, currentTick);
                }

                return;
            }

            pairState.RamContactBlend = Mathf.Min(1f, pairState.RamContactBlend + deltaTime * RamContactBlendRisePerSecond);

            Vector3 liveDirection = ResolveDirection(
                attacker._rigidbody.worldCenterOfMass,
                victim._rigidbody.worldCenterOfMass,
                pairState.ContactNormal);

            if (pairState.ContactDirection.sqrMagnitude > 0.0001f)
            {
                liveDirection = Vector3.Slerp(pairState.ContactDirection, liveDirection, PairDirectionBlend);
                if (liveDirection.sqrMagnitude < 0.0001f)
                {
                    liveDirection = pairState.ContactDirection;
                }

                liveDirection.Normalize();
            }

            pairState.ContactDirection = liveDirection;

            Vector3 attackerVelocity = attacker._rigidbody.linearVelocity;
            float attackerForwardSpeed = Mathf.Max(0f, Vector3.Dot(attackerVelocity, liveDirection));
            float directionDot = ComputeDirectionDot(attacker, attackerVelocity, liveDirection);
            pairState.DirectionDot = directionDot;

            float minPressureSpeed = GetPairMinRamPressureSpeed(attacker, victim);
            float minDirectionDot = GetPairRamMinDirectionDot(attacker, victim);
            bool isPressing = attackerForwardSpeed >= minPressureSpeed && directionDot >= minDirectionDot;

            SumoRamTickResult ramTick = SumoImpactResolver.ComputeRamTick(
                attacker.physicsConfig,
                deltaTime,
                pairState.RamEnergy,
                Mathf.Max(0.0001f, pairState.InitialRamEnergy),
                attackerForwardSpeed,
                directionDot,
                isPressing,
                pairState.RamContactBlend);

            if (ramTick.HasForce)
            {
                if (ShouldApplyPredictedVictimPush(attacker, victim, mode))
                {
                    ApplyAcceleration(victim, liveDirection * ramTick.VictimAcceleration, mode);
                }

                if (ShouldApplyPredictedAttackerRecoil(attacker, mode))
                {
                    ApplyAcceleration(attacker, -liveDirection * ramTick.AttackerAcceleration, mode);
                }
            }

            pairState.RamEnergy = Mathf.Max(0f, pairState.RamEnergy - ramTick.EnergyDecay);

            if (mode == SimulationMode.Authoritative && hasContact)
            {
                float presentationStrength = ComputeVictimPresentationRamStrength(attacker, pairState);
                if (presentationStrength > 0.0001f)
                {
                    victim.PublishVictimPresentationImpact(presentationStrength);
                }

                float energy01 = pairState.InitialRamEnergy > 0.0001f
                    ? Mathf.Clamp01(pairState.RamEnergy / pairState.InitialRamEnergy)
                    : 0f;

                PublishRamDrive(attacker, Mathf.Max(energy01, 0.15f));

                float desiredVictimSpeed = Mathf.Max(
                    Vector3.Dot(victim._rigidbody.linearVelocity, liveDirection),
                    Mathf.Max(0f, Vector3.Dot(attackerVelocity, liveDirection))
                        * Mathf.Lerp(0.82f, 0.96f, Mathf.Clamp01(pairState.RamContactBlend)));

                PublishVictimPush(
                    victim,
                    PushPhaseRam,
                    liveDirection,
                    desiredVictimSpeed,
                    ramTick.VictimAcceleration,
                    energy01,
                    pairState.LastImpactTick);
            }

            bool outOfEnergy = pairState.RamEnergy <= GetRamStopThreshold(attacker);
            if (ramTick.ShouldStop || outOfEnergy)
            {
                SetRamDepleted(ref pairState, true, currentTick);

                if (logStateMachine || attacker.logStateMachine || victim.logStateMachine)
                {
                    Debug.Log(
                        $"SumoCollisionController: ram stop {attacker.name} -> {victim.name}; forceScale={ramTick.RamForceScale:0.00}; pressure={attackerForwardSpeed:0.00}; dot={directionDot:0.00}; tick={currentTick}; mode={mode}");
                }
            }
        }

        private void ProcessRamDepletedState(
            ref SumoRamState pairState,
            int currentTick,
            bool hasContact,
            SumoCollisionController first,
            SumoCollisionController second)
        {
            pairState.RamEnergy = 0f;
            ClearAttacker(ref pairState);

            if (hasContact)
            {
                pairState.BreakStartTick = 0;
                pairState.MaxSeparationSinceBreak = 0f;
                pairState.ReengageReadyTick = 0;
                ConsumePendingEnter(ref pairState);

                return;
            }

            if (IsReengageSatisfied(pairState, currentTick, first, second))
            {
                pairState.State = SumoPairState.ReengageReady;
                pairState.ReengageReadyTick = currentTick;
                ConsumePendingEnter(ref pairState);

                if (logStateMachine || first.logStateMachine || second.logStateMachine)
                {
                    Debug.Log($"SumoCollisionController: reengage ready {first.name} <-> {second.name}; tick={currentTick}");
                }
            }
        }

        private void ProcessReengageReadyState(
            ref SumoRamState pairState,
            int currentTick,
            bool hasContact,
            SumoCollisionController first,
            SumoCollisionController second,
            SimulationMode mode)
        {
            if (!hasContact)
            {
                return;
            }

            pairState.BreakStartTick = 0;
            pairState.MaxSeparationSinceBreak = 0f;

            if (!HasFreshEnter(pairState))
            {
                return;
            }

            int pendingEnterTick = pairState.PendingEnterTick;
            bool hasValidReengageEnter = pairState.ReengageReadyTick > 0
                && pendingEnterTick >= pairState.ReengageReadyTick;

            ConsumePendingEnter(ref pairState);

            if (!hasValidReengageEnter)
            {
                return;
            }

            bool startedImpact = TryStartInitialImpact(ref pairState, currentTick, first, second, true, mode);

            if (!startedImpact)
            {
                pairState.State = SumoPairState.RamDepleted;
                pairState.ReengageReadyTick = 0;
                ClearAttacker(ref pairState);
            }
        }

        private bool TryStartInitialImpact(
            ref SumoRamState pairState,
            int currentTick,
            SumoCollisionController first,
            SumoCollisionController second,
            bool requireReengageSpeed,
            SimulationMode mode)
        {
            if (currentTick == pairState.LastImpactTick)
            {
                return false;
            }

            if (mode == SimulationMode.Predicted
                && PredictedPairLastImpactTick.TryGetValue(pairState.PairKey, out int lastPredictedTick)
                && currentTick == lastPredictedTick)
            {
                return false;
            }

            if (pairState.LastImpactTick > int.MinValue)
            {
                int minReimpactTicks = GetPairMinReimpactTicks(first, second);
                long impactDelta = (long)currentTick - pairState.LastImpactTick;
                if (impactDelta >= 0L && impactDelta < minReimpactTicks)
                {
                    return false;
                }
            }

            if (first == null
                || second == null
                || first._rigidbody == null
                || second._rigidbody == null
                || first._rigidbody.isKinematic
                || second._rigidbody.isKinematic)
            {
                return false;
            }

            Vector3 firstToSecond = ResolveDirection(
                first._rigidbody.worldCenterOfMass,
                second._rigidbody.worldCenterOfMass,
                pairState.ContactNormal);

            float firstApproachSpeed = pairState.EnterFirstApproachSpeed;
            float secondApproachSpeed = pairState.EnterSecondApproachSpeed;

            if (firstApproachSpeed <= 0.0001f)
            {
                firstApproachSpeed = GetApproachSpeed(first, first._rigidbody.linearVelocity, firstToSecond);
            }

            if (secondApproachSpeed <= 0.0001f)
            {
                secondApproachSpeed = GetApproachSpeed(second, second._rigidbody.linearVelocity, -firstToSecond);
            }

            int firstKey = pairState.FirstRef != 0 ? pairState.FirstRef : GetControllerKey(first);
            int secondKey = pairState.SecondRef != 0 ? pairState.SecondRef : GetControllerKey(second);

            bool hasExistingOwner = pairState.HasAttacker
                && (pairState.AttackerRef == firstKey || pairState.AttackerRef == secondKey);

            bool existingOwnerIsFirst = pairState.AttackerRef == firstKey;
            float tieSpeedEpsilon = GetPairTieSpeedEpsilon(first, second);
            bool resolveTieByLowerKey = mode == SimulationMode.Authoritative
                && GetPairResolveTieByLowerKey(first, second);

            SumoAttackerDecision attackerDecision = SumoImpactResolver.ResolveAttacker(
                firstApproachSpeed,
                secondApproachSpeed,
                tieSpeedEpsilon,
                hasExistingOwner,
                existingOwnerIsFirst,
                resolveTieByLowerKey,
                firstKey,
                secondKey);

            if (mode == SimulationMode.Predicted)
            {
                bool firstInput = first.HasInputAuthority;
                bool secondInput = second.HasInputAuthority;
                if (firstInput != secondInput)
                {
                    SumoAttackerRole localRole = firstInput ? SumoAttackerRole.First : SumoAttackerRole.Second;

                    if (!attackerDecision.HasAttacker)
                    {
                        attackerDecision = new SumoAttackerDecision(localRole, SumoTieResolvedBy.NeutralWithinEpsilon);
                    }
                    else if (attackerDecision.Role != localRole)
                    {
                        float speedDeltaAbs = Mathf.Abs(firstApproachSpeed - secondApproachSpeed);
                        if (speedDeltaAbs <= tieSpeedEpsilon + 0.0001f)
                        {
                            attackerDecision = new SumoAttackerDecision(localRole, attackerDecision.TieResolvedBy);
                        }
                    }
                }
            }

            pairState.TieResolvedBy = attackerDecision.TieResolvedBy;

            if (!attackerDecision.HasAttacker)
            {
                return false;
            }

            SumoCollisionController attacker = attackerDecision.Role == SumoAttackerRole.First ? first : second;
            SumoCollisionController victim = attacker == first ? second : first;

            if (attacker == null
                || victim == null
                || attacker._rigidbody == null
                || victim._rigidbody == null
                || attacker._rigidbody.isKinematic
                || victim._rigidbody.isKinematic)
            {
                return false;
            }

            if (!ShouldRunPredictedCombatForAttacker(attacker, victim, mode))
            {
                return false;
            }

            Vector3 attackerVelocity = attacker._rigidbody.linearVelocity;
            Vector3 victimVelocity = victim._rigidbody.linearVelocity;

            Vector3 attackerToVictim = ResolveDirection(
                attacker._rigidbody.worldCenterOfMass,
                victim._rigidbody.worldCenterOfMass,
                pairState.ContactNormal);

            Vector3 attackDirection = ResolveAttackDirection(attacker, attackerVelocity, attackerToVictim);
            float directionDot = Mathf.Clamp01(Vector3.Dot(attackDirection, attackerToVictim));

            float entryForwardSpeed = attacker == first ? firstApproachSpeed : secondApproachSpeed;
            float currentForwardSpeed = Mathf.Max(0f, Vector3.Dot(attackerVelocity, attackerToVictim));
            float intentForwardSpeed = GetIntentApproachSpeed(attacker, attackerToVictim, attackerVelocity);

            float relativeClosingSpeed = pairState.EnterRelativeClosingSpeed;
            float currentRelativeClosingSpeed = Mathf.Max(0f, Vector3.Dot(attackerVelocity - victimVelocity, attackerToVictim));
            relativeClosingSpeed = Mathf.Max(relativeClosingSpeed, currentRelativeClosingSpeed);

            float attackerForwardSpeed = Mathf.Max(entryForwardSpeed, Mathf.Max(currentForwardSpeed, intentForwardSpeed));
            relativeClosingSpeed = Mathf.Max(relativeClosingSpeed, attackerForwardSpeed);

            float minPushSpeed = attacker.physicsConfig != null
                ? attacker.physicsConfig.MinImpactSpeed
                : 0f;

            float reengageThreshold = GetPairReengageSpeedThreshold(attacker, victim);

            float requiredSpeed = requireReengageSpeed
                ? Mathf.Max(minPushSpeed, reengageThreshold)
                : minPushSpeed;

            if (requireReengageSpeed && entryForwardSpeed < reengageThreshold)
            {
                return false;
            }

            if (attackerForwardSpeed < requiredSpeed)
            {
                return false;
            }

            if (pairState.ReimpactSuppressedUntilHardBreak)
            {
                int ticksSinceContact = pairState.LastContactTick > 0
                    ? currentTick - pairState.LastContactTick
                    : int.MaxValue;
                if (ticksSinceContact >= ReimpactSuppressionContactResetTicks)
                {
                    pairState.ReimpactSuppressedUntilHardBreak = false;
                }
            }

            bool reimpactSuppressed = pairState.ReimpactSuppressedUntilHardBreak
                && pairState.LastImpactTick > int.MinValue;
            if (reimpactSuppressed)
            {
                if (!IsHardBreakForReimpactSatisfied(pairState, currentTick, first, second))
                {
                    return TryStartRamWithoutImpact(
                        ref pairState,
                        currentTick,
                        attacker,
                        victim,
                        attackerToVictim,
                        attackerForwardSpeed,
                        directionDot,
                        mode);
                }

                pairState.ReimpactSuppressedUntilHardBreak = false;
            }

            float configuredDashMultiplier = attacker.physicsConfig != null
                ? attacker.physicsConfig.DashImpactMultiplier
                : 1f;

            float dashMultiplier = attacker.ballController != null
                ? attacker.ballController.GetDashImpactMultiplier(configuredDashMultiplier)
                : Mathf.Max(1f, configuredDashMultiplier);

            float attackerReferenceTopSpeed = GetAttackerReferenceTopSpeed(attacker);

            SumoInitialImpactResult impactResult = SumoImpactResolver.ComputeInitialImpact(
                attacker.physicsConfig,
                attackerForwardSpeed,
                attackerReferenceTopSpeed,
                relativeClosingSpeed,
                directionDot,
                dashMultiplier);

            if (!impactResult.HasImpact)
            {
                return false;
            }

            Vector3 impulseDirection = BuildImpulseDirection(
                attackerToVictim,
                GetImpactVerticalLift(attacker));

            pairState.State = SumoPairState.InitialImpact;
            pairState.AttackerController = attacker;
            pairState.VictimController = victim;
            pairState.AttackerRef = GetControllerKey(attacker);
            pairState.VictimRef = GetControllerKey(victim);
            pairState.StartTick = currentTick;
            pairState.LastImpactTick = currentTick;
            pairState.LastRamTick = currentTick;
            pairState.MaxRamDurationTicks = GetPairMaxRamDurationTicks(attacker, victim);
            pairState.InitialImpactSpeed = attackerForwardSpeed;
            pairState.InitialImpulse = impactResult.VictimImpulse;
            pairState.InitialImpactDuration = GetPairImpactBurstDuration(attacker, victim);
            pairState.InitialImpactElapsed = 0f;
            pairState.InitialVictimDeltaV = impactResult.VictimImpulse / Mathf.Max(0.01f, victim._rigidbody.mass);
            pairState.InitialAttackerDeltaV = impactResult.AttackerRecoilImpulse / Mathf.Max(0.01f, attacker._rigidbody.mass);
            pairState.RamContactBlend = 0f;
            pairState.InitialRamEnergy = impactResult.OpensRamState ? impactResult.InitialRamEnergy : 0f;
            pairState.RamEnergy = pairState.InitialRamEnergy;
            pairState.ContactDirection = impulseDirection;
            pairState.DirectionDot = directionDot;
            pairState.BreakStartTick = 0;
            pairState.MaxSeparationSinceBreak = 0f;
            pairState.ReengageReadyTick = 0;
            pairState.ImpactLatchTick = currentTick;
            pairState.ReimpactSuppressedUntilHardBreak = true;
            ApplyInitialImpactKickoff(ref pairState, attacker, victim, mode);
            ApplyInitialImpactBurstStep(ref pairState, attacker, victim, true, mode);

            if (mode == SimulationMode.Authoritative)
            {
                SumoImpactData impactData = new SumoImpactData(
                    attacker.Object,
                    victim.Object,
                    impulseDirection,
                    attackerVelocity - victimVelocity,
                    attackerForwardSpeed,
                    relativeClosingSpeed,
                    impactResult.SpeedCurve,
                    impactResult.AngleScale,
                    impactResult.DashMultiplier,
                    impactResult.VictimImpulse,
                    currentTick,
                    pairState.ContactPoint);

                float presentationStrength = ComputeVictimPresentationStrength(attacker, impactResult);
                victim.PublishVictimPresentationImpact(presentationStrength);

                PublishRamDrive(attacker, 1f);

                Vector3 catchupDirection = GetHorizontalDirection(impulseDirection);
                if (catchupDirection.sqrMagnitude < 0.0001f)
                {
                    catchupDirection = GetHorizontalDirection(attackerToVictim);
                }

                if (catchupDirection.sqrMagnitude < 0.0001f)
                {
                    catchupDirection = impulseDirection.normalized;
                }

                float impactTargetSpeed = Mathf.Max(
                    Vector3.Dot(victim._rigidbody.linearVelocity, catchupDirection),
                    attackerForwardSpeed * Mathf.Lerp(0.70f, 0.88f, Mathf.Clamp01(impactResult.SpeedCurve)));

                PublishVictimPush(
                    victim,
                    PushPhaseImpact,
                    catchupDirection,
                    impactTargetSpeed,
                    victim.physicsConfig != null ? victim.physicsConfig.VictimLocalImpactCatchupAcceleration : 24f,
                    1f,
                    currentTick);

                attacker.ImpactApplied?.Invoke(impactData);
                victim.ImpactApplied?.Invoke(impactData);
            }
            else
            {
                PredictedPairLastImpactTick[pairState.PairKey] = currentTick;
            }

            if (logImpacts || attacker.logImpacts || victim.logImpacts)
            {
                Debug.Log(
                    $"SumoCollisionController: initial-impact {attacker.name} -> {victim.name}; impulse={impactResult.VictimImpulse:0.00}; speed={attackerForwardSpeed:0.00}; closing={relativeClosingSpeed:0.00}; dot={directionDot:0.00}; ramEnergy={pairState.RamEnergy:0.00}; tie={attackerDecision.TieResolvedBy}; tick={currentTick}; mode={mode}");
            }

            return true;
        }

        private bool TryStartRamWithoutImpact(
            ref SumoRamState pairState,
            int currentTick,
            SumoCollisionController attacker,
            SumoCollisionController victim,
            Vector3 attackerToVictim,
            float attackerForwardSpeed,
            float directionDot,
            SimulationMode mode)
        {
            if (attacker == null
                || victim == null
                || attacker._rigidbody == null
                || victim._rigidbody == null
                || attacker._rigidbody.isKinematic
                || victim._rigidbody.isKinematic)
            {
                return false;
            }

            float minPressureSpeed = GetPairMinRamPressureSpeed(attacker, victim);
            float minDirectionDot = GetPairRamMinDirectionDot(attacker, victim);
            if (attackerForwardSpeed < minPressureSpeed || directionDot < minDirectionDot)
            {
                return false;
            }

            Vector3 ramDirection = attackerToVictim;
            if (ramDirection.sqrMagnitude < 0.0001f)
            {
                ramDirection = ResolveDirection(
                    attacker._rigidbody.worldCenterOfMass,
                    victim._rigidbody.worldCenterOfMass,
                    pairState.ContactNormal);
            }

            if (ramDirection.sqrMagnitude < 0.0001f)
            {
                return false;
            }

            ramDirection.Normalize();

            float stopThreshold = GetRamStopThreshold(attacker);
            float seededRamEnergy = pairState.RamEnergy;
            if (seededRamEnergy <= stopThreshold)
            {
                float speed01 = attacker.physicsConfig != null
                    ? attacker.physicsConfig.EvaluateImpactSpeed01(attackerForwardSpeed)
                    : Mathf.Clamp01(attackerForwardSpeed / Mathf.Max(0.01f, FallbackAttackerReferenceTopSpeed));
                float minRamEnergy = attacker.physicsConfig != null
                    ? attacker.physicsConfig.RamMinEnergy
                    : 0.5f;
                seededRamEnergy = Mathf.Max(
                    stopThreshold + 0.05f,
                    minRamEnergy * Mathf.Lerp(0.45f, 0.9f, speed01));
            }

            float maxRamEnergy = attacker.physicsConfig != null
                ? Mathf.Max(attacker.physicsConfig.RamMinEnergy, attacker.physicsConfig.RamMaxEnergy)
                : 7.5f;
            seededRamEnergy = Mathf.Clamp(
                seededRamEnergy,
                stopThreshold + 0.01f,
                Mathf.Max(stopThreshold + 0.01f, maxRamEnergy));

            pairState.State = SumoPairState.Ramming;
            pairState.AttackerController = attacker;
            pairState.VictimController = victim;
            pairState.AttackerRef = GetControllerKey(attacker);
            pairState.VictimRef = GetControllerKey(victim);
            pairState.StartTick = currentTick;
            pairState.LastRamTick = int.MinValue;
            pairState.MaxRamDurationTicks = GetPairMaxRamDurationTicks(attacker, victim);
            pairState.InitialImpactDuration = 0f;
            pairState.InitialImpactElapsed = 0f;
            pairState.InitialVictimDeltaV = 0f;
            pairState.InitialAttackerDeltaV = 0f;
            pairState.InitialImpulse = 0f;
            pairState.InitialImpactSpeed = Mathf.Max(pairState.InitialImpactSpeed, attackerForwardSpeed);
            pairState.ContactDirection = ramDirection;
            pairState.DirectionDot = directionDot;
            pairState.BreakStartTick = 0;
            pairState.MaxSeparationSinceBreak = 0f;
            pairState.ReengageReadyTick = 0;
            pairState.RamContactBlend = Mathf.Max(pairState.RamContactBlend, RamContactStartBlend);
            pairState.InitialRamEnergy = Mathf.Max(pairState.InitialRamEnergy, seededRamEnergy);
            pairState.RamEnergy = seededRamEnergy;

            if (mode == SimulationMode.Authoritative)
            {
                PublishRamDrive(attacker, 1f);
                float desiredVictimSpeed = Mathf.Max(
                    Vector3.Dot(victim._rigidbody.linearVelocity, ramDirection),
                    attackerForwardSpeed * 0.9f);
                float energy01 = pairState.InitialRamEnergy > 0.0001f
                    ? Mathf.Clamp01(pairState.RamEnergy / pairState.InitialRamEnergy)
                    : 0f;
                float ramAcceleration = attacker.physicsConfig != null
                    ? attacker.physicsConfig.RamBaseAcceleration
                    : 14f;
                PublishVictimPush(
                    victim,
                    PushPhaseRam,
                    ramDirection,
                    desiredVictimSpeed,
                    ramAcceleration,
                    energy01,
                    pairState.LastImpactTick);
            }

            return true;
        }

        private static float ComputeVictimPresentationStrength(
            SumoCollisionController attacker,
            SumoInitialImpactResult impactResult)
        {
            float speedStrength = Mathf.Clamp01(impactResult.SpeedCurve);
            float impulseCap = attacker != null && attacker.physicsConfig != null
                ? Mathf.Max(1f, attacker.physicsConfig.MaxImpactImpulse)
                : 24f;
            float impulseStrength = Mathf.Clamp01(impactResult.VictimImpulse / impulseCap);
            return Mathf.Clamp01(Mathf.Max(speedStrength, impulseStrength));
        }

        private static float ComputeVictimPresentationRamStrength(
            SumoCollisionController attacker,
            in SumoRamState pairState)
        {
            float stopThreshold = GetRamStopThreshold(attacker);
            if (pairState.RamEnergy <= stopThreshold)
            {
                return 0f;
            }

            float normalizedEnergy = pairState.InitialRamEnergy > 0.0001f
                ? Mathf.Clamp01(pairState.RamEnergy / pairState.InitialRamEnergy)
                : 0f;
            float contactAssist = Mathf.Clamp01(pairState.RamContactBlend);
            float ramAssist = Mathf.Clamp01(Mathf.Max(normalizedEnergy, contactAssist));
            return Mathf.Clamp01(Mathf.Lerp(0.35f, 1f, ramAssist));
        }

        private void PublishVictimPresentationImpact(float normalizedStrength)
        {
            if (!HasStateAuthority)
            {
                return;
            }

            VictimPresentationStrength = Mathf.Clamp01(normalizedStrength);
            VictimPresentationSequence++;
            _victimPresentationStrengthFallback = VictimPresentationStrength;
            _victimPresentationSequenceFallback = VictimPresentationSequence;
        }

        private bool TryReadVictimPresentationSignal(out int sequence, out float strength)
        {
            sequence = _victimPresentationSequenceFallback;
            strength = _victimPresentationStrengthFallback;

            try
            {
                sequence = VictimPresentationSequence;
                strength = VictimPresentationStrength;
                _victimPresentationSequenceFallback = sequence;
                _victimPresentationStrengthFallback = strength;
                return true;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        private static bool IsReengageSatisfied(
            in SumoRamState pairState,
            int currentTick,
            SumoCollisionController first,
            SumoCollisionController second)
        {
            int breakTicks = GetPairReengageBreakTicks(first, second);
            bool breakSatisfied = pairState.BreakStartTick > 0 && currentTick - pairState.BreakStartTick >= breakTicks;

            float requiredDistance = GetPairReengageDistance(first, second);
            bool distanceSatisfied = pairState.MaxSeparationSinceBreak >= requiredDistance;
            return breakSatisfied && distanceSatisfied;
        }

        private static bool IsHardBreakForReimpactSatisfied(
            in SumoRamState pairState,
            int currentTick,
            SumoCollisionController first,
            SumoCollisionController second)
        {
            int breakTicks = GetPairReengageBreakTicks(first, second);
            bool breakSatisfied = pairState.BreakStartTick > 0
                && currentTick - pairState.BreakStartTick >= breakTicks;

            float requiredDistance = GetPairReengageDistance(first, second) * ReimpactHardBreakDistanceMultiplier;
            bool distanceSatisfied = pairState.MaxSeparationSinceBreak >= requiredDistance;
            return breakSatisfied && distanceSatisfied;
        }

        private static bool ShouldRemoveState(in SumoRamState pairState, int currentTick, int contactBreakTicks)
        {
            if (pairState.OwnerKey == 0 || !pairState.HasPairControllers)
            {
                return true;
            }

            int contactDelta = pairState.LastContactTick > 0
                ? currentTick - pairState.LastContactTick
                : int.MaxValue;

            if (pairState.LastContactTick > 0 && contactDelta < -4)
            {
                return true;
            }

            bool hasContact = pairState.LastContactTick > 0 && contactDelta <= contactBreakTicks;
            if (hasContact)
            {
                return false;
            }

            if (contactDelta > HardStalePairTicks)
            {
                return true;
            }

            return pairState.State == SumoPairState.None
                && !pairState.HasPendingEnter
                && contactDelta > SoftStalePairTicks;
        }

        private static bool HasFreshEnter(in SumoRamState pairState)
        {
            return pairState.HasPendingEnter && pairState.PendingEnterTick > pairState.LastProcessedEnterTick;
        }

        private static void ConsumePendingEnter(ref SumoRamState pairState)
        {
            if (pairState.HasPendingEnter)
            {
                pairState.LastProcessedEnterTick = Mathf.Max(pairState.LastProcessedEnterTick, pairState.PendingEnterTick);
            }

            pairState.HasPendingEnter = false;
        }

        private static void ApplyInitialImpactKickoff(
            ref SumoRamState pairState,
            SumoCollisionController attacker,
            SumoCollisionController victim,
            SimulationMode mode)
        {
            if (attacker == null
                || victim == null
                || attacker._rigidbody == null
                || victim._rigidbody == null
                || pairState.InitialVictimDeltaV <= 0.0001f)
            {
                return;
            }

            float configuredShare = attacker.physicsConfig != null
                ? attacker.physicsConfig.FirstImpactKickImpulseShare
                : FallbackFirstImpactKickShare;

            if (configuredShare <= 0.0001f)
            {
                return;
            }

            float speed01 = attacker.physicsConfig != null
                ? attacker.physicsConfig.EvaluateImpactSpeed01(pairState.InitialImpactSpeed)
                : Mathf.Clamp01(pairState.InitialImpactSpeed / Mathf.Max(0.01f, FallbackAttackerReferenceTopSpeed));
            float speedSmooth = speed01 * speed01 * (3f - 2f * speed01);

            float speedScaledShare = Mathf.Clamp01(configuredShare * Mathf.Lerp(0.44f, 1f, speedSmooth));
            if (speedScaledShare <= 0.0001f)
            {
                return;
            }

            Vector3 direction = pairState.ContactDirection;
            if (direction.sqrMagnitude < 0.0001f)
            {
                direction = ResolveDirection(
                    attacker._rigidbody.worldCenterOfMass,
                    victim._rigidbody.worldCenterOfMass,
                    pairState.ContactNormal);
            }
            else
            {
                direction.Normalize();
            }

            pairState.ContactDirection = direction;

            float victimKickDeltaV = pairState.InitialVictimDeltaV * speedScaledShare;
            float attackerKickDeltaV = pairState.InitialAttackerDeltaV * speedScaledShare;

            float victimKickImpulse = victimKickDeltaV * Mathf.Max(0.01f, victim._rigidbody.mass);
            float attackerKickImpulse = attackerKickDeltaV * Mathf.Max(0.01f, attacker._rigidbody.mass);

            if (ShouldApplyPredictedVictimPush(attacker, victim, mode))
            {
                ApplyImpulse(victim, direction * victimKickImpulse, mode);
            }

            if (ShouldApplyPredictedAttackerRecoil(attacker, mode))
            {
                ApplyImpulse(attacker, -direction * attackerKickImpulse, mode);
            }

            float remainingShare = Mathf.Clamp01(1f - speedScaledShare);
            pairState.InitialVictimDeltaV *= remainingShare;
            pairState.InitialAttackerDeltaV *= remainingShare;
        }

        private bool ApplyInitialImpactBurstStep(
            ref SumoRamState pairState,
            SumoCollisionController attacker,
            SumoCollisionController victim,
            bool hasContact,
            SimulationMode mode)
        {
            if (pairState.InitialVictimDeltaV <= 0.0001f && pairState.InitialAttackerDeltaV <= 0.0001f)
            {
                pairState.InitialImpactElapsed = pairState.InitialImpactDuration;
                return true;
            }

            float duration = Mathf.Max(0.01f, pairState.InitialImpactDuration);
            if (pairState.InitialImpactElapsed >= duration)
            {
                return true;
            }

            if (!hasContact)
            {
                pairState.InitialImpactElapsed = duration;
                return true;
            }

            float dt = Runner != null ? Runner.DeltaTime : Time.fixedDeltaTime;
            if (dt <= 0f)
            {
                return false;
            }

            float prev01 = Mathf.Clamp01(pairState.InitialImpactElapsed / duration);
            float nextElapsed = Mathf.Min(duration, pairState.InitialImpactElapsed + dt);
            float next01 = Mathf.Clamp01(nextElapsed / duration);

            float burstFrontload = attacker != null && attacker.physicsConfig != null
                ? attacker.physicsConfig.FirstImpactBurstFrontload
                : FallbackFirstImpactBurstFrontload;
            float prevIntegral = EvaluateImpactBurstIntegral(prev01, burstFrontload);
            float nextIntegral = EvaluateImpactBurstIntegral(next01, burstFrontload);
            float segmentWeight = Mathf.Max(0f, nextIntegral - prevIntegral);

            if (segmentWeight <= 0.0000001f)
            {
                pairState.InitialImpactElapsed = nextElapsed;
                return nextElapsed >= duration;
            }

            Vector3 direction = pairState.ContactDirection;
            if (direction.sqrMagnitude < 0.0001f)
            {
                direction = ResolveDirection(
                    attacker._rigidbody.worldCenterOfMass,
                    victim._rigidbody.worldCenterOfMass,
                    pairState.ContactNormal);
            }
            else
            {
                direction.Normalize();
            }

            pairState.ContactDirection = direction;

            float victimDeltaVStep = pairState.InitialVictimDeltaV * segmentWeight;
            float attackerDeltaVStep = pairState.InitialAttackerDeltaV * segmentWeight;

            if (ShouldApplyPredictedVictimPush(attacker, victim, mode))
            {
                ApplyAcceleration(victim, direction * (victimDeltaVStep / dt), mode);
            }

            if (ShouldApplyPredictedAttackerRecoil(attacker, mode))
            {
                ApplyAcceleration(attacker, -direction * (attackerDeltaVStep / dt), mode);
            }

            pairState.InitialImpactElapsed = nextElapsed;
            return nextElapsed >= duration - 0.0001f;
        }

        private static float EvaluateImpactBurstIntegral(float normalizedTime, float frontload01)
        {
            float u = Mathf.Clamp01(normalizedTime);
            float frontload = Mathf.Clamp01(frontload01);
            const float baseWeight = 0.42f;
            const float bellWeight = 1f - baseWeight;
            float bellIntegral = 2f * u * u - (4f / 3f) * u * u * u;
            float mixed = baseWeight * u + bellWeight * bellIntegral;
            const float normalization = baseWeight + bellWeight * (2f / 3f);
            float legacyIntegral = mixed / normalization;
            float frontloadExponent = Mathf.Lerp(1f, 2.6f, frontload);
            float frontLoadedIntegral = 1f - Mathf.Pow(1f - u, frontloadExponent);
            return Mathf.Lerp(legacyIntegral, frontLoadedIntegral, frontload);
        }

        // Predicted combat should only run on the client that owns the attacker input.
        // Running the ram state on the victim client causes speculative push/correction
        // fights and visible teleport loops.
        private static bool ShouldRunPredictedCombatForAttacker(
            SumoCollisionController attacker,
            SumoCollisionController victim,
            SimulationMode mode)
        {
            if (mode != SimulationMode.Predicted)
            {
                return true;
            }

            return attacker != null
                && victim != null
                && attacker.HasInputAuthority
                && !victim.HasInputAuthority;
        }

        // Keep predicted shove force for the attacker's view (remote proxy victim), but
        // avoid applying speculative victim push on the victim's local body because it
        // fights server reconciliation and causes teleport/jitter loops.
        private static bool ShouldApplyPredictedVictimPush(
            SumoCollisionController attacker,
            SumoCollisionController victim,
            SimulationMode mode)
        {
            if (mode != SimulationMode.Predicted)
            {
                return true;
            }

            return ShouldRunPredictedCombatForAttacker(attacker, victim, mode);
        }

        // Predicted attacker recoil stays disabled so clients do not introduce extra
        // two-body feedback jitter. Server authority still owns the final combat result.
        private static bool ShouldApplyPredictedAttackerRecoil(SumoCollisionController attacker, SimulationMode mode)
        {
            if (mode != SimulationMode.Predicted)
            {
                return true;
            }

            return false;
        }

        private static void SetRamDepleted(ref SumoRamState pairState, bool contactActive, int currentTick)
        {
            pairState.State = SumoPairState.RamDepleted;
            pairState.RamEnergy = 0f;
            pairState.RamContactBlend = 0f;
            pairState.ReengageReadyTick = 0;
            ConsumePendingEnter(ref pairState);

            if (contactActive)
            {
                pairState.BreakStartTick = 0;
                pairState.MaxSeparationSinceBreak = 0f;
                return;
            }

            if (pairState.BreakStartTick <= 0)
            {
                pairState.BreakStartTick = pairState.LastContactTick > 0
                    ? pairState.LastContactTick
                    : currentTick;
            }
        }

        private static void ClearAttacker(ref SumoRamState pairState)
        {
            pairState.AttackerRef = 0;
            pairState.VictimRef = 0;
            pairState.AttackerController = null;
            pairState.VictimController = null;
            pairState.TieResolvedBy = SumoTieResolvedBy.None;
            pairState.InitialImpactSpeed = 0f;
            pairState.InitialImpulse = 0f;
            pairState.InitialImpactDuration = 0f;
            pairState.InitialImpactElapsed = 0f;
            pairState.InitialVictimDeltaV = 0f;
            pairState.InitialAttackerDeltaV = 0f;
            pairState.RamContactBlend = 0f;
            pairState.InitialRamEnergy = 0f;
            pairState.RamEnergy = 0f;
        }

        private static bool TryResolvePairControllers(
            ref SumoRamState pairState,
            out SumoCollisionController first,
            out SumoCollisionController second)
        {
            first = pairState.FirstController;
            second = pairState.SecondController;

            if (first == null || second == null || first == second)
            {
                return false;
            }

            if (first._rigidbody == null || first._sphereCollider == null)
            {
                first.CacheComponents();
            }

            if (second._rigidbody == null || second._sphereCollider == null)
            {
                second.CacheComponents();
            }

            if (first._rigidbody == null
                || second._rigidbody == null
                || first._rigidbody.isKinematic
                || second._rigidbody.isKinematic)
            {
                return false;
            }

            if (first.Object == null
                || second.Object == null
                || !first.Object.IsInSimulation
                || !second.Object.IsInSimulation)
            {
                return false;
            }

            pairState.FirstRef = GetControllerKey(first);
            pairState.SecondRef = GetControllerKey(second);
            if (pairState.FirstRef == 0 || pairState.SecondRef == 0)
            {
                pairState.OwnerKey = 0;
                return false;
            }

            // Keep the owner assigned by the capture phase. Predicted pairs are owned
            // by the local processor (selfKey), while authoritative pairs use min key.
            if (pairState.OwnerKey == 0)
            {
                pairState.OwnerKey = Mathf.Min(pairState.FirstRef, pairState.SecondRef);
            }

            return pairState.OwnerKey != 0;
        }

        private static bool TryGetLiveAttackerVictim(
            ref SumoRamState pairState,
            out SumoCollisionController attacker,
            out SumoCollisionController victim)
        {
            attacker = pairState.AttackerController;
            victim = pairState.VictimController;

            if (attacker == null || victim == null || attacker == victim)
            {
                return false;
            }

            if (attacker._rigidbody == null || attacker._sphereCollider == null)
            {
                attacker.CacheComponents();
            }

            if (victim._rigidbody == null || victim._sphereCollider == null)
            {
                victim.CacheComponents();
            }

            if (attacker._rigidbody == null
                || victim._rigidbody == null
                || attacker._rigidbody.isKinematic
                || victim._rigidbody.isKinematic)
            {
                return false;
            }

            if (attacker.Object == null
                || victim.Object == null
                || !attacker.Object.IsInSimulation
                || !victim.Object.IsInSimulation)
            {
                return false;
            }

            int attackerRef = GetControllerKey(attacker);
            int victimRef = GetControllerKey(victim);

            return pairState.AttackerRef == attackerRef && pairState.VictimRef == victimRef;
        }

        private static bool IsPairInvalidForContact(
            in SumoRamState pairState,
            int expectedOwner,
            int expectedFirst,
            int expectedSecond,
            int currentTick)
        {
            if (pairState.OwnerKey == 0)
            {
                return true;
            }

            if (pairState.OwnerKey != expectedOwner)
            {
                return true;
            }

            if (pairState.FirstRef != 0 && pairState.FirstRef != expectedFirst)
            {
                return true;
            }

            if (pairState.SecondRef != 0 && pairState.SecondRef != expectedSecond)
            {
                return true;
            }

            return pairState.LastContactTick > 0 && currentTick + 4 < pairState.LastContactTick;
        }

        private SumoRamState CreateFreshState(long pairKey, int currentTick)
        {
            return new SumoRamState
            {
                PairKey = pairKey,
                OwnerKey = 0,
                FirstRef = 0,
                SecondRef = 0,
                FirstController = null,
                SecondController = null,
                State = SumoPairState.None,
                CreatedTick = currentTick,
                LastContactTick = 0,
                LastEnterTick = 0,
                LastProcessedEnterTick = 0,
                PendingEnterTick = 0,
                LastImpactTick = int.MinValue,
                LastRamTick = int.MinValue,
                MaxRamDurationTicks = FallbackMaxRamDurationTicks,
                ContactPoint = Vector3.zero,
                ContactNormal = Vector3.zero,
                ContactDirection = Vector3.zero,
                DirectionDot = 0f,
                TieResolvedBy = SumoTieResolvedBy.None,
                InitialImpactDuration = 0f,
                InitialImpactElapsed = 0f,
                InitialVictimDeltaV = 0f,
                InitialAttackerDeltaV = 0f,
                RamContactBlend = 0f,
                MaxSeparationSinceBreak = 0f,
                HasPendingEnter = false,
                BreakStartTick = 0,
                ReengageReadyTick = 0,
                ImpactLatchTick = int.MinValue,
                ReimpactSuppressedUntilHardBreak = false
            };
        }

        private void EnsurePredictedContext()
        {
            if (Runner == null)
            {
                return;
            }

            int runnerId = Runner.GetInstanceID();
            int currentTick = Runner.Tick.Raw;

            bool runnerChanged = _predictedCacheRunnerId != runnerId;
            bool tickRegressed = !runnerChanged
                && _predictedCacheLastTick != int.MinValue
                && currentTick < _predictedCacheLastTick;

            if (runnerChanged)
            {
                PredictedPairStates.Clear();
                PredictedPairLastImpactTick.Clear();
                PredictedPairLastContactTick.Clear();
                PredictedBlockNormals.Clear();
                PredictedHistory.Clear();
            }
            else if (tickRegressed)
            {
                RestorePredictedFrame(currentTick - 1);
                TrimPredictedHistory(currentTick, removeFutureTicks: true);
                PredictedBlockNormals.Clear();
            }

            _predictedCacheRunnerId = runnerId;
            _predictedCacheLastTick = currentTick;
        }

        private static void StorePredictedFrame(int currentTick)
        {
            if (currentTick <= 0)
            {
                return;
            }

            PredictedHistory[currentTick] = new PredictedFrameSnapshot(
                ClonePairStates(PredictedPairStates),
                new Dictionary<long, int>(PredictedPairLastImpactTick),
                new Dictionary<long, int>(PredictedPairLastContactTick));

            TrimPredictedHistory(currentTick, removeFutureTicks: false);
        }

        private static void RestorePredictedFrame(int sourceTick)
        {
            if (sourceTick <= 0 || !PredictedHistory.TryGetValue(sourceTick, out PredictedFrameSnapshot snapshot))
            {
                PredictedPairStates.Clear();
                PredictedPairLastImpactTick.Clear();
                PredictedPairLastContactTick.Clear();
                return;
            }

            CopyPairStates(snapshot.PairStates, PredictedPairStates);
            CopyIntMap(snapshot.LastImpactTicks, PredictedPairLastImpactTick);
            CopyIntMap(snapshot.LastContactTicks, PredictedPairLastContactTick);
        }

        private static Dictionary<long, SumoRamState> ClonePairStates(Dictionary<long, SumoRamState> source)
        {
            Dictionary<long, SumoRamState> clone = new Dictionary<long, SumoRamState>(source.Count);

            foreach (KeyValuePair<long, SumoRamState> pair in source)
            {
                clone[pair.Key] = pair.Value;
            }

            return clone;
        }

        private static void CopyPairStates(Dictionary<long, SumoRamState> source, Dictionary<long, SumoRamState> target)
        {
            target.Clear();

            foreach (KeyValuePair<long, SumoRamState> pair in source)
            {
                target[pair.Key] = pair.Value;
            }
        }

        private static void CopyIntMap(Dictionary<long, int> source, Dictionary<long, int> target)
        {
            target.Clear();

            foreach (KeyValuePair<long, int> pair in source)
            {
                target[pair.Key] = pair.Value;
            }
        }

        private static void TrimPredictedHistory(int currentTick, bool removeFutureTicks)
        {
            if (PredictedHistory.Count == 0)
            {
                return;
            }

            int minRetainedTick = currentTick - PredictedRollbackHistoryTicks;
            PredictedHistoryTickBuffer.Clear();

            foreach (KeyValuePair<int, PredictedFrameSnapshot> frame in PredictedHistory)
            {
                bool tooOld = frame.Key < minRetainedTick;
                bool inFuture = removeFutureTicks && frame.Key >= currentTick;
                if (tooOld || inFuture)
                {
                    PredictedHistoryTickBuffer.Add(frame.Key);
                }
            }

            for (int i = 0; i < PredictedHistoryTickBuffer.Count; i++)
            {
                PredictedHistory.Remove(PredictedHistoryTickBuffer[i]);
            }

            PredictedHistoryTickBuffer.Clear();
        }

        private void EnsurePairCacheContext()
        {
            if (Runner == null)
            {
                return;
            }

            int runnerId = Runner.GetInstanceID();
            int currentTick = Runner.Tick.Raw;

            bool runnerChanged = _pairCacheRunnerId != runnerId;
            bool tickRegressed = !runnerChanged
                && _pairCacheLastTick != int.MinValue
                && currentTick < _pairCacheLastTick;

            if (runnerChanged || tickRegressed)
            {
                PairStates.Clear();
                PredictedPairStates.Clear();
                PairKeysBuffer.Clear();
                PairPruneBuffer.Clear();
                PredictedPairLastImpactTick.Clear();
                PredictedPairLastContactTick.Clear();
                PredictedHistory.Clear();
                AuthorityBlockNormals.Clear();
                PredictedBlockNormals.Clear();
                ActiveControllers.Clear();
                IgnoredNativeCollisionPairs.Clear();
            }

            _pairCacheRunnerId = runnerId;
            _pairCacheLastTick = currentTick;
        }

        private void PrunePairStates(int currentTick, Dictionary<long, SumoRamState> states)
        {
            if (states.Count <= 96 || (currentTick & 63) != 0)
            {
                return;
            }

            PairPruneBuffer.Clear();

            foreach (KeyValuePair<long, SumoRamState> pair in states)
            {
                SumoRamState state = pair.Value;
                int contactBreakTicks = GetPairContactBreakGraceTicks(state.FirstController, state.SecondController);

                if (ShouldRemoveState(state, currentTick, contactBreakTicks))
                {
                    PairPruneBuffer.Add(pair.Key);
                }
            }

            for (int i = 0; i < PairPruneBuffer.Count; i++)
            {
                states.Remove(PairPruneBuffer[i]);
            }

            PairPruneBuffer.Clear();
        }

        private static void RemoveStatesForController(int controllerKey, Dictionary<long, SumoRamState> states)
        {
            if (controllerKey == 0 || states.Count == 0)
            {
                return;
            }

            PairPruneBuffer.Clear();

            foreach (KeyValuePair<long, SumoRamState> pair in states)
            {
                SumoRamState state = pair.Value;
                if (state.OwnerKey == controllerKey
                    || state.FirstRef == controllerKey
                    || state.SecondRef == controllerKey
                    || state.AttackerRef == controllerKey
                    || state.VictimRef == controllerKey)
                {
                    PairPruneBuffer.Add(pair.Key);
                }
            }

            for (int i = 0; i < PairPruneBuffer.Count; i++)
            {
                states.Remove(PairPruneBuffer[i]);
            }

            PairPruneBuffer.Clear();
        }

        private static void UpdateSeparationSinceBreak(
            ref SumoRamState pairState,
            SumoCollisionController first,
            SumoCollisionController second,
            int currentTick)
        {
            if (pairState.BreakStartTick <= 0)
            {
                pairState.BreakStartTick = pairState.LastContactTick > 0
                    ? pairState.LastContactTick
                    : currentTick;
            }

            float separation = ComputeEdgeSeparation(first, second);
            if (separation > pairState.MaxSeparationSinceBreak)
            {
                pairState.MaxSeparationSinceBreak = separation;
            }
        }

        private static bool IsContactActive(SumoRamState pairState, int currentTick, int contactBreakGraceTicks)
        {
            return pairState.LastContactTick > 0 && currentTick - pairState.LastContactTick <= contactBreakGraceTicks;
        }

        private static float GetApproachSpeed(
            SumoCollisionController source,
            Vector3 sourceVelocity,
            Vector3 towardDirection)
        {
            float velocityApproach = Mathf.Max(0f, Vector3.Dot(sourceVelocity, towardDirection));
            float intentApproach = GetIntentApproachSpeed(source, towardDirection, sourceVelocity);
            return Mathf.Max(velocityApproach, intentApproach);
        }

        private static float GetPairTieSpeedEpsilon(SumoCollisionController a, SumoCollisionController b)
        {
            return Mathf.Max(GetTieSpeedEpsilon(a), GetTieSpeedEpsilon(b));
        }

        private static float GetTieSpeedEpsilon(SumoCollisionController source)
        {
            return source != null && source.physicsConfig != null
                ? source.physicsConfig.AttackerTieSpeedEpsilon
                : FallbackTieSpeedEpsilon;
        }

        private static bool GetPairResolveTieByLowerKey(SumoCollisionController a, SumoCollisionController b)
        {
            bool fromA = a != null && a.physicsConfig != null ? a.physicsConfig.ResolveTieByLowerKey : true;
            bool fromB = b != null && b.physicsConfig != null ? b.physicsConfig.ResolveTieByLowerKey : true;
            return fromA || fromB;
        }

        private static int GetPairContactBreakGraceTicks(SumoCollisionController a, SumoCollisionController b)
        {
            int configured = Mathf.Max(GetContactBreakGraceTicks(a), GetContactBreakGraceTicks(b));
            return Mathf.Clamp(configured, 3, 12);
        }

        private static int GetContactBreakGraceTicks(SumoCollisionController source)
        {
            return source != null && source.physicsConfig != null
                ? source.physicsConfig.ContactBreakGraceTicks
                : FallbackContactBreakGraceTicks;
        }

        private static float GetPairContactEnterPadding(SumoCollisionController a, SumoCollisionController b)
        {
            float fromA = a != null && a.physicsConfig != null
                ? a.physicsConfig.PlayerContactEnterPadding
                : FallbackPlayerContactEnterPadding;
            float fromB = b != null && b.physicsConfig != null
                ? b.physicsConfig.PlayerContactEnterPadding
                : FallbackPlayerContactEnterPadding;
            return Mathf.Clamp(Mathf.Max(fromA, fromB), 0f, 0.025f);
        }

        private static float GetPairContactExitPadding(SumoCollisionController a, SumoCollisionController b)
        {
            float enterPadding = GetPairContactEnterPadding(a, b);
            float fromA = a != null && a.physicsConfig != null
                ? a.physicsConfig.PlayerContactExitPadding
                : FallbackPlayerContactExitPadding;
            float fromB = b != null && b.physicsConfig != null
                ? b.physicsConfig.PlayerContactExitPadding
                : FallbackPlayerContactExitPadding;
            return Mathf.Clamp(Mathf.Max(Mathf.Max(fromA, fromB), enterPadding), enterPadding, 0.06f);
        }

        private static float GetActiveContactSeparationTolerance(SumoCollisionController a, SumoCollisionController b)
        {
            float exitPadding = GetPairContactExitPadding(a, b);
            return Mathf.Max(
                ActivePhaseContactSeparationEpsilon,
                Mathf.Min(0.08f, exitPadding + ActivePhaseContactExtraTolerance));
        }

        private static float GetPairContactPenetrationSlop(SumoCollisionController a, SumoCollisionController b)
        {
            float fromA = a != null && a.physicsConfig != null
                ? a.physicsConfig.PlayerContactPenetrationSlop
                : FallbackPlayerContactPenetrationSlop;
            float fromB = b != null && b.physicsConfig != null
                ? b.physicsConfig.PlayerContactPenetrationSlop
                : FallbackPlayerContactPenetrationSlop;
            return Mathf.Clamp(Mathf.Max(fromA, fromB), 0f, 0.12f);
        }

        private static float GetPairContactPositionCorrection(SumoCollisionController a, SumoCollisionController b)
        {
            float fromA = a != null && a.physicsConfig != null
                ? a.physicsConfig.PlayerContactPositionCorrection
                : FallbackPlayerContactPositionCorrection;
            float fromB = b != null && b.physicsConfig != null
                ? b.physicsConfig.PlayerContactPositionCorrection
                : FallbackPlayerContactPositionCorrection;
            return Mathf.Clamp(Mathf.Max(fromA, fromB), 0f, 1f);
        }

        private static float GetPairContactVelocityDamping(SumoCollisionController a, SumoCollisionController b)
        {
            float fromA = a != null && a.physicsConfig != null
                ? a.physicsConfig.PlayerContactVelocityDamping
                : FallbackPlayerContactVelocityDamping;
            float fromB = b != null && b.physicsConfig != null
                ? b.physicsConfig.PlayerContactVelocityDamping
                : FallbackPlayerContactVelocityDamping;
            return Mathf.Clamp(Mathf.Max(fromA, fromB), 0f, 1.25f);
        }

        private static int GetPairReengageBreakTicks(SumoCollisionController a, SumoCollisionController b)
        {
            int fromA = a != null && a.physicsConfig != null ? a.physicsConfig.ReengageBreakTicks : FallbackReengageBreakTicks;
            int fromB = b != null && b.physicsConfig != null ? b.physicsConfig.ReengageBreakTicks : FallbackReengageBreakTicks;
            return Mathf.Clamp(Mathf.Max(FallbackReengageBreakTicks, Mathf.Max(fromA, fromB)), 4, 16);
        }

        private static float GetPairReengageDistance(SumoCollisionController a, SumoCollisionController b)
        {
            float fromA = a != null && a.physicsConfig != null ? a.physicsConfig.ReengageDistance : FallbackReengageDistance;
            float fromB = b != null && b.physicsConfig != null ? b.physicsConfig.ReengageDistance : FallbackReengageDistance;
            return Mathf.Clamp(Mathf.Max(FallbackReengageDistance, Mathf.Max(fromA, fromB)), 0.14f, 0.4f);
        }

        private static float GetPairReengageSpeedThreshold(SumoCollisionController a, SumoCollisionController b)
        {
            float fromA = a != null && a.physicsConfig != null ? a.physicsConfig.ReengageSpeedThreshold : FallbackReengageSpeed;
            float fromB = b != null && b.physicsConfig != null ? b.physicsConfig.ReengageSpeedThreshold : FallbackReengageSpeed;
            return Mathf.Max(FallbackReengageSpeed, Mathf.Max(fromA, fromB));
        }

        private static int GetPairMaxRamDurationTicks(SumoCollisionController a, SumoCollisionController b)
        {
            int fromA = a != null && a.physicsConfig != null ? a.physicsConfig.MaxRamDurationTicks : FallbackMaxRamDurationTicks;
            int fromB = b != null && b.physicsConfig != null ? b.physicsConfig.MaxRamDurationTicks : FallbackMaxRamDurationTicks;
            return Mathf.Clamp(Mathf.Max(1, Mathf.Max(fromA, fromB)), 1, 34);
        }

        private static int GetPairMinReimpactTicks(SumoCollisionController a, SumoCollisionController b)
        {
            int reengageTicks = GetPairReengageBreakTicks(a, b);
            return Mathf.Max(FallbackMinReimpactTicks, reengageTicks * 2);
        }

        private static float GetPairMinRamPressureSpeed(SumoCollisionController a, SumoCollisionController b)
        {
            float fromA = a != null && a.physicsConfig != null ? a.physicsConfig.MinRamPressureSpeed : FallbackMinRamPressureSpeed;
            float fromB = b != null && b.physicsConfig != null ? b.physicsConfig.MinRamPressureSpeed : FallbackMinRamPressureSpeed;
            return Mathf.Max(0f, Mathf.Max(fromA, fromB));
        }

        private static float GetPairRamMinDirectionDot(SumoCollisionController a, SumoCollisionController b)
        {
            float fromA = a != null && a.physicsConfig != null ? a.physicsConfig.RamMinDirectionDot : 0.2f;
            float fromB = b != null && b.physicsConfig != null ? b.physicsConfig.RamMinDirectionDot : 0.2f;
            return Mathf.Clamp01(Mathf.Max(fromA, fromB));
        }

        private static float GetRamStopThreshold(SumoCollisionController source)
        {
            return source != null && source.physicsConfig != null
                ? source.physicsConfig.RamStopEnergyThreshold
                : FallbackRamStopEnergyThreshold;
        }

        private static float GetImpactVerticalLift(SumoCollisionController source)
        {
            return source != null && source.physicsConfig != null
                ? source.physicsConfig.ImpactVerticalLift
                : FallbackImpactVerticalLift;
        }

        private static float GetPairImpactBurstDuration(SumoCollisionController a, SumoCollisionController b)
        {
            float fromA = a != null && a.physicsConfig != null ? a.physicsConfig.ImpactBurstDuration : FallbackImpactBurstDuration;
            float fromB = b != null && b.physicsConfig != null ? b.physicsConfig.ImpactBurstDuration : FallbackImpactBurstDuration;
            return Mathf.Clamp(Mathf.Max(fromA, fromB), 0.04f, 0.14f);
        }

        private static float GetAttackerReferenceTopSpeed(SumoCollisionController attacker)
        {
            if (attacker != null && attacker.ballController != null)
            {
                return Mathf.Max(0.01f, attacker.ballController.MaxSpeed);
            }

            if (attacker != null && attacker.physicsConfig != null)
            {
                return Mathf.Max(attacker.physicsConfig.MinImpactSpeed + 0.01f, attacker.physicsConfig.MaxImpactSpeed);
            }

            return FallbackAttackerReferenceTopSpeed;
        }

        private static bool RequiresPhysicalContact(SumoPairState state)
        {
            return state == SumoPairState.InitialImpact || state == SumoPairState.Ramming;
        }

        private static float GetIntentApproachSpeed(
            SumoCollisionController source,
            Vector3 towardDirection,
            Vector3 fallbackVelocity)
        {
            if (towardDirection.sqrMagnitude < 0.0001f)
            {
                return 0f;
            }

            float planarSpeed = GetPlanarSpeedEstimate(source, fallbackVelocity);
            if (planarSpeed <= 0.0001f)
            {
                return 0f;
            }

            Vector3 intentDirection = source != null && source.ballController != null
                ? source.ballController.GetContactIntentDirection(fallbackVelocity)
                : GetHorizontalDirection(fallbackVelocity);

            if (intentDirection.sqrMagnitude < 0.0001f)
            {
                return 0f;
            }

            float directionDot = Vector3.Dot(intentDirection.normalized, towardDirection.normalized);
            if (directionDot <= 0f)
            {
                return 0f;
            }

            return planarSpeed * Mathf.Clamp01(directionDot);
        }

        private bool IsPredictedImpactRuntimeEnabled()
        {
            if (Runner != null && Runner.IsClient && !HasStateAuthority)
            {
                return true;
            }

            return enableClientPredictedImpact;
        }

        private bool IsPredictedRamRuntimeEnabled()
        {
            if (Runner != null && Runner.IsClient && !HasStateAuthority)
            {
                return true;
            }

            return enableClientPredictedRam;
        }

        private static float GetPlanarSpeedEstimate(SumoCollisionController source, Vector3 fallbackVelocity)
        {
            Vector3 horizontalVelocity = new Vector3(fallbackVelocity.x, 0f, fallbackVelocity.z);
            float speed = horizontalVelocity.magnitude;

            if (source == null || source.ballController == null)
            {
                return speed;
            }

            return source.ballController.GetContactPlanarSpeed(fallbackVelocity);
        }

        private static float ComputeDirectionDot(
            SumoCollisionController attacker,
            Vector3 attackerVelocity,
            Vector3 targetDirection)
        {
            Vector3 moveDirection = attacker != null && attacker.ballController != null
                ? attacker.ballController.GetContactIntentDirection(attackerVelocity)
                : Vector3.zero;

            Vector3 velocityDirection = GetHorizontalDirection(attackerVelocity);

            float moveDot = moveDirection.sqrMagnitude > 0.0001f
                ? Vector3.Dot(moveDirection.normalized, targetDirection)
                : -1f;

            float velocityDot = velocityDirection.sqrMagnitude > 0.0001f
                ? Vector3.Dot(velocityDirection, targetDirection)
                : -1f;

            return Mathf.Clamp01(Mathf.Max(moveDot, velocityDot));
        }

        private static Vector3 ResolveAttackDirection(
            SumoCollisionController attacker,
            Vector3 attackerVelocity,
            Vector3 attackerToVictim)
        {
            Vector3 attackDirection = attacker != null && attacker.ballController != null
                ? attacker.ballController.GetContactIntentDirection(attackerVelocity)
                : GetHorizontalDirection(attackerVelocity);

            if (attackDirection.sqrMagnitude < 0.0001f)
            {
                attackDirection = GetHorizontalDirection(attackerVelocity);
            }

            if (attackDirection.sqrMagnitude < 0.0001f)
            {
                attackDirection = attackerToVictim;
            }

            return attackDirection.normalized;
        }

        private static float ComputeEdgeSeparation(SumoCollisionController a, SumoCollisionController b)
        {
            if (a == null || b == null)
            {
                return 0f;
            }

            if (a._rigidbody == null || a._sphereCollider == null)
            {
                a.CacheComponents();
            }

            if (b._rigidbody == null || b._sphereCollider == null)
            {
                b.CacheComponents();
            }

            if (a._rigidbody == null || b._rigidbody == null)
            {
                return 0f;
            }

            float distance = Vector3.Distance(a._rigidbody.worldCenterOfMass, b._rigidbody.worldCenterOfMass);
            float combinedRadius = GetScaledRadius(a) + GetScaledRadius(b);
            return Mathf.Max(0f, distance - combinedRadius);
        }

        private static float GetScaledRadius(SumoCollisionController source)
        {
            if (source == null)
            {
                return 0.5f;
            }

            if (source._sphereCollider == null)
            {
                source.CacheComponents();
            }

            if (source._sphereCollider == null)
            {
                return 0.5f;
            }

            Vector3 scale = source.transform.lossyScale;
            float maxAxis = Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.y), Mathf.Abs(scale.z));
            return Mathf.Max(0.01f, source._sphereCollider.radius * maxAxis);
        }

        private static void ApplyImpulse(SumoCollisionController controller, Vector3 impulse, SimulationMode mode)
        {
            if (!CanApplyGameplayForces(controller, mode))
            {
                return;
            }

            if (impulse.sqrMagnitude <= 0.0000001f)
            {
                return;
            }

            controller._rigidbody.AddForce(impulse, ForceMode.Impulse);
        }

        private static void ApplyAcceleration(SumoCollisionController controller, Vector3 acceleration, SimulationMode mode)
        {
            if (!CanApplyGameplayForces(controller, mode))
            {
                return;
            }

            if (acceleration.sqrMagnitude <= 0.0000001f)
            {
                return;
            }

            controller._rigidbody.AddForce(acceleration, ForceMode.Acceleration);
        }

        private static Vector3 ResolveDirection(Vector3 from, Vector3 to, Vector3 fallbackNormal)
        {
            Vector3 direction = to - from;
            if (direction.sqrMagnitude < 0.0001f)
            {
                direction = -fallbackNormal;
            }

            if (direction.sqrMagnitude < 0.0001f)
            {
                direction = Vector3.forward;
            }

            return direction.normalized;
        }

        private static Vector3 GetHorizontalDirection(Vector3 velocity)
        {
            Vector3 horizontal = new Vector3(velocity.x, 0f, velocity.z);
            if (horizontal.sqrMagnitude < 0.0001f)
            {
                return Vector3.zero;
            }

            return horizontal.normalized;
        }

        private static Vector3 BuildImpulseDirection(Vector3 impactDirection, float verticalMultiplier)
        {
            Vector3 horizontal = Vector3.ProjectOnPlane(impactDirection, Vector3.up);
            if (horizontal.sqrMagnitude < 0.0001f)
            {
                horizontal = impactDirection;
            }

            horizontal.Normalize();

            Vector3 direction = horizontal + Vector3.up * Mathf.Max(0f, verticalMultiplier);
            if (direction.sqrMagnitude < 0.0001f)
            {
                return horizontal;
            }

            return direction.normalized;
        }

        private static long BuildPairKey(int a, int b)
        {
            uint low = unchecked((uint)Mathf.Min(a, b));
            uint high = unchecked((uint)Mathf.Max(a, b));
            return ((long)high << 32) | low;
        }

        private static int GetControllerKey(SumoCollisionController controller)
        {
            if (controller == null)
            {
                return 0;
            }

            if (controller.Object != null)
            {
                return unchecked((int)controller.Object.Id.Raw);
            }

            return controller.GetInstanceID();
        }

        private Vector3 GetVelocitySampleForTick(int tick)
        {
            if (_preSimVelocityTick == tick)
            {
                return _preSimVelocity;
            }

            return _rigidbody != null ? _rigidbody.linearVelocity : Vector3.zero;
        }

        private void CachePreSimVelocity(int currentTick)
        {
            if (_rigidbody == null)
            {
                return;
            }

            if (_preSimVelocityTick == currentTick)
            {
                return;
            }

            _preSimVelocity = _rigidbody.linearVelocity;
            _preSimVelocityTick = currentTick;
        }

        private static void CaptureEnterSnapshot(
            ref SumoRamState pairState,
            SumoCollisionController first,
            SumoCollisionController second,
            int currentTick,
            bool snapshotSelfIsFirst,
            in ContactSnapshot snapshot)
        {
            if (first == null || second == null || first._rigidbody == null || second._rigidbody == null)
            {
                return;
            }

            Vector3 firstToSecond = ResolveDirection(
                first._rigidbody.worldCenterOfMass,
                second._rigidbody.worldCenterOfMass,
                pairState.ContactNormal);

            Vector3 firstVelocity = snapshotSelfIsFirst ? snapshot.SelfVelocity : snapshot.OtherVelocity;
            Vector3 secondVelocity = snapshotSelfIsFirst ? snapshot.OtherVelocity : snapshot.SelfVelocity;

            float firstApproachSpeed = GetApproachSpeed(first, firstVelocity, firstToSecond);
            float secondApproachSpeed = GetApproachSpeed(second, secondVelocity, -firstToSecond);
            float relativeClosing = Mathf.Max(0f, Vector3.Dot(firstVelocity - secondVelocity, firstToSecond));
            relativeClosing = Mathf.Max(relativeClosing, snapshot.RelativeClosingSpeed);

            pairState.EnterFirstApproachSpeed = firstApproachSpeed;
            pairState.EnterSecondApproachSpeed = secondApproachSpeed;
            pairState.EnterRelativeClosingSpeed = Mathf.Max(relativeClosing, Mathf.Max(firstApproachSpeed, secondApproachSpeed));
            pairState.LastEnterTick = currentTick;
            pairState.PendingEnterTick = currentTick;
            pairState.HasPendingEnter = true;
            pairState.BreakStartTick = 0;
            pairState.MaxSeparationSinceBreak = 0f;
        }

        private void RegisterActiveController()
        {
            if (Runner == null)
            {
                return;
            }

            int key = GetControllerKey(this);
            if (key == 0)
            {
                return;
            }

            ActiveControllers[key] = this;
        }

        private void UnregisterActiveController()
        {
            int key = GetControllerKey(this);
            if (key == 0)
            {
                return;
            }

            ActiveControllers.Remove(key);
            AuthorityBlockNormals.Remove(key);
            PredictedBlockNormals.Remove(key);
            RemoveStatesForController(key, PairStates);
            RemoveStatesForController(key, PredictedPairStates);
        }

        private void CacheComponents()
        {
            if (ballController == null)
            {
                ballController = GetComponent<SumoBallController>();
            }

            if (physicsConfig == null)
            {
                physicsConfig = GetComponent<SumoBallPhysicsConfig>();
            }

            if (_rigidbody == null)
            {
                _rigidbody = GetComponent<Rigidbody>();
            }

            if (_sphereCollider == null)
            {
                _sphereCollider = GetComponent<SphereCollider>();
            }
        }

        private void OnValidate()
        {
            if (ballController == null)
            {
                ballController = GetComponent<SumoBallController>();
            }

            if (physicsConfig == null)
            {
                physicsConfig = GetComponent<SumoBallPhysicsConfig>();
            }
        }
    }
}
