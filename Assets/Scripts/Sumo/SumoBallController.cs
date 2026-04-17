using System;
using Fusion;
using Fusion.Addons.Physics;
using UnityEngine;
using UnityEngine.Serialization;

namespace Sumo
{
    [DisallowMultipleComponent]
    [RequireComponent(typeof(NetworkObject))]
    [RequireComponent(typeof(Rigidbody))]
    [RequireComponent(typeof(SphereCollider))]
    [RequireComponent(typeof(NetworkRigidbody3D))]
    public sealed class SumoBallController : NetworkBehaviour
    {
        [Header("Movement")]
        [SerializeField] private float acceleration = 35f;
        [SerializeField] private float maxSpeed = 10f;
        [SerializeField] private float braking = 20f;
        [FormerlySerializedAs("turnResponsiveness")]
        [SerializeField] private float velocitySmoothing = 12f;
        [SerializeField] private float airControlMultiplier = 0.45f;
        [SerializeField] private float groundCheckDistance = 0.2f;
        [SerializeField] private ForceMode movementForceMode = ForceMode.Acceleration;
        [SerializeField] private float hardBrakeMultiplier = 1.8f;
        [SerializeField] private LayerMask groundMask = ~0;

        [Header("Visual Smoothing")]
        [SerializeField] private Transform visualTarget;

        [Header("Rigidbody")]
        [SerializeField] private float mass = 1f;
        [SerializeField] private float drag = 0.25f;
        [SerializeField] private float angularDrag = 0.1f;
        [SerializeField] private RigidbodyInterpolation interpolation = RigidbodyInterpolation.None;
        [SerializeField] private CollisionDetectionMode collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
        [SerializeField] private bool freezeTiltRotation = true;

        [Header("Visual")]
        [SerializeField] private bool enforceVisibleRuntimeMaterial = true;
        [SerializeField] private Color runtimeBallColor = new Color(0.24f, 0.82f, 0.35f, 1f);

        private Rigidbody _rigidbody;
        private SphereCollider _sphereCollider;
        private NetworkRigidbody3D _networkRigidbody;
        private MeshRenderer _meshRenderer;
        private float _scaledRadius = 0.5f;
        private static Material _sharedRuntimeMaterial;

        public Transform CameraFollowTarget => visualTarget != null ? visualTarget : transform;

        private void Awake()
        {
            CacheComponents();
        }

        public override void Spawned()
        {
            CacheComponents();
            ApplyRigidbodySettings();
            ConfigureInterpolationTarget();
            RefreshScaledRadius();
            EnsureVisibleRenderer();
        }

        public override void FixedUpdateNetwork()
        {
            if (_rigidbody == null)
            {
                return;
            }

            if (!HasStateAuthority && (!HasInputAuthority || !Object.IsInSimulation))
            {
                return;
            }

            if (!GetInput(out SumoInputData input))
            {
                return;
            }

            float deltaTime = Runner.DeltaTime;
            if (deltaTime <= 0f)
            {
                return;
            }

            Vector2 moveInput = input.Move;
            if (moveInput.sqrMagnitude > 1f)
            {
                moveInput.Normalize();
            }
            else if (moveInput.sqrMagnitude < 0.0001f)
            {
                moveInput = Vector2.zero;
            }

            bool hasMoveInput = moveInput.sqrMagnitude > 0.0001f;
            bool hardBrake = input.Buttons.IsSet((int)SumoInputButton.Brake);
            bool grounded = IsGrounded();
            float controlMultiplier = grounded ? 1f : airControlMultiplier;

            Vector3 velocity = _rigidbody.linearVelocity;
            Vector3 horizontalVelocity = new Vector3(velocity.x, 0f, velocity.z);

            Vector3 targetHorizontalVelocity = hasMoveInput
                ? GetTargetHorizontalVelocity(moveInput, input.CameraYaw)
                : Vector3.zero;

            float response = 1f - Mathf.Exp(-velocitySmoothing * deltaTime);
            Vector3 smoothedHorizontalVelocity = Vector3.Lerp(horizontalVelocity, targetHorizontalVelocity, response);
            Vector3 deltaVelocity = smoothedHorizontalVelocity - horizontalVelocity;
            Vector3 requiredAcceleration = deltaVelocity / deltaTime;

            float maxAppliedAcceleration = hasMoveInput
                ? acceleration * controlMultiplier
                : braking * (hardBrake ? hardBrakeMultiplier : 1f) * controlMultiplier;

            requiredAcceleration = Vector3.ClampMagnitude(requiredAcceleration, maxAppliedAcceleration);
            ApplyAcceleration(requiredAcceleration, deltaTime);

            ApplySoftSpeedLimit(controlMultiplier, deltaTime);
        }

        private void CacheComponents()
        {
            if (_rigidbody == null)
            {
                _rigidbody = GetComponent<Rigidbody>();
            }

            if (_sphereCollider == null)
            {
                _sphereCollider = GetComponent<SphereCollider>();
            }

            if (_networkRigidbody == null)
            {
                _networkRigidbody = GetComponent<NetworkRigidbody3D>();
            }

            if (_meshRenderer == null)
            {
                _meshRenderer = GetComponent<MeshRenderer>();
            }
        }

        private Vector3 GetTargetHorizontalVelocity(Vector2 moveInput, float cameraYaw)
        {
            Vector3 localInput = new Vector3(moveInput.x, 0f, moveInput.y);
            Vector3 worldDirection = Quaternion.Euler(0f, cameraYaw, 0f) * localInput;

            if (worldDirection.sqrMagnitude < 0.0001f)
            {
                return Vector3.zero;
            }

            worldDirection.Normalize();
            float inputMagnitude = Mathf.Clamp01(moveInput.magnitude);
            return worldDirection * (maxSpeed * inputMagnitude);
        }

        private void ApplyAcceleration(Vector3 accelerationVector, float deltaTime)
        {
            if (accelerationVector.sqrMagnitude <= 0.0000001f)
            {
                return;
            }

            switch (movementForceMode)
            {
                case ForceMode.Force:
                    _rigidbody.AddForce(accelerationVector * _rigidbody.mass, ForceMode.Force);
                    break;
                case ForceMode.Acceleration:
                    _rigidbody.AddForce(accelerationVector, ForceMode.Acceleration);
                    break;
                case ForceMode.Impulse:
                    _rigidbody.AddForce(accelerationVector * _rigidbody.mass * deltaTime, ForceMode.Impulse);
                    break;
                case ForceMode.VelocityChange:
                    _rigidbody.AddForce(accelerationVector * deltaTime, ForceMode.VelocityChange);
                    break;
                default:
                    _rigidbody.AddForce(accelerationVector, ForceMode.Acceleration);
                    break;
            }
        }

        private void ApplySoftSpeedLimit(float controlMultiplier, float deltaTime)
        {
            Vector3 velocity = _rigidbody.linearVelocity;
            Vector3 horizontalVelocity = new Vector3(velocity.x, 0f, velocity.z);
            float speed = horizontalVelocity.magnitude;
            float speedLimit = Mathf.Max(0.01f, maxSpeed);

            if (speed <= speedLimit)
            {
                return;
            }

            float overspeed = speed - speedLimit;
            float correctionAcceleration = Mathf.Min(overspeed / deltaTime, braking * controlMultiplier);
            if (correctionAcceleration <= 0f)
            {
                return;
            }

            Vector3 correctionDirection = -horizontalVelocity / speed;
            ApplyAcceleration(correctionDirection * correctionAcceleration, deltaTime);
        }

        private bool IsGrounded()
        {
            if (_rigidbody == null)
            {
                return false;
            }

            float checkDistance = _scaledRadius + groundCheckDistance;
            return Physics.Raycast(
                _rigidbody.worldCenterOfMass,
                Vector3.down,
                checkDistance,
                groundMask,
                QueryTriggerInteraction.Ignore);
        }

        private void ConfigureInterpolationTarget()
        {
            if (_networkRigidbody == null)
            {
                return;
            }

            if (visualTarget == transform)
            {
                visualTarget = null;
            }

            AlignVisualTargetToRootIfChild();

            if (visualTarget != null)
            {
                _networkRigidbody.SetInterpolationTarget(visualTarget);
                return;
            }

            if (_networkRigidbody.InterpolationTarget != null)
            {
                visualTarget = _networkRigidbody.InterpolationTarget;
            }
        }

        private void RefreshScaledRadius()
        {
            if (_sphereCollider == null)
            {
                _scaledRadius = 0.5f;
                return;
            }

            Vector3 scale = transform.lossyScale;
            float maxAxis = Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.y), Mathf.Abs(scale.z));
            _scaledRadius = Mathf.Max(0.01f, _sphereCollider.radius * maxAxis);
        }

        private void ApplyRigidbodySettings()
        {
            if (_rigidbody == null)
            {
                return;
            }

            _rigidbody.mass = Mathf.Max(0.01f, mass);
            _rigidbody.linearDamping = Mathf.Max(0f, drag);
            _rigidbody.angularDamping = Mathf.Max(0f, angularDrag);
            _rigidbody.interpolation = interpolation;
            _rigidbody.collisionDetectionMode = collisionDetectionMode;
            _rigidbody.constraints = freezeTiltRotation
                ? RigidbodyConstraints.FreezeRotationX | RigidbodyConstraints.FreezeRotationZ
                : RigidbodyConstraints.None;
            _rigidbody.isKinematic = false;
        }

        private void EnsureVisibleRenderer()
        {
            if (!enforceVisibleRuntimeMaterial)
            {
                return;
            }

            MeshRenderer targetRenderer = _meshRenderer;
            if (visualTarget != null)
            {
                MeshRenderer childRenderer = visualTarget.GetComponentInChildren<MeshRenderer>(true);
                if (childRenderer != null)
                {
                    targetRenderer = childRenderer;
                }
            }

            if (targetRenderer == null)
            {
                return;
            }

            Material current = targetRenderer.sharedMaterial;
            bool materialMissing = current == null || current.shader == null;
            bool knownInvisible = current != null && current.name.IndexOf("FusionStatsGraphMaterial", StringComparison.OrdinalIgnoreCase) >= 0;

            if (!materialMissing && !knownInvisible)
            {
                return;
            }

            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null)
            {
                shader = Shader.Find("Standard");
            }

            if (shader == null)
            {
                Debug.LogWarning("SumoBallController: could not find a fallback shader for player material.");
                return;
            }

            if (_sharedRuntimeMaterial == null || _sharedRuntimeMaterial.shader != shader)
            {
                _sharedRuntimeMaterial = new Material(shader)
                {
                    name = "SumoBallRuntimeMaterial",
                    color = runtimeBallColor
                };
            }

            targetRenderer.sharedMaterial = _sharedRuntimeMaterial;
            Debug.Log($"SumoBallController: applied fallback material on {name}.");
        }

        private void OnValidate()
        {
            acceleration = Mathf.Max(0f, acceleration);
            maxSpeed = Mathf.Max(0.01f, maxSpeed);
            braking = Mathf.Max(0f, braking);
            velocitySmoothing = Mathf.Max(0.01f, velocitySmoothing);
            airControlMultiplier = Mathf.Max(0f, airControlMultiplier);
            groundCheckDistance = Mathf.Max(0f, groundCheckDistance);
            hardBrakeMultiplier = Mathf.Max(1f, hardBrakeMultiplier);
            mass = Mathf.Max(0.01f, mass);
            drag = Mathf.Max(0f, drag);
            angularDrag = Mathf.Max(0f, angularDrag);

            if (visualTarget == transform)
            {
                visualTarget = null;
            }

            if (Application.isPlaying)
            {
                RefreshScaledRadius();
            }

            AlignVisualTargetToRootIfChild();
        }

        private void AlignVisualTargetToRootIfChild()
        {
            if (visualTarget == null || visualTarget == transform)
            {
                return;
            }

            if (!visualTarget.IsChildOf(transform))
            {
                return;
            }

            visualTarget.localPosition = Vector3.zero;
            visualTarget.localRotation = Quaternion.identity;
        }
    }
}
