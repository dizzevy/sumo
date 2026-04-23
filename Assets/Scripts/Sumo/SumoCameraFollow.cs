using Fusion;
using UnityEngine;

namespace Sumo
{
    [DisallowMultipleComponent]
    [RequireComponent(typeof(NetworkObject))]
    [RequireComponent(typeof(SumoPlayerInput))]
    public sealed class SumoCameraFollow : MonoBehaviour
    {
        [Header("Camera")]
        [SerializeField] private float distance = 6f;
        [SerializeField] private float height = 2f;
        [SerializeField] private float sensitivity = 2.5f;
        [SerializeField] private float minPitch = -25f;
        [SerializeField] private float maxPitch = 70f;
        [SerializeField] private float positionSmoothing = 14f;
        [SerializeField] private float rotationSmoothing = 16f;

        [Header("Collision")]
        [SerializeField] private LayerMask obstructionMask = ~0;
        [SerializeField] private float obstructionRadius = 0.2f;
        [SerializeField] private float obstructionPadding = 0.1f;

        [Header("Target")]
        [SerializeField] private Transform followTarget;
        [SerializeField] private bool usePresentationTarget = true;
        [SerializeField] private Camera cameraPrefab;
        [SerializeField] private bool disableSceneMainCamera = true;

        [Header("Impact Shake")]
        [SerializeField] private bool enableImpactShake = false;
        [SerializeField] private float shakeDecayPerSecond = 3.2f;
        [SerializeField] private float shakeFrequency = 24f;
        [SerializeField] private float maxShakePosition = 0.15f;
        [SerializeField] private float maxShakeRotation = 1.6f;

        private NetworkObject _networkObject;
        private SumoPlayerInput _input;
        private Camera _cameraInstance;
        private AudioListener _audioListener;
        private Transform _resolvedFollowTarget;
        private bool _ownsCameraInstance;
        private bool _wasLocalAuthority;
        private float _shakeTrauma;
        private float _shakeSeed;

        private void Awake()
        {
            CacheComponents();
            ClampParameters();
            ConfigureInputLook();
            _shakeSeed = Random.Range(0.11f, 19.37f);
        }

        private void OnEnable()
        {
        }

        private void OnDisable()
        {
            ReleaseOwnedCamera();
            _wasLocalAuthority = false;
        }

        public void ApplyLegacySettings(
            float followDistance,
            float followHeight,
            float lookSensitivity,
            float lookMinPitch,
            float lookMaxPitch,
            float followSmoothing,
            LayerMask collisionMask,
            float collisionRadius,
            float collisionPadding,
            Transform explicitFollowTarget,
            bool useBallVisualTarget,
            Camera preferredCameraPrefab,
            bool disableExistingMainCamera)
        {
            distance = followDistance;
            height = followHeight;
            sensitivity = lookSensitivity;
            minPitch = lookMinPitch;
            maxPitch = lookMaxPitch;
            positionSmoothing = followSmoothing;
            rotationSmoothing = followSmoothing;
            obstructionMask = collisionMask;
            obstructionRadius = collisionRadius;
            obstructionPadding = collisionPadding;
            followTarget = explicitFollowTarget;
            usePresentationTarget = useBallVisualTarget;
            cameraPrefab = preferredCameraPrefab;
            disableSceneMainCamera = disableExistingMainCamera;

            ClampParameters();
            ConfigureInputLook();
            _resolvedFollowTarget = null;
        }

        public void AddImpactShake(float normalizedStrength)
        {
            if (!enableImpactShake)
            {
                return;
            }

            float clamped = Mathf.Clamp01(normalizedStrength);
            if (clamped <= 0f)
            {
                return;
            }

            _shakeTrauma = Mathf.Clamp01(_shakeTrauma + clamped);
        }

        private void LateUpdate()
        {
            CacheComponents();

            bool localAuthority = HasLocalAuthority();
            if (!localAuthority)
            {
                if (_wasLocalAuthority)
                {
                    ReleaseOwnedCamera();
                }

                DisableProxyCameraComponents();
                _wasLocalAuthority = false;
                return;
            }

            if (!_wasLocalAuthority)
            {
                ConfigureInputLook();
                _resolvedFollowTarget = null;
            }

            if (_cameraInstance == null)
            {
                CreateOrReuseLocalCamera();
                if (disableSceneMainCamera)
                {
                    DisableSceneCameraIfNeeded();
                }
            }

            if (_cameraInstance == null)
            {
                return;
            }

            ResolveFollowTarget();

            float yaw = _input != null ? _input.CameraYaw : transform.eulerAngles.y;
            float pitch = _input != null ? _input.CameraPitch : 15f;

            Vector3 followPosition = _resolvedFollowTarget != null ? _resolvedFollowTarget.position : transform.position;
            Vector3 pivot = followPosition + Vector3.up * height;
            Quaternion targetRotation = Quaternion.Euler(pitch, yaw, 0f);

            Vector3 desiredPosition = pivot - targetRotation * Vector3.forward * distance;
            desiredPosition = ResolveCameraCollision(pivot, desiredPosition);

            Vector3 cameraPosition = desiredPosition;
            Quaternion cameraRotation = targetRotation;
            ApplyShake(ref cameraPosition, ref cameraRotation);
            _cameraInstance.transform.SetPositionAndRotation(cameraPosition, cameraRotation);

            _wasLocalAuthority = true;
        }

        private void ApplyShake(ref Vector3 cameraPosition, ref Quaternion cameraRotation)
        {
            if (!enableImpactShake)
            {
                _shakeTrauma = 0f;
                return;
            }

            _shakeTrauma = Mathf.MoveTowards(_shakeTrauma, 0f, Mathf.Max(0.01f, shakeDecayPerSecond) * Time.unscaledDeltaTime);
            if (_shakeTrauma <= 0.0001f)
            {
                _shakeTrauma = 0f;
                return;
            }

            float trauma = _shakeTrauma * _shakeTrauma;
            float time = Time.unscaledTime * Mathf.Max(0.1f, shakeFrequency);

            Vector3 shakeNoise = new Vector3(
                Mathf.PerlinNoise(_shakeSeed + 0.73f, time) - 0.5f,
                Mathf.PerlinNoise(_shakeSeed + 2.17f, time + 17f) - 0.5f,
                Mathf.PerlinNoise(_shakeSeed + 4.91f, time + 31f) - 0.5f) * 2f;

            Vector3 positionOffset = shakeNoise * (maxShakePosition * trauma);

            Vector3 rotationNoise = new Vector3(
                Mathf.PerlinNoise(_shakeSeed + 7.19f, time + 53f) - 0.5f,
                Mathf.PerlinNoise(_shakeSeed + 9.83f, time + 71f) - 0.5f,
                Mathf.PerlinNoise(_shakeSeed + 12.41f, time + 89f) - 0.5f) * 2f;

            Quaternion rotationOffset = Quaternion.Euler(rotationNoise * (maxShakeRotation * trauma));
            cameraPosition += cameraRotation * positionOffset;
            cameraRotation = cameraRotation * rotationOffset;
        }

        private bool HasLocalAuthority()
        {
            return _networkObject != null && _networkObject.HasInputAuthority;
        }

        private void ResolveFollowTarget()
        {
            if (usePresentationTarget && TryGetComponent(out SumoProxyPresentation proxyPresentation))
            {
                Transform target = proxyPresentation.CameraTarget;
                if (target != null)
                {
                    _resolvedFollowTarget = target;
                    return;
                }
            }

            if (followTarget != null)
            {
                _resolvedFollowTarget = followTarget;
                return;
            }

            if (TryGetComponent(out SumoBallController ballController))
            {
                Transform target = ballController.CameraFollowTarget;
                if (target != null)
                {
                    _resolvedFollowTarget = target;
                    return;
                }
            }

            _resolvedFollowTarget = transform;
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

            if (_cameraInstance != null)
            {
                _cameraInstance.enabled = true;
                _cameraInstance.tag = "MainCamera";

                if (_audioListener == null)
                {
                    _audioListener = _cameraInstance.gameObject.AddComponent<AudioListener>();
                }

                _audioListener.enabled = true;
            }
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

        private void CacheComponents()
        {
            if (_networkObject == null)
            {
                _networkObject = GetComponent<NetworkObject>();
            }

            if (_input == null)
            {
                _input = GetComponent<SumoPlayerInput>();
            }
        }

        private void ConfigureInputLook()
        {
            if (_input == null)
            {
                return;
            }

            _input.ConfigureLook(sensitivity, minPitch, maxPitch);
        }

        private void ReleaseOwnedCamera()
        {
            if (_ownsCameraInstance && _cameraInstance != null)
            {
                Destroy(_cameraInstance.gameObject);
            }

            _cameraInstance = null;
            _audioListener = null;
            _ownsCameraInstance = false;
        }

        private void ClampParameters()
        {
            distance = Mathf.Max(0.1f, distance);
            height = Mathf.Max(0f, height);
            sensitivity = Mathf.Max(0f, sensitivity);
            maxPitch = Mathf.Max(minPitch, maxPitch);
            positionSmoothing = Mathf.Max(0.01f, positionSmoothing);
            rotationSmoothing = Mathf.Max(0.01f, rotationSmoothing);
            obstructionRadius = Mathf.Max(0f, obstructionRadius);
            obstructionPadding = Mathf.Max(0f, obstructionPadding);
            shakeDecayPerSecond = Mathf.Max(0.01f, shakeDecayPerSecond);
            shakeFrequency = Mathf.Max(0.1f, shakeFrequency);
            maxShakePosition = Mathf.Max(0f, maxShakePosition);
            maxShakeRotation = Mathf.Max(0f, maxShakeRotation);
        }

        private void OnValidate()
        {
            ClampParameters();

            if (followTarget == transform)
            {
                followTarget = null;
            }
        }
    }
}
