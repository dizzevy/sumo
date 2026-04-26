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
        public readonly SumoCollisionTier CollisionTier;
        public readonly float SpeedCurve;
        public readonly float ImpactSpeed01;
        public readonly float AngleScale;
        public readonly float RelativeClosingScale;
        public readonly float DashMultiplier;
        public readonly float VictimImpulse;
        public readonly float AttackerBackstepImpulse;
        public readonly float AttackerForwardCancel01;
        public readonly float AttackerRecoverySeconds;
        public readonly float InitialRamEnergy;
        public readonly bool OpensRamState;

        public SumoInitialImpactResult(
            SumoCollisionTier collisionTier,
            float speedCurve,
            float impactSpeed01,
            float angleScale,
            float relativeClosingScale,
            float dashMultiplier,
            float victimImpulse,
            float attackerBackstepImpulse,
            float attackerForwardCancel01,
            float attackerRecoverySeconds,
            float initialRamEnergy,
            bool opensRamState)
        {
            CollisionTier = collisionTier;
            SpeedCurve = speedCurve;
            ImpactSpeed01 = impactSpeed01;
            AngleScale = angleScale;
            RelativeClosingScale = relativeClosingScale;
            DashMultiplier = dashMultiplier;
            VictimImpulse = victimImpulse;
            AttackerBackstepImpulse = attackerBackstepImpulse;
            AttackerForwardCancel01 = attackerForwardCancel01;
            AttackerRecoverySeconds = attackerRecoverySeconds;
            InitialRamEnergy = initialRamEnergy;
            OpensRamState = opensRamState;
        }

        public bool HasImpact => CollisionTier == SumoCollisionTier.MidImpact
            || CollisionTier == SumoCollisionTier.HighImpact;
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
        private const float DefaultLowTierShare01 = 0.77f;
        private const float DefaultMidTierShare01 = 0.15f;
        private const float DefaultTierHysteresisShare01 = 0.02f;
        private const float DefaultBackstepDeadZoneShare01 = 0.02f;

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

        public static void GetTierThresholds(
            SumoBallPhysicsConfig config,
            float attackerReferenceTopSpeed,
            out float tierRefSpeed,
            out float lowUpper,
            out float highStart,
            out float lowHysteresis,
            out float backstepDeadZone)
        {
            if (config != null)
            {
                config.GetCollisionTierThresholds(
                    attackerReferenceTopSpeed,
                    out tierRefSpeed,
                    out lowUpper,
                    out highStart,
                    out lowHysteresis,
                    out backstepDeadZone);
                return;
            }

            float rawReferenceSpeed = attackerReferenceTopSpeed > 0.0001f
                ? attackerReferenceTopSpeed
                : DefaultMaxImpactSpeed;
            tierRefSpeed = Mathf.Max(0.05f, Mathf.Min(rawReferenceSpeed, DefaultMaxImpactSpeed));
            lowUpper = tierRefSpeed * DefaultLowTierShare01;
            highStart = Mathf.Max(lowUpper + 0.01f, tierRefSpeed * (DefaultLowTierShare01 + DefaultMidTierShare01));
            lowHysteresis = tierRefSpeed * DefaultTierHysteresisShare01;
            backstepDeadZone = tierRefSpeed * DefaultBackstepDeadZoneShare01;
        }

        public static SumoCollisionTier ClassifyCollisionTier(
            SumoBallPhysicsConfig config,
            float attackerForwardSpeed,
            float attackerReferenceTopSpeed,
            bool keepLowRamBias)
        {
            GetTierThresholds(
                config,
                attackerReferenceTopSpeed,
                out _,
                out float lowUpper,
                out float highStart,
                out float lowHysteresis,
                out _);

            float lowThreshold = lowUpper;
            if (keepLowRamBias)
            {
                lowThreshold += lowHysteresis;
            }

            if (attackerForwardSpeed <= lowThreshold)
            {
                return SumoCollisionTier.LowRam;
            }

            return attackerForwardSpeed >= highStart
                ? SumoCollisionTier.HighImpact
                : SumoCollisionTier.MidImpact;
        }

        public static float ComputeLowRamSeedEnergy(
            SumoBallPhysicsConfig config,
            float attackerForwardSpeed,
            float attackerReferenceTopSpeed = 0f)
        {
            GetTierThresholds(
                config,
                attackerReferenceTopSpeed,
                out _,
                out float lowUpper,
                out _,
                out _,
                out _);

            float lowRamMaxSpeed = Mathf.Max(0.05f, lowUpper);
            float low01 = Mathf.Clamp01(attackerForwardSpeed / lowRamMaxSpeed);

            float minEnergy = config != null ? config.RamMinEnergy : 0.5f;
            float maxEnergy = config != null ? config.RamMaxEnergy : 7.5f;
            float stopThreshold = config != null ? config.RamStopEnergyThreshold : 0.08f;

            float startMin = Mathf.Max(stopThreshold + 0.05f, minEnergy * 0.7f);
            float startMax = Mathf.Clamp(Mathf.Lerp(minEnergy, minEnergy + 1.25f, 0.9f), startMin, maxEnergy);
            return Mathf.Lerp(startMin, startMax, low01);
        }

        public static float EvaluateSpeedCurve(
            SumoBallPhysicsConfig config,
            float attackerForwardSpeed,
            float attackerReferenceTopSpeed = 0f,
            float overrideLowerBound = float.NegativeInfinity)
        {
            float minImpactSpeed = config != null ? config.MinImpactSpeed : DefaultMinImpactSpeed;
            float activationMinSpeed = config != null
                ? config.ImpactActivationMinSpeed
                : DefaultImpactActivationMinSpeed;
            float lowerBound = float.IsNegativeInfinity(overrideLowerBound)
                ? Mathf.Min(minImpactSpeed, activationMinSpeed)
                : Mathf.Max(0f, overrideLowerBound);
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
            GetTierThresholds(
                config,
                attackerReferenceTopSpeed,
                out float tierRefSpeed,
                out float lowUpper,
                out _,
                out _,
                out float backstepDeadZone);

            SumoCollisionTier tier = ClassifyCollisionTier(
                config,
                attackerForwardSpeed,
                tierRefSpeed,
                keepLowRamBias: false);
            if (tier == SumoCollisionTier.LowRam)
            {
                return default;
            }

            float impactLowerBound = Mathf.Max(
                lowUpper + 0.01f,
                config != null ? config.ImpactActivationMinSpeed : DefaultImpactActivationMinSpeed);
            float maxImpactSpeed = ResolveReferenceTopSpeed(config, impactLowerBound, tierRefSpeed);

            float speed01 = Mathf.InverseLerp(
                impactLowerBound,
                Mathf.Max(impactLowerBound + 0.01f, maxImpactSpeed),
                attackerForwardSpeed);
            speed01 = Mathf.Clamp01(speed01);
            float speedCurve = EvaluateSpeedCurve(config, attackerForwardSpeed, maxImpactSpeed, impactLowerBound);

            float direction01 = Mathf.Clamp01(directionDot);
            float angleExponent = config != null ? config.ImpactAngleExponent : 1.15f;
            float shapedDirection = Mathf.Pow(direction01, Mathf.Max(0.01f, angleExponent));
            float glancing = config != null ? config.GlancingImpactMultiplier : 0.3f;
            float headOn = config != null ? config.HeadOnImpactMultiplier : 2.25f;
            float angleScale = Mathf.Lerp(glancing, headOn, shapedDirection);
            float relativeClosingScale = 1f + Mathf.Clamp01(relativeClosingSpeed / Mathf.Max(0.01f, maxImpactSpeed))
                * (config != null ? config.RelativeClosingBonus : 0.28f);

            float baseImpactImpulse = config != null ? config.BaseImpactImpulse : 42f;
            float maxImpactImpulse = config != null ? config.MaxImpactImpulse : 92f;
            float tierScale = tier == SumoCollisionTier.MidImpact ? 0.62f : 1f;
            float victimImpulse = Mathf.Lerp(baseImpactImpulse * 0.18f, maxImpactImpulse, speedCurve);
            victimImpulse *= tierScale;
            float maxSpeedVictimScale = Mathf.Lerp(1f, 1f / 3f, speed01);
            victimImpulse *= maxSpeedVictimScale;
            victimImpulse = Mathf.Clamp(victimImpulse, 0f, maxImpactImpulse);
            if (victimImpulse <= 0.0001f)
            {
                return default;
            }

            float backstepSpeed01 = Mathf.InverseLerp(
                lowUpper + Mathf.Max(0f, backstepDeadZone),
                Mathf.Max(lowUpper + Mathf.Max(0f, backstepDeadZone) + 0.01f, maxImpactSpeed),
                attackerForwardSpeed);
            backstepSpeed01 = Mathf.Clamp01(backstepSpeed01);

            float backstepCurve01 = config != null
                ? config.EvaluateBackstepCurve01(backstepSpeed01)
                : backstepSpeed01 * backstepSpeed01;
            float backstepMaxImpulse = config != null ? config.AttackerBackstepMaxImpulse : 5f;
            float attackerBackstepImpulse = Mathf.Max(0f, backstepMaxImpulse * backstepCurve01);
            attackerBackstepImpulse *= 2f;

            float attackerForwardCancel01 = 0f;
            if (tier == SumoCollisionTier.HighImpact)
            {
                float configuredCancel = config != null ? config.AttackerHighForwardCancel01 : 0.92f;
                attackerForwardCancel01 = Mathf.Clamp01(Mathf.Lerp(configuredCancel * 0.75f, configuredCancel, backstepCurve01));
            }

            float attackerRecoverySeconds = tier == SumoCollisionTier.HighImpact
                ? (config != null ? config.HighHitRecoverySeconds : 0.24f)
                : (config != null ? config.MidHitRecoverySeconds : 0.18f);

            float initialRamEnergy = ComputeInitialRamEnergy(
                config,
                victimImpulse,
                attackerForwardSpeed,
                maxImpactSpeed,
                direction01,
                1f);
            float stopThreshold = config != null ? config.RamStopEnergyThreshold : 0.08f;
            bool opensRamState = initialRamEnergy > stopThreshold;

            return new SumoInitialImpactResult(
                tier,
                speedCurve,
                speed01,
                angleScale,
                relativeClosingScale,
                Mathf.Max(1f, dashMultiplier),
                victimImpulse,
                attackerBackstepImpulse,
                attackerForwardCancel01,
                attackerRecoverySeconds,
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
            float contactBlend01 = 1f,
            float minRamPressureSpeedOverride = -1f)
        {
            float dt = Mathf.Max(0f, deltaTime);
            if (ramEnergy <= 0.0001f || dt <= 0f)
            {
                return new SumoRamTickResult(0f, 0f, ramEnergy, 0f, true);
            }

            float minRamPressureSpeed = minRamPressureSpeedOverride >= 0f
                ? minRamPressureSpeedOverride
                : (config != null ? config.MinRamPressureSpeed : 1.6f);
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
            float referenceTopSpeedRaw = attackerReferenceTopSpeed > 0.0001f
                ? attackerReferenceTopSpeed
                : configTopSpeed;
            float referenceTopSpeed = Mathf.Min(referenceTopSpeedRaw, configTopSpeed);
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
