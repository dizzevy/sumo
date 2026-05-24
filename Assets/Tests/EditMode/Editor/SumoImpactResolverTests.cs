using System.Reflection;
using NUnit.Framework;
using Sumo;
using UnityEngine;

namespace Sumo.Tests
{
    public sealed class SumoImpactResolverTests
    {
        [Test]
        public void ResolveAttacker_FasterPressureWins()
        {
            SumoAttackerDecision decision = SumoImpactResolver.ResolveAttacker(
                10f,
                2f,
                0.15f,
                false,
                false,
                false,
                1,
                2);

            Assert.AreEqual(SumoAttackerRole.First, decision.Role);
            Assert.AreEqual(SumoTieResolvedBy.SpeedDelta, decision.TieResolvedBy);
        }

        [Test]
        public void ResolveAttacker_EqualSpeedIgnoresExistingOwnerAndKeyFallback()
        {
            SumoAttackerDecision decision = SumoImpactResolver.ResolveAttacker(
                7f,
                7.1f,
                0.15f,
                true,
                true,
                true,
                1,
                2);

            Assert.AreEqual(SumoAttackerRole.Neutral, decision.Role);
            Assert.AreEqual(SumoTieResolvedBy.NeutralWithinEpsilon, decision.TieResolvedBy);
            Assert.IsFalse(decision.HasAttacker);
        }

        [Test]
        public void ResolveHighSpeedFatsoIncomingMultiplier_LowAndMidTiersKeepBaseResistance()
        {
            SumoImpactTierThresholds thresholds = new SumoImpactTierThresholds(10f, 7.7f, 9.2f);

            Assert.AreEqual(
                0.30f,
                SumoImpactResolver.ResolveHighSpeedFatsoIncomingMultiplier(
                    0.30f,
                    true,
                    SumoImpactTier.Low,
                    6f,
                    thresholds),
                0.0001f);
            Assert.AreEqual(
                0.30f,
                SumoImpactResolver.ResolveHighSpeedFatsoIncomingMultiplier(
                    0.30f,
                    true,
                    SumoImpactTier.Mid,
                    8.5f,
                    thresholds),
                0.0001f);
        }

        [Test]
        public void ResolveHighSpeedFatsoIncomingMultiplier_HighTierStartPiercesToFloor()
        {
            SumoImpactTierThresholds thresholds = new SumoImpactTierThresholds(10f, 7.7f, 9.2f);

            float multiplier = SumoImpactResolver.ResolveHighSpeedFatsoIncomingMultiplier(
                0.30f,
                true,
                SumoImpactTier.High,
                9.2f,
                thresholds);

            Assert.AreEqual(0.55f, multiplier, 0.0001f);
        }

        [Test]
        public void ResolveHighSpeedFatsoIncomingMultiplier_MaxReferenceSpeedPiercesToCeiling()
        {
            SumoImpactTierThresholds thresholds = new SumoImpactTierThresholds(10f, 7.7f, 9.2f);

            float multiplier = SumoImpactResolver.ResolveHighSpeedFatsoIncomingMultiplier(
                0.30f,
                true,
                SumoImpactTier.High,
                10f,
                thresholds);

            Assert.AreEqual(0.90f, multiplier, 0.0001f);
        }

        [Test]
        public void ShouldUseHighSpeedFatsoCounter_RequiresActiveFatsoVictimAndNonFatsoAttacker()
        {
            Assert.IsTrue(SumoImpactResolver.ShouldUseHighSpeedFatsoCounter(
                candidateIsFatso: false,
                targetIsActiveFatso: true,
                candidateTier: SumoImpactTier.High));
            Assert.IsFalse(SumoImpactResolver.ShouldUseHighSpeedFatsoCounter(
                candidateIsFatso: false,
                targetIsActiveFatso: false,
                candidateTier: SumoImpactTier.High));
            Assert.IsFalse(SumoImpactResolver.ShouldUseHighSpeedFatsoCounter(
                candidateIsFatso: true,
                targetIsActiveFatso: true,
                candidateTier: SumoImpactTier.High));
            Assert.IsFalse(SumoImpactResolver.ShouldUseHighSpeedFatsoCounter(
                candidateIsFatso: false,
                targetIsActiveFatso: true,
                candidateTier: SumoImpactTier.Mid));
        }

        [Test]
        public void ResolveHighSpeedFatsoCounterAttacker_NonFatsoHighSpeedHitOverridesFatsoPressureFloor()
        {
            SumoImpactTier counterTier = SumoImpactResolver.ResolveImpactTier(
                null,
                attackerTopSpeed: 10f,
                speed: 9.2f,
                SumoImpactTier.Unknown,
                false,
                out _);
            SumoAttackerDecision baseDecision = SumoImpactResolver.ResolveAttacker(
                9.2f,
                13.5f,
                0.15f,
                false,
                false,
                false,
                1,
                2);
            bool firstCanCounterFatso = SumoImpactResolver.ShouldUseHighSpeedFatsoCounter(
                candidateIsFatso: false,
                targetIsActiveFatso: true,
                candidateTier: counterTier);

            SumoAttackerDecision resolvedDecision = SumoImpactResolver.ResolveHighSpeedFatsoCounterAttacker(
                baseDecision,
                firstCanCounterFatso,
                false);

            Assert.AreEqual(SumoImpactTier.High, counterTier);
            Assert.AreEqual(SumoAttackerRole.Second, baseDecision.Role);
            Assert.AreEqual(SumoAttackerRole.First, resolvedDecision.Role);
            Assert.AreEqual(SumoTieResolvedBy.SpeedDelta, resolvedDecision.TieResolvedBy);
        }

        [Test]
        public void ComputeCappedPushTargetSpeed_RearPushDoesNotAddSpeeds()
        {
            float targetSpeed = SumoImpactResolver.ComputeCappedPushTargetSpeed(
                attackerForwardSpeed: 10f,
                victimForwardSpeed: 8f);

            Assert.AreEqual(10f, targetSpeed, 0.0001f);
        }

        [Test]
        public void ComputeCappedPushDeltaV_HeadOnResistanceStillGetsBounce()
        {
            float deltaV = SumoImpactResolver.ComputeCappedPushDeltaV(
                attackerForwardSpeed: 10f,
                victimForwardSpeed: -1f);

            Assert.AreEqual(11f, deltaV, 0.0001f);
        }

        [Test]
        public void ComputeCappedPushDeltaV_DoesNotPushVictimPastAttackerSpeed()
        {
            float victimForwardSpeed = 3.75f;
            float deltaV = SumoImpactResolver.ComputeCappedPushDeltaV(
                attackerForwardSpeed: 5f,
                victimForwardSpeed: victimForwardSpeed);

            Assert.AreEqual(5f, victimForwardSpeed + deltaV, 0.0001f);
        }

        [Test]
        public void ComputeCappedPhysicalImpactSpeed_UsesOnlyPhysicalSamples()
        {
            float inputIntentPressure = 20f;
            float impactSpeed = SumoImpactResolver.ComputeCappedPhysicalImpactSpeed(
                null,
                entryPhysicalForwardSpeed: 0.75f,
                currentPhysicalForwardSpeed: 1.25f);

            Assert.AreEqual(1.25f, impactSpeed, 0.0001f);
            Assert.Less(impactSpeed, inputIntentPressure);
        }

        [Test]
        public void ComputeCappedPhysicalImpactSpeed_ClampsToMaxImpactSpeed()
        {
            float impactSpeed = SumoImpactResolver.ComputeCappedPhysicalImpactSpeed(
                null,
                entryPhysicalForwardSpeed: 6f,
                currentPhysicalForwardSpeed: 25f);

            Assert.AreEqual(10f, impactSpeed, 0.0001f);
        }

        [Test]
        public void ResolveImpactResponseMode_NonDashLowAndMidContactUsesSoftShove()
        {
            Assert.AreEqual(
                SumoImpactResponseMode.SoftShove,
                SumoImpactResolver.ResolveImpactResponseMode(null, physicalImpactSpeed: 4.9f, isDashing: false));
        }

        [Test]
        public void ResolveImpactResponseMode_FullSpeedNonDashContactUsesArcadeBurst()
        {
            Assert.AreEqual(
                SumoImpactResponseMode.ArcadeBurst,
                SumoImpactResolver.ResolveImpactResponseMode(null, physicalImpactSpeed: 5f, isDashing: false));
        }

        [Test]
        public void ResolveImpactResponseMode_DashUsesLowerArcadeBurstThreshold()
        {
            Assert.AreEqual(
                SumoImpactResponseMode.ArcadeBurst,
                SumoImpactResolver.ResolveImpactResponseMode(null, physicalImpactSpeed: 2.5f, isDashing: true));
        }

        [Test]
        public void ComputeSoftShoveEntryNudgeDeltaV_ProducesCappedVictimNudge()
        {
            float deltaV = SumoImpactResolver.ComputeSoftShoveEntryNudgeDeltaV(
                physicalImpactSpeed: 2.5f,
                relativeClosingSpeed: 2f,
                victimForwardSpeed: 0f,
                maxDeltaVPerTick: 0.12f);

            Assert.AreEqual(0.12f, deltaV, 0.0001f);
        }

        [Test]
        public void ResolveTierShoveMultiplier_DefaultsMatchRequestedTuning()
        {
            Assert.AreEqual(2f, SumoImpactResolver.ResolveTierShoveMultiplier(null, SumoImpactTier.Low), 0.0001f);
            Assert.AreEqual(2f, SumoImpactResolver.ResolveTierShoveMultiplier(null, SumoImpactTier.Mid), 0.0001f);
            Assert.AreEqual(6.8f, SumoImpactResolver.ResolveTierShoveMultiplier(null, SumoImpactTier.High), 0.0001f);
            Assert.AreEqual(1f, SumoImpactResolver.ResolveTierShoveMultiplier(null, SumoImpactTier.Unknown), 0.0001f);
        }

        [Test]
        public void ComputeSoftShoveEntryNudgeDeltaV_MultiplierScalesRequestedNudge()
        {
            float deltaV = SumoImpactResolver.ComputeSoftShoveEntryNudgeDeltaV(
                physicalImpactSpeed: 0.1f,
                relativeClosingSpeed: 0f,
                victimForwardSpeed: 0f,
                maxDeltaVPerTick: 1f,
                targetSpeedScale: 2f);

            Assert.AreEqual(0.144f, deltaV, 0.0001f);
        }

        [Test]
        public void ComputeSoftShoveEntryNudgeDeltaV_MultiplierUsesScaledCap()
        {
            float multiplier = SumoImpactResolver.ResolveTierShoveMultiplier(null, SumoImpactTier.Low);
            float deltaV = SumoImpactResolver.ComputeSoftShoveEntryNudgeDeltaV(
                physicalImpactSpeed: 2.5f,
                relativeClosingSpeed: 2f,
                victimForwardSpeed: 0f,
                maxDeltaVPerTick: 0.12f * multiplier,
                targetSpeedScale: multiplier);

            Assert.AreEqual(0.24f, deltaV, 0.0001f);
        }

        [Test]
        public void ClampImpactDeltaVStep_HighTierUsesArcadePlusBurstCap()
        {
            float highMultiplier = SumoImpactResolver.ResolveTierShoveMultiplier(null, SumoImpactTier.High);
            float deltaV = SumoImpactResolver.ClampImpactDeltaVStep(
                requestedDeltaV: 5f,
                maxDeltaVPerTick: 0.48f * highMultiplier);

            Assert.AreEqual(3.264f, deltaV, 0.0001f);
        }

        [Test]
        public void ApplyShoveForceMultiplier_ScalesCatchupEnvelopeBudget()
        {
            float highMultiplier = SumoImpactResolver.ResolveTierShoveMultiplier(null, SumoImpactTier.High);

            Assert.AreEqual(0.68f, SumoImpactResolver.ApplyShoveForceMultiplier(0.10f, highMultiplier), 0.0001f);
        }

        [Test]
        public void ResolveShoveForceMultiplier_ClampsArcadePlusCeiling()
        {
            Assert.AreEqual(6.8f, SumoImpactResolver.ResolveShoveForceMultiplier(99f), 0.0001f);
        }

        [Test]
        public void ShouldUseFirstImpactVisualLaunch_OnlyForArcadeBurst()
        {
            Assert.IsFalse(SumoImpactResolver.ShouldUseFirstImpactVisualLaunch(SumoImpactResponseMode.SoftShove));
            Assert.IsTrue(SumoImpactResolver.ShouldUseFirstImpactVisualLaunch(SumoImpactResponseMode.ArcadeBurst));
        }

        [Test]
        public void ClampImpactDeltaVStep_ClampsBurstDeltaToConfiguredCap()
        {
            float deltaV = SumoImpactResolver.ClampImpactDeltaVStep(
                requestedDeltaV: 0.5f,
                maxDeltaVPerTick: 0.48f);

            Assert.AreEqual(0.48f, deltaV, 0.0001f);
        }

        [Test]
        public void ClampImpactDeltaVStep_NonFiniteOrNegativeValuesStaySafe()
        {
            Assert.AreEqual(0f, SumoImpactResolver.ClampImpactDeltaVStep(-2f, 0.48f), 0.0001f);
            Assert.AreEqual(0f, SumoImpactResolver.ClampImpactDeltaVStep(float.PositiveInfinity, 0.48f), 0.0001f);
            Assert.AreEqual(0f, SumoImpactResolver.ClampImpactDeltaVStep(0.48f, float.NaN), 0.0001f);
        }

        [Test]
        public void ShouldStartReImpact_RequiresBreakCooldownEnergyAndDelta()
        {
            Assert.IsFalse(SumoImpactResolver.ShouldStartReImpact(
                currentTick: 12,
                lastImpactTick: 4,
                breakStartTick: 0,
                maxSeparationSinceBreak: 0.06f,
                ramEnergy: 0.8f,
                initialRamEnergy: 2f,
                cappedDeltaV: 0.08f,
                attackerStillWins: true,
                directionValid: true));
            Assert.IsFalse(SumoImpactResolver.ShouldStartReImpact(
                currentTick: 7,
                lastImpactTick: 4,
                breakStartTick: 5,
                maxSeparationSinceBreak: 0.06f,
                ramEnergy: 0.8f,
                initialRamEnergy: 2f,
                cappedDeltaV: 0.08f,
                attackerStillWins: true,
                directionValid: true));
            Assert.IsFalse(SumoImpactResolver.ShouldStartReImpact(
                currentTick: 12,
                lastImpactTick: 4,
                breakStartTick: 10,
                maxSeparationSinceBreak: 0.06f,
                ramEnergy: 0.6f,
                initialRamEnergy: 2f,
                cappedDeltaV: 0.08f,
                attackerStillWins: true,
                directionValid: true));
            Assert.IsFalse(SumoImpactResolver.ShouldStartReImpact(
                currentTick: 12,
                lastImpactTick: 4,
                breakStartTick: 10,
                maxSeparationSinceBreak: 0.06f,
                ramEnergy: 0.8f,
                initialRamEnergy: 2f,
                cappedDeltaV: 0.02f,
                attackerStillWins: true,
                directionValid: true));
            Assert.IsFalse(SumoImpactResolver.ShouldStartReImpact(
                currentTick: 12,
                lastImpactTick: 4,
                breakStartTick: 10,
                maxSeparationSinceBreak: 0.06f,
                ramEnergy: 0.8f,
                initialRamEnergy: 2f,
                cappedDeltaV: 0.08f,
                attackerStillWins: false,
                directionValid: true));

            Assert.IsTrue(SumoImpactResolver.ShouldStartReImpact(
                currentTick: 12,
                lastImpactTick: 4,
                breakStartTick: 10,
                maxSeparationSinceBreak: 0.06f,
                ramEnergy: 0.8f,
                initialRamEnergy: 2f,
                cappedDeltaV: 0.08f,
                attackerStillWins: true,
                directionValid: true));
        }

        [Test]
        public void ComputeReImpactImpulseScale_DecreasesWithEnergy()
        {
            float lowScale = SumoImpactResolver.ComputeReImpactImpulseScale(0.68f, 2f);
            float midScale = SumoImpactResolver.ComputeReImpactImpulseScale(1.2f, 2f);
            float highScale = SumoImpactResolver.ComputeReImpactImpulseScale(2f, 2f);

            Assert.AreEqual(0f, SumoImpactResolver.ComputeReImpactImpulseScale(0.6f, 2f), 0.0001f);
            Assert.AreEqual(0.28f, lowScale, 0.0001f);
            Assert.Less(lowScale, midScale);
            Assert.Less(midScale, highScale);
            Assert.AreEqual(0.72f, highScale, 0.0001f);
        }

        [Test]
        public void ComputeRemainingRamEnergyAfterReImpact_SpendsWithoutIncreasingAndLeavesReserve()
        {
            float initialEnergy = 2f;
            float fullEnergyRemaining = SumoImpactResolver.ComputeRemainingRamEnergyAfterReImpact(2f, initialEnergy);
            float thresholdEnergyRemaining = SumoImpactResolver.ComputeRemainingRamEnergyAfterReImpact(0.68f, initialEnergy);

            Assert.LessOrEqual(fullEnergyRemaining, 2f);
            Assert.AreEqual(1.32f, fullEnergyRemaining, 0.0001f);
            Assert.AreEqual(0.48f, thresholdEnergyRemaining, 0.0001f);
            Assert.IsFalse(SumoImpactResolver.ShouldStartReImpact(
                currentTick: 20,
                lastImpactTick: 12,
                breakStartTick: 18,
                maxSeparationSinceBreak: 0.06f,
                ramEnergy: thresholdEnergyRemaining,
                initialRamEnergy: initialEnergy,
                cappedDeltaV: 0.08f,
                attackerStillWins: true,
                directionValid: true));
        }

        [Test]
        public void ComputeReImpactDeltaV_RespectsCappedPushTarget()
        {
            float victimForwardSpeed = 4f;
            float cappedPushDeltaV = SumoImpactResolver.ComputeCappedPushDeltaV(
                attackerForwardSpeed: 5f,
                victimForwardSpeed: victimForwardSpeed);

            float deltaV = SumoImpactResolver.ComputeReImpactDeltaV(
                requestedDeltaV: 10f,
                cappedPushDeltaV: cappedPushDeltaV,
                energyScale: 0.72f,
                maxDeltaVPerTick: 10f);

            Assert.AreEqual(1f, deltaV, 0.0001f);
            Assert.AreEqual(5f, victimForwardSpeed + deltaV, 0.0001f);
        }

        [Test]
        public void ComputeEnergyScaledRamDeltaVCap_DecreasesWithEnergy()
        {
            float low = SumoImpactResolver.ComputeEnergyScaledRamDeltaVCap(0.24f, 0.2f);
            float mid = SumoImpactResolver.ComputeEnergyScaledRamDeltaVCap(0.24f, 0.55f);
            float high = SumoImpactResolver.ComputeEnergyScaledRamDeltaVCap(0.24f, 1f);

            Assert.Less(low, mid);
            Assert.Less(mid, high);
            Assert.AreEqual(0.24f, high, 0.0001f);
        }

        [Test]
        public void ComputeImpactToRamHandoffScale_RampsFromFloorToOne()
        {
            float start = SumoImpactResolver.ComputeImpactToRamHandoffScale(
                currentTick: 10,
                handoffStartTick: 10,
                handoffDurationTicks: 5,
                startScale: 0.34f);
            float mid = SumoImpactResolver.ComputeImpactToRamHandoffScale(
                currentTick: 12,
                handoffStartTick: 10,
                handoffDurationTicks: 5,
                startScale: 0.34f);
            float end = SumoImpactResolver.ComputeImpactToRamHandoffScale(
                currentTick: 15,
                handoffStartTick: 10,
                handoffDurationTicks: 5,
                startScale: 0.34f);

            Assert.AreEqual(0.34f, start, 0.0001f);
            Assert.Less(start, mid);
            Assert.Less(mid, end);
            Assert.AreEqual(1f, end, 0.0001f);
        }

        [Test]
        public void ComputeImpactToRamHandoffScale_InvalidOrExpiredReturnsOne()
        {
            Assert.AreEqual(1f, SumoImpactResolver.ComputeImpactToRamHandoffScale(
                currentTick: 10,
                handoffStartTick: -1,
                handoffDurationTicks: 5,
                startScale: 0.34f), 0.0001f);
            Assert.AreEqual(1f, SumoImpactResolver.ComputeImpactToRamHandoffScale(
                currentTick: 10,
                handoffStartTick: 5,
                handoffDurationTicks: 0,
                startScale: 0.34f), 0.0001f);
            Assert.AreEqual(1f, SumoImpactResolver.ComputeImpactToRamHandoffScale(
                currentTick: 10,
                handoffStartTick: 5,
                handoffDurationTicks: 5,
                startScale: 0.34f), 0.0001f);
        }

        [Test]
        public void ComputeImpactToRamHandoffScale_ClampsToUnitRange()
        {
            float negativeFloor = SumoImpactResolver.ComputeImpactToRamHandoffScale(
                currentTick: 10,
                handoffStartTick: 10,
                handoffDurationTicks: 5,
                startScale: -2f);
            float tooLargeFloor = SumoImpactResolver.ComputeImpactToRamHandoffScale(
                currentTick: 10,
                handoffStartTick: 10,
                handoffDurationTicks: 5,
                startScale: 2f);

            Assert.AreEqual(0f, negativeFloor, 0.0001f);
            Assert.AreEqual(1f, tooLargeFloor, 0.0001f);
        }

        [Test]
        public void ComputeEnergyScaledPushTargetSpeed_DecreasesWithEnergy()
        {
            float victimForwardSpeed = 0f;
            float low = SumoImpactResolver.ComputeEnergyScaledPushTargetSpeed(
                attackerForwardSpeed: 5f,
                victimForwardSpeed: victimForwardSpeed,
                targetSpeedScale: 2f,
                energy01: 0.2f);
            float high = SumoImpactResolver.ComputeEnergyScaledPushTargetSpeed(
                attackerForwardSpeed: 5f,
                victimForwardSpeed: victimForwardSpeed,
                targetSpeedScale: 2f,
                energy01: 1f);

            Assert.Less(low, high);
            Assert.AreEqual(10f, high, 0.0001f);
        }

        [Test]
        public void ComputeMonotonicReImpactDeltaV_DoesNotExceedPreviousImpact()
        {
            float deltaV = SumoImpactResolver.ComputeMonotonicReImpactDeltaV(
                requestedDeltaV: 20f,
                cappedPushDeltaV: 12f,
                energyScale: 1f,
                maxDeltaVPerTick: 10f,
                previousImpactDeltaV: 0.75f);

            Assert.AreEqual(0.75f, deltaV, 0.0001f);
        }

        [Test]
        public void ShouldStartReImpact_RejectsMicroBreakWithReengageThresholds()
        {
            Assert.IsFalse(SumoImpactResolver.ShouldStartReImpact(
                currentTick: 20,
                lastImpactTick: 10,
                breakStartTick: 18,
                maxSeparationSinceBreak: 0.04f,
                ramEnergy: 1f,
                initialRamEnergy: 2f,
                cappedDeltaV: 0.08f,
                attackerStillWins: true,
                directionValid: true,
                minBreakTicks: 4,
                minSeparation: 0.18f));

            Assert.IsTrue(SumoImpactResolver.ShouldStartReImpact(
                currentTick: 24,
                lastImpactTick: 10,
                breakStartTick: 18,
                maxSeparationSinceBreak: 0.2f,
                ramEnergy: 1f,
                initialRamEnergy: 2f,
                cappedDeltaV: 0.08f,
                attackerStillWins: true,
                directionValid: true,
                minBreakTicks: 4,
                minSeparation: 0.18f));
        }

        [Test]
        public void ShouldApplyPredictedVictimPush_RequiresContactAndAttackerOwnedRemoteProxy()
        {
            Assert.IsTrue(SumoImpactResolver.ShouldApplyPredictedVictimPush(
                isPredicted: true,
                attackerHasInputAuthority: true,
                victimHasInputAuthority: false,
                victimCanApplyPredictedProxyForces: true,
                hasLocalContact: true));
            Assert.IsFalse(SumoImpactResolver.ShouldApplyPredictedVictimPush(
                isPredicted: true,
                attackerHasInputAuthority: true,
                victimHasInputAuthority: false,
                victimCanApplyPredictedProxyForces: true,
                hasLocalContact: false));
            Assert.IsFalse(SumoImpactResolver.ShouldApplyPredictedVictimPush(
                isPredicted: true,
                attackerHasInputAuthority: true,
                victimHasInputAuthority: true,
                victimCanApplyPredictedProxyForces: true,
                hasLocalContact: true));
            Assert.IsFalse(SumoImpactResolver.ShouldApplyPredictedVictimPush(
                isPredicted: true,
                attackerHasInputAuthority: false,
                victimHasInputAuthority: false,
                victimCanApplyPredictedProxyForces: true,
                hasLocalContact: true));
            Assert.IsTrue(SumoImpactResolver.ShouldApplyPredictedVictimPush(
                isPredicted: false,
                attackerHasInputAuthority: false,
                victimHasInputAuthority: true,
                victimCanApplyPredictedProxyForces: false,
                hasLocalContact: false));
        }

        [Test]
        public void SelectLocalCameraRenderTarget_PrefersInterpolationTargetOverRawRoot()
        {
            GameObject root = new GameObject("root");
            GameObject interpolationTarget = new GameObject("interpolationTarget");
            GameObject interpolationAnchor = new GameObject("interpolationAnchor");
            GameObject visualShell = new GameObject("visualShell");

            try
            {
                Transform selected = SumoProxyPresentation.SelectLocalCameraRenderTarget(
                    root.transform,
                    interpolationTarget.transform,
                    interpolationAnchor.transform,
                    visualShell.transform);

                Assert.AreSame(interpolationTarget.transform, selected);
                Assert.AreNotSame(root.transform, selected);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(visualShell);
                UnityEngine.Object.DestroyImmediate(interpolationAnchor);
                UnityEngine.Object.DestroyImmediate(interpolationTarget);
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void SmoothFollowPosition_SmoothsSmallDeltaButSnapsLargeCorrection()
        {
            Vector3 currentPosition = Vector3.zero;
            Vector3 smallTarget = Vector3.forward;

            Vector3 smoothed = SumoCameraFollow.SmoothFollowPosition(
                currentPosition,
                smallTarget,
                sharpness: 28f,
                deltaTime: 1f / 60f,
                snapDistance: 1.5f,
                hasCurrentPosition: true);

            Assert.Greater(smoothed.z, currentPosition.z);
            Assert.Less(smoothed.z, smallTarget.z);

            Vector3 largeTarget = Vector3.forward * 2f;
            Vector3 snapped = SumoCameraFollow.SmoothFollowPosition(
                currentPosition,
                largeTarget,
                sharpness: 28f,
                deltaTime: 1f / 60f,
                snapDistance: 1.5f,
                hasCurrentPosition: true);

            Assert.Less(Vector3.Distance(snapped, largeTarget), 0.0001f);
        }

        [Test]
        public void ProxyPresentation_DefaultRemoteMovementUsesDirectPresentationMode()
        {
            GameObject proxyObject = new GameObject("proxy");

            try
            {
                SumoProxyPresentation presentation = proxyObject.AddComponent<SumoProxyPresentation>();
                MethodInfo method = typeof(SumoProxyPresentation).GetMethod(
                    "ShouldUseDirectPresentationMode",
                    BindingFlags.Instance | BindingFlags.NonPublic);

                Assert.IsNotNull(method);
                bool directMode = (bool)method.Invoke(presentation, new object[] { false, 0f });

                Assert.IsTrue(directMode);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(proxyObject);
            }
        }

        [Test]
        public void ResolvePredictedLocalAttacker_LocalFirstWinsDespiteStaleRemotePressure()
        {
            SumoAttackerDecision staleRemoteDecision = new SumoAttackerDecision(
                SumoAttackerRole.Second,
                SumoTieResolvedBy.SpeedDelta);

            SumoAttackerDecision resolved = SumoImpactResolver.ResolvePredictedLocalAttacker(
                isPredicted: true,
                currentDecision: staleRemoteDecision,
                localRole: SumoAttackerRole.First,
                localHasInputAuthority: true,
                remoteHasInputAuthority: false,
                localPressure: 0.8f,
                localIntentPressure: 1.1f,
                remotePressure: 6f,
                minLocalPressure: 0.25f,
                tieSpeedEpsilon: 0.15f);

            Assert.AreEqual(SumoAttackerRole.First, resolved.Role);
            Assert.AreEqual(SumoTieResolvedBy.SpeedDelta, resolved.TieResolvedBy);
        }

        [Test]
        public void ResolvePredictedLocalAttacker_LocalSecondWinsWhenObjectOrderIsReversed()
        {
            SumoAttackerDecision staleRemoteDecision = new SumoAttackerDecision(
                SumoAttackerRole.First,
                SumoTieResolvedBy.SpeedDelta);

            SumoAttackerDecision resolved = SumoImpactResolver.ResolvePredictedLocalAttacker(
                isPredicted: true,
                currentDecision: staleRemoteDecision,
                localRole: SumoAttackerRole.Second,
                localHasInputAuthority: true,
                remoteHasInputAuthority: false,
                localPressure: 0.8f,
                localIntentPressure: 1.1f,
                remotePressure: 6f,
                minLocalPressure: 0.25f,
                tieSpeedEpsilon: 0.15f);

            Assert.AreEqual(SumoAttackerRole.Second, resolved.Role);
            Assert.AreEqual(SumoTieResolvedBy.SpeedDelta, resolved.TieResolvedBy);
        }

        [Test]
        public void ResolvePredictedLocalAttacker_RequiresLocalInputAuthorityAndIntent()
        {
            SumoAttackerDecision staleRemoteDecision = new SumoAttackerDecision(
                SumoAttackerRole.Second,
                SumoTieResolvedBy.SpeedDelta);

            SumoAttackerDecision noAuthority = SumoImpactResolver.ResolvePredictedLocalAttacker(
                isPredicted: true,
                currentDecision: staleRemoteDecision,
                localRole: SumoAttackerRole.First,
                localHasInputAuthority: false,
                remoteHasInputAuthority: false,
                localPressure: 0.8f,
                localIntentPressure: 1.1f,
                remotePressure: 6f,
                minLocalPressure: 0.25f,
                tieSpeedEpsilon: 0.15f);
            SumoAttackerDecision noIntent = SumoImpactResolver.ResolvePredictedLocalAttacker(
                isPredicted: true,
                currentDecision: staleRemoteDecision,
                localRole: SumoAttackerRole.First,
                localHasInputAuthority: true,
                remoteHasInputAuthority: false,
                localPressure: 0.1f,
                localIntentPressure: 0.1f,
                remotePressure: 6f,
                minLocalPressure: 0.25f,
                tieSpeedEpsilon: 0.15f);

            Assert.AreEqual(SumoAttackerRole.Second, noAuthority.Role);
            Assert.AreEqual(SumoAttackerRole.Second, noIntent.Role);
        }

        [Test]
        public void ShouldApplyPredictedAttackerRecoil_AllowsOnlyLocalInputAttacker()
        {
            Assert.IsTrue(SumoImpactResolver.ShouldApplyPredictedAttackerRecoil(
                isPredicted: true,
                attackerHasInputAuthority: true));
            Assert.IsFalse(SumoImpactResolver.ShouldApplyPredictedAttackerRecoil(
                isPredicted: true,
                attackerHasInputAuthority: false));
            Assert.IsTrue(SumoImpactResolver.ShouldApplyPredictedAttackerRecoil(
                isPredicted: false,
                attackerHasInputAuthority: false));
        }

        [Test]
        public void ResolveAttacker_EqualHeadOnPressureStaysNeutral()
        {
            SumoAttackerDecision decision = SumoImpactResolver.ResolveAttacker(
                6f,
                6f,
                0.15f,
                false,
                false,
                false,
                1,
                2);

            Assert.AreEqual(SumoAttackerRole.Neutral, decision.Role);
            Assert.AreEqual(SumoTieResolvedBy.NeutralWithinEpsilon, decision.TieResolvedBy);
        }

        [Test]
        public void ResolveAttacker_RamOwnerCanFlipWhenVictimBecomesFaster()
        {
            SumoAttackerDecision decision = SumoImpactResolver.ResolveAttacker(
                3f,
                6f,
                0.15f,
                true,
                true,
                false,
                1,
                2);

            Assert.AreEqual(SumoAttackerRole.Second, decision.Role);
            Assert.AreEqual(SumoTieResolvedBy.SpeedDelta, decision.TieResolvedBy);
        }

        [Test]
        public void LowTierAdvantageStillProducesPositiveBounceBudget()
        {
            SumoImpactTier tier = SumoImpactResolver.ResolveImpactTier(
                null,
                attackerTopSpeed: 10f,
                speed: 1f,
                SumoImpactTier.Unknown,
                false,
                out _);
            SumoInitialImpactResult impact = SumoImpactResolver.ComputeInitialImpact(
                null,
                attackerForwardSpeed: 1f,
                attackerReferenceTopSpeed: 10f,
                relativeClosingSpeed: 1f,
                directionDot: 1f,
                dashMultiplier: 1f);
            float cappedDeltaV = SumoImpactResolver.ComputeCappedPushDeltaV(1f, 0f);

            Assert.AreEqual(SumoImpactTier.Low, tier);
            Assert.IsTrue(impact.HasImpact);
            Assert.Greater(cappedDeltaV, 0f);
        }
    }
}
