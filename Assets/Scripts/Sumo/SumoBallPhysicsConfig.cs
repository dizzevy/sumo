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
        [FormerlySerializedAs("maxEffectivePushSpeed")]
        [SerializeField] private float maxImpactSpeed = 10f;
        [FormerlySerializedAs("basePushImpulse")]
        [SerializeField] private float baseImpactImpulse = 18f;
        [FormerlySerializedAs("maxPushImpulse")]
        [SerializeField] private float maxImpactImpulse = 30f;
        [FormerlySerializedAs("initialImpactSpeedExponent")]
        [SerializeField] private float impactSpeedExponent = 1.85f;
        [FormerlySerializedAs("initialImpactTopSpeedBonus")]
        [SerializeField] private float impactTopSpeedBonus = 0.85f;
        [FormerlySerializedAs("initialImpactAngleExponent")]
        [SerializeField] private float impactAngleExponent = 1.15f;
        [FormerlySerializedAs("headOnMultiplier")]
        [SerializeField] private float headOnImpactMultiplier = 1.55f;
        [FormerlySerializedAs("glancingHitMultiplier")]
        [SerializeField] private float glancingImpactMultiplier = 0.55f;
        [SerializeField] private float relativeClosingBonus = 0.22f;
        [FormerlySerializedAs("initialImpactHighSpeedFloorScale")]
        [SerializeField] private float highSpeedReadableFloor = 0.58f;
        [FormerlySerializedAs("initialImpactAttackerRecoilScale")]
        [SerializeField] private float impactAttackerRecoilScale = 0.17f;
        [FormerlySerializedAs("verticalImpulseMultiplier")]
        [SerializeField] private float impactVerticalLift = 0.02f;
        [FormerlySerializedAs("dashImpactMultiplier")]
        [SerializeField] private float dashImpactMultiplier = 1.4f;

        [Header("Impact Arbitration")]
        [SerializeField] private float attackerTieSpeedEpsilon = 0.15f;
        [SerializeField] private bool resolveTieByLowerKey = true;
        [SerializeField] private int contactBreakGraceTicks = 2;

        [Header("Ramming")]
        [SerializeField] private float minRamStartSpeed = 2.4f;
        [SerializeField] private float minRamPressureSpeed = 1.6f;
        [SerializeField] private int maxRamDurationTicks = 48;
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
        [SerializeField] private float ramBaseDecayPerSecond = 1.25f;
        [SerializeField] private float ramPressureDecayPerSecond = 1.1f;
        [FormerlySerializedAs("ramPoorAngleDecayPerSecond")]
        [SerializeField] private float ramAngleDecayPerSecond = 1.4f;
        [SerializeField] private float ramNoPressureDecayPerSecond = 4f;
        [FormerlySerializedAs("ramBudgetCostPerImpulse")]
        [SerializeField] private float ramAccelerationEnergyCost = 0.14f;
        [FormerlySerializedAs("ramStopBudgetThreshold")]
        [SerializeField] private float ramStopEnergyThreshold = 0.08f;

        [Header("Re-Engage Gate")]
        [SerializeField] private int reengageBreakTicks = 3;
        [SerializeField] private float reengageDistance = 0.12f;
        [SerializeField] private float reengageSpeedThreshold = 4.8f;

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
        public float ImpactAttackerRecoilScale => impactAttackerRecoilScale;
        public float ImpactVerticalLift => impactVerticalLift;
        public float DashImpactMultiplier => dashImpactMultiplier;
        public float AttackerTieSpeedEpsilon => attackerTieSpeedEpsilon;
        public bool ResolveTieByLowerKey => resolveTieByLowerKey;
        public int ContactBreakGraceTicks => contactBreakGraceTicks;

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

        public bool LimitAccelerationIntoPlayers => limitAccelerationIntoPlayers;
        public float AntiBulldozeSpeedThreshold => antiBulldozeSpeedThreshold;
        public float IntoPlayerAccelerationScale => intoPlayerAccelerationScale;

        public float EvaluateImpactSpeed01(float attackerForwardSpeed)
        {
            float top = Mathf.Max(minImpactSpeed + 0.01f, maxImpactSpeed);
            return Mathf.InverseLerp(minImpactSpeed, top, attackerForwardSpeed);
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
            baseImpactImpulse = Mathf.Max(0f, baseImpactImpulse);
            maxImpactImpulse = Mathf.Max(baseImpactImpulse + 0.01f, maxImpactImpulse);
            impactSpeedExponent = Mathf.Clamp(impactSpeedExponent, 0.25f, 4f);
            impactTopSpeedBonus = Mathf.Max(0f, impactTopSpeedBonus);
            impactAngleExponent = Mathf.Clamp(impactAngleExponent, 0.25f, 4f);
            headOnImpactMultiplier = Mathf.Max(0f, headOnImpactMultiplier);
            glancingImpactMultiplier = Mathf.Clamp(glancingImpactMultiplier, 0f, headOnImpactMultiplier);
            relativeClosingBonus = Mathf.Max(0f, relativeClosingBonus);
            highSpeedReadableFloor = Mathf.Clamp01(highSpeedReadableFloor);
            impactAttackerRecoilScale = Mathf.Clamp01(impactAttackerRecoilScale);
            impactVerticalLift = Mathf.Max(0f, impactVerticalLift);
            dashImpactMultiplier = Mathf.Max(1f, dashImpactMultiplier);

            attackerTieSpeedEpsilon = Mathf.Max(0f, attackerTieSpeedEpsilon);
            contactBreakGraceTicks = Mathf.Max(1, contactBreakGraceTicks);

            minRamStartSpeed = Mathf.Clamp(minRamStartSpeed, minImpactSpeed, maxImpactSpeed);
            minRamPressureSpeed = Mathf.Max(0f, minRamPressureSpeed);
            maxRamDurationTicks = Mathf.Max(1, maxRamDurationTicks);
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

            reengageBreakTicks = Mathf.Max(1, reengageBreakTicks);
            reengageDistance = Mathf.Max(0f, reengageDistance);
            reengageSpeedThreshold = Mathf.Max(0f, reengageSpeedThreshold);

            antiBulldozeSpeedThreshold = Mathf.Max(0f, antiBulldozeSpeedThreshold);
            intoPlayerAccelerationScale = Mathf.Clamp01(intoPlayerAccelerationScale);
        }
    }
}
