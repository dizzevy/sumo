using System;
using System.Collections.Generic;
using Fusion;
using Fusion.Sockets;
using UnityEngine;

#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace Sumo
{
    [DisallowMultipleComponent]
    public sealed class SumoPlayerInput : NetworkBehaviour, INetworkRunnerCallbacks
    {
        [Header("Look")]
        [SerializeField] private float mouseSensitivity = 2.5f;
        [SerializeField] private float minPitch = -25f;
        [SerializeField] private float maxPitch = 70f;
        [SerializeField] private bool lockCursorOnSpawn = true;

        private bool _callbacksRegistered;
        private float _yaw;
        private float _pitch = 15f;

        public float CameraYaw => _yaw;
        public float CameraPitch => _pitch;

        public void ConfigureLook(float sensitivity, float minPitchClamp, float maxPitchClamp)
        {
            mouseSensitivity = Mathf.Max(0f, sensitivity);
            minPitch = minPitchClamp;
            maxPitch = Mathf.Max(minPitchClamp, maxPitchClamp);
            _pitch = Mathf.Clamp(_pitch, minPitch, maxPitch);
        }

        public override void Spawned()
        {
            _yaw = transform.eulerAngles.y;

            if (!HasInputAuthority || Runner == null)
            {
                return;
            }

            RegisterCallbacks(Runner);

            if (lockCursorOnSpawn)
            {
                SetCursorLock(true);
            }
        }

        public override void Despawned(NetworkRunner runner, bool hasState)
        {
            UnregisterCallbacks(runner);

            if (HasInputAuthority)
            {
                SetCursorLock(false);
            }
        }

        private void Update()
        {
            if (!HasInputAuthority)
            {
                return;
            }

            Vector2 lookDelta = ReadLookDelta();
            _yaw = Mathf.Repeat(_yaw + lookDelta.x * mouseSensitivity, 360f);
            _pitch = Mathf.Clamp(_pitch - lookDelta.y * mouseSensitivity, minPitch, maxPitch);

            if (WasCursorUnlockPressed())
            {
                SetCursorLock(false);
            }
            else if (lockCursorOnSpawn && WasCursorLockPressed())
            {
                SetCursorLock(true);
            }
        }

        public void OnInput(NetworkRunner runner, NetworkInput input)
        {
            if (!_callbacksRegistered || !HasInputAuthority || runner != Runner)
            {
                return;
            }

            Vector2 moveInput = ReadMoveInput();
            if (moveInput.sqrMagnitude > 1f)
            {
                moveInput.Normalize();
            }
            else if (moveInput.sqrMagnitude < 0.0001f)
            {
                moveInput = Vector2.zero;
            }

            SumoInputData data = new SumoInputData
            {
                Move = moveInput,
                CameraYaw = Mathf.Repeat(_yaw, 360f),
                Buttons = ReadButtons()
            };

            input.Set(data);
        }

        public void OnInputMissing(NetworkRunner runner, PlayerRef player, NetworkInput input)
        {
        }

        public void OnPlayerJoined(NetworkRunner runner, PlayerRef player)
        {
        }

        public void OnPlayerLeft(NetworkRunner runner, PlayerRef player)
        {
        }

        public void OnShutdown(NetworkRunner runner, ShutdownReason shutdownReason)
        {
            UnregisterCallbacks(runner);
        }

        public void OnConnectedToServer(NetworkRunner runner)
        {
        }

        public void OnDisconnectedFromServer(NetworkRunner runner, NetDisconnectReason reason)
        {
        }

        public void OnConnectRequest(NetworkRunner runner, NetworkRunnerCallbackArgs.ConnectRequest request, byte[] token)
        {
        }

        public void OnConnectFailed(NetworkRunner runner, NetAddress remoteAddress, NetConnectFailedReason reason)
        {
        }

        public void OnUserSimulationMessage(NetworkRunner runner, SimulationMessagePtr message)
        {
        }

        public void OnSessionListUpdated(NetworkRunner runner, List<SessionInfo> sessionList)
        {
        }

        public void OnCustomAuthenticationResponse(NetworkRunner runner, Dictionary<string, object> data)
        {
        }

        public void OnHostMigration(NetworkRunner runner, HostMigrationToken hostMigrationToken)
        {
        }

        public void OnReliableDataReceived(NetworkRunner runner, PlayerRef player, ReliableKey key, ArraySegment<byte> data)
        {
        }

        public void OnReliableDataProgress(NetworkRunner runner, PlayerRef player, ReliableKey key, float progress)
        {
        }

        public void OnSceneLoadDone(NetworkRunner runner)
        {
        }

        public void OnSceneLoadStart(NetworkRunner runner)
        {
        }

        public void OnObjectEnterAOI(NetworkRunner runner, NetworkObject obj, PlayerRef player)
        {
        }

        public void OnObjectExitAOI(NetworkRunner runner, NetworkObject obj, PlayerRef player)
        {
        }

        private static Vector2 ReadMoveInput()
        {
            Vector2 move = Vector2.zero;

#if ENABLE_INPUT_SYSTEM
            Keyboard keyboard = Keyboard.current;
            if (keyboard != null)
            {
                if (keyboard.wKey.isPressed || keyboard.upArrowKey.isPressed) move.y += 1f;
                if (keyboard.sKey.isPressed || keyboard.downArrowKey.isPressed) move.y -= 1f;
                if (keyboard.dKey.isPressed || keyboard.rightArrowKey.isPressed) move.x += 1f;
                if (keyboard.aKey.isPressed || keyboard.leftArrowKey.isPressed) move.x -= 1f;
                return move;
            }
#endif

#if ENABLE_LEGACY_INPUT_MANAGER
            if (Input.GetKey(KeyCode.W) || Input.GetKey(KeyCode.UpArrow)) move.y += 1f;
            if (Input.GetKey(KeyCode.S) || Input.GetKey(KeyCode.DownArrow)) move.y -= 1f;
            if (Input.GetKey(KeyCode.D) || Input.GetKey(KeyCode.RightArrow)) move.x += 1f;
            if (Input.GetKey(KeyCode.A) || Input.GetKey(KeyCode.LeftArrow)) move.x -= 1f;
#endif

            return move;
        }

        private static NetworkButtons ReadButtons()
        {
            NetworkButtons buttons = default;
            buttons.Set((int)SumoInputButton.Brake, IsBrakePressed());
            return buttons;
        }

        private static bool IsBrakePressed()
        {
#if ENABLE_INPUT_SYSTEM
            Keyboard keyboard = Keyboard.current;
            if (keyboard != null)
            {
                return keyboard.spaceKey.isPressed;
            }
#endif

#if ENABLE_LEGACY_INPUT_MANAGER
            return Input.GetKey(KeyCode.Space);
#else
            return false;
#endif
        }

        private static Vector2 ReadLookDelta()
        {
#if ENABLE_INPUT_SYSTEM
            Mouse mouse = Mouse.current;
            if (mouse != null)
            {
                return mouse.delta.ReadValue() * 0.01f;
            }
#endif

#if ENABLE_LEGACY_INPUT_MANAGER
            return new Vector2(Input.GetAxisRaw("Mouse X"), Input.GetAxisRaw("Mouse Y"));
#else
            return Vector2.zero;
#endif
        }

        private static bool WasCursorUnlockPressed()
        {
#if ENABLE_INPUT_SYSTEM
            Keyboard keyboard = Keyboard.current;
            if (keyboard != null)
            {
                return keyboard.escapeKey.wasPressedThisFrame;
            }
#endif

#if ENABLE_LEGACY_INPUT_MANAGER
            return Input.GetKeyDown(KeyCode.Escape);
#else
            return false;
#endif
        }

        private static bool WasCursorLockPressed()
        {
#if ENABLE_INPUT_SYSTEM
            Mouse mouse = Mouse.current;
            if (mouse != null)
            {
                return mouse.leftButton.wasPressedThisFrame;
            }
#endif

#if ENABLE_LEGACY_INPUT_MANAGER
            return Input.GetMouseButtonDown(0);
#else
            return false;
#endif
        }

        private void RegisterCallbacks(NetworkRunner runner)
        {
            if (_callbacksRegistered || runner == null)
            {
                return;
            }

            runner.AddCallbacks(this);
            _callbacksRegistered = true;
        }

        private void UnregisterCallbacks(NetworkRunner runner)
        {
            if (!_callbacksRegistered || runner == null)
            {
                return;
            }

            runner.RemoveCallbacks(this);
            _callbacksRegistered = false;
        }

        private static void SetCursorLock(bool locked)
        {
            Cursor.lockState = locked ? CursorLockMode.Locked : CursorLockMode.None;
            Cursor.visible = !locked;
        }
    }
}
