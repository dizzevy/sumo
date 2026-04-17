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

        private SumoPlayerInput _input;
        private Camera _cameraInstance;
        private AudioListener _audioListener;
        private Transform _resolvedFollowTarget;
        private bool _ownsCameraInstance;
        private bool _cameraInitialized;

        public override void Spawned()
        {
            _input = GetComponent<SumoPlayerInput>();

            if (!HasInputAuthority)
            {
                DisableProxyCameraComponents();
                enabled = false;
                return;
            }

            if (_input != null)
            {
                _input.ConfigureLook(sensitivity, minPitch, maxPitch);
            }

            ResolveFollowTarget();

            CreateOrReuseLocalCamera();

            if (disableSceneMainCamera)
            {
                DisableSceneCameraIfNeeded();
            }
        }

        public override void Despawned(NetworkRunner runner, bool hasState)
        {
            if (_ownsCameraInstance && _cameraInstance != null)
            {
                Destroy(_cameraInstance.gameObject);
            }

            _cameraInstance = null;
            _audioListener = null;
            _cameraInitialized = false;
            _ownsCameraInstance = false;
        }

        private void LateUpdate()
        {
            if (!HasInputAuthority || _cameraInstance == null)
            {
                return;
            }

            if (_resolvedFollowTarget == null)
            {
                ResolveFollowTarget();
            }

            float yaw = _input != null ? _input.CameraYaw : transform.eulerAngles.y;
            float pitch = _input != null ? _input.CameraPitch : 15f;

            Vector3 followPosition = _resolvedFollowTarget != null ? _resolvedFollowTarget.position : transform.position;
            Vector3 pivot = followPosition + Vector3.up * height;
            Quaternion targetRotation = Quaternion.Euler(pitch, yaw, 0f);

            Vector3 desiredPosition = pivot - targetRotation * Vector3.forward * distance;
            desiredPosition = ResolveCameraCollision(pivot, desiredPosition);

            if (!_cameraInitialized)
            {
                _cameraInstance.transform.SetPositionAndRotation(desiredPosition, targetRotation);
                _cameraInitialized = true;
                return;
            }

            float blend = 1f - Mathf.Exp(-Mathf.Max(0.01f, smoothing) * Time.unscaledDeltaTime);
            Transform cameraTransform = _cameraInstance.transform;
            cameraTransform.position = Vector3.Lerp(cameraTransform.position, desiredPosition, blend);
            cameraTransform.rotation = Quaternion.Slerp(cameraTransform.rotation, targetRotation, blend);
        }

        private void CreateOrReuseLocalCamera()
        {
            Camera childCamera = GetComponentInChildren<Camera>(true);
            if (childCamera != null)
            {
                _cameraInstance = childCamera;
                _audioListener = childCamera.GetComponent<AudioListener>();
                _ownsCameraInstance = false;
            }
            else if (cameraPrefab != null)
            {
                _cameraInstance = Instantiate(cameraPrefab);
                _audioListener = _cameraInstance.GetComponent<AudioListener>();
                _ownsCameraInstance = true;
            }
            else
            {
                GameObject cameraObject = new GameObject("SumoLocalCamera");
                _cameraInstance = cameraObject.AddComponent<Camera>();
                _audioListener = cameraObject.AddComponent<AudioListener>();
                _ownsCameraInstance = true;
            }

            _cameraInstance.enabled = true;
            _cameraInstance.tag = "MainCamera";

            if (_audioListener == null)
            {
                _audioListener = _cameraInstance.gameObject.AddComponent<AudioListener>();
            }

            _audioListener.enabled = true;
            _cameraInitialized = false;
        }

        private void DisableProxyCameraComponents()
        {
            Camera[] cameras = GetComponentsInChildren<Camera>(true);
            for (int i = 0; i < cameras.Length; i++)
            {
                cameras[i].enabled = false;
            }

            AudioListener[] listeners = GetComponentsInChildren<AudioListener>(true);
            for (int i = 0; i < listeners.Length; i++)
            {
                listeners[i].enabled = false;
            }
        }

        private void DisableSceneCameraIfNeeded()
        {
            Camera mainCamera = Camera.main;
            if (mainCamera == null || mainCamera == _cameraInstance)
            {
                return;
            }

            mainCamera.enabled = false;

            AudioListener listener = mainCamera.GetComponent<AudioListener>();
            if (listener != null)
            {
                listener.enabled = false;
            }
        }

        private void ResolveFollowTarget()
        {
            if (followTarget != null)
            {
                _resolvedFollowTarget = followTarget;
                return;
            }

            if (useBallVisualTargetForCamera && TryGetComponent(out SumoBallController ballController))
            {
                Transform ballTarget = ballController.CameraFollowTarget;
                if (ballTarget != null)
                {
                    _resolvedFollowTarget = ballTarget;
                    return;
                }
            }

            _resolvedFollowTarget = transform;
        }

        private Vector3 ResolveCameraCollision(Vector3 pivot, Vector3 desiredPosition)
        {
            Vector3 direction = desiredPosition - pivot;
            float distanceToCamera = direction.magnitude;

            if (distanceToCamera <= 0.001f)
            {
                return desiredPosition;
            }

            direction /= distanceToCamera;

            if (Physics.SphereCast(
                    pivot,
                    obstructionRadius,
                    direction,
                    out RaycastHit hit,
                    distanceToCamera,
                    obstructionMask,
                    QueryTriggerInteraction.Ignore))
            {
                return hit.point - direction * obstructionPadding;
            }

            return desiredPosition;
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
