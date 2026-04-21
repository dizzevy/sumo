using Fusion;
using Fusion.Addons.Physics;
using UnityEngine;

namespace Sumo
{
    [DisallowMultipleComponent]
    [RequireComponent(typeof(NetworkObject))]
    [RequireComponent(typeof(NetworkRigidbody3D))]
    [RequireComponent(typeof(SumoBallController))]
    [RequireComponent(typeof(SumoCollisionController))]
    public sealed class SumoProxyPresentation : MonoBehaviour
    {
        [Header("References")]
        [SerializeField] private NetworkObject networkObject;
        [SerializeField] private NetworkRigidbody3D networkRigidbody;
        [SerializeField] private SumoBallController ballController;
        [SerializeField] private SumoCollisionController collisionController;
        [SerializeField] private SumoVisualSmoothing visualSmoothing;
        [SerializeField] private Transform visualShell;
        [SerializeField] private Transform interpolationAnchor;
        [SerializeField] private Transform cameraTarget;

        [Header("Hierarchy")]
        [SerializeField] private bool createAnchorIfMissing = true;
        [SerializeField] private bool createCameraTargetIfMissing = true;
        [SerializeField] private Vector3 cameraTargetLocalOffset = new Vector3(0f, 0.15f, 0f);
        [SerializeField] private bool forceInterpolationTargetEachFrame = true;

        [Header("Smoothing Profiles")]
        [SerializeField] private bool enableLocalExtraSmoothing = false;
        [SerializeField] private bool enableProxyExtraSmoothing = true;
        [SerializeField] [Range(0f, 1f)] private float authoritativeRemoteBaseBlend = 0.45f;
        [SerializeField] [Range(0f, 1f)] private float remoteVictimPushMinBlend = 0.68f;
        [SerializeField] [Range(0f, 1f)] private float remoteVictimPushMaxBlend = 0.92f;
        [SerializeField] private SumoVisualSmoothing.SmoothingProfile localProfile = default;
        [SerializeField] private SumoVisualSmoothing.SmoothingProfile proxyProfile = default;
        [SerializeField] private SumoVisualSmoothing.SmoothingProfile localVictimProfile = default;
        [SerializeField] private SumoVisualSmoothing.SmoothingProfile proxyVictimProfile = default;

        [Header("Victim Push Smoothing")]
        [SerializeField] private bool enableVictimPushSmoothing = true;
        [SerializeField] private bool applyVictimPushSmoothingToLocalPlayer = false;
        [SerializeField] private bool allowLocalVictimSignalSmoothing = true;
        [SerializeField] private float victimMinSmoothingDuration = 0.22f;
        [SerializeField] private float victimMaxSmoothingDuration = 0.6f;
        [SerializeField] private float victimSmoothingExtension = 0.05f;
        [SerializeField] private bool triggerVictimSmoothingOnPlayerCollision = false;
        [SerializeField] private float collisionTriggeredStrength = 0.7f;
        [SerializeField] private float collisionTriggerCooldown = 0.08f;
        [SerializeField] private bool triggerVictimSmoothingOnCorrectionSpike = false;
        [SerializeField] private float correctionSpikeStartDistance = 0.14f;
        [SerializeField] private float correctionSpikeMaxDistance = 0.85f;
        [SerializeField] private float correctionSpikeTriggerCooldown = 0.06f;
        [SerializeField] private float victimBlendRiseSpeed = 14f;
        [SerializeField] private float victimBlendFallSpeed = 3.6f;
        [SerializeField] private float victimBlendFloor = 0.2f;
        [SerializeField] private float victimBlendResetThreshold = 0.01f;

        [Header("Dedicated Server")]
        [SerializeField] private bool disablePresentationOnDedicatedServer = true;

        private bool _initialized;
        private bool _isLocalProfileApplied;
        private bool _modeInitialized;
        private bool _isDirectLocalModeApplied;
        private float _victimSmoothingUntil;
        private float _victimBlend;
        private float _victimTargetBlend;
        private int _lastVictimSignalSequence = int.MinValue;
        private float _nextCollisionTriggerTime;
        private float _nextCorrectionSpikeTriggerTime;

        public Transform VisualShell => visualShell != null ? visualShell : transform;
        public Transform InterpolationAnchor => interpolationAnchor != null ? interpolationAnchor : transform;
        public Transform CameraTarget => cameraTarget != null ? cameraTarget : VisualShell;

        private void Reset()
        {
            localProfile = SumoVisualSmoothing.SmoothingProfile.CreateLocalDefault();
            proxyProfile = SumoVisualSmoothing.SmoothingProfile.CreateProxyDefault();
            localVictimProfile = SumoVisualSmoothing.SmoothingProfile.CreateLocalVictimDefault();
            proxyVictimProfile = SumoVisualSmoothing.SmoothingProfile.CreateProxyVictimDefault();
        }

        private void Awake()
        {
            CacheComponents();
            EnsureDefaultProfiles();
            UpgradeLegacyVictimProfiles();
        }

        private void OnEnable()
        {
            _initialized = false;
            _victimSmoothingUntil = 0f;
            _victimBlend = 0f;
            _victimTargetBlend = 0f;
            _lastVictimSignalSequence = int.MinValue;
            _nextCollisionTriggerTime = 0f;
            _nextCorrectionSpikeTriggerTime = 0f;
        }

        private void OnDisable()
        {
            _victimSmoothingUntil = 0f;
            _victimBlend = 0f;
            _victimTargetBlend = 0f;
            _lastVictimSignalSequence = int.MinValue;
            _modeInitialized = false;
            _isDirectLocalModeApplied = false;
            _nextCollisionTriggerTime = 0f;
            _nextCorrectionSpikeTriggerTime = 0f;
        }

        private void LateUpdate()
        {
            if (!_initialized)
            {
                TryInitialize();
                return;
            }

            if (ShouldDisablePresentationForDedicatedServer())
            {
                enabled = false;
                return;
            }

            bool localPresentation = IsLocalPresentation();
            TryTriggerVictimSmoothingFromNetworkSignal(localPresentation);
            TryMaintainVictimSmoothingFromActivePush(localPresentation);
            TryTriggerVictimSmoothingFromCorrectionSpike(localPresentation);
            UpdateVictimBlend(localPresentation);
            bool directLocalMode = ShouldUseDirectPresentationMode(localPresentation, _victimBlend);

            if (!_modeInitialized
                || localPresentation != _isLocalProfileApplied
                || directLocalMode != _isDirectLocalModeApplied)
            {
                ApplyPresentationMode(localPresentation, directLocalMode, false);
            }

            ApplyDynamicSmoothingProfile(localPresentation, false);

            if (forceInterpolationTargetEachFrame && networkRigidbody != null)
            {
                Transform expectedTarget = directLocalMode
                    ? visualShell
                    : interpolationAnchor;

                if (expectedTarget != null && networkRigidbody.InterpolationTarget != expectedTarget)
                {
                    networkRigidbody.SetInterpolationTarget(expectedTarget);
                }
            }

        }

        private void TryInitialize()
        {
            CacheComponents();
            EnsureDefaultProfiles();

            if (ShouldDisablePresentationForDedicatedServer())
            {
                enabled = false;
                return;
            }

            visualShell = ResolveVisualShell();
            if (visualShell == null || visualShell == transform)
            {
                return;
            }

            interpolationAnchor = ResolveInterpolationAnchor();
            if (interpolationAnchor == null || interpolationAnchor == visualShell)
            {
                return;
            }

            if (!interpolationAnchor.IsChildOf(transform))
            {
                interpolationAnchor.SetParent(transform, true);
            }

            interpolationAnchor.localScale = Vector3.one;

            if (networkRigidbody != null && networkRigidbody.InterpolationTarget != interpolationAnchor)
            {
                interpolationAnchor.SetPositionAndRotation(visualShell.position, visualShell.rotation);
                networkRigidbody.SetInterpolationTarget(interpolationAnchor);
            }

            if (visualSmoothing == null)
            {
                visualSmoothing = GetComponent<SumoVisualSmoothing>();
            }

            if (visualSmoothing == null)
            {
                visualSmoothing = gameObject.AddComponent<SumoVisualSmoothing>();
            }

            visualSmoothing.SetTargets(interpolationAnchor, visualShell, true);
            bool localPresentation = IsLocalPresentation();
            UpdateVictimBlend(localPresentation);
            bool directLocalMode = ShouldUseDirectPresentationMode(localPresentation, _victimBlend);
            ApplyPresentationMode(localPresentation, directLocalMode, true);
            ApplyDynamicSmoothingProfile(localPresentation, true);

            ResolveCameraTarget();
            _initialized = true;
        }

        private void CacheComponents()
        {
            if (networkObject == null)
            {
                networkObject = GetComponent<NetworkObject>();
            }

            if (networkRigidbody == null)
            {
                networkRigidbody = GetComponent<NetworkRigidbody3D>();
            }

            if (ballController == null)
            {
                ballController = GetComponent<SumoBallController>();
            }

            if (collisionController == null)
            {
                collisionController = GetComponent<SumoCollisionController>();
            }
        }

        private void EnsureDefaultProfiles()
        {
            if (localProfile.Position.MediumError <= 0f)
            {
                localProfile = SumoVisualSmoothing.SmoothingProfile.CreateLocalDefault();
            }

            if (proxyProfile.Position.MediumError <= 0f)
            {
                proxyProfile = SumoVisualSmoothing.SmoothingProfile.CreateProxyDefault();
            }

            if (localVictimProfile.Position.MediumError <= 0f)
            {
                localVictimProfile = SumoVisualSmoothing.SmoothingProfile.CreateLocalVictimDefault();
            }

            if (proxyVictimProfile.Position.MediumError <= 0f)
            {
                proxyVictimProfile = SumoVisualSmoothing.SmoothingProfile.CreateProxyVictimDefault();
            }
        }

        private void UpgradeLegacyVictimProfiles()
        {
            bool looksLikeLegacyLocalVictim = localVictimProfile.Position.HardSnapDistance <= 10.5f
                && localVictimProfile.Position.MediumError <= 0.35f;
            if (looksLikeLegacyLocalVictim)
            {
                localVictimProfile = SumoVisualSmoothing.SmoothingProfile.CreateLocalVictimDefault();
            }

            bool looksLikeLegacyProxyVictim = proxyVictimProfile.Position.HardSnapDistance <= 10.5f
                && proxyVictimProfile.Position.MediumError <= 0.4f;
            if (looksLikeLegacyProxyVictim)
            {
                proxyVictimProfile = SumoVisualSmoothing.SmoothingProfile.CreateProxyVictimDefault();
            }

            if (correctionSpikeTriggerCooldown <= 0f)
            {
                correctionSpikeTriggerCooldown = 0.06f;
            }

            if (victimBlendRiseSpeed <= 0f)
            {
                victimBlendRiseSpeed = 14f;
            }

            if (victimBlendFallSpeed <= 0f)
            {
                victimBlendFallSpeed = 3.6f;
            }

            if (victimBlendFloor <= 0f)
            {
                victimBlendFloor = 0.2f;
            }

            if (victimBlendResetThreshold <= 0f)
            {
                victimBlendResetThreshold = 0.01f;
            }
        }

        private Transform ResolveVisualShell()
        {
            if (visualShell != null && visualShell != transform)
            {
                return visualShell;
            }

            if (ballController != null)
            {
                Transform fromBallController = ballController.CameraFollowTarget;
                if (fromBallController != null && fromBallController != transform)
                {
                    return fromBallController;
                }
            }

            for (int i = 0; i < transform.childCount; i++)
            {
                Transform child = transform.GetChild(i);
                if (child != null && child != interpolationAnchor)
                {
                    return child;
                }
            }

            return null;
        }

        private Transform ResolveInterpolationAnchor()
        {
            if (interpolationAnchor != null && interpolationAnchor != visualShell)
            {
                return interpolationAnchor;
            }

            if (networkRigidbody != null
                && networkRigidbody.InterpolationTarget != null
                && networkRigidbody.InterpolationTarget != visualShell)
            {
                return networkRigidbody.InterpolationTarget;
            }

            Transform existing = transform.Find("VisualAnchor");
            if (existing != null && existing != visualShell)
            {
                return existing;
            }

            if (!createAnchorIfMissing)
            {
                return null;
            }

            GameObject anchorObject = new GameObject("VisualAnchor");
            Transform anchor = anchorObject.transform;
            anchor.SetParent(transform, true);
            anchor.SetPositionAndRotation(visualShell != null ? visualShell.position : transform.position, visualShell != null ? visualShell.rotation : transform.rotation);
            return anchor;
        }

        private void ResolveCameraTarget()
        {
            if (visualShell == null)
            {
                cameraTarget = null;
                return;
            }

            if (cameraTarget != null)
            {
                return;
            }

            if (!createCameraTargetIfMissing)
            {
                cameraTarget = visualShell;
                return;
            }

            Transform existing = transform.Find("CameraTarget");
            if (existing != null)
            {
                cameraTarget = existing;
            }
            else
            {
                GameObject targetObject = new GameObject("CameraTarget");
                cameraTarget = targetObject.transform;
            }

            if (!cameraTarget.IsChildOf(visualShell))
            {
                cameraTarget.SetParent(visualShell, false);
            }

            cameraTarget.localPosition = cameraTargetLocalOffset;
            cameraTarget.localRotation = Quaternion.identity;
        }

        private void ApplyPresentationMode(bool localPresentation, bool directLocalMode, bool snapNow)
        {
            if (networkRigidbody == null || visualShell == null)
            {
                return;
            }

            if (directLocalMode)
            {
                if (visualSmoothing != null)
                {
                    visualSmoothing.enabled = false;
                }

                if (networkRigidbody.InterpolationTarget != visualShell)
                {
                    networkRigidbody.SetInterpolationTarget(visualShell);
                }

                _isLocalProfileApplied = localPresentation;
                _isDirectLocalModeApplied = true;
                _modeInitialized = true;
                return;
            }

            if (interpolationAnchor == null)
            {
                interpolationAnchor = ResolveInterpolationAnchor();
            }

            if (interpolationAnchor == null || interpolationAnchor == visualShell)
            {
                return;
            }

            if (networkRigidbody.InterpolationTarget != interpolationAnchor)
            {
                interpolationAnchor.SetPositionAndRotation(visualShell.position, visualShell.rotation);
                networkRigidbody.SetInterpolationTarget(interpolationAnchor);
            }

            if (visualSmoothing != null)
            {
                visualSmoothing.enabled = true;
                visualSmoothing.SetTargets(interpolationAnchor, visualShell, snapNow);
            }

            _isLocalProfileApplied = localPresentation;
            _isDirectLocalModeApplied = false;
            _modeInitialized = true;
        }

        private void ApplyDynamicSmoothingProfile(bool localPresentation, bool snapNow)
        {
            if (visualSmoothing == null || !visualSmoothing.enabled)
            {
                return;
            }

            if (ShouldUseDirectPresentationMode(localPresentation, _victimBlend))
            {
                return;
            }

            SumoVisualSmoothing.SmoothingProfile baseProfile = localPresentation ? localProfile : proxyProfile;
            SumoVisualSmoothing.SmoothingProfile victimProfile = localPresentation ? localVictimProfile : proxyVictimProfile;
            float effectiveBlend = Mathf.Max(
                _victimBlend,
                Mathf.Max(
                    GetPersistentRemoteBlend(localPresentation),
                    GetActiveRemoteVictimBlend(localPresentation)));
            SumoVisualSmoothing.SmoothingProfile activeProfile = LerpProfile(baseProfile, victimProfile, effectiveBlend);
            visualSmoothing.SetProfile(activeProfile, snapNow);
        }

        private bool ShouldUseDirectPresentationMode(bool localPresentation, float victimBlend)
        {
            if (!localPresentation)
            {
                // On host/client the remote player's authoritative body can look much
                // harsher than a normal client proxy during shoves. Keep that branch on
                // the interpolation anchor full-time so both sides get the same visual
                // smoothing path instead of one side dropping back to direct root motion.
                if (ShouldForceContinuousRemoteSmoothing())
                {
                    return false;
                }

                if (GetActiveRemoteVictimBlend(localPresentation) > victimBlendResetThreshold)
                {
                    return false;
                }

                return !enableProxyExtraSmoothing || victimBlend <= victimBlendResetThreshold;
            }

            bool allowVictimSmoothing = applyVictimPushSmoothingToLocalPlayer || IsTemporaryLocalVictimSmoothingActive();
            if (!allowVictimSmoothing)
            {
                return true;
            }

            return !enableLocalExtraSmoothing && victimBlend <= victimBlendResetThreshold;
        }

        private bool ShouldForceContinuousRemoteSmoothing()
        {
            return enableProxyExtraSmoothing
                && networkObject != null
                && networkObject.HasStateAuthority
                && !networkObject.HasInputAuthority;
        }

        private float GetPersistentRemoteBlend(bool localPresentation)
        {
            if (localPresentation || !ShouldForceContinuousRemoteSmoothing())
            {
                return 0f;
            }

            return Mathf.Clamp01(authoritativeRemoteBaseBlend);
        }

        private float GetActiveRemoteVictimBlend(bool localPresentation)
        {
            if (localPresentation
                || !enableVictimPushSmoothing
                || !enableProxyExtraSmoothing
                || collisionController == null)
            {
                return 0f;
            }

            float pushAssist = collisionController.GetVictimPushAssist01();
            if (pushAssist <= victimBlendResetThreshold)
            {
                return 0f;
            }

            float minBlend = Mathf.Max(authoritativeRemoteBaseBlend, remoteVictimPushMinBlend);
            float maxBlend = Mathf.Max(minBlend, remoteVictimPushMaxBlend);
            return Mathf.Lerp(minBlend, maxBlend, Mathf.Clamp01(pushAssist));
        }

        private void UpdateVictimBlend(bool localPresentation)
        {
            if (!enableVictimPushSmoothing)
            {
                _victimBlend = 0f;
                _victimTargetBlend = 0f;
                return;
            }

            if (!localPresentation && !enableProxyExtraSmoothing)
            {
                _victimBlend = 0f;
                _victimTargetBlend = 0f;
                return;
            }

            if (localPresentation && !CanApplyLocalVictimSmoothing(localPresentation))
            {
                _victimBlend = 0f;
                _victimTargetBlend = 0f;
                return;
            }

            float deltaTime = Time.unscaledDeltaTime;
            if (deltaTime <= 0f)
            {
                return;
            }

            bool activeWindow = Time.unscaledTime < _victimSmoothingUntil;
            float desiredBlend = activeWindow
                ? Mathf.Max(_victimTargetBlend, victimBlendFloor)
                : 0f;

            float blendSpeed = desiredBlend >= _victimBlend
                ? Mathf.Max(0.01f, victimBlendRiseSpeed)
                : Mathf.Max(0.01f, victimBlendFallSpeed);

            _victimBlend = Mathf.MoveTowards(_victimBlend, desiredBlend, blendSpeed * deltaTime);

            if (!activeWindow && _victimBlend <= victimBlendResetThreshold)
            {
                _victimBlend = 0f;
                _victimTargetBlend = 0f;
            }
        }

        private void TryTriggerVictimSmoothingFromNetworkSignal(bool localPresentation)
        {
            if (!enableVictimPushSmoothing || networkObject == null)
            {
                return;
            }

            if (!localPresentation && !enableProxyExtraSmoothing)
            {
                return;
            }

            if (localPresentation && !CanStartLocalVictimSmoothingFromNetworkSignal(localPresentation))
            {
                return;
            }

            if (collisionController == null)
            {
                return;
            }

            int signalSequence = collisionController.VictimPresentationSignalSequence;
            if (_lastVictimSignalSequence == int.MinValue)
            {
                _lastVictimSignalSequence = signalSequence;
                if (signalSequence != 0)
                {
                    float initialSignalStrength = collisionController.VictimPresentationSignalStrength;
                    TriggerVictimSmoothing(initialSignalStrength);
                }
                return;
            }

            if (signalSequence == _lastVictimSignalSequence)
            {
                return;
            }

            _lastVictimSignalSequence = signalSequence;
            float networkSignalStrength = collisionController.VictimPresentationSignalStrength;
            TriggerVictimSmoothing(networkSignalStrength);
        }

        private void TryTriggerVictimSmoothingFromCorrectionSpike(bool localPresentation)
        {
            if (!enableVictimPushSmoothing
                || !triggerVictimSmoothingOnCorrectionSpike
                || interpolationAnchor == null
                || visualShell == null)
            {
                return;
            }

            if (!localPresentation && !enableProxyExtraSmoothing)
            {
                return;
            }

            if (localPresentation && !CanExtendLocalVictimSmoothingFromCorrectionSpike(localPresentation))
            {
                return;
            }

            if (Time.unscaledTime < _nextCorrectionSpikeTriggerTime)
            {
                return;
            }

            float error = Vector3.Distance(interpolationAnchor.position, visualShell.position);
            if (error < correctionSpikeStartDistance)
            {
                return;
            }

            float normalized = Mathf.InverseLerp(correctionSpikeStartDistance, correctionSpikeMaxDistance, error);
            _nextCorrectionSpikeTriggerTime = Time.unscaledTime + Mathf.Max(0.01f, correctionSpikeTriggerCooldown);
            TriggerVictimSmoothing(normalized);
        }

        private void TryMaintainVictimSmoothingFromActivePush(bool localPresentation)
        {
            if (!enableVictimPushSmoothing || collisionController == null)
            {
                return;
            }

            bool canSustainLocalVictimSmoothing = applyVictimPushSmoothingToLocalPlayer
                || allowLocalVictimSignalSmoothing;
            if (localPresentation && !canSustainLocalVictimSmoothing)
            {
                return;
            }

            if (!localPresentation && !enableProxyExtraSmoothing)
            {
                return;
            }

            float pushAssist = collisionController.GetVictimPushAssist01();
            if (pushAssist <= victimBlendResetThreshold)
            {
                return;
            }

            SustainVictimSmoothing(pushAssist);
        }

        private void OnCollisionEnter(Collision collision)
        {
            if (!enableVictimPushSmoothing || !triggerVictimSmoothingOnPlayerCollision)
            {
                return;
            }

            if (IsLocalPresentation() && !applyVictimPushSmoothingToLocalPlayer)
            {
                return;
            }

            if (collision == null || collision.rigidbody == null)
            {
                return;
            }

            if (!collision.rigidbody.TryGetComponent(out SumoBallController _))
            {
                return;
            }

            if (Time.unscaledTime < _nextCollisionTriggerTime)
            {
                return;
            }

            _nextCollisionTriggerTime = Time.unscaledTime + Mathf.Max(0.01f, collisionTriggerCooldown);
            TriggerVictimSmoothing(collisionTriggeredStrength);
        }

        private void TriggerVictimSmoothing(float normalizedStrength)
        {
            if (!enableVictimPushSmoothing)
            {
                return;
            }

            float clamped = Mathf.Clamp01(normalizedStrength);
            if (clamped <= 0f)
            {
                return;
            }

            _victimTargetBlend = Mathf.Max(_victimTargetBlend, Mathf.Max(victimBlendFloor, clamped));
            _victimBlend = Mathf.Max(_victimBlend, victimBlendFloor * 0.8f);
            float duration = Mathf.Lerp(victimMinSmoothingDuration, victimMaxSmoothingDuration, clamped);
            float extension = Mathf.Max(0f, victimSmoothingExtension);
            _victimSmoothingUntil = Mathf.Max(_victimSmoothingUntil, Time.unscaledTime + duration + extension);
        }

        private void SustainVictimSmoothing(float normalizedStrength)
        {
            float clamped = Mathf.Clamp01(normalizedStrength);
            if (clamped <= 0f)
            {
                return;
            }

            _victimTargetBlend = Mathf.Max(_victimTargetBlend, Mathf.Max(victimBlendFloor, clamped));
            _victimBlend = Mathf.Max(_victimBlend, victimBlendFloor);

            float sustainWindow = Mathf.Lerp(
                Mathf.Max(0.04f, victimSmoothingExtension),
                Mathf.Max(victimMinSmoothingDuration, 0.12f),
                clamped);

            _victimSmoothingUntil = Mathf.Max(_victimSmoothingUntil, Time.unscaledTime + sustainWindow);
        }

        private bool CanApplyLocalVictimSmoothing(bool localPresentation)
        {
            return !localPresentation
                || applyVictimPushSmoothingToLocalPlayer
                || IsTemporaryLocalVictimSmoothingActive();
        }

        private bool CanStartLocalVictimSmoothingFromNetworkSignal(bool localPresentation)
        {
            return CanStartLocalVictimSmoothing(localPresentation);
        }

        private bool CanExtendLocalVictimSmoothingFromCorrectionSpike(bool localPresentation)
        {
            return !localPresentation
                || applyVictimPushSmoothingToLocalPlayer
                || (allowLocalVictimSignalSmoothing && IsTemporaryLocalVictimSmoothingActive());
        }

        private bool CanStartLocalVictimSmoothing(bool localPresentation)
        {
            return !localPresentation
                || applyVictimPushSmoothingToLocalPlayer
                || allowLocalVictimSignalSmoothing;
        }

        // Let the local player stay in direct mode by default, but keep a short
        // smoothing window alive after a confirmed authoritative victim hit.
        private bool IsTemporaryLocalVictimSmoothingActive()
        {
            if (!allowLocalVictimSignalSmoothing)
            {
                return false;
            }

            return _victimBlend > victimBlendResetThreshold
                || _victimTargetBlend > victimBlendResetThreshold
                || Time.unscaledTime < _victimSmoothingUntil;
        }

        private static SumoVisualSmoothing.SmoothingProfile LerpProfile(
            SumoVisualSmoothing.SmoothingProfile from,
            SumoVisualSmoothing.SmoothingProfile to,
            float t)
        {
            float blend = Mathf.Clamp01(t);
            return new SumoVisualSmoothing.SmoothingProfile
            {
                Position = LerpPosition(from.Position, to.Position, blend),
                Rotation = LerpRotation(from.Rotation, to.Rotation, blend)
            };
        }

        private static SumoVisualSmoothing.PositionSettings LerpPosition(
            SumoVisualSmoothing.PositionSettings from,
            SumoVisualSmoothing.PositionSettings to,
            float t)
        {
            return new SumoVisualSmoothing.PositionSettings
            {
                DeadZone = Mathf.Lerp(from.DeadZone, to.DeadZone, t),
                SmallError = Mathf.Lerp(from.SmallError, to.SmallError, t),
                MediumError = Mathf.Lerp(from.MediumError, to.MediumError, t),
                SmallSharpness = Mathf.Lerp(from.SmallSharpness, to.SmallSharpness, t),
                MediumSharpness = Mathf.Lerp(from.MediumSharpness, to.MediumSharpness, t),
                LargeSharpness = Mathf.Lerp(from.LargeSharpness, to.LargeSharpness, t),
                SmallCatchUpSpeed = Mathf.Lerp(from.SmallCatchUpSpeed, to.SmallCatchUpSpeed, t),
                MediumCatchUpSpeed = Mathf.Lerp(from.MediumCatchUpSpeed, to.MediumCatchUpSpeed, t),
                LargeCatchUpSpeed = Mathf.Lerp(from.LargeCatchUpSpeed, to.LargeCatchUpSpeed, t),
                HardSnapDistance = Mathf.Lerp(from.HardSnapDistance, to.HardSnapDistance, t)
            };
        }

        private static SumoVisualSmoothing.RotationSettings LerpRotation(
            SumoVisualSmoothing.RotationSettings from,
            SumoVisualSmoothing.RotationSettings to,
            float t)
        {
            return new SumoVisualSmoothing.RotationSettings
            {
                DeadZoneDegrees = Mathf.Lerp(from.DeadZoneDegrees, to.DeadZoneDegrees, t),
                SmallErrorDegrees = Mathf.Lerp(from.SmallErrorDegrees, to.SmallErrorDegrees, t),
                MediumErrorDegrees = Mathf.Lerp(from.MediumErrorDegrees, to.MediumErrorDegrees, t),
                SmallSharpness = Mathf.Lerp(from.SmallSharpness, to.SmallSharpness, t),
                MediumSharpness = Mathf.Lerp(from.MediumSharpness, to.MediumSharpness, t),
                LargeSharpness = Mathf.Lerp(from.LargeSharpness, to.LargeSharpness, t),
                SmallCatchUpDegreesPerSecond = Mathf.Lerp(from.SmallCatchUpDegreesPerSecond, to.SmallCatchUpDegreesPerSecond, t),
                MediumCatchUpDegreesPerSecond = Mathf.Lerp(from.MediumCatchUpDegreesPerSecond, to.MediumCatchUpDegreesPerSecond, t),
                LargeCatchUpDegreesPerSecond = Mathf.Lerp(from.LargeCatchUpDegreesPerSecond, to.LargeCatchUpDegreesPerSecond, t),
                HardSnapDegrees = Mathf.Lerp(from.HardSnapDegrees, to.HardSnapDegrees, t)
            };
        }

        private bool IsLocalPresentation()
        {
            return networkObject != null && networkObject.HasInputAuthority;
        }

        private bool ShouldDisablePresentationForDedicatedServer()
        {
            if (!disablePresentationOnDedicatedServer)
            {
                return false;
            }

            if (!Application.isBatchMode)
            {
                return false;
            }

            if (networkObject == null || networkObject.Runner == null)
            {
                return false;
            }

            return networkObject.Runner.IsServer && !networkObject.Runner.IsClient;
        }

        private void OnValidate()
        {
            if (networkObject == null)
            {
                networkObject = GetComponent<NetworkObject>();
            }

            if (networkRigidbody == null)
            {
                networkRigidbody = GetComponent<NetworkRigidbody3D>();
            }

            if (ballController == null)
            {
                ballController = GetComponent<SumoBallController>();
            }

            if (collisionController == null)
            {
                collisionController = GetComponent<SumoCollisionController>();
            }

            if (visualSmoothing == null)
            {
                visualSmoothing = GetComponent<SumoVisualSmoothing>();
            }

            if (visualShell == transform)
            {
                visualShell = null;
            }

            if (interpolationAnchor == transform)
            {
                interpolationAnchor = null;
            }

            if (localProfile.Position.MediumError <= 0f)
            {
                localProfile = SumoVisualSmoothing.SmoothingProfile.CreateLocalDefault();
            }

            if (proxyProfile.Position.MediumError <= 0f)
            {
                proxyProfile = SumoVisualSmoothing.SmoothingProfile.CreateProxyDefault();
            }

            if (localVictimProfile.Position.MediumError <= 0f)
            {
                localVictimProfile = SumoVisualSmoothing.SmoothingProfile.CreateLocalVictimDefault();
            }

            if (proxyVictimProfile.Position.MediumError <= 0f)
            {
                proxyVictimProfile = SumoVisualSmoothing.SmoothingProfile.CreateProxyVictimDefault();
            }

            UpgradeLegacyVictimProfiles();

            victimMinSmoothingDuration = Mathf.Max(0.01f, victimMinSmoothingDuration);
            victimMaxSmoothingDuration = Mathf.Max(victimMinSmoothingDuration, victimMaxSmoothingDuration);
            victimSmoothingExtension = Mathf.Max(0f, victimSmoothingExtension);
            collisionTriggeredStrength = Mathf.Clamp01(collisionTriggeredStrength);
            collisionTriggerCooldown = Mathf.Max(0f, collisionTriggerCooldown);
            correctionSpikeStartDistance = Mathf.Max(0.01f, correctionSpikeStartDistance);
            correctionSpikeMaxDistance = Mathf.Max(correctionSpikeStartDistance + 0.01f, correctionSpikeMaxDistance);
            correctionSpikeTriggerCooldown = Mathf.Max(0f, correctionSpikeTriggerCooldown);
            victimBlendRiseSpeed = Mathf.Max(0.01f, victimBlendRiseSpeed);
            victimBlendFallSpeed = Mathf.Max(0.01f, victimBlendFallSpeed);
            victimBlendFloor = Mathf.Clamp01(victimBlendFloor);
            victimBlendResetThreshold = Mathf.Clamp(victimBlendResetThreshold, 0f, 0.5f);
        }
    }
}
