using UnityEngine;

namespace Sumo
{
    public enum SumoAttackerRole : byte
    {
        None = 0,
        First = 1,
        Second = 2,
        Neutral = 3
    }

    public readonly struct SumoAttackerDecision
    {
        public readonly SumoAttackerRole Role;
        public readonly SumoTieResolvedBy TieResolvedBy;

        public SumoAttackerDecision(SumoAttackerRole role, SumoTieResolvedBy tieResolvedBy)
        {
            Role = role;
            TieResolvedBy = tieResolvedBy;
        }

        public bool HasAttacker => Role == SumoAttackerRole.First || Role == SumoAttackerRole.Second;
    }

    public readonly struct SumoInitialImpactResult
    {
        public readonly float SpeedCurve;
        public readonly float AngleScale;
        public readonly float RelativeClosingScale;
        public readonly float DashMultiplier;
        public readonly float VictimImpulse;
        public readonly float AttackerRecoilImpulse;
        public readonly float InitialRamEnergy;
        public readonly bool OpensRamState;

        public SumoInitialImpactResult(
            float speedCurve,
            float angleScale,
            float relativeClosingScale,
            float dashMultiplier,
            float victimImpulse,
            float attackerRecoilImpulse,
            float initialRamEnergy,
            bool opensRamState)
        {
            SpeedCurve = speedCurve;
            AngleScale = angleScale;
            RelativeClosingScale = relativeClosingScale;
            DashMultiplier = dashMultiplier;
            VictimImpulse = victimImpulse;
            AttackerRecoilImpulse = attackerRecoilImpulse;
            InitialRamEnergy = initialRamEnergy;
            OpensRamState = opensRamState;
        }

        public bool HasImpact => VictimImpulse > 0.0001f;
    }

    public readonly struct SumoRamTickResult
    {
        public readonly float VictimAcceleration;
        public readonly float AttackerAcceleration;
        public readonly float EnergyDecay;
        public readonly float RamForceScale;
        public readonly bool ShouldStop;

        public SumoRamTickResult(
            float victimAcceleration,
            float attackerAcceleration,
            float energyDecay,
            float ramForceScale,
            bool shouldStop)
        {
            VictimAcceleration = victimAcceleration;
            AttackerAcceleration = attackerAcceleration;
            EnergyDecay = energyDecay;
            RamForceScale = ramForceScale;
            ShouldStop = shouldStop;
        }

        public bool HasForce => VictimAcceleration > 0.0001f || AttackerAcceleration > 0.0001f;
    }

    public static class SumoImpactResolver
    {
        private const float DefaultMinImpactSpeed = 2.2f;
        private const float DefaultMaxImpactSpeed = 10f;

        public static SumoAttackerDecision ResolveAttacker(
            float firstApproachSpeed,
            float secondApproachSpeed,
            float tieSpeedEpsilon,
            bool hasExistingOwner,
            bool existingOwnerIsFirst,
            bool resolveTieByLowerKey,
            int firstKey,
            int secondKey)
        {
            float epsilon = Mathf.Max(0f, tieSpeedEpsilon);
            float delta = firstApproachSpeed - secondApproachSpeed;

            if (Mathf.Abs(delta) > epsilon)
            {
                return delta > 0f
                    ? new SumoAttackerDecision(SumoAttackerRole.First, SumoTieResolvedBy.SpeedDelta)
                    : new SumoAttackerDecision(SumoAttackerRole.Second, SumoTieResolvedBy.SpeedDelta);
            }

            if (hasExistingOwner)
            {
                return existingOwnerIsFirst
                    ? new SumoAttackerDecision(SumoAttackerRole.First, SumoTieResolvedBy.ExistingOwner)
                    : new SumoAttackerDecision(SumoAttackerRole.Second, SumoTieResolvedBy.ExistingOwner);
            }

            if (resolveTieByLowerKey && firstKey != secondKey)
            {
                return firstKey < secondKey
                    ? new SumoAttackerDecision(SumoAttackerRole.First, SumoTieResolvedBy.KeyOrderFallback)
                    : new SumoAttackerDecision(SumoAttackerRole.Second, SumoTieResolvedBy.KeyOrderFallback);
            }

            return new SumoAttackerDecision(SumoAttackerRole.Neutral, SumoTieResolvedBy.NeutralWithinEpsilon);
        }

        public static float EvaluateSpeedCurve(SumoBallPhysicsConfig config, float attackerForwardSpeed)
        {
            float minImpactSpeed = config != null ? config.MinImpactSpeed : DefaultMinImpactSpeed;
            float maxImpactSpeed = config != null ? config.MaxImpactSpeed : DefaultMaxImpactSpeed;
            float speed01 = Mathf.InverseLerp(
                minImpactSpeed,
                Mathf.Max(minImpactSpeed + 0.01f, maxImpactSpeed),
                attackerForwardSpeed);

            speed01 = Mathf.Clamp01(speed01);
            float smoothSpeed = speed01 * speed01 * (3f - 2f * speed01);
            float exponent = config != null ? config.ImpactSpeedExponent : 1.85f;
            float topSpeedBonus = config != null ? config.ImpactTopSpeedBonus : 0.85f;

            float shaped = Mathf.Pow(smoothSpeed, Mathf.Max(0.01f, exponent));
            return shaped * (1f + Mathf.Max(0f, topSpeedBonus) * speed01 * speed01);
        }

        public static SumoInitialImpactResult ComputeInitialImpact(
            SumoBallPhysicsConfig config,
            float attackerForwardSpeed,
            float relativeClosingSpeed,
            float directionDot,
            float dashMultiplier)
        {
            float minImpactSpeed = config != null ? config.MinImpactSpeed : DefaultMinImpactSpeed;
            if (attackerForwardSpeed < minImpactSpeed)
            {
                return default;
            }

            float speedCurve = EvaluateSpeedCurve(config, attackerForwardSpeed);
            float speedCurve01 = Mathf.Clamp01(speedCurve);

            float angleExponent = config != null ? config.ImpactAngleExponent : 1.15f;
            float direction01 = Mathf.Clamp01(directionDot);
            float shapedDirection = Mathf.Pow(direction01, Mathf.Max(0.01f, angleExponent));
            float headOn = config != null ? config.HeadOnImpactMultiplier : 1.55f;
            float glancing = config != null ? config.GlancingImpactMultiplier : 0.55f;
            float angleScale = Mathf.Lerp(glancing, headOn, shapedDirection);

            float maxImpactSpeed = config != null ? config.MaxImpactSpeed : DefaultMaxImpactSpeed;
            float closing01 = Mathf.Clamp01(relativeClosingSpeed / Mathf.Max(0.01f, maxImpactSpeed));
            float relativeClosingScale = 1f + (config != null ? config.RelativeClosingBonus : 0.22f) * closing01;

            float dash = Mathf.Max(1f, dashMultiplier);
            float dashScale = Mathf.Lerp(1f, dash, speedCurve01);

            float baseImpactImpulse = config != null ? config.BaseImpactImpulse : 18f;
            float maxImpactImpulse = config != null ? config.MaxImpactImpulse : 30f;
            float firstImpactForce = Mathf.Lerp(baseImpactImpulse, maxImpactImpulse, speedCurve01);
            firstImpactForce *= Mathf.Max(0f, angleScale) * Mathf.Max(1f, relativeClosingScale) * Mathf.Max(1f, dashScale);

            float speed01 = Mathf.InverseLerp(minImpactSpeed, Mathf.Max(minImpactSpeed + 0.01f, maxImpactSpeed), attackerForwardSpeed);
            float readableFloorScale = config != null ? config.HighSpeedReadableFloor : 0.58f;
            float readableFloor = maxImpactImpulse * Mathf.Clamp01(readableFloorScale) * speed01 * speed01;

            float victimImpulse = Mathf.Clamp(Mathf.Max(firstImpactForce, readableFloor), 0f, maxImpactImpulse);
            if (victimImpulse <= 0.0001f)
            {
                return default;
            }

            float recoilScale = config != null ? config.ImpactAttackerRecoilScale : 0.17f;
            float attackerRecoilImpulse = victimImpulse * Mathf.Clamp01(recoilScale);
            float initialRamEnergy = ComputeInitialRamEnergy(config, victimImpulse, attackerForwardSpeed, direction01, dashScale);
            float minRamStartSpeed = config != null ? config.MinRamStartSpeed : minImpactSpeed;
            float stopThreshold = config != null ? config.RamStopEnergyThreshold : 0.08f;
            bool opensRamState = attackerForwardSpeed >= minRamStartSpeed && initialRamEnergy > stopThreshold;

            return new SumoInitialImpactResult(
                speedCurve,
                angleScale,
                relativeClosingScale,
                dashScale,
                victimImpulse,
                attackerRecoilImpulse,
                initialRamEnergy,
                opensRamState);
        }

        public static SumoRamTickResult ComputeRamTick(
            SumoBallPhysicsConfig config,
            float deltaTime,
            float ramEnergy,
            float initialRamEnergy,
            float attackerForwardSpeed,
            float directionDot,
            bool isPressing)
        {
            float dt = Mathf.Max(0f, deltaTime);
            if (ramEnergy <= 0.0001f || dt <= 0f)
            {
                return new SumoRamTickResult(0f, 0f, ramEnergy, 0f, true);
            }

            float minRamPressureSpeed = config != null ? config.MinRamPressureSpeed : 1.6f;
            float maxImpactSpeed = config != null ? config.MaxImpactSpeed : DefaultMaxImpactSpeed;
            float pressure01 = Mathf.InverseLerp(
                minRamPressureSpeed,
                Mathf.Max(minRamPressureSpeed + 0.01f, maxImpactSpeed),
                attackerForwardSpeed);

            if (!isPressing)
            {
                pressure01 = 0f;
            }

            float direction01 = Mathf.Clamp01(directionDot);
            float energy01 = initialRamEnergy > 0.0001f ? Mathf.Clamp01(ramEnergy / initialRamEnergy) : 0f;

            float ramForceScale = EvaluateRamForceScale(config, energy01);
            float glancingForceScale = config != null ? config.RamGlancingForceScale : 0.42f;
            float angleForceScale = Mathf.Lerp(glancingForceScale, 1f, direction01);

            float victimAcceleration = 0f;
            if (isPressing)
            {
                float ramBaseAcceleration = config != null ? config.RamBaseAcceleration : 14f;
                float ramMaxAcceleration = config != null ? config.RamMaxAcceleration : 24f;
                victimAcceleration = ramBaseAcceleration * pressure01 * angleForceScale * ramForceScale;
                victimAcceleration = Mathf.Clamp(victimAcceleration, 0f, Mathf.Max(ramBaseAcceleration, ramMaxAcceleration));
            }

            float attackerDragScale = config != null ? config.RamAttackerDragScale : 0.24f;
            float attackerAcceleration = victimAcceleration * Mathf.Clamp01(attackerDragScale);

            float baseDecay = config != null ? config.RamBaseDecayPerSecond : 1.25f;
            float lowPressureDecay = config != null ? config.RamPressureDecayPerSecond : 1.1f;
            float angleDecay = config != null ? config.RamAngleDecayPerSecond : 1.4f;
            float noPressureDecay = config != null ? config.RamNoPressureDecayPerSecond : 4f;
            float accelerationEnergyCost = config != null ? config.RamAccelerationEnergyCost : 0.14f;

            float energyDecay = dt * baseDecay
                              + dt * (1f - pressure01) * lowPressureDecay
                              + dt * (1f - direction01) * angleDecay
                              + dt * victimAcceleration * accelerationEnergyCost;

            if (!isPressing)
            {
                energyDecay += noPressureDecay * dt;
            }

            energyDecay = Mathf.Max(0f, energyDecay);
            float stopThreshold = config != null ? config.RamStopEnergyThreshold : 0.08f;
            bool shouldStop = ramEnergy - energyDecay <= stopThreshold;

            return new SumoRamTickResult(
                victimAcceleration,
                attackerAcceleration,
                energyDecay,
                ramForceScale,
                shouldStop);
        }

        private static float ComputeInitialRamEnergy(
            SumoBallPhysicsConfig config,
            float firstImpactForce,
            float attackerForwardSpeed,
            float directionDot,
            float dashScale)
        {
            if (firstImpactForce <= 0.0001f)
            {
                return default;
            }

            float minRamStartSpeed = config != null ? config.MinRamStartSpeed : 2.4f;
            float maxImpactSpeed = config != null ? config.MaxImpactSpeed : DefaultMaxImpactSpeed;
            float maxImpactImpulse = config != null ? config.MaxImpactImpulse : 30f;

            float speed01 = Mathf.InverseLerp(
                minRamStartSpeed,
                Mathf.Max(minRamStartSpeed + 0.01f, maxImpactSpeed),
                attackerForwardSpeed);

            float impact01 = Mathf.Clamp01(firstImpactForce / Mathf.Max(0.01f, maxImpactImpulse));
            float energyFromImpact = (config != null ? config.RamEnergyFromImpact : 0.2f) * firstImpactForce;
            float energyFromSpeed = (config != null ? config.RamEnergyFromSpeed : 1.15f) * Mathf.Pow(speed01, 0.9f);
            float energyFromDash = (config != null ? config.RamEnergyFromDash : 0.4f) * Mathf.Max(0f, dashScale - 1f);

            float rawEnergy = energyFromImpact + energyFromSpeed + energyFromDash;
            rawEnergy *= Mathf.Lerp(0.65f, 1f, Mathf.Clamp01(directionDot));
            rawEnergy = Mathf.Max(rawEnergy, impact01 * (config != null ? config.RamMinEnergy : 0.5f));

            float minRamEnergy = config != null ? config.RamMinEnergy : 0.5f;
            float maxRamEnergy = config != null ? config.RamMaxEnergy : 7.5f;
            return Mathf.Clamp(rawEnergy, minRamEnergy, maxRamEnergy);
        }

        private static float EvaluateRamForceScale(SumoBallPhysicsConfig config, float energy01)
        {
            float clampedEnergy = Mathf.Clamp01(energy01);
            float minScale = config != null ? config.RamMinForceScale : 0.18f;
            float maxScale = config != null ? config.RamMaxForceScale : 1.1f;
            float exponent = config != null ? config.RamForceExponent : 0.7f;
            float shaped = Mathf.Pow(clampedEnergy, Mathf.Max(0.01f, exponent));
            return Mathf.Lerp(minScale, maxScale, shaped);
        }
    }
}
