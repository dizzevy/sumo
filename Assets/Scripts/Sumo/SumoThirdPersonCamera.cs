using Fusion;
using UnityEngine;

namespace Sumo
{
    [DisallowMultipleComponent]
    [RequireComponent(typeof(SumoPlayerInput))]
    public sealed class SumoThirdPersonCamera : NetworkBehaviour
    {
        [Header("Camera")]
        [SerializeField] private float distance = 6f;
        [SerializeField] private float height = 2f;
        [SerializeField] private float sensitivity = 2.5f;
        [SerializeField] private float minPitch = -25f;
        [SerializeField] private float maxPitch = 70f;
        [SerializeField] private float smoothing = 12f;

        [Header("Collision")]
        [SerializeField] private LayerMask obstructionMask = ~0;
        [SerializeField] private float obstructionRadius = 0.2f;
        [SerializeField] private float obstructionPadding = 0.1f;

        [Header("Optional")]
        [SerializeField] private Transform followTarget;
        [SerializeField] private bool useBallVisualTargetForCamera = true;
        [SerializeField] private Camera cameraPrefab;
        [SerializeField] private bool disableSceneMainCamera = true;

        private SumoCameraFollow _cameraFollow;

        public override void Spawned()
        {
            EnsureBridge();
            if (_cameraFollow == null)
            {
                return;
            }

            _cameraFollow.ApplyLegacySettings(
                distance,
                height,
                sensitivity,
                minPitch,
                maxPitch,
                smoothing,
                obstructionMask,
                obstructionRadius,
                obstructionPadding,
                followTarget,
                useBallVisualTargetForCamera,
                cameraPrefab,
                disableSceneMainCamera);
            _cameraFollow.enabled = true;
            enabled = false;
        }

        private void EnsureBridge()
        {
            if (_cameraFollow != null)
            {
                return;
            }

            _cameraFollow = GetComponent<SumoCameraFollow>();
            if (_cameraFollow == null)
            {
                _cameraFollow = gameObject.AddComponent<SumoCameraFollow>();
            }
        }

        private void OnValidate()
        {
            distance = Mathf.Max(0.1f, distance);
            height = Mathf.Max(0f, height);
            sensitivity = Mathf.Max(0f, sensitivity);
            maxPitch = Mathf.Max(minPitch, maxPitch);
            smoothing = Mathf.Max(0.01f, smoothing);
            obstructionRadius = Mathf.Max(0f, obstructionRadius);
            obstructionPadding = Mathf.Max(0f, obstructionPadding);

            if (followTarget == transform)
            {
                followTarget = null;
            }
        }
    }
}
