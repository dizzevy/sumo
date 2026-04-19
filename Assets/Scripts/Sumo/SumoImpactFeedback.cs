using Fusion;
using UnityEngine;
using UnityEngine.Serialization;

namespace Sumo
{
    [DisallowMultipleComponent]
    [RequireComponent(typeof(NetworkObject))]
    [RequireComponent(typeof(SumoCollisionController))]
    public sealed class SumoImpactFeedback : MonoBehaviour
    {
        [Header("References")]
        [SerializeField] private NetworkObject networkObject;
        [SerializeField] private SumoCollisionController collisionController;
        [SerializeField] private Transform visualModel;
        [SerializeField] private ParticleSystem impactVfxPrefab;
        [SerializeField] private AudioSource impactAudioSource;
        [SerializeField] private AudioClip impactAudioClip;

        [Header("Thresholds")]
        [SerializeField] private bool localPlayerOnly = true;
        [FormerlySerializedAs("minRelativeImpactSpeed")]
        [SerializeField] private float minImpactImpulse = 2f;
        [FormerlySerializedAs("maxRelativeImpactSpeed")]
        [SerializeField] private float maxImpactImpulse = 20f;
        [SerializeField] private int feedbackCooldownFrames = 2;

        [Header("Visual Squash")]
        [SerializeField] private bool enableSquash = true;
        [SerializeField] private float maxSquashAmount = 0.1f;
        [SerializeField] private float squashRecoverySpeed = 12f;

        [Header("Audio")]
        [SerializeField] private Vector2 impactVolumeRange = new Vector2(0.15f, 0.8f);

        private Vector3 _initialVisualScale = Vector3.one;
        private float _squashAmount;
        private int _lastFeedbackFrame = int.MinValue;

        private void Awake()
        {
            if (networkObject == null)
            {
                networkObject = GetComponent<NetworkObject>();
            }

            if (collisionController == null)
            {
                collisionController = GetComponent<SumoCollisionController>();
            }

            if (visualModel == null)
            {
                if (TryGetComponent(out SumoBallController ballController) && ballController.CameraFollowTarget != null)
                {
                    visualModel = ballController.CameraFollowTarget;
                }
                else if (transform.childCount > 0)
                {
                    visualModel = transform.GetChild(0);
                }
            }

            if (visualModel == transform)
            {
                visualModel = null;
                enableSquash = false;
            }

            if (visualModel != null)
            {
                _initialVisualScale = visualModel.localScale;
            }
        }

        private void OnEnable()
        {
            if (collisionController != null)
            {
                collisionController.ImpactApplied += HandleImpactApplied;
            }
        }

        private void LateUpdate()
        {
            if (!enableSquash || visualModel == null)
            {
                return;
            }

            _squashAmount = Mathf.MoveTowards(_squashAmount, 0f, squashRecoverySpeed * Time.deltaTime);
            ApplySquash(_squashAmount);
        }

        private void HandleImpactApplied(SumoImpactData impactData)
        {
            if (!ShouldPlayLocalFeedback())
            {
                return;
            }

            if (impactData.Attacker != networkObject && impactData.Victim != networkObject)
            {
                return;
            }

            if (Time.frameCount - _lastFeedbackFrame <= feedbackCooldownFrames)
            {
                return;
            }

            float normalizedByImpulse = Mathf.InverseLerp(minImpactImpulse, maxImpactImpulse, impactData.FinalImpulse);
            float normalizedImpact = Mathf.Clamp01(Mathf.Max(normalizedByImpulse, impactData.SpeedStrength));
            if (normalizedImpact <= 0f)
            {
                return;
            }

            _lastFeedbackFrame = Time.frameCount;

            SpawnVfx(impactData.ContactPoint, impactData.ImpactDirection, normalizedImpact);
            PlayImpactAudio(normalizedImpact);
            TriggerSquash(normalizedImpact);
        }

        private bool ShouldPlayLocalFeedback()
        {
            if (!isActiveAndEnabled || networkObject == null)
            {
                return false;
            }

            if (Application.isBatchMode)
            {
                return false;
            }

            if (networkObject.Runner != null && networkObject.Runner.IsServer && !networkObject.Runner.IsClient)
            {
                return false;
            }

            if (localPlayerOnly && !networkObject.HasInputAuthority)
            {
                return false;
            }

            return true;
        }

        private void SpawnVfx(Vector3 impactPoint, Vector3 impactDirection, float normalizedImpact)
        {
            if (impactVfxPrefab == null)
            {
                return;
            }

            Vector3 direction = impactDirection.sqrMagnitude > 0.0001f
                ? impactDirection.normalized
                : Vector3.up;

            Quaternion rotation = Quaternion.LookRotation(direction, Vector3.up);
            ParticleSystem effect = Instantiate(impactVfxPrefab, impactPoint, rotation);

            var main = effect.main;
            float scaleMultiplier = Mathf.Lerp(0.9f, 1.4f, normalizedImpact);
            main.startSizeMultiplier = main.startSizeMultiplier * scaleMultiplier;

            float life = main.duration + main.startLifetime.constantMax;
            effect.Play();
            Destroy(effect.gameObject, Mathf.Max(0.2f, life + 0.2f));
        }

        private void PlayImpactAudio(float normalizedImpact)
        {
            if (impactAudioSource == null || impactAudioClip == null)
            {
                return;
            }

            float volume = Mathf.Lerp(impactVolumeRange.x, impactVolumeRange.y, normalizedImpact);
            impactAudioSource.PlayOneShot(impactAudioClip, Mathf.Clamp01(volume));
        }

        private void TriggerSquash(float normalizedImpact)
        {
            if (!enableSquash)
            {
                return;
            }

            _squashAmount = Mathf.Clamp01(Mathf.Max(_squashAmount, normalizedImpact));
            ApplySquash(_squashAmount);
        }

        private void ApplySquash(float normalizedAmount)
        {
            if (visualModel == null)
            {
                return;
            }

            float squash = Mathf.Clamp01(normalizedAmount) * Mathf.Max(0f, maxSquashAmount);
            Vector3 stretch = new Vector3(1f + squash * 0.5f, 1f - squash, 1f + squash * 0.5f);
            visualModel.localScale = Vector3.Scale(_initialVisualScale, stretch);
        }

        private void OnDisable()
        {
            if (collisionController != null)
            {
                collisionController.ImpactApplied -= HandleImpactApplied;
            }

            if (visualModel != null)
            {
                visualModel.localScale = _initialVisualScale;
            }

            _squashAmount = 0f;
            _lastFeedbackFrame = int.MinValue;
        }

        private void OnValidate()
        {
            minImpactImpulse = Mathf.Max(0f, minImpactImpulse);
            maxImpactImpulse = Mathf.Max(minImpactImpulse + 0.01f, maxImpactImpulse);
            feedbackCooldownFrames = Mathf.Max(0, feedbackCooldownFrames);
            maxSquashAmount = Mathf.Max(0f, maxSquashAmount);
            squashRecoverySpeed = Mathf.Max(0.01f, squashRecoverySpeed);
            impactVolumeRange.x = Mathf.Clamp01(impactVolumeRange.x);
            impactVolumeRange.y = Mathf.Clamp01(impactVolumeRange.y);

            if (visualModel == transform)
            {
                visualModel = null;
            }

            if (networkObject == null)
            {
                networkObject = GetComponent<NetworkObject>();
            }

            if (collisionController == null)
            {
                collisionController = GetComponent<SumoCollisionController>();
            }
        }
    }
}
