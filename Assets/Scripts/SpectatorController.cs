using System.Collections.Generic;
using Fusion;
using UnityEngine;

#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace Sumo.Gameplay
{
    [DisallowMultipleComponent]
    [RequireComponent(typeof(PlayerRoundState))]
    [RequireComponent(typeof(NetworkObject))]
    public sealed class SpectatorController : NetworkBehaviour
    {
        [Header("References")]
        [SerializeField] private PlayerRoundState roundState;
        [SerializeField] private Sumo.SumoCameraFollow gameplayCameraFollow;
        [SerializeField] private Sumo.SumoThirdPersonCamera gameplayLegacyCamera;
        [SerializeField] private Camera spectatorCameraPrefab;

        [Header("Spectator Camera")]
        [SerializeField] private float followDistance = 10f;
        [SerializeField] private float followHeight = 4f;
        [SerializeField] private float followSmoothing = 8f;
        [SerializeField] private float lookOffsetHeight = 1.1f;
        [SerializeField] private float fallbackOrbitRadius = 7f;
        [SerializeField] private bool disableSceneMainCamera = true;

        [Header("Manual Look")]
        [SerializeField] private float mouseDegreesPerPixel = 0.085f;
        [SerializeField] private float legacyMouseDegreesPerFrame = 8f;
        [SerializeField] private float gamepadLookSpeed = 220f;
        [SerializeField] private float keyboardLookSpeed = 120f;
        [SerializeField] private float minPitch = -65f;
        [SerializeField] private float maxPitch = 75f;

        [Header("Target Search")]
        [SerializeField] private float targetRefreshInterval = 0.35f;

        private readonly List<PlayerRoundState> _aliveTargets = new List<PlayerRoundState>(16);
        private Camera _spectatorCamera;
        private AudioListener _spectatorAudioListener;
        private bool _ownsCamera;
        private int _targetIndex;
        private float _nextRefreshTime;
        private bool _spectatorModeActive;
        private float _yaw;
        private float _pitch;
        private bool _rotationInitialized;

        private void Awake()
        {
            CacheReferencesIfNeeded();
        }

        public override void Render()
        {
            if (Object == null || !Object.HasInputAuthority)
            {
                return;
            }

            CacheReferencesIfNeeded();
            bool shouldSpectate = roundState != null && roundState.IsSpectator && !roundState.IsAlive;

            if (shouldSpectate)
            {
                if (!_spectatorModeActive)
                {
                    EnterSpectatorMode();
                }

                TickSpectatorMode();
                return;
            }

            if (_spectatorModeActive)
            {
                ExitSpectatorMode();
            }
        }

        private void OnDisable()
        {
            if (_spectatorModeActive)
            {
                ExitSpectatorMode();
            }
        }

        private void EnterSpectatorMode()
        {
            _spectatorModeActive = true;
            _nextRefreshTime = 0f;
            _targetIndex = 0;

            if (gameplayCameraFollow != null)
            {
                gameplayCameraFollow.enabled = false;
            }

            if (gameplayLegacyCamera != null)
            {
                gameplayLegacyCamera.enabled = false;
            }

            EnsureSpectatorCamera();
            InitializeLookAngles();
            RefreshTargets();
        }

        private void ExitSpectatorMode()
        {
            _spectatorModeActive = false;
            _aliveTargets.Clear();
            _targetIndex = 0;
            _rotationInitialized = false;

            ReleaseSpectatorCamera();

            if (gameplayLegacyCamera != null)
            {
                gameplayLegacyCamera.enabled = true;
            }

            if (gameplayCameraFollow != null)
            {
                gameplayCameraFollow.enabled = true;
            }
        }

        private void TickSpectatorMode()
        {
            if (_spectatorCamera == null)
            {
                EnsureSpectatorCamera();
                if (_spectatorCamera == null)
                {
                    return;
                }
            }

            if (Time.unscaledTime >= _nextRefreshTime)
            {
                _nextRefreshTime = Time.unscaledTime + Mathf.Max(0.1f, targetRefreshInterval);
                RefreshTargets();
            }

            if (ReadNextTargetPressed() && _aliveTargets.Count > 0)
            {
                _targetIndex = (_targetIndex + 1) % _aliveTargets.Count;
            }
            else if (ReadPreviousTargetPressed() && _aliveTargets.Count > 0)
            {
                _targetIndex = (_targetIndex - 1 + _aliveTargets.Count) % _aliveTargets.Count;
            }

            ApplyManualLook();

            Vector3 desiredPosition;
            Quaternion desiredRotation;
            ResolveCameraPose(out desiredPosition, out desiredRotation);

            Transform camTransform = _spectatorCamera.transform;
            float blend = 1f - Mathf.Exp(-Mathf.Max(0.01f, followSmoothing) * Time.unscaledDeltaTime);
            Vector3 smoothedPos = Vector3.Lerp(camTransform.position, desiredPosition, blend);
            Quaternion smoothedRot = Quaternion.Slerp(camTransform.rotation, desiredRotation, blend);

            camTransform.SetPositionAndRotation(smoothedPos, smoothedRot);
        }

        private void ResolveCameraPose(out Vector3 desiredPosition, out Quaternion desiredRotation)
        {
            Quaternion lookRotation = Quaternion.Euler(_pitch, _yaw, 0f);
            Vector3 localOffset = new Vector3(0f, Mathf.Max(0f, followHeight), -Mathf.Max(2f, followDistance));

            PlayerRoundState target = GetCurrentTarget();
            if (target != null)
            {
                Vector3 center = target.ZoneCheckPosition + Vector3.up * Mathf.Max(0f, lookOffsetHeight);
                desiredPosition = center + lookRotation * localOffset;
                desiredRotation = lookRotation;
                return;
            }

            Vector3 centerFallback = transform.position + Vector3.up * (lookOffsetHeight + 1f);
            desiredPosition = centerFallback + lookRotation * new Vector3(0f, Mathf.Max(0f, followHeight), -Mathf.Max(2f, fallbackOrbitRadius));
            desiredRotation = lookRotation;
        }

        private PlayerRoundState GetCurrentTarget()
        {
            if (_aliveTargets.Count == 0)
            {
                return null;
            }

            _targetIndex = Mathf.Clamp(_targetIndex, 0, _aliveTargets.Count - 1);
            PlayerRoundState target = _aliveTargets[_targetIndex];
            if (target != null && target.IsAliveInRound)
            {
                return target;
            }

            RefreshTargets();
            if (_aliveTargets.Count == 0)
            {
                return null;
            }

            _targetIndex = Mathf.Clamp(_targetIndex, 0, _aliveTargets.Count - 1);
            return _aliveTargets[_targetIndex];
        }

        private void RefreshTargets()
        {
            PlayerRoundState current = null;
            if (_aliveTargets.Count > 0 && _targetIndex >= 0 && _targetIndex < _aliveTargets.Count)
            {
                current = _aliveTargets[_targetIndex];
            }

            _aliveTargets.Clear();
            PlayerRoundState[] allPlayers = FindObjectsOfType<PlayerRoundState>(includeInactive: false);
            if (allPlayers != null)
            {
                for (int i = 0; i < allPlayers.Length; i++)
                {
                    PlayerRoundState candidate = allPlayers[i];
                    if (candidate == null || candidate == roundState)
                    {
                        continue;
                    }

                    if (candidate.IsAliveInRound)
                    {
                        _aliveTargets.Add(candidate);
                    }
                }
            }

            if (_aliveTargets.Count == 0)
            {
                _targetIndex = 0;
                return;
            }

            if (current != null)
            {
                int idx = _aliveTargets.IndexOf(current);
                if (idx >= 0)
                {
                    _targetIndex = idx;
                    return;
                }
            }

            _targetIndex = Mathf.Clamp(_targetIndex, 0, _aliveTargets.Count - 1);
        }

        private void EnsureSpectatorCamera()
        {
            if (_spectatorCamera != null)
            {
                _spectatorCamera.enabled = true;
                if (_spectatorAudioListener != null)
                {
                    _spectatorAudioListener.enabled = true;
                }

                return;
            }

            Camera existingChild = GetComponentInChildren<Camera>(includeInactive: true);
            if (existingChild != null && existingChild != _spectatorCamera)
            {
                _spectatorCamera = existingChild;
                _spectatorAudioListener = existingChild.GetComponent<AudioListener>();
                _ownsCamera = false;
            }
            else if (spectatorCameraPrefab != null)
            {
                _spectatorCamera = Instantiate(spectatorCameraPrefab);
                _spectatorAudioListener = _spectatorCamera.GetComponent<AudioListener>();
                _ownsCamera = true;
            }
            else
            {
                GameObject camObject = new GameObject("SpectatorCamera");
                _spectatorCamera = camObject.AddComponent<Camera>();
                _spectatorAudioListener = camObject.AddComponent<AudioListener>();
                _ownsCamera = true;
            }

            if (_spectatorCamera != null)
            {
                _spectatorCamera.enabled = true;
                _spectatorCamera.tag = "MainCamera";

                if (_spectatorAudioListener == null)
                {
                    _spectatorAudioListener = _spectatorCamera.gameObject.AddComponent<AudioListener>();
                }

                _spectatorAudioListener.enabled = true;
            }

            if (disableSceneMainCamera)
            {
                Camera main = Camera.main;
                if (main != null && main != _spectatorCamera)
                {
                    main.enabled = false;
                    AudioListener listener = main.GetComponent<AudioListener>();
                    if (listener != null)
                    {
                        listener.enabled = false;
                    }
                }
            }
        }

        private void ReleaseSpectatorCamera()
        {
            if (_spectatorCamera == null)
            {
                return;
            }

            if (_ownsCamera)
            {
                Destroy(_spectatorCamera.gameObject);
            }
            else
            {
                _spectatorCamera.enabled = false;
                if (_spectatorAudioListener != null)
                {
                    _spectatorAudioListener.enabled = false;
                }
            }

            _spectatorCamera = null;
            _spectatorAudioListener = null;
            _ownsCamera = false;
        }

        private void CacheReferencesIfNeeded()
        {
            if (roundState == null)
            {
                roundState = GetComponent<PlayerRoundState>();
            }

            if (gameplayCameraFollow == null)
            {
                gameplayCameraFollow = GetComponent<Sumo.SumoCameraFollow>();
            }

            if (gameplayLegacyCamera == null)
            {
                gameplayLegacyCamera = GetComponent<Sumo.SumoThirdPersonCamera>();
            }
        }

        private static bool ReadNextTargetPressed()
        {
#if ENABLE_INPUT_SYSTEM
            Keyboard keyboard = Keyboard.current;
            if (keyboard != null && keyboard.eKey.wasPressedThisFrame)
            {
                return true;
            }
#endif

#if ENABLE_LEGACY_INPUT_MANAGER
            if (Input.GetKeyDown(KeyCode.E))
            {
                return true;
            }
#endif

            return false;
        }

        private static bool ReadPreviousTargetPressed()
        {
#if ENABLE_INPUT_SYSTEM
            Keyboard keyboard = Keyboard.current;
            if (keyboard != null && keyboard.qKey.wasPressedThisFrame)
            {
                return true;
            }
#endif

#if ENABLE_LEGACY_INPUT_MANAGER
            if (Input.GetKeyDown(KeyCode.Q))
            {
                return true;
            }
#endif

            return false;
        }

        private void OnValidate()
        {
            followDistance = Mathf.Max(2f, followDistance);
            followHeight = Mathf.Max(1f, followHeight);
            followSmoothing = Mathf.Max(0.01f, followSmoothing);
            lookOffsetHeight = Mathf.Max(0f, lookOffsetHeight);
            fallbackOrbitRadius = Mathf.Max(2f, fallbackOrbitRadius);
            targetRefreshInterval = Mathf.Max(0.1f, targetRefreshInterval);
            mouseDegreesPerPixel = Mathf.Max(0.01f, mouseDegreesPerPixel);
            legacyMouseDegreesPerFrame = Mathf.Max(0.25f, legacyMouseDegreesPerFrame);
            gamepadLookSpeed = Mathf.Max(10f, gamepadLookSpeed);
            keyboardLookSpeed = Mathf.Max(10f, keyboardLookSpeed);
            minPitch = Mathf.Clamp(minPitch, -89f, 89f);
            maxPitch = Mathf.Clamp(maxPitch, -89f, 89f);
            if (maxPitch < minPitch)
            {
                maxPitch = minPitch;
            }

            CacheReferencesIfNeeded();
        }

        private void InitializeLookAngles()
        {
            if (_rotationInitialized || _spectatorCamera == null)
            {
                return;
            }

            Vector3 euler = _spectatorCamera.transform.rotation.eulerAngles;
            _yaw = euler.y;
            _pitch = NormalizePitch(euler.x);
            _rotationInitialized = true;
        }

        private void ApplyManualLook()
        {
            if (!_rotationInitialized)
            {
                InitializeLookAngles();
            }

            Vector2 lookDelta = ReadLookDelta();
            if (lookDelta.sqrMagnitude <= 0.0000001f)
            {
                return;
            }

            _yaw += lookDelta.x;
            _pitch = Mathf.Clamp(_pitch - lookDelta.y, minPitch, maxPitch);
        }

        private Vector2 ReadLookDelta()
        {
            Vector2 delta = Vector2.zero;
            bool hasModernInput = false;

#if ENABLE_INPUT_SYSTEM
            Mouse mouse = Mouse.current;
            if (mouse != null)
            {
                Vector2 mousePixels = mouse.delta.ReadValue();
                if (mousePixels.sqrMagnitude > 0.000001f)
                {
                    delta += mousePixels * mouseDegreesPerPixel;
                    hasModernInput = true;
                }
            }

            Gamepad gamepad = Gamepad.current;
            if (gamepad != null)
            {
                Vector2 stick = gamepad.rightStick.ReadValue();
                if (stick.sqrMagnitude > 0.0001f)
                {
                    delta += stick * gamepadLookSpeed * Time.unscaledDeltaTime;
                    hasModernInput = true;
                }
            }

            Keyboard keyboard = Keyboard.current;
            if (keyboard != null)
            {
                float keyX = 0f;
                float keyY = 0f;

                if (keyboard.leftArrowKey.isPressed)
                {
                    keyX -= 1f;
                }

                if (keyboard.rightArrowKey.isPressed)
                {
                    keyX += 1f;
                }

                if (keyboard.upArrowKey.isPressed)
                {
                    keyY += 1f;
                }

                if (keyboard.downArrowKey.isPressed)
                {
                    keyY -= 1f;
                }

                if (Mathf.Abs(keyX) > 0.01f || Mathf.Abs(keyY) > 0.01f)
                {
                    delta += new Vector2(keyX, keyY) * keyboardLookSpeed * Time.unscaledDeltaTime;
                    hasModernInput = true;
                }
            }
#endif

#if ENABLE_LEGACY_INPUT_MANAGER
            if (!hasModernInput)
            {
                float mouseX = Input.GetAxisRaw("Mouse X");
                float mouseY = Input.GetAxisRaw("Mouse Y");
                delta += new Vector2(mouseX, mouseY) * legacyMouseDegreesPerFrame;

                float keyX = 0f;
                float keyY = 0f;
                if (Input.GetKey(KeyCode.LeftArrow))
                {
                    keyX -= 1f;
                }

                if (Input.GetKey(KeyCode.RightArrow))
                {
                    keyX += 1f;
                }

                if (Input.GetKey(KeyCode.UpArrow))
                {
                    keyY += 1f;
                }

                if (Input.GetKey(KeyCode.DownArrow))
                {
                    keyY -= 1f;
                }

                delta += new Vector2(keyX, keyY) * keyboardLookSpeed * Time.unscaledDeltaTime;
            }
#endif
            _ = hasModernInput;

            return delta;
        }

        private static float NormalizePitch(float pitchDegrees)
        {
            float pitch = pitchDegrees;
            if (pitch > 180f)
            {
                pitch -= 360f;
            }

            return pitch;
        }
    }
}
