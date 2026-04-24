using UnityEngine;
using UnityEngine.Serialization;

namespace Sumo
{
    [DisallowMultipleComponent]
    public sealed class SumoBallPhysicsConfig : MonoBehaviour
    {
        [Header("Rigidbody")]
        [SerializeField] private float mass = 1.1f;
        [SerializeField] private float linearDamping = 0.22f;
        [SerializeField] private float angularDamping = 0.14f;
        [SerializeField] private RigidbodyInterpolation interpolation = RigidbodyInterpolation.None;
        [SerializeField] private CollisionDetectionMode collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
        [SerializeField] private bool freezeTiltRotation = true;
        [SerializeField] private int solverIterations = 10;
        [SerializeField] private int solverVelocityIterations = 4;
        [SerializeField] private float maxDepenetrationVelocity = 12f;

        [Header("Collider")]
        [SerializeField] private PhysicsMaterial physicsMaterial;
        [SerializeField] private bool forceNonTriggerCollider = true;
        [SerializeField] private float colliderContactOffset = 0.015f;

        [Header("Initial Impact")]
        [FormerlySerializedAs("minPushSpeed")]
        [SerializeField] private float minImpactSpeed = 2.2f;
        [SerializeField] private float impactActivationMinSpeed = 0.25f;
        [FormerlySerializedAs("maxEffectivePushSpeed")]
        [SerializeField] private float maxImpactSpeed = 10f;
        [FormerlySerializedAs("basePushImpulse")]
        [SerializeField] private float baseImpactImpulse = 42f;
        [FormerlySerializedAs("maxPushImpulse")]
        [SerializeField] private float maxImpactImpulse = 92f;
        [FormerlySerializedAs("initialImpactSpeedExponent")]
        [SerializeField] private float impactSpeedExponent = 1.85f;
        [FormerlySerializedAs("initialImpactTopSpeedBonus")]
        [SerializeField] private float impactTopSpeedBonus = 1.45f;
        [FormerlySerializedAs("initialImpactAngleExponent")]
        [SerializeField] private float impactAngleExponent = 1.15f;
        [FormerlySerializedAs("headOnMultiplier")]
        [SerializeField] private float headOnImpactMultiplier = 2.25f;
        [FormerlySerializedAs("glancingHitMultiplier")]
        [SerializeField] private float glancingImpactMultiplier = 0.3f;
        [SerializeField] private float relativeClosingBonus = 0.28f;
        [FormerlySerializedAs("initialImpactHighSpeedFloorScale")]
        [SerializeField] private float highSpeedReadableFloor = 0.62f;
        [SerializeField] private float firstImpactArcadeThreshold01 = 0.32f;
        [SerializeField] private float firstImpactArcadeBoost = 5.6f;
        [SerializeField] private float firstImpactArcadeCapBoost = 4.2f;
        [FormerlySerializedAs("initialImpactAttackerRecoilScale")]
        [SerializeField] private float impactAttackerRecoilScale = 0.20f;
        [FormerlySerializedAs("verticalImpulseMultiplier")]
        [SerializeField] private float impactVerticalLift = 0.02f;
        [FormerlySerializedAs("dashImpactMultiplier")]
        [SerializeField] private float dashImpactMultiplier = 1.6f;
        [SerializeField] private float impactBurstDuration = 0.11f;
        [SerializeField] private float firstImpactBurstFrontload = 0.85f;
        [SerializeField] private float firstImpactKickImpulseShare = 0.97f;

        [Header("Impact Arbitration")]
        [SerializeField] private float attackerTieSpeedEpsilon = 0.15f;
        [SerializeField] private bool resolveTieByLowerKey = true;
        [SerializeField] private int contactBreakGraceTicks = 6;
        [SerializeField] private float playerContactEnterPadding = 0.004f;
        [SerializeField] private float playerContactExitPadding = 0.02f;
        [SerializeField] private float playerContactPenetrationSlop = 0.01f;
        [SerializeField] private float playerContactPositionCorrection = 0.85f;
        [SerializeField] private float playerContactVelocityDamping = 1f;

        [Header("Ramming")]
        [SerializeField] private float minRamStartSpeed = 2.4f;
        [SerializeField] private float minRamPressureSpeed = 1.6f;
        [SerializeField] private int maxRamDurationTicks = 34;
        [FormerlySerializedAs("ramBaseImpulsePerSecond")]
        [SerializeField] private float ramBaseAcceleration = 14f;
        [FormerlySerializedAs("ramMaxImpulsePerTick")]
        [SerializeField] private float ramMaxAcceleration = 24f;
        [FormerlySerializedAs("ramAttackerRecoilScale")]
        [SerializeField] private float ramAttackerDragScale = 0.24f;
        [FormerlySerializedAs("ramGlancingImpulseScale")]
        [SerializeField] private float ramGlancingForceScale = 0.42f;
        [SerializeField] private float ramMinDirectionDot = 0.2f;
        [FormerlySerializedAs("ramBudgetFromImpactScale")]
        [SerializeField] private float ramEnergyFromImpact = 0.2f;
        [FormerlySerializedAs("ramBudgetFromSpeedScale")]
        [SerializeField] private float ramEnergyFromSpeed = 1.15f;
        [FormerlySerializedAs("ramBudgetDashBonusScale")]
        [SerializeField] private float ramEnergyFromDash = 0.4f;
        [FormerlySerializedAs("minRamBudget")]
        [SerializeField] private float ramMinEnergy = 0.5f;
        [FormerlySerializedAs("maxRamBudget")]
        [SerializeField] private float ramMaxEnergy = 7.5f;
        [FormerlySerializedAs("ramLowBudgetImpulseScale")]
        [SerializeField] private float ramMinForceScale = 0.18f;
        [SerializeField] private float ramMaxForceScale = 1.1f;
        [SerializeField] private float ramForceExponent = 0.7f;
        [SerializeField] private float ramBaseDecayPerSecond = 1.5f;
        [SerializeField] private float ramPressureDecayPerSecond = 1.25f;
        [FormerlySerializedAs("ramPoorAngleDecayPerSecond")]
        [SerializeField] private float ramAngleDecayPerSecond = 1.4f;
        [SerializeField] private float ramNoPressureDecayPerSecond = 4.8f;
        [FormerlySerializedAs("ramBudgetCostPerImpulse")]
        [SerializeField] private float ramAccelerationEnergyCost = 0.14f;
        [FormerlySerializedAs("ramStopBudgetThreshold")]
        [SerializeField] private float ramStopEnergyThreshold = 0.08f;

        [Header("Re-Engage Gate")]
        [SerializeField] private int reengageBreakTicks = 6;
        [SerializeField] private float reengageDistance = 0.22f;
        [SerializeField] private float reengageSpeedThreshold = 4.8f;

        [Header("Victim Local Catchup")]
        [SerializeField] private float victimLocalPushPrediction = 1.02f;
        [SerializeField] private float victimLocalPushMaxDeltaVPerTick = 0.22f;
        [SerializeField] private float victimLocalPushBrakeDeltaVPerTick = 0.12f;
        [SerializeField] private float victimLocalImpactCatchupAcceleration = 72f;
        [SerializeField] private float victimLocalImpactMaxDeltaVPerTick = 0.32f;
        [SerializeField] private float victimLocalRamCatchupAcceleration = 30f;

        [Header("Victim Anticipation")]
        [SerializeField] private float victimAnticipationMinClosingSpeed = 0.95f;
        [SerializeField] private float victimAnticipationMinDirectionDot = 0.20f;
        [SerializeField] private int victimAnticipationImpactTicks = 2;
        [SerializeField] private int victimAnticipationTtlTicks = 8;
        [SerializeField] private int victimAnticipationContactLossTicks = 2;
        [SerializeField] private int victimAnticipationHandoffTicks = 3;
        [SerializeField] private float victimAnticipationTargetSpeedScale = 0.98f;
        [SerializeField] private float victimAnticipationImpactMaxDeltaVPerTick = 0.28f;
        [SerializeField] private float victimAnticipationRamMaxDeltaVPerTick = 0.10f;

        [Header("Anti-Bulldoze")]
        [SerializeField] private bool limitAccelerationIntoPlayers = true;
        [SerializeField] private float antiBulldozeSpeedThreshold = 3.8f;
        [SerializeField] private float intoPlayerAccelerationScale = 0.12f;

        public float Mass => mass;
        public float LinearDamping => linearDamping;
        public float AngularDamping => angularDamping;
        public RigidbodyInterpolation Interpolation => interpolation;
        public CollisionDetectionMode CollisionDetectionMode => collisionDetectionMode;
        public bool FreezeTiltRotation => freezeTiltRotation;
        public int SolverIterations => solverIterations;
        public int SolverVelocityIterations => solverVelocityIterations;
        public float MaxDepenetrationVelocity => maxDepenetrationVelocity;
        public PhysicsMaterial PhysicsMaterial => physicsMaterial;
        public bool ForceNonTriggerCollider => forceNonTriggerCollider;
        public float ColliderContactOffset => colliderContactOffset;

        public float MinImpactSpeed => minImpactSpeed;
        public float ImpactActivationMinSpeed => impactActivationMinSpeed;
        public float MaxImpactSpeed => maxImpactSpeed;
        public float BaseImpactImpulse => baseImpactImpulse;
        public float MaxImpactImpulse => maxImpactImpulse;
        public float ImpactSpeedExponent => impactSpeedExponent;
        public float ImpactTopSpeedBonus => impactTopSpeedBonus;
        public float ImpactAngleExponent => impactAngleExponent;
        public float HeadOnImpactMultiplier => headOnImpactMultiplier;
        public float GlancingImpactMultiplier => glancingImpactMultiplier;
        public float RelativeClosingBonus => relativeClosingBonus;
        public float HighSpeedReadableFloor => highSpeedReadableFloor;
        public float FirstImpactArcadeThreshold01 => firstImpactArcadeThreshold01;
        public float FirstImpactArcadeBoost => firstImpactArcadeBoost;
        public float FirstImpactArcadeCapBoost => firstImpactArcadeCapBoost;
        public float ImpactAttackerRecoilScale => impactAttackerRecoilScale;
        public float ImpactVerticalLift => impactVerticalLift;
        public float DashImpactMultiplier => dashImpactMultiplier;
        public float ImpactBurstDuration => impactBurstDuration;
        public float FirstImpactBurstFrontload => firstImpactBurstFrontload;
        public float FirstImpactKickImpulseShare => firstImpactKickImpulseShare;
        public float AttackerTieSpeedEpsilon => attackerTieSpeedEpsilon;
        public bool ResolveTieByLowerKey => resolveTieByLowerKey;
        public int ContactBreakGraceTicks => contactBreakGraceTicks;
        public float PlayerContactEnterPadding => playerContactEnterPadding;
        public float PlayerContactExitPadding => playerContactExitPadding;
        public float PlayerContactPenetrationSlop => playerContactPenetrationSlop;
        public float PlayerContactPositionCorrection => playerContactPositionCorrection;
        public float PlayerContactVelocityDamping => playerContactVelocityDamping;

        public float MinRamStartSpeed => minRamStartSpeed;
        public float MinRamPressureSpeed => minRamPressureSpeed;
        public int MaxRamDurationTicks => maxRamDurationTicks;
        public float RamBaseAcceleration => ramBaseAcceleration;
        public float RamMaxAcceleration => ramMaxAcceleration;
        public float RamAttackerDragScale => ramAttackerDragScale;
        public float RamGlancingForceScale => ramGlancingForceScale;
        public float RamMinDirectionDot => ramMinDirectionDot;
        public float RamEnergyFromImpact => ramEnergyFromImpact;
        public float RamEnergyFromSpeed => ramEnergyFromSpeed;
        public float RamEnergyFromDash => ramEnergyFromDash;
        public float RamMinEnergy => ramMinEnergy;
        public float RamMaxEnergy => ramMaxEnergy;
        public float RamMinForceScale => ramMinForceScale;
        public float RamMaxForceScale => ramMaxForceScale;
        public float RamForceExponent => ramForceExponent;
        public float RamBaseDecayPerSecond => ramBaseDecayPerSecond;
        public float RamPressureDecayPerSecond => ramPressureDecayPerSecond;
        public float RamAngleDecayPerSecond => ramAngleDecayPerSecond;
        public float RamNoPressureDecayPerSecond => ramNoPressureDecayPerSecond;
        public float RamAccelerationEnergyCost => ramAccelerationEnergyCost;
        public float RamStopEnergyThreshold => ramStopEnergyThreshold;

        public int ReengageBreakTicks => reengageBreakTicks;
        public float ReengageDistance => reengageDistance;
        public float ReengageSpeedThreshold => reengageSpeedThreshold;

        public float VictimLocalPushPrediction => victimLocalPushPrediction;
        public float VictimLocalPushMaxDeltaVPerTick => victimLocalPushMaxDeltaVPerTick;
        public float VictimLocalPushBrakeDeltaVPerTick => victimLocalPushBrakeDeltaVPerTick;
        public float VictimLocalImpactCatchupAcceleration => victimLocalImpactCatchupAcceleration;
        public float VictimLocalImpactMaxDeltaVPerTick => victimLocalImpactMaxDeltaVPerTick;
        public float VictimLocalRamCatchupAcceleration => victimLocalRamCatchupAcceleration;
        public float VictimAnticipationMinClosingSpeed => victimAnticipationMinClosingSpeed;
        public float VictimAnticipationMinDirectionDot => victimAnticipationMinDirectionDot;
        public int VictimAnticipationImpactTicks => victimAnticipationImpactTicks;
        public int VictimAnticipationTtlTicks => victimAnticipationTtlTicks;
        public int VictimAnticipationContactLossTicks => victimAnticipationContactLossTicks;
        public int VictimAnticipationHandoffTicks => victimAnticipationHandoffTicks;
        public float VictimAnticipationTargetSpeedScale => victimAnticipationTargetSpeedScale;
        public float VictimAnticipationImpactMaxDeltaVPerTick => victimAnticipationImpactMaxDeltaVPerTick;
        public float VictimAnticipationRamMaxDeltaVPerTick => victimAnticipationRamMaxDeltaVPerTick;

        public bool LimitAccelerationIntoPlayers => limitAccelerationIntoPlayers;
        public float AntiBulldozeSpeedThreshold => antiBulldozeSpeedThreshold;
        public float IntoPlayerAccelerationScale => intoPlayerAccelerationScale;

        public float EvaluateImpactSpeed01(float attackerForwardSpeed)
        {
            float lowerBound = Mathf.Clamp(
                impactActivationMinSpeed,
                0f,
                Mathf.Max(0.01f, maxImpactSpeed - 0.01f));
            float top = Mathf.Max(lowerBound + 0.01f, maxImpactSpeed);
            return Mathf.InverseLerp(lowerBound, top, attackerForwardSpeed);
        }

        public void ApplyTo(Rigidbody targetRigidbody, SphereCollider targetCollider)
        {
            if (targetRigidbody != null)
            {
                targetRigidbody.mass = Mathf.Max(0.01f, mass);
                targetRigidbody.linearDamping = Mathf.Max(0f, linearDamping);
                targetRigidbody.angularDamping = Mathf.Max(0f, angularDamping);
                targetRigidbody.interpolation = interpolation;
                targetRigidbody.collisionDetectionMode = collisionDetectionMode;
                targetRigidbody.maxDepenetrationVelocity = Mathf.Max(0.1f, maxDepenetrationVelocity);
                targetRigidbody.solverIterations = Mathf.Max(1, solverIterations);
                targetRigidbody.solverVelocityIterations = Mathf.Max(1, solverVelocityIterations);
                targetRigidbody.constraints = freezeTiltRotation
                    ? RigidbodyConstraints.FreezeRotationX | RigidbodyConstraints.FreezeRotationZ
                    : RigidbodyConstraints.None;
                targetRigidbody.isKinematic = false;
            }

            if (targetCollider != null)
            {
                if (physicsMaterial != null)
                {
                    targetCollider.material = physicsMaterial;
                }

                if (forceNonTriggerCollider)
                {
                    targetCollider.isTrigger = false;
                }

                if (colliderContactOffset > 0f)
                {
                    targetCollider.contactOffset = Mathf.Max(0.001f, colliderContactOffset);
                }
            }
        }

        private void OnValidate()
        {
            mass = Mathf.Max(0.01f, mass);
            linearDamping = Mathf.Max(0f, linearDamping);
            angularDamping = Mathf.Max(0f, angularDamping);
            solverIterations = Mathf.Max(1, solverIterations);
            solverVelocityIterations = Mathf.Max(1, solverVelocityIterations);
            maxDepenetrationVelocity = Mathf.Max(0.1f, maxDepenetrationVelocity);
            colliderContactOffset = Mathf.Max(0f, colliderContactOffset);

            minImpactSpeed = Mathf.Max(0f, minImpactSpeed);
            maxImpactSpeed = Mathf.Max(minImpactSpeed + 0.01f, maxImpactSpeed);
            impactActivationMinSpeed = Mathf.Clamp(impactActivationMinSpeed, 0.05f, maxImpactSpeed - 0.01f);
            baseImpactImpulse = Mathf.Max(0f, baseImpactImpulse);
            maxImpactImpulse = Mathf.Max(baseImpactImpulse + 0.01f, maxImpactImpulse);
            impactSpeedExponent = Mathf.Clamp(impactSpeedExponent, 0.25f, 4f);
            impactTopSpeedBonus = Mathf.Max(0f, impactTopSpeedBonus);
            impactAngleExponent = Mathf.Clamp(impactAngleExponent, 0.25f, 4f);
            headOnImpactMultiplier = Mathf.Max(0f, headOnImpactMultiplier);
            glancingImpactMultiplier = Mathf.Clamp(glancingImpactMultiplier, 0f, headOnImpactMultiplier);
            relativeClosingBonus = Mathf.Max(0f, relativeClosingBonus);
            highSpeedReadableFloor = Mathf.Clamp01(highSpeedReadableFloor);
            firstImpactArcadeThreshold01 = Mathf.Clamp01(firstImpactArcadeThreshold01);
            firstImpactArcadeBoost = Mathf.Max(1f, firstImpactArcadeBoost);
            firstImpactArcadeCapBoost = Mathf.Max(1f, firstImpactArcadeCapBoost);
            impactAttackerRecoilScale = Mathf.Clamp01(impactAttackerRecoilScale);
            impactVerticalLift = Mathf.Max(0f, impactVerticalLift);
            dashImpactMultiplier = Mathf.Max(1f, dashImpactMultiplier);
            impactBurstDuration = Mathf.Clamp(impactBurstDuration, 0.04f, 0.14f);
            firstImpactBurstFrontload = Mathf.Clamp01(firstImpactBurstFrontload);
            firstImpactKickImpulseShare = Mathf.Clamp01(firstImpactKickImpulseShare);

            attackerTieSpeedEpsilon = Mathf.Max(0f, attackerTieSpeedEpsilon);
            contactBreakGraceTicks = Mathf.Clamp(contactBreakGraceTicks, 4, 12);
            playerContactEnterPadding = Mathf.Clamp(playerContactEnterPadding, 0f, 0.05f);
            playerContactExitPadding = Mathf.Clamp(playerContactExitPadding, playerContactEnterPadding, 0.08f);
            playerContactPenetrationSlop = Mathf.Clamp(playerContactPenetrationSlop, 0f, 0.12f);
            playerContactPositionCorrection = Mathf.Clamp(playerContactPositionCorrection, 0f, 1f);
            playerContactVelocityDamping = Mathf.Clamp(playerContactVelocityDamping, 0f, 1.25f);

            minRamStartSpeed = Mathf.Clamp(minRamStartSpeed, minImpactSpeed, maxImpactSpeed);
            minRamPressureSpeed = Mathf.Max(0f, minRamPressureSpeed);
            maxRamDurationTicks = Mathf.Clamp(maxRamDurationTicks, 1, 34);
            ramBaseAcceleration = Mathf.Max(0f, ramBaseAcceleration);
            ramMaxAcceleration = Mathf.Max(ramBaseAcceleration, ramMaxAcceleration);
            ramAttackerDragScale = Mathf.Clamp01(ramAttackerDragScale);
            ramGlancingForceScale = Mathf.Clamp01(ramGlancingForceScale);
            ramMinDirectionDot = Mathf.Clamp01(ramMinDirectionDot);
            ramEnergyFromImpact = Mathf.Max(0f, ramEnergyFromImpact);
            ramEnergyFromSpeed = Mathf.Max(0f, ramEnergyFromSpeed);
            ramEnergyFromDash = Mathf.Max(0f, ramEnergyFromDash);
            ramMinEnergy = Mathf.Max(0f, ramMinEnergy);
            ramMaxEnergy = Mathf.Max(ramMinEnergy + 0.01f, ramMaxEnergy);
            ramMinForceScale = Mathf.Clamp(ramMinForceScale, 0f, 1f);
            ramMaxForceScale = Mathf.Max(1f, ramMaxForceScale);
            ramForceExponent = Mathf.Clamp(ramForceExponent, 0.25f, 3f);
            ramBaseDecayPerSecond = Mathf.Max(0f, ramBaseDecayPerSecond);
            ramPressureDecayPerSecond = Mathf.Max(0f, ramPressureDecayPerSecond);
            ramAngleDecayPerSecond = Mathf.Max(0f, ramAngleDecayPerSecond);
            ramNoPressureDecayPerSecond = Mathf.Max(0f, ramNoPressureDecayPerSecond);
            ramAccelerationEnergyCost = Mathf.Max(0f, ramAccelerationEnergyCost);
            ramStopEnergyThreshold = Mathf.Max(0f, ramStopEnergyThreshold);

            reengageBreakTicks = Mathf.Clamp(reengageBreakTicks, 4, 16);
            reengageDistance = Mathf.Clamp(reengageDistance, 0.16f, 0.4f);
            reengageSpeedThreshold = Mathf.Max(0f, reengageSpeedThreshold);

            victimLocalPushPrediction = Mathf.Clamp(victimLocalPushPrediction, 0f, 1.25f);
            victimLocalPushMaxDeltaVPerTick = Mathf.Clamp(victimLocalPushMaxDeltaVPerTick, 0.005f, 0.3f);
            victimLocalPushBrakeDeltaVPerTick = Mathf.Clamp(victimLocalPushBrakeDeltaVPerTick, 0.005f, 0.2f);
            victimLocalImpactCatchupAcceleration = Mathf.Clamp(victimLocalImpactCatchupAcceleration, 1f, 90f);
            victimLocalImpactMaxDeltaVPerTick = Mathf.Clamp(victimLocalImpactMaxDeltaVPerTick, 0.01f, 0.35f);
            victimLocalRamCatchupAcceleration = Mathf.Clamp(victimLocalRamCatchupAcceleration, 1f, 60f);
            victimAnticipationMinClosingSpeed = Mathf.Clamp(victimAnticipationMinClosingSpeed, 0.05f, 20f);
            victimAnticipationMinDirectionDot = Mathf.Clamp01(victimAnticipationMinDirectionDot);
            victimAnticipationImpactTicks = Mathf.Clamp(victimAnticipationImpactTicks, 1, 6);
            victimAnticipationTtlTicks = Mathf.Clamp(victimAnticipationTtlTicks, 2, 24);
            victimAnticipationContactLossTicks = Mathf.Clamp(victimAnticipationContactLossTicks, 1, 8);
            victimAnticipationHandoffTicks = Mathf.Clamp(victimAnticipationHandoffTicks, 1, 8);
            victimAnticipationTargetSpeedScale = Mathf.Clamp(victimAnticipationTargetSpeedScale, 0.5f, 1.2f);
            victimAnticipationImpactMaxDeltaVPerTick = Mathf.Clamp(victimAnticipationImpactMaxDeltaVPerTick, 0.01f, 0.4f);
            victimAnticipationRamMaxDeltaVPerTick = Mathf.Clamp(victimAnticipationRamMaxDeltaVPerTick, 0.01f, 0.4f);

            antiBulldozeSpeedThreshold = Mathf.Max(0f, antiBulldozeSpeedThreshold);
            intoPlayerAccelerationScale = Mathf.Clamp01(intoPlayerAccelerationScale);
        }
    }
}
