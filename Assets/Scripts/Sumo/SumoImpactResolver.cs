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

    public enum SumoImpactTier : byte
    {
        Unknown = 0,
        Low = 1,
        Mid = 2,
        High = 3
    }

    public enum SumoImpactResponseMode : byte
    {
        SoftShove = 0,
        ArcadeBurst = 1
    }

    public readonly struct SumoImpactTierThresholds
    {
        public readonly float TierRefSpeed;
        public readonly float LowUpper;
        public readonly float HighStart;

        public SumoImpactTierThresholds(float tierRefSpeed, float lowUpper, float highStart)
        {
            TierRefSpeed = tierRefSpeed;
            LowUpper = lowUpper;
            HighStart = highStart;
        }
    }

    public static class SumoImpactResolver
    {
        private const float DefaultMinImpactSpeed = 2.2f;
        private const float DefaultImpactActivationMinSpeed = 0.25f;
        private const float DefaultMaxImpactSpeed = 10f;
        private const bool DefaultUseNormalizedTierThresholds = true;
        private const float DefaultLowTierShare01 = 0.77f;
        private const float DefaultMidTierShare01 = 0.15f;
        private const float DefaultTierHysteresisShare01 = 0.02f;
        private const float DefaultBackstepDeadZoneShare01 = 0.02f;
        private const float DefaultLowTierShoveMultiplier = 2f;
        private const float DefaultMidTierShoveMultiplier = 2f;
        private const float DefaultHighTierShoveMultiplier = 6.8f;
        private const float DefaultMaxShoveForceMultiplier = DefaultHighTierShoveMultiplier;
        private const float DefaultHighSpeedFatsoCounterMinIncomingMultiplier = 0.55f;
        private const float DefaultHighSpeedFatsoCounterMaxIncomingMultiplier = 0.90f;

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
            float delta = SanitizeNonNegativeFinite(firstApproachSpeed) - SanitizeNonNegativeFinite(secondApproachSpeed);

            if (Mathf.Abs(delta) > epsilon)
            {
                return delta > 0f
                    ? new SumoAttackerDecision(SumoAttackerRole.First, SumoTieResolvedBy.SpeedDelta)
                    : new SumoAttackerDecision(SumoAttackerRole.Second, SumoTieResolvedBy.SpeedDelta);
            }

            return new SumoAttackerDecision(SumoAttackerRole.Neutral, SumoTieResolvedBy.NeutralWithinEpsilon);
        }

        public static bool ShouldUseHighSpeedFatsoCounter(
            bool candidateIsFatso,
            bool targetIsActiveFatso,
            SumoImpactTier candidateTier)
        {
            return !candidateIsFatso
                && targetIsActiveFatso
                && candidateTier == SumoImpactTier.High;
        }

        public static SumoAttackerDecision ResolveHighSpeedFatsoCounterAttacker(
            SumoAttackerDecision baseDecision,
            bool firstCanCounterActiveFatso,
            bool secondCanCounterActiveFatso)
        {
            if (firstCanCounterActiveFatso && !secondCanCounterActiveFatso)
            {
                return new SumoAttackerDecision(SumoAttackerRole.First, SumoTieResolvedBy.SpeedDelta);
            }

            if (secondCanCounterActiveFatso && !firstCanCounterActiveFatso)
            {
                return new SumoAttackerDecision(SumoAttackerRole.Second, SumoTieResolvedBy.SpeedDelta);
            }

            return baseDecision;
        }

        public static float ResolveHighSpeedFatsoIncomingMultiplier(
            float baseIncomingMultiplier,
            bool highSpeedCounterApplies,
            SumoImpactTier impactTier,
            float physicalImpactSpeed,
            SumoImpactTierThresholds thresholds)
        {
            float resolvedBase = SanitizeFinite(baseIncomingMultiplier);
            if (resolvedBase <= 0f)
            {
                resolvedBase = 1f;
            }

            resolvedBase = Mathf.Clamp(resolvedBase, 0.02f, 1f);
            if (!highSpeedCounterApplies || impactTier != SumoImpactTier.High)
            {
                return resolvedBase;
            }

            float highStart = Mathf.Max(0f, thresholds.HighStart);
            float tierReferenceSpeed = Mathf.Max(highStart + 0.01f, thresholds.TierRefSpeed);
            float speed01 = Mathf.InverseLerp(
                highStart,
                tierReferenceSpeed,
                SanitizeNonNegativeFinite(physicalImpactSpeed));
            float piercedMultiplier = Mathf.Lerp(
                DefaultHighSpeedFatsoCounterMinIncomingMultiplier,
                DefaultHighSpeedFatsoCounterMaxIncomingMultiplier,
                Mathf.Clamp01(speed01));

            return Mathf.Clamp(Mathf.Max(resolvedBase, piercedMultiplier), 0.02f, 1f);
        }

        public static float ComputeCappedPushDeltaV(
            float attackerForwardSpeed,
            float victimForwardSpeed,
            float targetSpeedScale = 1f)
        {
            float attackerSpeed = SanitizeNonNegativeFinite(attackerForwardSpeed);
            float victimSpeed = SanitizeFinite(victimForwardSpeed);
            float targetSpeed = attackerSpeed * Mathf.Max(0f, targetSpeedScale);
            return Mathf.Max(0f, targetSpeed - victimSpeed);
        }

        public static float ComputeCappedPushTargetSpeed(
            float attackerForwardSpeed,
            float victimForwardSpeed,
            float targetSpeedScale = 1f)
        {
            return SanitizeFinite(victimForwardSpeed)
                + ComputeCappedPushDeltaV(attackerForwardSpeed, victimForwardSpeed, targetSpeedScale);
        }

        public static float ComputeCappedPhysicalImpactSpeed(
            SumoBallPhysicsConfig config,
            float entryPhysicalForwardSpeed,
            float currentPhysicalForwardSpeed)
        {
            float physicalSpeed = Mathf.Max(
                SanitizeNonNegativeFinite(entryPhysicalForwardSpeed),
                SanitizeNonNegativeFinite(currentPhysicalForwardSpeed));
            float maxImpactSpeed = config != null ? config.MaxImpactSpeed : DefaultMaxImpactSpeed;
            return Mathf.Min(physicalSpeed, Mathf.Max(0.01f, maxImpactSpeed));
        }

        public static float ComputeCappedRamDriveSpeed(float physicalForwardSpeed, float engagementEntrySpeed)
        {
            float physicalSpeed = SanitizeNonNegativeFinite(physicalForwardSpeed);
            float entrySpeed = SanitizeNonNegativeFinite(engagementEntrySpeed);
            return entrySpeed > 0.0001f
                ? Mathf.Min(physicalSpeed, entrySpeed)
                : physicalSpeed;
        }

        public static float ResolveMonotonicRamEnergy(
            float requestedRamEnergy,
            float currentRamEnergy,
            bool continuousEngagement)
        {
            float requested = SanitizeNonNegativeFinite(requestedRamEnergy);
            if (!continuousEngagement)
            {
                return requested;
            }

            return Mathf.Min(requested, SanitizeNonNegativeFinite(currentRamEnergy));
        }

        public static bool IsHardBreakQualified(
            int currentTick,
            int qualifiedBreakStartTick,
            int requiredBreakTicks,
            float maxSeparationSinceBreak,
            float requiredSeparation)
        {
            return qualifiedBreakStartTick > 0
                && currentTick - qualifiedBreakStartTick >= Mathf.Max(1, requiredBreakTicks)
                && SanitizeNonNegativeFinite(maxSeparationSinceBreak) >= Mathf.Max(0f, SanitizeFinite(requiredSeparation));
        }

        public static float ComputeExhaustedContactStabilizationDeltaV(float closingSpeed, float maxDeltaV)
        {
            return Mathf.Min(
                SanitizeNonNegativeFinite(closingSpeed),
                Mathf.Max(0f, SanitizeFinite(maxDeltaV)));
        }

        public static float ComputeResidualMeaningfulDeltaV(float maxDeltaVPerTick, SumoImpactResponseMode responseMode)
        {
            float maxStep = SanitizeNonNegativeFinite(maxDeltaVPerTick);
            if (maxStep <= 0.0001f)
            {
                return 0f;
            }

            if (responseMode == SumoImpactResponseMode.ArcadeBurst)
            {
                return Mathf.Clamp(maxStep * 0.06f, 0.045f, 0.16f);
            }

            return Mathf.Clamp(maxStep * 0.14f, 0.025f, 0.07f);
        }

        public static float ComputeDynamicResidualImpulseDeltaV(
            float tailSpeed,
            float physicalClosingSpeed,
            float victimForwardSpeed,
            float targetSpeedScale,
            float remainingBudget,
            float initialBudget,
            float previousDeltaV,
            float maxDeltaVPerTick,
            SumoImpactResponseMode responseMode)
        {
            float tail = SanitizeNonNegativeFinite(tailSpeed);
            float closing = SanitizeNonNegativeFinite(physicalClosingSpeed);
            float pressureSpeed = Mathf.Max(tail, closing * 0.55f);
            if (pressureSpeed <= 0.0001f)
            {
                return 0f;
            }

            float responseFactor = responseMode == SumoImpactResponseMode.ArcadeBurst ? 0.18f : 0.095f;
            float requested = Mathf.Min(
                SanitizeNonNegativeFinite(maxDeltaVPerTick),
                pressureSpeed * responseFactor);
            requested = Mathf.Min(
                requested,
                ComputeCappedPushDeltaV(tail, victimForwardSpeed, targetSpeedScale));

            return ComputeDiminishingResidualDeltaV(
                requested,
                remainingBudget,
                initialBudget,
                previousDeltaV,
                responseMode);
        }

        public static float ComputeEntryToTailResidualCap(float entryDeltaV, SumoImpactResponseMode responseMode)
        {
            float entry = SanitizeNonNegativeFinite(entryDeltaV);
            if (entry <= 0.0001f)
            {
                return float.PositiveInfinity;
            }

            float share = responseMode == SumoImpactResponseMode.ArcadeBurst ? 0.82f : 0.72f;
            return entry * share;
        }

        public static float ComputeImpactTailResidualDeltaV(
            float tailSpeed,
            float physicalClosingSpeed,
            float victimForwardSpeed,
            float targetSpeedScale,
            float remainingBudget,
            float initialBudget,
            float lastResidualDeltaV,
            float entryImpactDeltaV,
            float residualAccumulator,
            float maxDeltaVPerTick,
            SumoImpactResponseMode responseMode)
        {
            float tail = SanitizeNonNegativeFinite(tailSpeed);
            float closing = SanitizeNonNegativeFinite(physicalClosingSpeed);
            float pressureSpeed = Mathf.Max(tail, closing * 0.55f);
            if (pressureSpeed <= 0.0001f)
            {
                return 0f;
            }

            float responseFactor = responseMode == SumoImpactResponseMode.ArcadeBurst ? 0.18f : 0.095f;
            float requested = Mathf.Min(
                SanitizeNonNegativeFinite(maxDeltaVPerTick),
                pressureSpeed * responseFactor);
            requested = Mathf.Min(
                requested,
                ComputeCappedPushDeltaV(tail, victimForwardSpeed, targetSpeedScale));

            float previousResidual = SanitizeNonNegativeFinite(lastResidualDeltaV);
            float firstResidualCap = float.PositiveInfinity;
            if (previousResidual <= 0.0001f)
            {
                firstResidualCap = ComputeEntryToTailResidualCap(entryImpactDeltaV, responseMode);
                requested = Mathf.Min(requested, firstResidualCap);
            }

            requested += SanitizeNonNegativeFinite(residualAccumulator);
            if (previousResidual <= 0.0001f)
            {
                requested = Mathf.Min(requested, firstResidualCap);
            }

            return ComputeDiminishingResidualDeltaV(
                requested,
                remainingBudget,
                initialBudget,
                previousResidual,
                responseMode);
        }

        public static bool IsResidualImpactTailExhausted(
            float residualDeltaV,
            float tailSpeed,
            float remainingBudget,
            float meaningfulDeltaV)
        {
            float meaningful = Mathf.Max(0.0001f, SanitizeNonNegativeFinite(meaningfulDeltaV));
            return SanitizeNonNegativeFinite(residualDeltaV) < meaningful
                || SanitizeNonNegativeFinite(tailSpeed) <= meaningful * 1.25f
                || SanitizeNonNegativeFinite(remainingBudget) <= meaningful * 0.5f;
        }

        public static bool IsImpactTailFullyExhausted(
            float residualDeltaV,
            float tailSpeed,
            float remainingBudget,
            float meaningfulDeltaV,
            int silentDrainTicks)
        {
            float meaningful = Mathf.Max(0.0001f, SanitizeNonNegativeFinite(meaningfulDeltaV));
            float residual = SanitizeNonNegativeFinite(residualDeltaV);
            float tail = SanitizeNonNegativeFinite(tailSpeed);
            float remaining = SanitizeNonNegativeFinite(remainingBudget);
            int drains = Mathf.Max(0, silentDrainTicks);

            return residual < meaningful
                && tail <= meaningful * 3.0f
                && remaining <= meaningful * 2.0f
                && drains >= 1;
        }

        public static bool CanStartRamAfterImpactTailHandoff(
            bool impactTailExhausted,
            int currentTick,
            int impactTailExhaustedTick,
            float ramEnergy,
            float stopThreshold,
            float physicalForwardSpeed,
            float directionDot,
            float physicalClosingSpeed,
            float minPressureSpeed,
            float minDirectionDot,
            float minClosingSpeed)
        {
            return impactTailExhausted
                && impactTailExhaustedTick > 0
                && currentTick > impactTailExhaustedTick
                && ShouldStartRamAfterImpactTail(
                    true,
                    ramEnergy,
                    stopThreshold,
                    physicalForwardSpeed,
                    directionDot,
                    physicalClosingSpeed,
                    minPressureSpeed,
                    minDirectionDot,
                    minClosingSpeed);
        }

        public static float ComputeResidualAttackerSpeedLoss(
            float victimDeltaV,
            float directionDot,
            float shoveForceMultiplier,
            SumoImpactResponseMode responseMode)
        {
            float deltaV = SanitizeNonNegativeFinite(victimDeltaV);
            if (deltaV <= 0.0001f)
            {
                return 0f;
            }

            float angle01 = Mathf.Clamp01(SanitizeFinite(directionDot));
            float force01 = Mathf.Clamp01((ResolveShoveForceMultiplier(shoveForceMultiplier) - 1f)
                / (DefaultMaxShoveForceMultiplier - 1f));
            float baseLoss = responseMode == SumoImpactResponseMode.ArcadeBurst ? 0.32f : 0.46f;
            float angleLoss = Mathf.Lerp(0.72f, 1f, angle01);
            float forceLoss = Mathf.Lerp(0.9f, 1.12f, force01);
            return deltaV * baseLoss * angleLoss * forceLoss;
        }

        public static float ComputeNextImpactTailSpeed(
            float currentTailSpeed,
            float attackerSpeedLoss,
            float unappliedResidualDeltaV,
            float deltaTime)
        {
            float tail = SanitizeNonNegativeFinite(currentTailSpeed);
            float appliedLoss = SanitizeNonNegativeFinite(attackerSpeedLoss);
            float suppressedLoss = SanitizeNonNegativeFinite(unappliedResidualDeltaV) * 0.55f;
            float passiveLoss = tail * Mathf.Clamp01(SanitizeNonNegativeFinite(deltaTime) * 1.75f);
            return Mathf.Max(0f, tail - appliedLoss - suppressedLoss - passiveLoss);
        }

        public static bool ShouldStartRamAfterImpactTail(
            bool impactTailExhausted,
            float ramEnergy,
            float stopThreshold,
            float physicalForwardSpeed,
            float directionDot,
            float physicalClosingSpeed,
            float minPressureSpeed,
            float minDirectionDot,
            float minClosingSpeed)
        {
            return impactTailExhausted
                && SanitizeNonNegativeFinite(ramEnergy) > Mathf.Max(0f, SanitizeFinite(stopThreshold))
                && ShouldApplyRamContactDrive(
                    physicalForwardSpeed,
                    directionDot,
                    physicalClosingSpeed,
                    minPressureSpeed,
                    minDirectionDot,
                    minClosingSpeed);
        }

        public static Vector3 SuppressMovementAgainstPush(
            Vector3 targetHorizontalVelocity,
            Vector3 pushDirection,
            float strength01)
        {
            Vector3 target = new Vector3(targetHorizontalVelocity.x, 0f, targetHorizontalVelocity.z);
            Vector3 direction = new Vector3(pushDirection.x, 0f, pushDirection.z);
            if (target.sqrMagnitude <= 0.0000001f || direction.sqrMagnitude <= 0.0000001f)
            {
                return target;
            }

            direction.Normalize();
            float againstPushSpeed = Vector3.Dot(target, -direction);
            if (againstPushSpeed <= 0f)
            {
                return target;
            }

            return target + direction * (againstPushSpeed * Mathf.Clamp01(SanitizeFinite(strength01)));
        }

        public static SumoImpactResponseMode ResolveImpactResponseMode(
            SumoBallPhysicsConfig config,
            float physicalImpactSpeed,
            bool isDashing)
        {
            float maxImpactSpeed = config != null ? config.MaxImpactSpeed : DefaultMaxImpactSpeed;
            float speed01 = Mathf.Clamp01(SanitizeNonNegativeFinite(physicalImpactSpeed) / Mathf.Max(0.01f, maxImpactSpeed));
            float threshold01 = config != null ? config.ArcadeBurstMinSpeed01 : 0.5f;
            float dashThreshold01 = config != null ? config.ArcadeBurstDashMinSpeed01 : 0.25f;
            float activeThreshold01 = isDashing ? dashThreshold01 : threshold01;

            return speed01 >= Mathf.Clamp01(activeThreshold01)
                ? SumoImpactResponseMode.ArcadeBurst
                : SumoImpactResponseMode.SoftShove;
        }

        public static float ResolveTierShoveMultiplier(SumoBallPhysicsConfig config, SumoImpactTier tier)
        {
            float multiplier;
            switch (tier)
            {
                case SumoImpactTier.Low:
                    multiplier = config != null ? config.LowTierShoveMultiplier : DefaultLowTierShoveMultiplier;
                    break;

                case SumoImpactTier.Mid:
                    multiplier = config != null ? config.MidTierShoveMultiplier : DefaultMidTierShoveMultiplier;
                    break;

                case SumoImpactTier.High:
                    multiplier = config != null ? config.HighTierShoveMultiplier : DefaultHighTierShoveMultiplier;
                    break;

                default:
                    multiplier = 1f;
                    break;
            }

            return ResolveShoveForceMultiplier(multiplier);
        }

        public static float ResolveShoveForceMultiplier(float multiplier)
        {
            return Mathf.Clamp(SanitizeFinite(multiplier), 0.25f, DefaultMaxShoveForceMultiplier);
        }

        public static float ApplyShoveForceMultiplier(float value, float multiplier)
        {
            return SanitizeNonNegativeFinite(value) * ResolveShoveForceMultiplier(multiplier);
        }

        public static float ComputeSoftShoveEntryNudgeDeltaV(
            float physicalImpactSpeed,
            float relativeClosingSpeed,
            float victimForwardSpeed,
            float maxDeltaVPerTick,
            float targetSpeedScale = 1f)
        {
            float entrySpeed = Mathf.Max(
                SanitizeNonNegativeFinite(physicalImpactSpeed),
                SanitizeNonNegativeFinite(relativeClosingSpeed));
            if (entrySpeed <= 0.0001f)
            {
                return 0f;
            }

            float targetSpeed = entrySpeed * 0.72f * ResolveShoveForceMultiplier(targetSpeedScale);
            float requestedDeltaV = targetSpeed - SanitizeFinite(victimForwardSpeed);
            return ClampImpactDeltaVStep(requestedDeltaV, maxDeltaVPerTick);
        }

        public static float ComputeTierAwareRamSeedEnergy(
            SumoBallPhysicsConfig config,
            SumoImpactTier tier,
            float attackerForwardSpeed,
            float relativeClosingSpeed,
            float directionDot,
            float shoveForceMultiplier,
            float currentRamEnergy = 0f)
        {
            float stopThreshold = config != null ? config.RamStopEnergyThreshold : 0.08f;
            float minRamEnergy = config != null ? config.RamMinEnergy : 0.5f;
            float maxRamEnergy = config != null ? config.RamMaxEnergy : 7.6f;
            float maxImpactSpeed = config != null ? config.MaxImpactSpeed : DefaultMaxImpactSpeed;
            maxImpactSpeed = Mathf.Max(0.01f, maxImpactSpeed);

            float pressureSpeed = Mathf.Max(
                SanitizeNonNegativeFinite(attackerForwardSpeed),
                SanitizeNonNegativeFinite(relativeClosingSpeed) * 0.72f);
            float speed01 = Mathf.Clamp01(pressureSpeed / maxImpactSpeed);
            float closing01 = Mathf.Clamp01(SanitizeNonNegativeFinite(relativeClosingSpeed) / maxImpactSpeed);
            float direction01 = Mathf.Clamp01(SanitizeFinite(directionDot));
            float forceMultiplier = ResolveShoveForceMultiplier(shoveForceMultiplier);

            float tierWeight = ResolveTierWeight01(tier);
            float baseEnergy = Mathf.Max(
                stopThreshold + 0.12f,
                minRamEnergy * Mathf.Lerp(0.92f, 1.45f, tierWeight));
            float speedEnergy = (config != null ? config.RamEnergyFromSpeed : 1.5f)
                * Mathf.Pow(speed01, 0.82f)
                * Mathf.Lerp(0.35f, 0.85f, tierWeight);
            float closingEnergy = closing01 * Mathf.Lerp(0.08f, 0.32f, tierWeight);
            float forceScale = Mathf.Lerp(0.9f, 1.18f, Mathf.Clamp01((forceMultiplier - 1f) / (DefaultMaxShoveForceMultiplier - 1f)));
            float angleScale = Mathf.Lerp(0.72f, 1f, direction01);

            float rawEnergy = (baseEnergy + speedEnergy + closingEnergy) * forceScale * angleScale;
            rawEnergy = Mathf.Max(rawEnergy, SanitizeNonNegativeFinite(currentRamEnergy));

            float hardMinimum = Mathf.Max(stopThreshold + 0.12f, minRamEnergy * 0.72f);
            return Mathf.Clamp(
                rawEnergy,
                hardMinimum,
                Mathf.Max(hardMinimum, maxRamEnergy));
        }

        public static float ComputeImpactEngagementBudgetDeltaV(
            SumoBallPhysicsConfig config,
            SumoImpactTier tier,
            SumoImpactResponseMode responseMode,
            float attackerForwardSpeed,
            float relativeClosingSpeed,
            float directionDot,
            float shoveForceMultiplier)
        {
            float maxImpactSpeed = config != null ? config.MaxImpactSpeed : DefaultMaxImpactSpeed;
            maxImpactSpeed = Mathf.Max(0.01f, maxImpactSpeed);
            float pressureSpeed = Mathf.Max(
                SanitizeNonNegativeFinite(attackerForwardSpeed),
                SanitizeNonNegativeFinite(relativeClosingSpeed) * 0.72f);
            float speed01 = Mathf.Clamp01(pressureSpeed / maxImpactSpeed);
            float direction01 = Mathf.Clamp01(SanitizeFinite(directionDot));
            float tierWeight = ResolveTierWeight01(tier);
            float forceMultiplier = ResolveShoveForceMultiplier(shoveForceMultiplier);

            float baseStep = responseMode == SumoImpactResponseMode.ArcadeBurst
                ? (config != null ? config.ArcadeBurstMaxDeltaVPerTick : 0.48f)
                : (config != null ? config.SoftShoveEntryMaxDeltaVPerTick : 0.13f);
            float responseScale = responseMode == SumoImpactResponseMode.ArcadeBurst
                ? Mathf.Lerp(1.8f, 3.0f, tierWeight)
                : Mathf.Lerp(2.3f, 4.1f, tierWeight);
            float speedScale = Mathf.Lerp(0.88f, 1.45f, speed01);
            float angleScale = Mathf.Lerp(0.58f, 1f, direction01);
            float forceScale = Mathf.Sqrt(forceMultiplier);

            float budget = baseStep * responseScale * speedScale * angleScale * forceScale;
            if (responseMode == SumoImpactResponseMode.ArcadeBurst)
            {
                budget = Mathf.Max(budget, pressureSpeed * Mathf.Lerp(0.10f, 0.22f, tierWeight) * angleScale);
            }
            else
            {
                budget = Mathf.Max(
                    budget,
                    pressureSpeed * Mathf.Lerp(0.12f, 0.20f, tierWeight) * angleScale * Mathf.Sqrt(forceMultiplier));
            }

            return Mathf.Max(0.01f, SanitizeNonNegativeFinite(budget));
        }

        public static float ComputeDiminishingResidualDeltaV(
            float requestedDeltaV,
            float remainingBudget,
            float initialBudget,
            float previousDeltaV,
            SumoImpactResponseMode responseMode)
        {
            float requested = SanitizeNonNegativeFinite(requestedDeltaV);
            float remaining = SanitizeNonNegativeFinite(remainingBudget);
            if (requested <= 0.0001f || remaining <= 0.0001f)
            {
                return 0f;
            }

            float initial = Mathf.Max(0.0001f, SanitizeNonNegativeFinite(initialBudget));
            float energy01 = Mathf.Clamp01(remaining / initial);
            float firstWindow = responseMode == SumoImpactResponseMode.ArcadeBurst ? 0.68f : 0.82f;
            float lowWindow = responseMode == SumoImpactResponseMode.ArcadeBurst ? 0.16f : 0.20f;
            float budgetWindow = Mathf.Lerp(lowWindow, firstWindow, energy01);
            float cappedByBudget = remaining * budgetWindow;

            float previous = SanitizeNonNegativeFinite(previousDeltaV);
            if (previous > 0.0001f)
            {
                float decay = responseMode == SumoImpactResponseMode.ArcadeBurst ? 0.86f : 0.78f;
                cappedByBudget = Mathf.Min(cappedByBudget, previous * decay);
            }

            return Mathf.Min(requested, remaining, Mathf.Max(0f, cappedByBudget));
        }

        public static float ClampDiminishingContactDeltaV(
            float requestedDeltaV,
            float attackerForwardSpeed,
            float victimForwardSpeed,
            float targetSpeedScale,
            float remainingBudget,
            float initialBudget,
            float previousDeltaV,
            SumoImpactResponseMode responseMode)
        {
            float cappedByTarget = Mathf.Min(
                SanitizeNonNegativeFinite(requestedDeltaV),
                ComputeCappedPushDeltaV(attackerForwardSpeed, victimForwardSpeed, targetSpeedScale));

            return ComputeDiminishingResidualDeltaV(
                cappedByTarget,
                remainingBudget,
                initialBudget,
                previousDeltaV,
                responseMode);
        }

        public static float ClampImpactDeltaVStep(float requestedDeltaV, float maxDeltaVPerTick)
        {
            return Mathf.Min(
                Mathf.Max(0f, SanitizeFinite(requestedDeltaV)),
                Mathf.Max(0f, SanitizeFinite(maxDeltaVPerTick)));
        }

        public static float ComputeFirstImpactKickoffShare(float configuredShare, float speed01)
        {
            float share = Mathf.Clamp01(SanitizeFinite(configuredShare));
            float speed = Mathf.Clamp01(SanitizeFinite(speed01));
            float speedSmooth = speed * speed * (3f - 2f * speed);
            return Mathf.Clamp01(share * Mathf.Lerp(0.94f, 1f, speedSmooth));
        }

        public static float ComputeFirstImpactKickoffDeltaV(float remainingDeltaV, float kickoffShare01, float maxKickDeltaV)
        {
            float requestedDeltaV = SanitizeNonNegativeFinite(remainingDeltaV) * Mathf.Clamp01(SanitizeFinite(kickoffShare01));
            return ClampImpactDeltaVStep(requestedDeltaV, maxKickDeltaV);
        }

        public static float ComputeResidualImpactDeltaV(
            float remainingDeltaV,
            float targetForwardSpeed,
            float currentForwardSpeed,
            float segmentWeight,
            float maxDeltaVPerTick)
        {
            float remainingBudget = SanitizeNonNegativeFinite(remainingDeltaV);
            float remainingToTarget = Mathf.Max(
                0f,
                SanitizeFinite(targetForwardSpeed) - SanitizeFinite(currentForwardSpeed));
            float effectiveRemaining = Mathf.Min(remainingBudget, remainingToTarget);
            float requestedDeltaV = effectiveRemaining * Mathf.Clamp01(SanitizeFinite(segmentWeight));
            return ClampImpactDeltaVStep(requestedDeltaV, maxDeltaVPerTick);
        }

        public static bool ShouldApplyRamContactDrive(
            float attackerForwardSpeed,
            float directionDot,
            float physicalClosingSpeed,
            float minPressureSpeed,
            float minDirectionDot,
            float minClosingSpeed)
        {
            return SanitizeNonNegativeFinite(attackerForwardSpeed) >= Mathf.Max(0f, SanitizeFinite(minPressureSpeed))
                && Mathf.Clamp01(SanitizeFinite(directionDot)) >= Mathf.Clamp01(SanitizeFinite(minDirectionDot))
                && SanitizeNonNegativeFinite(physicalClosingSpeed) >= Mathf.Max(0f, SanitizeFinite(minClosingSpeed));
        }

        public static bool ShouldUseFirstImpactVisualLaunch(SumoImpactResponseMode responseMode)
        {
            return responseMode == SumoImpactResponseMode.ArcadeBurst;
        }

        public static bool ShouldApplyPredictedVictimPush(
            bool isPredicted,
            bool attackerHasInputAuthority,
            bool victimHasInputAuthority,
            bool victimCanApplyPredictedProxyForces,
            bool hasLocalContact)
        {
            return !isPredicted
                || (attackerHasInputAuthority
                    && !victimHasInputAuthority
                    && victimCanApplyPredictedProxyForces
                    && hasLocalContact);
        }

        public static SumoAttackerDecision ResolvePredictedLocalAttacker(
            bool isPredicted,
            SumoAttackerDecision currentDecision,
            SumoAttackerRole localRole,
            bool localHasInputAuthority,
            bool remoteHasInputAuthority,
            float localPressure,
            float localIntentPressure,
            float remotePressure,
            float minLocalPressure,
            float tieSpeedEpsilon)
        {
            if (!isPredicted
                || !localHasInputAuthority
                || remoteHasInputAuthority
                || (localRole != SumoAttackerRole.First && localRole != SumoAttackerRole.Second))
            {
                return currentDecision;
            }

            float minimum = Mathf.Max(0f, SanitizeFinite(minLocalPressure));
            float pressure = SanitizeNonNegativeFinite(localPressure);
            float intentPressure = SanitizeNonNegativeFinite(localIntentPressure);
            float remote = SanitizeNonNegativeFinite(remotePressure);
            float epsilon = Mathf.Max(0f, SanitizeFinite(tieSpeedEpsilon));

            bool hasForwardIntent = intentPressure >= minimum;
            bool hasLocalPressure = Mathf.Max(pressure, intentPressure) >= minimum;
            if (!hasLocalPressure)
            {
                return currentDecision;
            }

            if (hasForwardIntent || pressure + epsilon >= remote)
            {
                return new SumoAttackerDecision(localRole, SumoTieResolvedBy.SpeedDelta);
            }

            return currentDecision;
        }

        public static bool ShouldApplyPredictedAttackerRecoil(
            bool isPredicted,
            bool attackerHasInputAuthority)
        {
            return !isPredicted || attackerHasInputAuthority;
        }

        public static SumoImpactTier ResolveImpactTier(
            SumoBallPhysicsConfig config,
            float attackerTopSpeed,
            float speed,
            SumoImpactTier previousTier,
            bool hasPreviousTier,
            out SumoImpactTierThresholds thresholds)
        {
            thresholds = ComputeImpactTierThresholds(config, attackerTopSpeed);
            float resolvedSpeed = SanitizeNonNegativeFinite(speed);
            if (!hasPreviousTier || previousTier == SumoImpactTier.Unknown)
            {
                return ResolveTierWithoutHysteresis(resolvedSpeed, thresholds.LowUpper, thresholds.HighStart);
            }

            float hysteresisShare = config != null
                ? config.TierHysteresisShare01
                : DefaultTierHysteresisShare01;
            float backstepDeadZoneShare = config != null
                ? config.BackstepDeadZoneShare01
                : DefaultBackstepDeadZoneShare01;

            float hysteresis = thresholds.TierRefSpeed * Mathf.Max(0f, hysteresisShare);
            float demotePadding = thresholds.TierRefSpeed * Mathf.Max(0f, backstepDeadZoneShare);
            float demoteMargin = Mathf.Max(hysteresis, demotePadding);

            switch (previousTier)
            {
                case SumoImpactTier.Low:
                    if (resolvedSpeed < thresholds.LowUpper + hysteresis)
                    {
                        return SumoImpactTier.Low;
                    }

                    return resolvedSpeed < thresholds.HighStart
                        ? SumoImpactTier.Mid
                        : SumoImpactTier.High;

                case SumoImpactTier.Mid:
                    if (resolvedSpeed >= thresholds.HighStart + hysteresis)
                    {
                        return SumoImpactTier.High;
                    }

                    if (resolvedSpeed < thresholds.LowUpper - demoteMargin)
                    {
                        return SumoImpactTier.Low;
                    }

                    return SumoImpactTier.Mid;

                case SumoImpactTier.High:
                    if (resolvedSpeed >= thresholds.HighStart - demoteMargin)
                    {
                        return SumoImpactTier.High;
                    }

                    return resolvedSpeed < thresholds.LowUpper
                        ? SumoImpactTier.Low
                        : SumoImpactTier.Mid;

                default:
                    return ResolveTierWithoutHysteresis(resolvedSpeed, thresholds.LowUpper, thresholds.HighStart);
            }
        }

        public static SumoImpactTierThresholds ComputeImpactTierThresholds(
            SumoBallPhysicsConfig config,
            float attackerTopSpeed)
        {
            float maxImpactSpeed = config != null ? config.MaxImpactSpeed : DefaultMaxImpactSpeed;
            maxImpactSpeed = Mathf.Max(0.01f, maxImpactSpeed);

            float rawTopSpeed = SanitizeNonNegativeFinite(attackerTopSpeed);
            if (rawTopSpeed <= 0.0001f)
            {
                rawTopSpeed = maxImpactSpeed;
            }

            float tierRefSpeed = Mathf.Max(0.01f, Mathf.Min(rawTopSpeed, maxImpactSpeed));

            float lowShare = config != null ? config.LowTierShare01 : DefaultLowTierShare01;
            float midShare = config != null ? config.MidTierShare01 : DefaultMidTierShare01;
            bool useNormalizedThresholds = config != null
                ? config.UseNormalizedTierThresholds
                : DefaultUseNormalizedTierThresholds;

            if (useNormalizedThresholds)
            {
                lowShare = Mathf.Clamp01(lowShare);
                midShare = Mathf.Clamp(midShare, 0f, 1f - lowShare);
            }
            else
            {
                lowShare = Mathf.Max(0f, lowShare);
                midShare = Mathf.Max(0f, midShare);
                float total = lowShare + midShare;
                if (total > 1f)
                {
                    lowShare /= total;
                    midShare /= total;
                }
            }

            float lowUpper = tierRefSpeed * lowShare;
            float highStart = tierRefSpeed * Mathf.Clamp01(lowShare + midShare);
            return new SumoImpactTierThresholds(tierRefSpeed, lowUpper, highStart);
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
            float topSpeedBonus = config != null ? config.ImpactTopSpeedBonus : 1.6f;

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
            float headOn = config != null ? config.HeadOnImpactMultiplier : 2.45f;
            float glancing = config != null ? config.GlancingImpactMultiplier : 0.3f;
            float angleScale = Mathf.Lerp(glancing, headOn, shapedDirection);

            float closing01 = Mathf.Clamp01(relativeClosingSpeed / Mathf.Max(0.01f, maxImpactSpeed));
            float relativeClosingScale = 1f + (config != null ? config.RelativeClosingBonus : 0.28f) * closing01;

            float dash = Mathf.Max(1f, dashMultiplier);
            float dashScale = Mathf.Lerp(1f, dash, speedCurve01);

            float baseImpactImpulse = config != null ? config.BaseImpactImpulse : 44f;
            float maxImpactImpulse = config != null ? config.MaxImpactImpulse : 105f;
            float topSpeedBonus = config != null ? config.ImpactTopSpeedBonus : 1.6f;
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
            float maxArcadeBoost = config != null ? config.FirstImpactArcadeBoost : 6.2f;
            float maxArcadeCapBoost = config != null ? config.FirstImpactArcadeCapBoost : 4.8f;
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
                float ramBaseAcceleration = config != null ? config.RamBaseAcceleration : 18f;
                float ramMaxAcceleration = config != null ? config.RamMaxAcceleration : 30f;
                victimAcceleration = ramBaseAcceleration * shapedPressure * angleForceScale * ramForceScale * contactBlendShaped;
                victimAcceleration = Mathf.Clamp(victimAcceleration, 0f, Mathf.Max(ramBaseAcceleration, ramMaxAcceleration));
            }

            float attackerDragScale = config != null ? config.RamAttackerDragScale : 0.24f;
            float attackerAcceleration = victimAcceleration * Mathf.Clamp01(attackerDragScale);

            float baseDecay = config != null ? config.RamBaseDecayPerSecond : 1.55f;
            float lowPressureDecay = config != null ? config.RamPressureDecayPerSecond : 1.35f;
            float angleDecay = config != null ? config.RamAngleDecayPerSecond : 1.4f;
            float noPressureDecay = config != null ? config.RamNoPressureDecayPerSecond : 4f;
            noPressureDecay = Mathf.Clamp(noPressureDecay, 0f, 3.6f);
            float accelerationEnergyCost = config != null ? config.RamAccelerationEnergyCost : 0.85f;

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

        private static SumoImpactTier ResolveTierWithoutHysteresis(
            float speed,
            float lowUpper,
            float highStart)
        {
            if (speed < lowUpper)
            {
                return SumoImpactTier.Low;
            }

            if (speed < highStart)
            {
                return SumoImpactTier.Mid;
            }

            return SumoImpactTier.High;
        }

        private static float ResolveTierWeight01(SumoImpactTier tier)
        {
            switch (tier)
            {
                case SumoImpactTier.Low:
                    return 0f;

                case SumoImpactTier.Mid:
                    return 0.5f;

                case SumoImpactTier.High:
                    return 1f;

                default:
                    return 0.25f;
            }
        }

        private static float SanitizeNonNegativeFinite(float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value) || value < 0f)
            {
                return 0f;
            }

            return value;
        }

        private static float SanitizeFinite(float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
            {
                return 0f;
            }

            return value;
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
            float maxImpactImpulse = config != null ? config.MaxImpactImpulse : 105f;

            float speed01 = Mathf.InverseLerp(
                minRamStartSpeed,
                Mathf.Max(minRamStartSpeed + 0.01f, maxImpactSpeed),
                attackerForwardSpeed);

            float impact01 = Mathf.Clamp01(firstImpactForce / Mathf.Max(0.01f, maxImpactImpulse));
            float energyFromImpact = (config != null ? config.RamEnergyFromImpact : 0.2f) * firstImpactForce;
            float energyFromSpeed = (config != null ? config.RamEnergyFromSpeed : 1.5f) * Mathf.Pow(speed01, 0.9f);
            float energyFromDash = (config != null ? config.RamEnergyFromDash : 0.55f) * Mathf.Max(0f, dashScale - 1f);

            float rawEnergy = energyFromImpact + energyFromSpeed + energyFromDash;
            rawEnergy *= Mathf.Lerp(0.65f, 1f, Mathf.Clamp01(directionDot));
            rawEnergy *= 0.52f;
            rawEnergy = Mathf.Max(rawEnergy, impact01 * (config != null ? config.RamMinEnergy : 0.5f));

            float minRamEnergy = config != null ? config.RamMinEnergy : 0.5f;
            float maxRamEnergy = config != null ? config.RamMaxEnergy : 7.6f;
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
