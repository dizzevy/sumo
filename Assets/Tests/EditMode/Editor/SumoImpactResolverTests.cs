using NUnit.Framework;
using Sumo;

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
        public void ShouldApplyPredictedVictimPush_AllowsOnlyAttackerOwnedRemoteProxy()
        {
            Assert.IsTrue(SumoImpactResolver.ShouldApplyPredictedVictimPush(
                isPredicted: true,
                attackerHasInputAuthority: true,
                victimHasInputAuthority: false,
                victimCanApplyPredictedProxyForces: true));
            Assert.IsFalse(SumoImpactResolver.ShouldApplyPredictedVictimPush(
                isPredicted: true,
                attackerHasInputAuthority: true,
                victimHasInputAuthority: true,
                victimCanApplyPredictedProxyForces: true));
            Assert.IsFalse(SumoImpactResolver.ShouldApplyPredictedVictimPush(
                isPredicted: true,
                attackerHasInputAuthority: false,
                victimHasInputAuthority: false,
                victimCanApplyPredictedProxyForces: true));
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
