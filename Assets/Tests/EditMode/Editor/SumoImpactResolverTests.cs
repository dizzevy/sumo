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
        public void ComputeCappedRamDriveSpeed_CannotGrowPastEngagementEntrySpeed()
        {
            float driveSpeed = SumoImpactResolver.ComputeCappedRamDriveSpeed(
                physicalForwardSpeed: 8f,
                engagementEntrySpeed: 3f);

            Assert.AreEqual(3f, driveSpeed, 0.0001f);
        }

        [Test]
        public void ResolveMonotonicRamEnergy_ContinuousEngagementCannotReseedHigher()
        {
            float continuedEnergy = SumoImpactResolver.ResolveMonotonicRamEnergy(
                requestedRamEnergy: 4f,
                currentRamEnergy: 1.25f,
                continuousEngagement: true);
            float freshEnergy = SumoImpactResolver.ResolveMonotonicRamEnergy(
                requestedRamEnergy: 4f,
                currentRamEnergy: 1.25f,
                continuousEngagement: false);

            Assert.AreEqual(1.25f, continuedEnergy, 0.0001f);
            Assert.AreEqual(4f, freshEnergy, 0.0001f);
        }

        [Test]
        public void IsHardBreakQualified_RequiresContinuousQualifiedBreakWindow()
        {
            Assert.IsFalse(SumoImpactResolver.IsHardBreakQualified(
                currentTick: 12,
                qualifiedBreakStartTick: 0,
                requiredBreakTicks: 6,
                maxSeparationSinceBreak: 0.4f,
                requiredSeparation: 0.2f));
            Assert.IsFalse(SumoImpactResolver.IsHardBreakQualified(
                currentTick: 12,
                qualifiedBreakStartTick: 8,
                requiredBreakTicks: 6,
                maxSeparationSinceBreak: 0.4f,
                requiredSeparation: 0.2f));
            Assert.IsTrue(SumoImpactResolver.IsHardBreakQualified(
                currentTick: 14,
                qualifiedBreakStartTick: 8,
                requiredBreakTicks: 6,
                maxSeparationSinceBreak: 0.4f,
                requiredSeparation: 0.2f));
        }

        [Test]
        public void ComputeExhaustedContactStabilizationDeltaV_OnlyCapsInwardVelocity()
        {
            Assert.AreEqual(0f, SumoImpactResolver.ComputeExhaustedContactStabilizationDeltaV(0f, 0.04f), 0.0001f);
            Assert.AreEqual(0.02f, SumoImpactResolver.ComputeExhaustedContactStabilizationDeltaV(0.02f, 0.04f), 0.0001f);
            Assert.AreEqual(0.04f, SumoImpactResolver.ComputeExhaustedContactStabilizationDeltaV(0.4f, 0.04f), 0.0001f);
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
        public void ComputeFirstImpactKickoffDeltaV_UsesLargeContactKickBeforeTail()
        {
            float kickoffShare = SumoImpactResolver.ComputeFirstImpactKickoffShare(
                configuredShare: 0.70f,
                speed01: 0.5f);
            float kickoffDeltaV = SumoImpactResolver.ComputeFirstImpactKickoffDeltaV(
                remainingDeltaV: 3f,
                kickoffShare01: kickoffShare,
                maxKickDeltaV: 1.44f);

            Assert.GreaterOrEqual(kickoffShare, 0.65f);
            Assert.LessOrEqual(kickoffShare, 0.70f);
            Assert.AreEqual(1.44f, kickoffDeltaV, 0.0001f);
        }

        [Test]
        public void ComputeResidualImpactDeltaV_DoesNotOvershootTargetSpeed()
        {
            Assert.AreEqual(
                0f,
                SumoImpactResolver.ComputeResidualImpactDeltaV(
                    remainingDeltaV: 1f,
                    targetForwardSpeed: 4f,
                    currentForwardSpeed: 4f,
                    segmentWeight: 1f,
                    maxDeltaVPerTick: 1f),
                0.0001f);
            Assert.AreEqual(
                0.2f,
                SumoImpactResolver.ComputeResidualImpactDeltaV(
                    remainingDeltaV: 1f,
                    targetForwardSpeed: 4f,
                    currentForwardSpeed: 3.8f,
                    segmentWeight: 1f,
                    maxDeltaVPerTick: 1f),
                0.0001f);
        }

        [Test]
        public void ShouldApplyRamContactDrive_RequiresPhysicalClosingPressure()
        {
            Assert.IsTrue(SumoImpactResolver.ShouldApplyRamContactDrive(
                attackerForwardSpeed: 3f,
                directionDot: 0.8f,
                physicalClosingSpeed: 0.2f,
                minPressureSpeed: 1.6f,
                minDirectionDot: 0.2f,
                minClosingSpeed: 0.12f));
            Assert.IsFalse(SumoImpactResolver.ShouldApplyRamContactDrive(
                attackerForwardSpeed: 3f,
                directionDot: 0.8f,
                physicalClosingSpeed: 0f,
                minPressureSpeed: 1.6f,
                minDirectionDot: 0.2f,
                minClosingSpeed: 0.12f));
        }

        [Test]
        public void ComputeRamTick_ForceDropsAsEnergyDecays()
        {
            SumoRamTickResult fullEnergy = SumoImpactResolver.ComputeRamTick(
                null,
                deltaTime: 1f / 60f,
                ramEnergy: 5f,
                initialRamEnergy: 5f,
                attackerForwardSpeed: 5f,
                directionDot: 1f,
                isPressing: true,
                contactBlend01: 1f);
            SumoRamTickResult partialEnergy = SumoImpactResolver.ComputeRamTick(
                null,
                deltaTime: 1f / 60f,
                ramEnergy: 2f,
                initialRamEnergy: 5f,
                attackerForwardSpeed: 5f,
                directionDot: 1f,
                isPressing: true,
                contactBlend01: 1f);
            SumoRamTickResult lowEnergy = SumoImpactResolver.ComputeRamTick(
                null,
                deltaTime: 1f / 60f,
                ramEnergy: 0.6f,
                initialRamEnergy: 5f,
                attackerForwardSpeed: 5f,
                directionDot: 1f,
                isPressing: true,
                contactBlend01: 1f);

            Assert.Greater(fullEnergy.VictimAcceleration, partialEnergy.VictimAcceleration);
            Assert.Greater(partialEnergy.VictimAcceleration, lowEnergy.VictimAcceleration);
        }

        [Test]
        public void ComputeRamTick_NoPhysicalPressingConsumesEnergyWithoutAcceleration()
        {
            SumoRamTickResult tick = SumoImpactResolver.ComputeRamTick(
                null,
                deltaTime: 1f / 60f,
                ramEnergy: 3f,
                initialRamEnergy: 3f,
                attackerForwardSpeed: 0f,
                directionDot: 1f,
                isPressing: false,
                contactBlend01: 1f);

            Assert.AreEqual(0f, tick.VictimAcceleration, 0.0001f);
            Assert.Greater(tick.EnergyDecay, 0f);
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

        [Test]
        public void ComputeTierAwareRamSeedEnergy_LowMediumAndHighStayAboveStopThreshold()
        {
            float lowSeed = SumoImpactResolver.ComputeTierAwareRamSeedEnergy(
                null,
                SumoImpactTier.Low,
                attackerForwardSpeed: 1.6f,
                relativeClosingSpeed: 1.4f,
                directionDot: 1f,
                shoveForceMultiplier: 2f);
            float midSeed = SumoImpactResolver.ComputeTierAwareRamSeedEnergy(
                null,
                SumoImpactTier.Mid,
                attackerForwardSpeed: 4f,
                relativeClosingSpeed: 3f,
                directionDot: 1f,
                shoveForceMultiplier: 2f);
            float highSeed = SumoImpactResolver.ComputeTierAwareRamSeedEnergy(
                null,
                SumoImpactTier.High,
                attackerForwardSpeed: 8f,
                relativeClosingSpeed: 6f,
                directionDot: 1f,
                shoveForceMultiplier: 6.8f);

            Assert.Greater(lowSeed, 0.20f);
            Assert.Greater(midSeed, lowSeed);
            Assert.Greater(highSeed, midSeed);
        }

        [Test]
        public void ComputeImpactEngagementBudgetDeltaV_MediumSoftShoveHasReusableBudget()
        {
            float budget = SumoImpactResolver.ComputeImpactEngagementBudgetDeltaV(
                null,
                SumoImpactTier.Mid,
                SumoImpactResponseMode.SoftShove,
                attackerForwardSpeed: 3.5f,
                relativeClosingSpeed: 2.8f,
                directionDot: 0.9f,
                shoveForceMultiplier: 2f);

            Assert.Greater(budget, 0.13f);
        }

        [Test]
        public void ComputeDiminishingResidualDeltaV_RepeatedSoftImpulsesGetWeaker()
        {
            float initialBudget = 0.6f;
            float remainingBudget = initialBudget;

            float first = SumoImpactResolver.ComputeDiminishingResidualDeltaV(
                requestedDeltaV: 0.5f,
                remainingBudget: remainingBudget,
                initialBudget: initialBudget,
                previousDeltaV: 0f,
                responseMode: SumoImpactResponseMode.SoftShove);
            remainingBudget -= first;
            float second = SumoImpactResolver.ComputeDiminishingResidualDeltaV(
                requestedDeltaV: 0.5f,
                remainingBudget: remainingBudget,
                initialBudget: initialBudget,
                previousDeltaV: first,
                responseMode: SumoImpactResponseMode.SoftShove);
            remainingBudget -= second;
            float third = SumoImpactResolver.ComputeDiminishingResidualDeltaV(
                requestedDeltaV: 0.5f,
                remainingBudget: remainingBudget,
                initialBudget: initialBudget,
                previousDeltaV: second,
                responseMode: SumoImpactResponseMode.SoftShove);

            Assert.Greater(first, 0f);
            Assert.Less(second, first);
            Assert.Less(third, second);
        }

        [Test]
        public void ComputeDiminishingResidualDeltaV_RepeatedArcadeImpulsesGetWeaker()
        {
            float initialBudget = 4f;
            float remainingBudget = initialBudget;

            float first = SumoImpactResolver.ComputeDiminishingResidualDeltaV(
                requestedDeltaV: 3f,
                remainingBudget: remainingBudget,
                initialBudget: initialBudget,
                previousDeltaV: 0f,
                responseMode: SumoImpactResponseMode.ArcadeBurst);
            remainingBudget -= first;
            float second = SumoImpactResolver.ComputeDiminishingResidualDeltaV(
                requestedDeltaV: 3f,
                remainingBudget: remainingBudget,
                initialBudget: initialBudget,
                previousDeltaV: first,
                responseMode: SumoImpactResponseMode.ArcadeBurst);
            remainingBudget -= second;
            float third = SumoImpactResolver.ComputeDiminishingResidualDeltaV(
                requestedDeltaV: 3f,
                remainingBudget: remainingBudget,
                initialBudget: initialBudget,
                previousDeltaV: second,
                responseMode: SumoImpactResponseMode.ArcadeBurst);

            Assert.Greater(first, 0f);
            Assert.Less(second, first);
            Assert.Less(third, second);
        }

        [TestCase(SumoImpactTier.Low, SumoImpactResponseMode.SoftShove, 1.6f, 1.2f, 2f)]
        [TestCase(SumoImpactTier.Mid, SumoImpactResponseMode.SoftShove, 3.4f, 2.6f, 2f)]
        [TestCase(SumoImpactTier.High, SumoImpactResponseMode.ArcadeBurst, 6.5f, 5.2f, 6.8f)]
        [TestCase(SumoImpactTier.High, SumoImpactResponseMode.ArcadeBurst, 9f, 7f, 6.8f)]
        public void ComputeImpactEngagementBudgetDeltaV_AllTiersProduceDiminishingResiduals(
            SumoImpactTier tier,
            SumoImpactResponseMode responseMode,
            float attackerForwardSpeed,
            float relativeClosingSpeed,
            float shoveForceMultiplier)
        {
            float initialBudget = SumoImpactResolver.ComputeImpactEngagementBudgetDeltaV(
                null,
                tier,
                responseMode,
                attackerForwardSpeed,
                relativeClosingSpeed,
                directionDot: 1f,
                shoveForceMultiplier: shoveForceMultiplier);
            float remainingBudget = initialBudget;

            float first = SumoImpactResolver.ComputeDiminishingResidualDeltaV(
                requestedDeltaV: initialBudget,
                remainingBudget: remainingBudget,
                initialBudget: initialBudget,
                previousDeltaV: 0f,
                responseMode: responseMode);
            remainingBudget -= first;
            float second = SumoImpactResolver.ComputeDiminishingResidualDeltaV(
                requestedDeltaV: initialBudget,
                remainingBudget: remainingBudget,
                initialBudget: initialBudget,
                previousDeltaV: first,
                responseMode: responseMode);
            remainingBudget -= second;
            float third = SumoImpactResolver.ComputeDiminishingResidualDeltaV(
                requestedDeltaV: initialBudget,
                remainingBudget: remainingBudget,
                initialBudget: initialBudget,
                previousDeltaV: second,
                responseMode: responseMode);

            Assert.Greater(first, 0f);
            Assert.Less(second, first);
            Assert.Less(third, second);
        }

        [Test]
        public void ComputeDynamicResidualImpulseDeltaV_TailLengthEmergesFromEntrySpeed()
        {
            int lowSteps = CountDynamicResidualTailSteps(
                SumoImpactTier.Low,
                SumoImpactResponseMode.SoftShove,
                entrySpeed: 1.5f,
                shoveForceMultiplier: 2f);
            int midSteps = CountDynamicResidualTailSteps(
                SumoImpactTier.Mid,
                SumoImpactResponseMode.SoftShove,
                entrySpeed: 3f,
                shoveForceMultiplier: 2f);
            int highSteps = CountDynamicResidualTailSteps(
                SumoImpactTier.High,
                SumoImpactResponseMode.ArcadeBurst,
                entrySpeed: 8f,
                shoveForceMultiplier: 6.8f);

            Assert.Greater(lowSteps, 0);
            Assert.Greater(midSteps, lowSteps);
            Assert.Greater(highSteps, midSteps);
        }

        [Test]
        public void ComputeDynamicResidualImpulseDeltaV_AttackerTailSpeedDropsAfterResidual()
        {
            float deltaV = SumoImpactResolver.ComputeDynamicResidualImpulseDeltaV(
                tailSpeed: 3f,
                physicalClosingSpeed: 2.5f,
                victimForwardSpeed: 0f,
                targetSpeedScale: 2f,
                remainingBudget: 0.8f,
                initialBudget: 0.8f,
                previousDeltaV: 0.2f,
                maxDeltaVPerTick: 0.26f,
                responseMode: SumoImpactResponseMode.SoftShove);
            float attackerLoss = SumoImpactResolver.ComputeResidualAttackerSpeedLoss(
                deltaV,
                directionDot: 1f,
                shoveForceMultiplier: 2f,
                responseMode: SumoImpactResponseMode.SoftShove);
            float nextTailSpeed = SumoImpactResolver.ComputeNextImpactTailSpeed(
                currentTailSpeed: 3f,
                attackerSpeedLoss: attackerLoss,
                unappliedResidualDeltaV: 0f,
                deltaTime: 1f / 60f);

            Assert.Greater(deltaV, 0f);
            Assert.Greater(attackerLoss, 0f);
            Assert.Less(nextTailSpeed, 3f);
        }

        [Test]
        public void ShouldStartRamAfterImpactTail_WaitsForTailExhaustion()
        {
            Assert.IsFalse(SumoImpactResolver.ShouldStartRamAfterImpactTail(
                impactTailExhausted: false,
                ramEnergy: 1f,
                stopThreshold: 0.08f,
                physicalForwardSpeed: 3f,
                directionDot: 1f,
                physicalClosingSpeed: 0.5f,
                minPressureSpeed: 1.6f,
                minDirectionDot: 0.2f,
                minClosingSpeed: 0.12f));
            Assert.IsTrue(SumoImpactResolver.ShouldStartRamAfterImpactTail(
                impactTailExhausted: true,
                ramEnergy: 1f,
                stopThreshold: 0.08f,
                physicalForwardSpeed: 3f,
                directionDot: 1f,
                physicalClosingSpeed: 0.5f,
                minPressureSpeed: 1.6f,
                minDirectionDot: 0.2f,
                minClosingSpeed: 0.12f));
        }

        [Test]
        public void SuppressMovementAgainstPush_OnlyRemovesMovementIntoPush()
        {
            Vector3 pushDirection = Vector3.forward;
            Vector3 intoPush = SumoImpactResolver.SuppressMovementAgainstPush(
                targetHorizontalVelocity: Vector3.back * 8f,
                pushDirection,
                strength01: 1f);
            Vector3 sideMove = SumoImpactResolver.SuppressMovementAgainstPush(
                targetHorizontalVelocity: Vector3.right * 8f,
                pushDirection,
                strength01: 1f);
            Vector3 awayMove = SumoImpactResolver.SuppressMovementAgainstPush(
                targetHorizontalVelocity: Vector3.forward * 8f,
                pushDirection,
                strength01: 1f);

            Assert.AreEqual(Vector3.zero, intoPush);
            Assert.AreEqual(Vector3.right * 8f, sideMove);
            Assert.AreEqual(Vector3.forward * 8f, awayMove);
        }

        [Test]
        public void ClampDiminishingContactDeltaV_DoesNotPushVictimPastAttackerTargetSpeed()
        {
            float deltaV = SumoImpactResolver.ClampDiminishingContactDeltaV(
                requestedDeltaV: 1f,
                attackerForwardSpeed: 3f,
                victimForwardSpeed: 2.92f,
                targetSpeedScale: 1f,
                remainingBudget: 2f,
                initialBudget: 2f,
                previousDeltaV: 0f,
                responseMode: SumoImpactResponseMode.SoftShove);

            Assert.AreEqual(0.08f, deltaV, 0.0001f);
        }

        [Test]
        public void ComputeImpactEngagementBudgetDeltaV_HighSpeedArcadeKeepsReadableBurstBudget()
        {
            float budget = SumoImpactResolver.ComputeImpactEngagementBudgetDeltaV(
                null,
                SumoImpactTier.High,
                SumoImpactResponseMode.ArcadeBurst,
                attackerForwardSpeed: 8f,
                relativeClosingSpeed: 6f,
                directionDot: 1f,
                shoveForceMultiplier: 6.8f);

            Assert.Greater(budget, 0.48f);
        }

        [Test]
        public void ComputeImpactTailResidualDeltaV_FirstResidualIsCappedBelowEntry()
        {
            float entryDeltaV = 0.3f;
            float firstResidual = SumoImpactResolver.ComputeImpactTailResidualDeltaV(
                tailSpeed: 4f,
                physicalClosingSpeed: 4f,
                victimForwardSpeed: 0f,
                targetSpeedScale: 2f,
                remainingBudget: 2f,
                initialBudget: 2f,
                lastResidualDeltaV: 0f,
                entryImpactDeltaV: entryDeltaV,
                residualAccumulator: 0f,
                maxDeltaVPerTick: 0.5f,
                responseMode: SumoImpactResponseMode.SoftShove);
            float secondResidual = SumoImpactResolver.ComputeImpactTailResidualDeltaV(
                tailSpeed: 3.8f,
                physicalClosingSpeed: 3.8f,
                victimForwardSpeed: firstResidual,
                targetSpeedScale: 2f,
                remainingBudget: 2f - firstResidual,
                initialBudget: 2f,
                lastResidualDeltaV: firstResidual,
                entryImpactDeltaV: entryDeltaV,
                residualAccumulator: 0f,
                maxDeltaVPerTick: 0.5f,
                responseMode: SumoImpactResponseMode.SoftShove);

            Assert.Greater(firstResidual, 0f);
            Assert.LessOrEqual(firstResidual, SumoImpactResolver.ComputeEntryToTailResidualCap(entryDeltaV, SumoImpactResponseMode.SoftShove) + 0.0001f);
            Assert.Greater(secondResidual, 0f);
            Assert.Less(secondResidual, firstResidual);
        }

        [Test]
        public void ImpactTailHandoff_WaitsUntilTickAfterFullExhaustion()
        {
            Assert.IsFalse(SumoImpactResolver.IsImpactTailFullyExhausted(
                residualDeltaV: 0.01f,
                tailSpeed: 1.5f,
                remainingBudget: 0.5f,
                meaningfulDeltaV: 0.05f,
                silentDrainTicks: 3));
            Assert.IsTrue(SumoImpactResolver.IsImpactTailFullyExhausted(
                residualDeltaV: 0.01f,
                tailSpeed: 0.08f,
                remainingBudget: 0.08f,
                meaningfulDeltaV: 0.05f,
                silentDrainTicks: 1));
            Assert.IsFalse(SumoImpactResolver.CanStartRamAfterImpactTailHandoff(
                impactTailExhausted: true,
                currentTick: 100,
                impactTailExhaustedTick: 100,
                ramEnergy: 1f,
                stopThreshold: 0.08f,
                physicalForwardSpeed: 3f,
                directionDot: 1f,
                physicalClosingSpeed: 0.5f,
                minPressureSpeed: 1.6f,
                minDirectionDot: 0.2f,
                minClosingSpeed: 0.12f));
            Assert.IsTrue(SumoImpactResolver.CanStartRamAfterImpactTailHandoff(
                impactTailExhausted: true,
                currentTick: 101,
                impactTailExhaustedTick: 100,
                ramEnergy: 1f,
                stopThreshold: 0.08f,
                physicalForwardSpeed: 3f,
                directionDot: 1f,
                physicalClosingSpeed: 0.5f,
                minPressureSpeed: 1.6f,
                minDirectionDot: 0.2f,
                minClosingSpeed: 0.12f));
        }

        [Test]
        public void ComputeImpactTailResidualDeltaV_TailLengthStillEmergesAfterEntryHit()
        {
            int lowSteps = CountSeparatedResidualTailSteps(
                SumoImpactTier.Low,
                SumoImpactResponseMode.SoftShove,
                entrySpeed: 1.5f,
                entryDeltaV: 0.12f,
                shoveForceMultiplier: 2f);
            int midSteps = CountSeparatedResidualTailSteps(
                SumoImpactTier.Mid,
                SumoImpactResponseMode.SoftShove,
                entrySpeed: 3f,
                entryDeltaV: 0.26f,
                shoveForceMultiplier: 2f);
            int highSteps = CountSeparatedResidualTailSteps(
                SumoImpactTier.High,
                SumoImpactResponseMode.ArcadeBurst,
                entrySpeed: 8f,
                entryDeltaV: 0.75f,
                shoveForceMultiplier: 6.8f);

            Assert.Greater(lowSteps, 0);
            Assert.Greater(midSteps, lowSteps);
            Assert.Greater(highSteps, midSteps);
        }

        [Test]
        public void NpcSuppressionPreview_RemovesDriveIntoPushBeforeControllerTarget()
        {
            GameObject go = new GameObject("npc-suppression-test");
            try
            {
                SumoNpcBallDriver driver = go.AddComponent<SumoNpcBallDriver>();
                const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
                typeof(SumoNpcBallDriver).GetField("_gameplayPushSuppressionDirection", Flags)
                    ?.SetValue(driver, Vector3.forward);
                typeof(SumoNpcBallDriver).GetField("_gameplayPushSuppressionStrength01", Flags)
                    ?.SetValue(driver, 1f);
                typeof(SumoNpcBallDriver).GetField("_gameplayPushSuppressionUntilFrame", Flags)
                    ?.SetValue(driver, Time.frameCount + 10);
                MethodInfo preview = typeof(SumoNpcBallDriver).GetMethod(
                    "PreviewSuppressedMovementTargetForTests",
                    Flags);

                Vector3 suppressed = (Vector3)preview.Invoke(driver, new object[] { Vector3.back * 8f });
                Vector3 side = (Vector3)preview.Invoke(driver, new object[] { Vector3.right * 8f });

                Assert.AreEqual(Vector3.zero, suppressed);
                Assert.AreEqual(Vector3.right * 8f, side);
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        private static int CountDynamicResidualTailSteps(
            SumoImpactTier tier,
            SumoImpactResponseMode responseMode,
            float entrySpeed,
            float shoveForceMultiplier)
        {
            float maxStep = responseMode == SumoImpactResponseMode.ArcadeBurst
                ? 0.48f * shoveForceMultiplier
                : 0.13f * shoveForceMultiplier;
            float initialBudget = SumoImpactResolver.ComputeImpactEngagementBudgetDeltaV(
                null,
                tier,
                responseMode,
                entrySpeed,
                relativeClosingSpeed: entrySpeed,
                directionDot: 1f,
                shoveForceMultiplier: shoveForceMultiplier);
            float remainingBudget = initialBudget;
            float previousDeltaV = 0f;
            float tailSpeed = entrySpeed;
            int steps = 0;

            for (int i = 0; i < 32; i++)
            {
                float meaningful = SumoImpactResolver.ComputeResidualMeaningfulDeltaV(maxStep, responseMode);
                float deltaV = SumoImpactResolver.ComputeDynamicResidualImpulseDeltaV(
                    tailSpeed,
                    physicalClosingSpeed: tailSpeed,
                    victimForwardSpeed: 0f,
                    targetSpeedScale: shoveForceMultiplier,
                    remainingBudget,
                    initialBudget,
                    previousDeltaV,
                    maxStep,
                    responseMode);

                if (SumoImpactResolver.IsResidualImpactTailExhausted(
                    deltaV,
                    tailSpeed,
                    remainingBudget,
                    meaningful))
                {
                    break;
                }

                if (previousDeltaV > 0f)
                {
                    Assert.Less(deltaV, previousDeltaV);
                }

                float attackerLoss = SumoImpactResolver.ComputeResidualAttackerSpeedLoss(
                    deltaV,
                    directionDot: 1f,
                    shoveForceMultiplier,
                    responseMode);
                tailSpeed = SumoImpactResolver.ComputeNextImpactTailSpeed(
                    tailSpeed,
                    attackerLoss,
                    unappliedResidualDeltaV: 0f,
                    deltaTime: 1f / 60f);
                remainingBudget = Mathf.Max(0f, remainingBudget - deltaV);
                previousDeltaV = deltaV;
                steps++;
            }

            return steps;
        }

        private static int CountSeparatedResidualTailSteps(
            SumoImpactTier tier,
            SumoImpactResponseMode responseMode,
            float entrySpeed,
            float entryDeltaV,
            float shoveForceMultiplier)
        {
            float maxStep = responseMode == SumoImpactResponseMode.ArcadeBurst
                ? 0.48f * shoveForceMultiplier
                : 0.13f * shoveForceMultiplier;
            float initialBudget = SumoImpactResolver.ComputeImpactEngagementBudgetDeltaV(
                null,
                tier,
                responseMode,
                entrySpeed,
                relativeClosingSpeed: entrySpeed,
                directionDot: 1f,
                shoveForceMultiplier: shoveForceMultiplier);
            float remainingBudget = initialBudget;
            float lastResidualDeltaV = 0f;
            float tailSpeed = entrySpeed;
            int silentDrainTicks = 0;
            int steps = 0;

            for (int i = 0; i < 48; i++)
            {
                float meaningful = SumoImpactResolver.ComputeResidualMeaningfulDeltaV(maxStep, responseMode);
                float deltaV = SumoImpactResolver.ComputeImpactTailResidualDeltaV(
                    tailSpeed,
                    physicalClosingSpeed: tailSpeed,
                    victimForwardSpeed: steps * 0.02f,
                    targetSpeedScale: shoveForceMultiplier,
                    remainingBudget,
                    initialBudget,
                    lastResidualDeltaV,
                    entryDeltaV,
                    residualAccumulator: 0f,
                    maxDeltaVPerTick: maxStep,
                    responseMode: responseMode);

                if (deltaV < meaningful)
                {
                    silentDrainTicks++;
                    tailSpeed = SumoImpactResolver.ComputeNextImpactTailSpeed(
                        tailSpeed,
                        attackerSpeedLoss: 0f,
                        unappliedResidualDeltaV: meaningful,
                        deltaTime: 1f / 60f);
                    remainingBudget = Mathf.Max(0f, remainingBudget - meaningful * 0.18f);
                    if (SumoImpactResolver.IsImpactTailFullyExhausted(
                        deltaV,
                        tailSpeed,
                        remainingBudget,
                        meaningful,
                        silentDrainTicks))
                    {
                        break;
                    }

                    continue;
                }

                if (lastResidualDeltaV > 0f)
                {
                    Assert.Less(deltaV, lastResidualDeltaV);
                }

                float attackerLoss = SumoImpactResolver.ComputeResidualAttackerSpeedLoss(
                    deltaV,
                    directionDot: 1f,
                    shoveForceMultiplier,
                    responseMode);
                tailSpeed = SumoImpactResolver.ComputeNextImpactTailSpeed(
                    tailSpeed,
                    attackerLoss,
                    unappliedResidualDeltaV: 0f,
                    deltaTime: 1f / 60f);
                remainingBudget = Mathf.Max(0f, remainingBudget - deltaV);
                lastResidualDeltaV = deltaV;
                silentDrainTicks = 0;
                steps++;
            }

            return steps;
        }
    }
}
