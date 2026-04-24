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
        private const float DefaultImpactActivationMinSpeed = 0.25f;
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

        public static float EvaluateSpeedCurve(
            SumoBallPhysicsConfig config,
            float attackerForwardSpeed,
            float attackerReferenceTopSpeed = 0f)
        {
            float minImpactSpeed = config != null ? config.MinImpactSpeed : DefaultMinImpactSpeed;
            float activationMinSpeed = config != null
                ? config.ImpactActivationMinSpeed
                : DefaultImpactActivationMinSpeed;
            float lowerBound = Mathf.Min(minImpactSpeed, activationMinSpeed);
            float maxImpactSpeed = ResolveReferenceTopSpeed(config, lowerBound, attackerReferenceTopSpeed);
            float speed01 = Mathf.InverseLerp(
                lowerBound,
                Mathf.Max(lowerBound + 0.01f, maxImpactSpeed),
                attackerForwardSpeed);

            speed01 = Mathf.Clamp01(speed01);
            float smoothSpeed = speed01 * speed01 * (3f - 2f * speed01);
            float exponent = config != null ? config.ImpactSpeedExponent : 1.85f;
            float topSpeedBonus = config != null ? config.ImpactTopSpeedBonus : 0.85f;

            float shaped = Mathf.Pow(smoothSpeed, Mathf.Max(0.01f, exponent));
            return shaped * (1f + Mathf.Max(0f, topSpeedBonus) * speed01 * speed01 * speed01);
        }

        public static SumoInitialImpactResult ComputeInitialImpact(
            SumoBallPhysicsConfig config,
            float attackerForwardSpeed,
            float attackerReferenceTopSpeed,
            float relativeClosingSpeed,
            float directionDot,
            float dashMultiplier)
        {
            float minImpactSpeed = config != null ? config.MinImpactSpeed : DefaultMinImpactSpeed;
            float activationMinSpeed = config != null
                ? config.ImpactActivationMinSpeed
                : DefaultImpactActivationMinSpeed;
            float lowerBound = Mathf.Min(minImpactSpeed, activationMinSpeed);
            if (attackerForwardSpeed < activationMinSpeed)
            {
                return default;
            }

            float maxImpactSpeed = ResolveReferenceTopSpeed(config, lowerBound, attackerReferenceTopSpeed);
            float speedCurve = EvaluateSpeedCurve(config, attackerForwardSpeed, maxImpactSpeed);
            float speedCurve01 = Mathf.Clamp01(speedCurve);
            float speed01 = Mathf.InverseLerp(
                lowerBound,
                Mathf.Max(lowerBound + 0.01f, maxImpactSpeed),
                attackerForwardSpeed);

            float angleExponent = config != null ? config.ImpactAngleExponent : 1.15f;
            float direction01 = Mathf.Clamp01(directionDot);
            float shapedDirection = Mathf.Pow(direction01, Mathf.Max(0.01f, angleExponent));
            float headOn = config != null ? config.HeadOnImpactMultiplier : 2.25f;
            float glancing = config != null ? config.GlancingImpactMultiplier : 0.3f;
            float angleScale = Mathf.Lerp(glancing, headOn, shapedDirection);

            float closing01 = Mathf.Clamp01(relativeClosingSpeed / Mathf.Max(0.01f, maxImpactSpeed));
            float relativeClosingScale = 1f + (config != null ? config.RelativeClosingBonus : 0.28f) * closing01;

            float dash = Mathf.Max(1f, dashMultiplier);
            float dashScale = Mathf.Lerp(1f, dash, speedCurve01);

            float baseImpactImpulse = config != null ? config.BaseImpactImpulse : 42f;
            float maxImpactImpulse = config != null ? config.MaxImpactImpulse : 92f;
            float topSpeedBonus = config != null ? config.ImpactTopSpeedBonus : 1.45f;
            float lowSpeedSuppression = Mathf.Pow(speed01, 1.85f);
            float topSpeedScale = 1f + Mathf.Max(0f, topSpeedBonus) * speed01 * speed01 * speed01 * 1.95f;
            float baseShapedImpulse = Mathf.Lerp(baseImpactImpulse * 0.14f, maxImpactImpulse * 0.82f, lowSpeedSuppression);
            float firstImpactForce = baseShapedImpulse * topSpeedScale;
            firstImpactForce *= Mathf.Max(0f, angleScale) * Mathf.Max(1f, relativeClosingScale) * Mathf.Max(1f, dashScale);

            float readableFloorScale = config != null ? config.HighSpeedReadableFloor : 0.62f;
            float readableFloor = maxImpactImpulse
                * Mathf.Clamp01(readableFloorScale)
                * speed01
                * speed01
                * 0.22f;

            float speedSq = speed01 * speed01;
            float momentumCap = attackerForwardSpeed * Mathf.Lerp(0.55f, 1.65f, speedSq);
            float topSpeedCap = maxImpactSpeed * Mathf.Lerp(0.24f, 1.40f, speedSq);
            float configuredCap = maxImpactImpulse * Mathf.Lerp(0.28f, 1.0f, speedSq);
            float dynamicImpactCap = Mathf.Max(0.8f, Mathf.Min(configuredCap, Mathf.Max(momentumCap, topSpeedCap)));
            float aggressiveBounceFloor = Mathf.Min(
                maxImpactImpulse,
                attackerForwardSpeed * Mathf.Lerp(0.95f, 1.75f, speedCurve01));
            dynamicImpactCap = Mathf.Min(maxImpactImpulse, Mathf.Max(dynamicImpactCap, aggressiveBounceFloor));

            float baseVictimImpulse = Mathf.Clamp(Mathf.Max(firstImpactForce, readableFloor, aggressiveBounceFloor), 0f, dynamicImpactCap);
            if (baseVictimImpulse <= 0.0001f)
            {
                return default;
            }

            float arcadeThreshold01 = config != null ? config.FirstImpactArcadeThreshold01 : 0.32f;
            float charged01 = speed01 <= arcadeThreshold01
                ? 0f
                : Mathf.Clamp01((speed01 - arcadeThreshold01) / Mathf.Max(0.01f, 1f - arcadeThreshold01));
            float chargedSmooth = charged01 * charged01 * (3f - 2f * charged01);
            float maxArcadeBoost = config != null ? config.FirstImpactArcadeBoost : 5.6f;
            float maxArcadeCapBoost = config != null ? config.FirstImpactArcadeCapBoost : 4.2f;
            float arcadeBoost = Mathf.Lerp(1f, Mathf.Max(1f, maxArcadeBoost), chargedSmooth);
            float arcadeCapMultiplier = Mathf.Lerp(1f, Mathf.Max(1f, maxArcadeCapBoost), chargedSmooth);
            float victimImpulse = Mathf.Min(
                maxImpactImpulse,
                Mathf.Min(baseVictimImpulse * arcadeBoost, dynamicImpactCap * arcadeCapMultiplier));

            if (victimImpulse <= 0.0001f)
            {
                return default;
            }

            float recoilScale = config != null ? config.ImpactAttackerRecoilScale : 0.17f;
            float attackerRecoilImpulse = victimImpulse * Mathf.Clamp01(recoilScale);
            float initialRamEnergy = ComputeInitialRamEnergy(config, baseVictimImpulse, attackerForwardSpeed, maxImpactSpeed, direction01, dashScale);
            float stopThreshold = config != null ? config.RamStopEnergyThreshold : 0.08f;
            bool opensRamState = initialRamEnergy > stopThreshold;

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
            bool isPressing,
            float contactBlend01 = 1f)
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

            float shapedPressure = pressure01 * pressure01 * (3f - 2f * pressure01);

            float direction01 = Mathf.Clamp01(directionDot);
            float energy01 = initialRamEnergy > 0.0001f ? Mathf.Clamp01(ramEnergy / initialRamEnergy) : 0f;
            float contactBlend = Mathf.Clamp01(contactBlend01);
            float contactBlendShaped = contactBlend * contactBlend * (3f - 2f * contactBlend);

            float ramForceScale = EvaluateRamForceScale(config, energy01);
            float glancingForceScale = config != null ? config.RamGlancingForceScale : 0.42f;
            float angleForceScale = Mathf.Lerp(glancingForceScale, 1f, direction01);

            float victimAcceleration = 0f;
            if (isPressing)
            {
                float ramBaseAcceleration = config != null ? config.RamBaseAcceleration : 14f;
                float ramMaxAcceleration = config != null ? config.RamMaxAcceleration : 24f;
                victimAcceleration = ramBaseAcceleration * shapedPressure * angleForceScale * ramForceScale * contactBlendShaped;
                victimAcceleration = Mathf.Clamp(victimAcceleration, 0f, Mathf.Max(ramBaseAcceleration, ramMaxAcceleration));
            }

            float attackerDragScale = config != null ? config.RamAttackerDragScale : 0.24f;
            float attackerAcceleration = victimAcceleration * Mathf.Clamp01(attackerDragScale);

            float baseDecay = config != null ? config.RamBaseDecayPerSecond : 1.25f;
            float lowPressureDecay = config != null ? config.RamPressureDecayPerSecond : 1.1f;
            float angleDecay = config != null ? config.RamAngleDecayPerSecond : 1.4f;
            float noPressureDecay = config != null ? config.RamNoPressureDecayPerSecond : 4f;
            noPressureDecay = Mathf.Clamp(noPressureDecay, 0f, 3.6f);
            float accelerationEnergyCost = config != null ? config.RamAccelerationEnergyCost : 0.14f;

            float energyDecay = dt * baseDecay
                              + dt * (1f - pressure01) * lowPressureDecay
                              + dt * (1f - direction01) * angleDecay
                              + dt * victimAcceleration * accelerationEnergyCost;

            energyDecay += dt * (1f - contactBlendShaped) * 1.1f;

            float proportionalDecay = dt * ramEnergy * 0.55f;
            energyDecay += proportionalDecay;

            if (!isPressing)
            {
                energyDecay += noPressureDecay * dt * 0.78f;
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
            float attackerReferenceTopSpeed,
            float directionDot,
            float dashScale)
        {
            if (firstImpactForce <= 0.0001f)
            {
                return default;
            }

            float minRamStartSpeed = config != null ? config.MinRamStartSpeed : 2.4f;
            float maxImpactSpeed = ResolveReferenceTopSpeed(config, minRamStartSpeed, attackerReferenceTopSpeed);
            float maxImpactImpulse = config != null ? config.MaxImpactImpulse : 92f;

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
            rawEnergy *= 0.52f;
            rawEnergy = Mathf.Max(rawEnergy, impact01 * (config != null ? config.RamMinEnergy : 0.5f));

            float minRamEnergy = config != null ? config.RamMinEnergy : 0.5f;
            float maxRamEnergy = config != null ? config.RamMaxEnergy : 7.5f;
            float speedWeightedCap = Mathf.Lerp(1.6f, 3.0f, Mathf.Clamp01(speed01));
            float cappedMaxEnergy = Mathf.Min(maxRamEnergy, speedWeightedCap);
            return Mathf.Clamp(rawEnergy, minRamEnergy, Mathf.Max(minRamEnergy, cappedMaxEnergy));
        }

        private static float ResolveReferenceTopSpeed(
            SumoBallPhysicsConfig config,
            float minImpactSpeed,
            float attackerReferenceTopSpeed)
        {
            float configTopSpeed = config != null ? config.MaxImpactSpeed : DefaultMaxImpactSpeed;
            float referenceTopSpeed = attackerReferenceTopSpeed > 0.0001f
                ? attackerReferenceTopSpeed
                : configTopSpeed;
            return Mathf.Max(minImpactSpeed + 0.01f, referenceTopSpeed);
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
