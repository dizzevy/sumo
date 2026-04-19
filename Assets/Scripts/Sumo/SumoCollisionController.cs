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

        [Header("Prediction")]
        [SerializeField] private bool enableClientPredictedImpact;

        [Header("Debug")]
        [SerializeField] private bool logImpacts;
        [SerializeField] private bool logStateMachine;

        public event Action<SumoImpactData> ImpactApplied;

        public Vector3 CurrentVelocity => _rigidbody != null ? _rigidbody.linearVelocity : Vector3.zero;

        private Rigidbody _rigidbody;
        private SphereCollider _sphereCollider;
        private Vector3 _preSimVelocity;
        private int _preSimVelocityTick = int.MinValue;

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

        private static readonly Dictionary<long, SumoRamState> PairStates = new Dictionary<long, SumoRamState>(128);
        private static readonly List<long> PairKeysBuffer = new List<long>(64);
        private static readonly List<long> PairPruneBuffer = new List<long>(64);
        private static readonly Dictionary<long, int> PredictedPairLastImpactTick = new Dictionary<long, int>(128);
        private static readonly Dictionary<long, int> PredictedPairLastContactTick = new Dictionary<long, int>(128);

        private static int _pairCacheRunnerId = int.MinValue;
        private static int _pairCacheLastTick = int.MinValue;
        private static int _predictedCacheRunnerId = int.MinValue;
        private static int _predictedCacheLastTick = int.MinValue;

        private const int FallbackContactBreakGraceTicks = 1;
        private const int FallbackReengageBreakTicks = 5;
        private const float FallbackReengageDistance = 0.22f;
        private const float FallbackReengageSpeed = 4.8f;
        private const float FallbackTieSpeedEpsilon = 0.15f;
        private const float FallbackMinRamPressureSpeed = 1.6f;
        private const float FallbackRamStopEnergyThreshold = 0.08f;
        private const float FallbackImpactVerticalLift = 0.02f;
        private const float FallbackImpactBurstDuration = 0.08f;
        private const int FallbackMaxRamDurationTicks = 32;
        private const int FallbackMinReimpactTicks = 16;
        private const float RamContactBlendRisePerSecond = 13f;
        private const float RamContactBlendFallPerSecond = 7f;
        private const float RamContactStartBlend = 0.24f;
        private const float ActivePhaseContactSeparationEpsilon = 0.02f;
        private const float PredictedImpactScale = 0.92f;
        private const float PairDirectionBlend = 0.35f;
        private const int SoftStalePairTicks = 420;
        private const int HardStalePairTicks = 1200;

        private void Awake()
        {
            CacheComponents();
        }

        public override void Spawned()
        {
            CacheComponents();
            EnsurePairCacheContext();
        }

        public override void Despawned(NetworkRunner runner, bool hasState)
        {
            if (Runner == null || Object == null)
            {
                return;
            }

            if (!HasStateAuthority)
            {
                return;
            }

            RemoveStatesForController(GetControllerKey(this));
        }

        public override void FixedUpdateNetwork()
        {
            if (!CanProcessThisTick())
            {
                return;
            }

            int currentTick = Runner.Tick.Raw;
            CachePreSimVelocity(currentTick);
            ProcessOwnedPairStates(currentTick);
            PrunePairStates(currentTick);
        }

        public bool HasActiveRamDrive()
        {
            if (Runner == null || PairStates.Count == 0)
            {
                return false;
            }

            int selfKey = GetControllerKey(this);
            if (selfKey == 0)
            {
                return false;
            }

            int currentTick = Runner.Tick.Raw;

            foreach (KeyValuePair<long, SumoRamState> pair in PairStates)
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
                    && ComputeEdgeSeparation(state.FirstController, state.SecondController) > ActivePhaseContactSeparationEpsilon)
                {
                    continue;
                }

                if (state.State == SumoPairState.InitialImpact)
                {
                    return true;
                }

                SumoCollisionController attacker = state.AttackerController;
                float stopThreshold = GetRamStopThreshold(attacker != null ? attacker : this);
                if (state.RamEnergy > stopThreshold)
                {
                    return true;
                }
            }

            return false;
        }

        private void OnCollisionEnter(Collision collision)
        {
            RegisterContact(collision, true);
        }

        private void OnCollisionStay(Collision collision)
        {
            RegisterContact(collision, false);
        }

        private void RegisterContact(Collision collision, bool isEnter)
        {
            if (Runner == null || Object == null || !Object.IsInSimulation)
            {
                return;
            }

            if (!HasStateAuthority)
            {
                if (enableClientPredictedImpact)
                {
                    TryApplyPredictedImpact(collision, isEnter);
                }

                return;
            }

            if (!CanCaptureContactThisTick())
            {
                return;
            }

            int currentTick = Runner.Tick.Raw;

            if (!TryBuildContactSnapshot(collision, currentTick, out ContactSnapshot snapshot))
            {
                return;
            }

            bool selfIsFirst = snapshot.SelfKey <= snapshot.OtherKey;
            SumoCollisionController firstController = selfIsFirst ? this : snapshot.Other;
            SumoCollisionController secondController = selfIsFirst ? snapshot.Other : this;
            int firstKey = selfIsFirst ? snapshot.SelfKey : snapshot.OtherKey;
            int secondKey = selfIsFirst ? snapshot.OtherKey : snapshot.SelfKey;
            int ownerKey = Mathf.Min(firstKey, secondKey);

            long pairKey = BuildPairKey(firstKey, secondKey);
            bool hadState = PairStates.TryGetValue(pairKey, out SumoRamState pairState);

            if (hadState && IsPairInvalidForContact(pairState, ownerKey, firstKey, secondKey, currentTick))
            {
                hadState = false;
            }

            int previousContactTick = hadState ? pairState.LastContactTick : int.MinValue;

            if (!hadState)
            {
                pairState = CreateFreshState(pairKey, currentTick);
            }

            pairState.PairKey = pairKey;
            pairState.OwnerKey = ownerKey;
            pairState.FirstRef = firstKey;
            pairState.SecondRef = secondKey;
            pairState.FirstController = firstController;
            pairState.SecondController = secondController;
            pairState.ContactPoint = snapshot.ContactPoint;
            if (snapshot.ContactNormal.sqrMagnitude > 0.0001f)
            {
                pairState.ContactNormal = snapshot.ContactNormal.normalized;
            }

            int contactBreakTicks = GetPairContactBreakGraceTicks(firstController, secondController);
            bool contactRestarted = !hadState
                || previousContactTick == int.MinValue
                || currentTick - previousContactTick > contactBreakTicks;

            pairState.LastContactTick = currentTick;
            if (contactRestarted || isEnter)
            {
                bool canCaptureEnter = pairState.State != SumoPairState.InitialImpact
                    && pairState.State != SumoPairState.Ramming;

                if (canCaptureEnter)
                {
                    CaptureEnterSnapshot(
                        ref pairState,
                        firstController,
                        secondController,
                        currentTick,
                        selfIsFirst,
                        snapshot);
                }
            }

            // Start first impact in the same collision tick to remove perceived delay.
            if (isEnter)
            {
                bool canStartFromCurrentState = pairState.State == SumoPairState.None
                    || pairState.State == SumoPairState.ReengageReady;

                if (canStartFromCurrentState && HasFreshEnter(pairState))
                {
                    bool requireReengageSpeed = pairState.State == SumoPairState.ReengageReady;
                    int pendingEnterTick = pairState.PendingEnterTick;

                    if (!requireReengageSpeed
                        || (pairState.ReengageReadyTick > 0 && pendingEnterTick >= pairState.ReengageReadyTick))
                    {
                        bool startedImpactNow = TryStartInitialImpact(
                            ref pairState,
                            currentTick,
                            firstController,
                            secondController,
                            requireReengageSpeed);

                        ConsumePendingEnter(ref pairState);

                        if (!startedImpactNow && requireReengageSpeed)
                        {
                            pairState.State = SumoPairState.RamDepleted;
                            pairState.ReengageReadyTick = 0;
                            ClearAttacker(ref pairState);
                        }
                    }
                }
            }

            PairStates[pairKey] = pairState;
        }

        private bool TryBuildContactSnapshot(Collision collision, int currentTick, out ContactSnapshot snapshot)
        {
            snapshot = default;

            if (collision == null || collision.rigidbody == null)
            {
                return false;
            }

            if (!collision.rigidbody.TryGetComponent(out SumoCollisionController other) || other == null || other == this)
            {
                return false;
            }

            if ((playerMask.value & (1 << other.gameObject.layer)) == 0)
            {
                return false;
            }

            if (other.Object == null || !other.Object.IsInSimulation)
            {
                return false;
            }

            if (other._rigidbody == null || other._sphereCollider == null)
            {
                other.CacheComponents();
            }

            if (other._rigidbody == null)
            {
                return false;
            }

            ContactPoint contact = collision.contactCount > 0 ? collision.GetContact(0) : default;

            Vector3 selfVelocity = GetVelocitySampleForTick(currentTick);
            Vector3 otherVelocity = other.GetVelocitySampleForTick(currentTick);
            Vector3 contactDirection = ResolveDirection(
                _rigidbody.worldCenterOfMass,
                other._rigidbody.worldCenterOfMass,
                collision.contactCount > 0 ? contact.normal : Vector3.zero);

            float relativeClosingSpeed = Mathf.Max(0f, Vector3.Dot(collision.relativeVelocity, contactDirection));
            if (relativeClosingSpeed <= 0.0001f)
            {
                relativeClosingSpeed = Mathf.Max(0f, Vector3.Dot(selfVelocity - otherVelocity, contactDirection));
            }

            snapshot = new ContactSnapshot
            {
                Other = other,
                SelfKey = GetControllerKey(this),
                OtherKey = GetControllerKey(other),
                ContactPoint = collision.contactCount > 0
                    ? contact.point
                    : 0.5f * (_rigidbody.worldCenterOfMass + other._rigidbody.worldCenterOfMass),
                ContactNormal = collision.contactCount > 0 ? contact.normal : Vector3.zero,
                SelfVelocity = selfVelocity,
                OtherVelocity = otherVelocity,
                RelativeClosingSpeed = relativeClosingSpeed
            };

            return snapshot.SelfKey != 0 && snapshot.OtherKey != 0;
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

        private bool TryApplyPredictedImpact(Collision collision, bool isEnter)
        {
            if (Runner == null || !Runner.IsClient || HasStateAuthority || Object == null || !Object.IsInSimulation)
            {
                return false;
            }

            if (!HasInputAuthority)
            {
                return false;
            }

            if (!isEnter)
            {
                return false;
            }

            if (_rigidbody == null || _sphereCollider == null)
            {
                CacheComponents();
            }

            if (_rigidbody == null || _rigidbody.isKinematic)
            {
                return false;
            }

            int currentTick = Runner.Tick.Raw;
            EnsurePredictedContext();

            if (!TryBuildContactSnapshot(collision, currentTick, out ContactSnapshot snapshot))
            {
                return false;
            }

            bool selfIsFirst = snapshot.SelfKey <= snapshot.OtherKey;
            SumoCollisionController first = selfIsFirst ? this : snapshot.Other;
            SumoCollisionController second = selfIsFirst ? snapshot.Other : this;
            int firstKey = selfIsFirst ? snapshot.SelfKey : snapshot.OtherKey;
            int secondKey = selfIsFirst ? snapshot.OtherKey : snapshot.SelfKey;
            long pairKey = BuildPairKey(firstKey, secondKey);

            PredictedPairLastContactTick[pairKey] = currentTick;

            bool hasPreviousPrediction = PredictedPairLastImpactTick.TryGetValue(pairKey, out int lastPredictedTick);

            if (hasPreviousPrediction && currentTick == lastPredictedTick)
            {
                return false;
            }

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

            if (!CanPredictController(first) || !CanPredictController(second))
            {
                return false;
            }

            Vector3 firstToSecond = ResolveDirection(
                first._rigidbody.worldCenterOfMass,
                second._rigidbody.worldCenterOfMass,
                snapshot.ContactNormal);

            Vector3 firstVelocity = selfIsFirst ? snapshot.SelfVelocity : snapshot.OtherVelocity;
            Vector3 secondVelocity = selfIsFirst ? snapshot.OtherVelocity : snapshot.SelfVelocity;

            float firstApproachSpeed = GetApproachSpeed(first, firstVelocity, firstToSecond);
            float secondApproachSpeed = GetApproachSpeed(second, secondVelocity, -firstToSecond);

            SumoAttackerDecision attackerDecision = SumoImpactResolver.ResolveAttacker(
                firstApproachSpeed,
                secondApproachSpeed,
                GetPairTieSpeedEpsilon(first, second),
                false,
                false,
                GetPairResolveTieByLowerKey(first, second),
                firstKey,
                secondKey);

            if (!attackerDecision.HasAttacker)
            {
                return false;
            }

            SumoCollisionController attacker = attackerDecision.Role == SumoAttackerRole.First ? first : second;
            SumoCollisionController victim = attacker == first ? second : first;
            if (!CanPredictController(attacker) || !CanPredictController(victim))
            {
                return false;
            }

            Vector3 attackerVelocity = attacker._rigidbody.linearVelocity;
            Vector3 victimVelocity = victim._rigidbody.linearVelocity;
            Vector3 attackerToVictim = ResolveDirection(
                attacker._rigidbody.worldCenterOfMass,
                victim._rigidbody.worldCenterOfMass,
                snapshot.ContactNormal);

            Vector3 attackDirection = ResolveAttackDirection(attacker, attackerVelocity, attackerToVictim);
            float directionDot = Mathf.Clamp01(Vector3.Dot(attackDirection, attackerToVictim));

            float entryForwardSpeed = attacker == first ? firstApproachSpeed : secondApproachSpeed;
            float currentForwardSpeed = Mathf.Max(0f, Vector3.Dot(attackerVelocity, attackerToVictim));
            float intentForwardSpeed = GetIntentApproachSpeed(attacker, attackerToVictim, attackerVelocity);
            float relativeClosingSpeed = Mathf.Max(snapshot.RelativeClosingSpeed, Mathf.Max(0f, Vector3.Dot(attackerVelocity - victimVelocity, attackerToVictim)));

            float attackerForwardSpeed = Mathf.Max(entryForwardSpeed, Mathf.Max(currentForwardSpeed, intentForwardSpeed));
            attackerForwardSpeed = Mathf.Max(attackerForwardSpeed, relativeClosingSpeed);

            float minImpactSpeed = attacker.physicsConfig != null ? attacker.physicsConfig.MinImpactSpeed : 0f;
            if (attackerForwardSpeed < minImpactSpeed)
            {
                return false;
            }

            float configuredDashMultiplier = attacker.physicsConfig != null
                ? attacker.physicsConfig.DashImpactMultiplier
                : 1f;

            float dashMultiplier = attacker.ballController != null
                ? attacker.ballController.GetDashImpactMultiplier(configuredDashMultiplier)
                : Mathf.Max(1f, configuredDashMultiplier);

            SumoInitialImpactResult impactResult = SumoImpactResolver.ComputeInitialImpact(
                attacker.physicsConfig,
                attackerForwardSpeed,
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

            ApplyPredictedImpulse(victim, impulseDirection * impactResult.VictimImpulse * PredictedImpactScale);
            ApplyPredictedImpulse(attacker, -impulseDirection * impactResult.AttackerRecoilImpulse * PredictedImpactScale);

            PredictedPairLastImpactTick[pairKey] = currentTick;
            return true;
        }

        private static bool CanPredictController(SumoCollisionController controller)
        {
            return controller != null
                && controller._rigidbody != null
                && !controller._rigidbody.isKinematic
                && controller.Runner != null
                && controller.Runner.IsClient
                && !controller.HasStateAuthority
                && controller.Object != null
                && controller.Object.IsInSimulation;
        }

        private static void ApplyPredictedImpulse(SumoCollisionController controller, Vector3 impulse)
        {
            if (!CanPredictController(controller))
            {
                return;
            }

            if (impulse.sqrMagnitude <= 0.0000001f)
            {
                return;
            }

            controller._rigidbody.AddForce(impulse, ForceMode.Impulse);
        }

        private void ProcessOwnedPairStates(int currentTick)
        {
            int selfKey = GetControllerKey(this);
            if (selfKey == 0 || PairStates.Count == 0)
            {
                return;
            }

            PairKeysBuffer.Clear();

            foreach (KeyValuePair<long, SumoRamState> pair in PairStates)
            {
                if (pair.Value.OwnerKey == selfKey)
                {
                    PairKeysBuffer.Add(pair.Key);
                }
            }

            for (int i = 0; i < PairKeysBuffer.Count; i++)
            {
                long pairKey = PairKeysBuffer[i];
                if (!PairStates.TryGetValue(pairKey, out SumoRamState pairState))
                {
                    continue;
                }

                if (!TryResolvePairControllers(ref pairState, out SumoCollisionController first, out SumoCollisionController second))
                {
                    PairStates.Remove(pairKey);
                    continue;
                }

                int contactBreakTicks = GetPairContactBreakGraceTicks(first, second);
                bool hasContact = IsContactActive(pairState, currentTick, contactBreakTicks);
                if (hasContact && RequiresPhysicalContact(pairState.State))
                {
                    float separation = ComputeEdgeSeparation(first, second);
                    if (separation > ActivePhaseContactSeparationEpsilon)
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
                        ProcessNoneState(ref pairState, currentTick, hasContact, first, second);
                        break;

                    case SumoPairState.InitialImpact:
                        ProcessInitialImpactState(ref pairState, currentTick, hasContact);
                        break;

                    case SumoPairState.Ramming:
                        ProcessRammingState(ref pairState, currentTick, hasContact);
                        break;

                    case SumoPairState.RamDepleted:
                        ProcessRamDepletedState(ref pairState, currentTick, hasContact, first, second);
                        break;

                    case SumoPairState.ReengageReady:
                        ProcessReengageReadyState(ref pairState, currentTick, hasContact, first, second);
                        break;
                }

                if (ShouldRemoveState(pairState, currentTick, contactBreakTicks))
                {
                    PairStates.Remove(pairKey);
                }
                else
                {
                    PairStates[pairKey] = pairState;
                }
            }

            PairKeysBuffer.Clear();
        }

        private void ProcessNoneState(
            ref SumoRamState pairState,
            int currentTick,
            bool hasContact,
            SumoCollisionController first,
            SumoCollisionController second)
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

            bool startedImpact = TryStartInitialImpact(ref pairState, currentTick, first, second, false);
            ConsumePendingEnter(ref pairState);

            if (!startedImpact)
            {
                pairState.State = SumoPairState.None;
                ClearAttacker(ref pairState);
            }
        }

        private void ProcessInitialImpactState(ref SumoRamState pairState, int currentTick, bool hasContact)
        {
            if (!TryGetLiveAttackerVictim(ref pairState, out SumoCollisionController attacker, out SumoCollisionController victim))
            {
                pairState.State = SumoPairState.None;
                ClearAttacker(ref pairState);
                return;
            }

            ApplyInitialImpactBurstStep(ref pairState, attacker, victim, hasContact);

            float stopThreshold = GetRamStopThreshold(attacker);
            if (pairState.RamEnergy <= stopThreshold)
            {
                SetRamDepleted(ref pairState, hasContact, currentTick);
                return;
            }

            pairState.RamContactBlend = Mathf.Max(pairState.RamContactBlend, RamContactStartBlend);
            pairState.State = SumoPairState.Ramming;
        }

        private void ProcessRammingState(ref SumoRamState pairState, int currentTick, bool hasContact)
        {
            if (!TryGetLiveAttackerVictim(ref pairState, out SumoCollisionController attacker, out SumoCollisionController victim))
            {
                pairState.State = SumoPairState.None;
                ClearAttacker(ref pairState);
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
            ApplyInitialImpactBurstStep(ref pairState, attacker, victim, hasContact);

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
                ApplyAcceleration(victim, liveDirection * ramTick.VictimAcceleration);
                ApplyAcceleration(attacker, -liveDirection * ramTick.AttackerAcceleration);
            }

            pairState.RamEnergy = Mathf.Max(0f, pairState.RamEnergy - ramTick.EnergyDecay);

            bool outOfEnergy = pairState.RamEnergy <= GetRamStopThreshold(attacker);
            if (ramTick.ShouldStop || outOfEnergy)
            {
                SetRamDepleted(ref pairState, true, currentTick);

                if (logStateMachine || attacker.logStateMachine || victim.logStateMachine)
                {
                    Debug.Log(
                        $"SumoCollisionController: ram stop {attacker.name} -> {victim.name}; forceScale={ramTick.RamForceScale:0.00}; pressure={attackerForwardSpeed:0.00}; dot={directionDot:0.00}; tick={currentTick}");
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
            SumoCollisionController second)
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

            bool startedImpact = TryStartInitialImpact(ref pairState, currentTick, first, second, true);

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
            bool requireReengageSpeed)
        {
            if (currentTick == pairState.LastImpactTick)
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

            SumoAttackerDecision attackerDecision = SumoImpactResolver.ResolveAttacker(
                firstApproachSpeed,
                secondApproachSpeed,
                GetPairTieSpeedEpsilon(first, second),
                hasExistingOwner,
                existingOwnerIsFirst,
                GetPairResolveTieByLowerKey(first, second),
                firstKey,
                secondKey);

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
            attackerForwardSpeed = Mathf.Max(attackerForwardSpeed, relativeClosingSpeed);
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

            float configuredDashMultiplier = attacker.physicsConfig != null
                ? attacker.physicsConfig.DashImpactMultiplier
                : 1f;

            float dashMultiplier = attacker.ballController != null
                ? attacker.ballController.GetDashImpactMultiplier(configuredDashMultiplier)
                : Mathf.Max(1f, configuredDashMultiplier);

            SumoInitialImpactResult impactResult = SumoImpactResolver.ComputeInitialImpact(
                attacker.physicsConfig,
                attackerForwardSpeed,
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
            ApplyInitialImpactBurstStep(ref pairState, attacker, victim, true);

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

            attacker.ImpactApplied?.Invoke(impactData);
            victim.ImpactApplied?.Invoke(impactData);

            if (logImpacts || attacker.logImpacts || victim.logImpacts)
            {
                Debug.Log(
                    $"SumoCollisionController: initial-impact {attacker.name} -> {victim.name}; impulse={impactResult.VictimImpulse:0.00}; speed={attackerForwardSpeed:0.00}; closing={relativeClosingSpeed:0.00}; dot={directionDot:0.00}; ramEnergy={pairState.RamEnergy:0.00}; tie={attackerDecision.TieResolvedBy}; tick={currentTick}");
            }

            return true;
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

        private bool ApplyInitialImpactBurstStep(
            ref SumoRamState pairState,
            SumoCollisionController attacker,
            SumoCollisionController victim,
            bool hasContact)
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

            float prevIntegral = EvaluateImpactBurstIntegral(prev01);
            float nextIntegral = EvaluateImpactBurstIntegral(next01);
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

            ApplyAcceleration(victim, direction * (victimDeltaVStep / dt));
            ApplyAcceleration(attacker, -direction * (attackerDeltaVStep / dt));

            pairState.InitialImpactElapsed = nextElapsed;
            return nextElapsed >= duration - 0.0001f;
        }

        private static float EvaluateImpactBurstIntegral(float normalizedTime)
        {
            float u = Mathf.Clamp01(normalizedTime);
            const float baseWeight = 0.42f;
            const float bellWeight = 1f - baseWeight;
            float bellIntegral = 2f * u * u - (4f / 3f) * u * u * u;
            float mixed = baseWeight * u + bellWeight * bellIntegral;
            const float normalization = baseWeight + bellWeight * (2f / 3f);
            return mixed / normalization;
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
            pairState.OwnerKey = Mathf.Min(pairState.FirstRef, pairState.SecondRef);
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
                ReengageReadyTick = 0
            };
        }

        private bool CanProcessThisTick()
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

        private bool CanCaptureContactThisTick()
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

            if (runnerChanged || tickRegressed)
            {
                PredictedPairLastImpactTick.Clear();
                PredictedPairLastContactTick.Clear();
            }

            _predictedCacheRunnerId = runnerId;
            _predictedCacheLastTick = currentTick;
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
                PairKeysBuffer.Clear();
                PairPruneBuffer.Clear();
            }

            _pairCacheRunnerId = runnerId;
            _pairCacheLastTick = currentTick;
        }

        private void PrunePairStates(int currentTick)
        {
            if (PairStates.Count <= 96 || (currentTick & 63) != 0)
            {
                return;
            }

            PairPruneBuffer.Clear();

            foreach (KeyValuePair<long, SumoRamState> pair in PairStates)
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
                PairStates.Remove(PairPruneBuffer[i]);
            }

            PairPruneBuffer.Clear();
        }

        private static void RemoveStatesForController(int controllerKey)
        {
            if (controllerKey == 0 || PairStates.Count == 0)
            {
                return;
            }

            PairPruneBuffer.Clear();

            foreach (KeyValuePair<long, SumoRamState> pair in PairStates)
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
                PairStates.Remove(PairPruneBuffer[i]);
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
            return Mathf.Clamp(configured, 1, 2);
        }

        private static int GetContactBreakGraceTicks(SumoCollisionController source)
        {
            return source != null && source.physicsConfig != null
                ? source.physicsConfig.ContactBreakGraceTicks
                : FallbackContactBreakGraceTicks;
        }

        private static int GetPairReengageBreakTicks(SumoCollisionController a, SumoCollisionController b)
        {
            int fromA = a != null && a.physicsConfig != null ? a.physicsConfig.ReengageBreakTicks : FallbackReengageBreakTicks;
            int fromB = b != null && b.physicsConfig != null ? b.physicsConfig.ReengageBreakTicks : FallbackReengageBreakTicks;
            return Mathf.Max(FallbackReengageBreakTicks, Mathf.Max(fromA, fromB));
        }

        private static float GetPairReengageDistance(SumoCollisionController a, SumoCollisionController b)
        {
            float fromA = a != null && a.physicsConfig != null ? a.physicsConfig.ReengageDistance : FallbackReengageDistance;
            float fromB = b != null && b.physicsConfig != null ? b.physicsConfig.ReengageDistance : FallbackReengageDistance;
            return Mathf.Max(FallbackReengageDistance, Mathf.Max(fromA, fromB));
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
                ? source.ballController.CurrentMoveDirection
                : Vector3.zero;

            if (intentDirection.sqrMagnitude < 0.0001f)
            {
                intentDirection = GetHorizontalDirection(fallbackVelocity);
            }

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

        private static float GetPlanarSpeedEstimate(SumoCollisionController source, Vector3 fallbackVelocity)
        {
            Vector3 horizontalVelocity = new Vector3(fallbackVelocity.x, 0f, fallbackVelocity.z);
            float speed = horizontalVelocity.magnitude;

            if (source == null || source.ballController == null)
            {
                return speed;
            }

            float controllerSpeedEstimate = source.ballController.CurrentSpeed01 * Mathf.Max(0.01f, source.ballController.MaxSpeed);
            return Mathf.Max(speed, controllerSpeedEstimate);
        }

        private static float ComputeDirectionDot(
            SumoCollisionController attacker,
            Vector3 attackerVelocity,
            Vector3 targetDirection)
        {
            Vector3 moveDirection = attacker != null && attacker.ballController != null
                ? attacker.ballController.CurrentMoveDirection
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
                ? attacker.ballController.CurrentMoveDirection
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

        private static void ApplyImpulse(SumoCollisionController controller, Vector3 impulse)
        {
            if (controller == null
                || controller._rigidbody == null
                || controller._rigidbody.isKinematic
                || controller.Runner == null
                || controller.Object == null
                || !controller.Object.IsInSimulation)
            {
                return;
            }

            if (impulse.sqrMagnitude <= 0.0000001f)
            {
                return;
            }

            if (!controller.HasStateAuthority)
            {
                return;
            }

            controller._rigidbody.AddForce(impulse, ForceMode.Impulse);
        }

        private static void ApplyAcceleration(SumoCollisionController controller, Vector3 acceleration)
        {
            if (controller == null
                || controller._rigidbody == null
                || controller._rigidbody.isKinematic
                || controller.Runner == null
                || controller.Object == null
                || !controller.Object.IsInSimulation)
            {
                return;
            }

            if (acceleration.sqrMagnitude <= 0.0000001f)
            {
                return;
            }

            if (!controller.HasStateAuthority)
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
