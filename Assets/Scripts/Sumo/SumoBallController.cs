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
    [RequireComponent(typeof(SumoBallPhysicsConfig))]
    [RequireComponent(typeof(SumoCollisionController))]
    public sealed class SumoBallController : NetworkBehaviour
    {
        [Header("Movement")]
        [SerializeField] private float acceleration = 35f;
        [SerializeField] private float maxSpeed = 10f;
        [SerializeField] private float braking = 20f;
        [FormerlySerializedAs("turnResponsiveness")]
        [SerializeField] private float velocitySmoothing = 12f;
        [SerializeField] private float airControlMultiplier = 0.12f;
        [SerializeField] private float airMaxSpeedMultiplier = 1f;
        [SerializeField] private float groundCheckDistance = 0.2f;
        [SerializeField] private float minGroundNormalDot = 0.55f;
        [SerializeField] private float fallGravityMultiplier = 1.15f;
        [SerializeField] private float wallDetachAcceleration = 6f;
        [SerializeField] private float wallSurfaceMaxUpDot = 0.3f;
        [SerializeField] private ForceMode movementForceMode = ForceMode.Acceleration;
        [SerializeField] private float hardBrakeMultiplier = 1.8f;
        [SerializeField] private LayerMask groundMask = ~0;

        [Header("Visual Smoothing")]
        [SerializeField] private Transform visualTarget;

        [Header("Network Physics")]
        [SerializeField] private bool forceClientSimulationForRemotePlayers = true;

        [Header("Rigidbody")]
        [SerializeField] private float mass = 1f;
        [SerializeField] private float drag = 0.25f;
        [SerializeField] private float angularDrag = 0.1f;
        [SerializeField] private RigidbodyInterpolation interpolation = RigidbodyInterpolation.None;
        [SerializeField] private CollisionDetectionMode collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
        [SerializeField] private bool freezeTiltRotation = true;
        [SerializeField] private SumoBallPhysicsConfig physicsConfig;

        [Header("Visual")]
        [SerializeField] private bool enforceVisibleRuntimeMaterial = true;
        [SerializeField] private Color runtimeBallColor = new Color(0.24f, 0.82f, 0.35f, 1f);

        private Rigidbody _rigidbody;
        private SphereCollider _sphereCollider;
        private NetworkRigidbody3D _networkRigidbody;
        private SumoCollisionController _collisionController;
        private MeshRenderer _meshRenderer;
        private float _scaledRadius = 0.5f;
        private static Material _sharedRuntimeMaterial;
        private Vector3 _cachedWallNormal;
        private int _cachedWallTick = int.MinValue;
        private Vector3 _cachedPlayerBlockNormal;
        private int _cachedPlayerBlockTick = int.MinValue;

        private const float FallbackAntiBulldozeSpeedThreshold = 3.8f;
        private const float FallbackIntoPlayerAccelerationScale = 0.12f;

        private Vector3 _currentMoveDirection = Vector3.forward;
        private float _currentSpeed01;
        private bool _isDashing;
        private float _dashPower;
        private Tick _lastDashTick;

        public Transform CameraFollowTarget => visualTarget != null ? visualTarget : transform;
        public Vector3 CurrentVelocity => _rigidbody != null ? _rigidbody.linearVelocity : Vector3.zero;
        public Vector3 CurrentMoveDirection => _currentMoveDirection;
        public float CurrentSpeed01 => _currentSpeed01;
        public float MaxSpeed => maxSpeed;
        public bool IsDashing => _isDashing;
        public float DashPower => _dashPower;
        public Tick LastDashTick => _lastDashTick;
        public SumoBallPhysicsConfig PhysicsConfig => physicsConfig;

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

            if (forceClientSimulationForRemotePlayers
                && Runner != null
                && Runner.IsClient
                && Object != null
                && !HasStateAuthority)
            {
                Runner.SetIsSimulated(Object, true);
                if (_rigidbody != null)
                {
                    _rigidbody.isKinematic = false;
                }
            }
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

            Vector3 velocity = _rigidbody.linearVelocity;
            Vector3 horizontalVelocity = new Vector3(velocity.x, 0f, velocity.z);

            Vector3 targetHorizontalVelocity = hasMoveInput
                ? GetTargetHorizontalVelocity(moveInput, input.CameraYaw)
                : Vector3.zero;

            if (grounded && targetHorizontalVelocity.sqrMagnitude > 0.0001f)
            {
                _currentMoveDirection = targetHorizontalVelocity.normalized;
            }
            else if (horizontalVelocity.sqrMagnitude > 0.0001f)
            {
                _currentMoveDirection = horizontalVelocity.normalized;
            }
            else if (targetHorizontalVelocity.sqrMagnitude > 0.0001f)
            {
                _currentMoveDirection = targetHorizontalVelocity.normalized;
            }

            _currentSpeed01 = Mathf.Clamp01(horizontalVelocity.magnitude / Mathf.Max(0.01f, maxSpeed));

            if (grounded)
            {
                bool hasPlayerBlockContact = TryGetCachedPlayerBlockNormal(out Vector3 playerBlockNormal);
                ApplyGroundMovement(
                    targetHorizontalVelocity,
                    horizontalVelocity,
                    hasMoveInput,
                    hardBrake,
                    hasPlayerBlockContact,
                    playerBlockNormal,
                    deltaTime);
                ApplySoftSpeedLimit(1f, deltaTime);
                return;
            }

            bool hasWallContact = TryGetCachedWallNormal(out Vector3 wallNormal);
            if (hasWallContact)
            {
                Vector3 adjustedVelocity = RemoveIntoWallHorizontalVelocity(velocity, wallNormal);
                if ((adjustedVelocity - velocity).sqrMagnitude > 0.0000001f)
                {
                    velocity = adjustedVelocity;
                    _rigidbody.linearVelocity = velocity;
                    horizontalVelocity = new Vector3(velocity.x, 0f, velocity.z);
                }
            }

            Vector3 airAcceleration = ComputeAirAcceleration(targetHorizontalVelocity, horizontalVelocity);
            if (hasWallContact && airAcceleration.sqrMagnitude > 0.0000001f)
            {
                airAcceleration = ProjectAccelerationAwayFromWall(airAcceleration, wallNormal);
            }

            ApplyAcceleration(airAcceleration, deltaTime);
            ApplyAdditionalFallForces(velocity.y, hasWallContact);
        }

        private void OnCollisionEnter(Collision collision)
        {
            CacheWallContactFromCollision(collision);
            CachePlayerBlockFromCollision(collision);
        }

        private void OnCollisionStay(Collision collision)
        {
            CacheWallContactFromCollision(collision);
            CachePlayerBlockFromCollision(collision);
        }

        private void ApplyGroundMovement(
            Vector3 targetHorizontalVelocity,
            Vector3 horizontalVelocity,
            bool hasMoveInput,
            bool hardBrake,
            bool hasPlayerBlockContact,
            Vector3 playerBlockNormal,
            float deltaTime)
        {
            float response = 1f - Mathf.Exp(-velocitySmoothing * deltaTime);
            Vector3 smoothedHorizontalVelocity = Vector3.Lerp(horizontalVelocity, targetHorizontalVelocity, response);
            Vector3 deltaVelocity = smoothedHorizontalVelocity - horizontalVelocity;
            Vector3 requiredAcceleration = deltaVelocity / deltaTime;

            float maxAppliedAcceleration = hasMoveInput
                ? acceleration
                : braking * (hardBrake ? hardBrakeMultiplier : 1f);

            requiredAcceleration = Vector3.ClampMagnitude(requiredAcceleration, maxAppliedAcceleration);

            bool limitIntoPlayers = physicsConfig == null || physicsConfig.LimitAccelerationIntoPlayers;
            float intoPlayerScale = physicsConfig != null
                ? physicsConfig.IntoPlayerAccelerationScale
                : FallbackIntoPlayerAccelerationScale;

            bool hasActiveRamDrive = _collisionController != null && _collisionController.HasActiveRamDrive();
            bool shouldLimitIntoPlayer = !hasActiveRamDrive;
            float effectiveIntoPlayerScale = intoPlayerScale;

            if (!hasActiveRamDrive)
            {
                float antiBulldozeSpeedThreshold = physicsConfig != null
                    ? physicsConfig.AntiBulldozeSpeedThreshold
                    : FallbackAntiBulldozeSpeedThreshold;

                float speed01 = Mathf.Clamp01(horizontalVelocity.magnitude / Mathf.Max(0.01f, antiBulldozeSpeedThreshold));
                float minIntoScale = Mathf.Max(0.02f, intoPlayerScale * 0.45f);
                effectiveIntoPlayerScale = Mathf.Lerp(intoPlayerScale, minIntoScale, speed01);
            }

            if (limitIntoPlayers
                && hasPlayerBlockContact
                && shouldLimitIntoPlayer)
            {
                requiredAcceleration = ScaleAccelerationIntoBlocker(requiredAcceleration, playerBlockNormal, effectiveIntoPlayerScale);
            }

            ApplyAcceleration(requiredAcceleration, deltaTime);
        }

        private Vector3 ComputeAirAcceleration(Vector3 targetHorizontalVelocity, Vector3 horizontalVelocity)
        {
            if (airControlMultiplier <= 0f || targetHorizontalVelocity.sqrMagnitude < 0.0001f)
            {
                return Vector3.zero;
            }

            float speedCap = Mathf.Max(0.01f, maxSpeed * Mathf.Max(0f, airMaxSpeedMultiplier));
            Vector3 inputDirection = targetHorizontalVelocity.normalized;
            float speedAlongInput = Vector3.Dot(horizontalVelocity, inputDirection);
            if (speedAlongInput >= speedCap)
            {
                return Vector3.zero;
            }

            float inputStrength = Mathf.Clamp01(targetHorizontalVelocity.magnitude / Mathf.Max(0.01f, maxSpeed));
            float limitScale = Mathf.Clamp01((speedCap - speedAlongInput) / speedCap);
            float accelerationMagnitude = acceleration * airControlMultiplier * inputStrength * limitScale;
            return inputDirection * Mathf.Max(0f, accelerationMagnitude);
        }

        private static Vector3 ProjectAccelerationAwayFromWall(Vector3 accelerationVector, Vector3 wallNormal)
        {
            float intoWallAcceleration = -Vector3.Dot(accelerationVector, wallNormal);
            if (intoWallAcceleration <= 0f)
            {
                return accelerationVector;
            }

            return accelerationVector + wallNormal * intoWallAcceleration;
        }

        private static Vector3 RemoveIntoWallHorizontalVelocity(Vector3 velocity, Vector3 wallNormal)
        {
            Vector3 horizontalVelocity = new Vector3(velocity.x, 0f, velocity.z);
            float intoWallSpeed = -Vector3.Dot(horizontalVelocity, wallNormal);
            if (intoWallSpeed <= 0f)
            {
                return velocity;
            }

            horizontalVelocity += wallNormal * intoWallSpeed;
            velocity.x = horizontalVelocity.x;
            velocity.z = horizontalVelocity.z;
            return velocity;
        }

        private static Vector3 ScaleAccelerationIntoBlocker(Vector3 accelerationVector, Vector3 blockerNormal, float intoScale)
        {
            if (blockerNormal.sqrMagnitude < 0.0001f)
            {
                return accelerationVector;
            }

            float intoBlockAcceleration = -Vector3.Dot(accelerationVector, blockerNormal.normalized);
            if (intoBlockAcceleration <= 0f)
            {
                return accelerationVector;
            }

            float clampedScale = Mathf.Clamp01(intoScale);
            float removedAcceleration = intoBlockAcceleration * (1f - clampedScale);
            return accelerationVector + blockerNormal.normalized * removedAcceleration;
        }

        private void ApplyAdditionalFallForces(float verticalVelocity, bool hasWallContact)
        {
            if (verticalVelocity >= 0f)
            {
                return;
            }

            Vector3 gravity = Physics.gravity;
            float gravityMagnitude = gravity.magnitude;
            if (gravityMagnitude > 0.0001f)
            {
                float extraGravity = gravityMagnitude * Mathf.Max(0f, fallGravityMultiplier - 1f);
                if (extraGravity > 0f)
                {
                    _rigidbody.AddForce(gravity.normalized * extraGravity, ForceMode.Acceleration);
                }
            }

            if (!hasWallContact || wallDetachAcceleration <= 0f)
            {
                return;
            }

            Vector3 detachDirection = gravityMagnitude > 0.0001f ? gravity.normalized : Vector3.down;
            _rigidbody.AddForce(detachDirection * wallDetachAcceleration, ForceMode.Acceleration);
        }

        private bool TryGetCachedWallNormal(out Vector3 wallNormal)
        {
            wallNormal = Vector3.zero;

            int currentTick = Runner != null ? Runner.Tick.Raw : Time.frameCount;
            long tickDelta = (long)currentTick - _cachedWallTick;
            if (tickDelta < 0L || tickDelta > 1L)
            {
                return false;
            }

            if (_cachedWallNormal.sqrMagnitude < 0.0001f)
            {
                return false;
            }

            wallNormal = _cachedWallNormal.normalized;
            return true;
        }

        private bool TryGetCachedPlayerBlockNormal(out Vector3 playerBlockNormal)
        {
            playerBlockNormal = Vector3.zero;

            int currentTick = Runner != null ? Runner.Tick.Raw : Time.frameCount;
            long tickDelta = (long)currentTick - _cachedPlayerBlockTick;
            if (tickDelta < 0L || tickDelta > 1L)
            {
                return false;
            }

            if (_cachedPlayerBlockNormal.sqrMagnitude < 0.0001f)
            {
                return false;
            }

            playerBlockNormal = _cachedPlayerBlockNormal.normalized;
            return true;
        }

        private void CacheWallContactFromCollision(Collision collision)
        {
            if (collision == null || collision.contactCount <= 0)
            {
                return;
            }

            if (collision.rigidbody != null
                && collision.rigidbody.TryGetComponent(out SumoBallController _))
            {
                return;
            }

            Vector3 accumulated = Vector3.zero;
            for (int i = 0; i < collision.contactCount; i++)
            {
                Vector3 normal = collision.GetContact(i).normal;
                if (normal.sqrMagnitude < 0.0001f)
                {
                    continue;
                }

                normal.Normalize();
                if (normal.y > wallSurfaceMaxUpDot)
                {
                    continue;
                }

                Vector3 horizontal = Vector3.ProjectOnPlane(normal, Vector3.up);
                if (horizontal.sqrMagnitude < 0.0001f)
                {
                    continue;
                }

                float weight = 1f - Mathf.Abs(normal.y);
                if (weight <= 0f)
                {
                    continue;
                }

                accumulated += horizontal.normalized * weight;
            }

            if (accumulated.sqrMagnitude < 0.0001f)
            {
                return;
            }

            int tick = Runner != null ? Runner.Tick.Raw : Time.frameCount;
            if (_cachedWallTick == tick)
            {
                _cachedWallNormal += accumulated;
            }
            else
            {
                _cachedWallNormal = accumulated;
            }

            _cachedWallTick = tick;
        }

        private void CachePlayerBlockFromCollision(Collision collision)
        {
            if (collision == null || collision.contactCount <= 0)
            {
                return;
            }

            if (collision.rigidbody == null
                || !collision.rigidbody.TryGetComponent(out SumoBallController _))
            {
                return;
            }

            Vector3 accumulated = Vector3.zero;
            for (int i = 0; i < collision.contactCount; i++)
            {
                Vector3 normal = collision.GetContact(i).normal;
                if (normal.sqrMagnitude < 0.0001f)
                {
                    continue;
                }

                normal.Normalize();
                Vector3 horizontalNormal = Vector3.ProjectOnPlane(normal, Vector3.up);
                if (horizontalNormal.sqrMagnitude < 0.0001f)
                {
                    continue;
                }

                float weight = 1f - Mathf.Abs(normal.y);
                if (weight <= 0f)
                {
                    continue;
                }

                accumulated += horizontalNormal.normalized * weight;
            }

            if (accumulated.sqrMagnitude < 0.0001f)
            {
                return;
            }

            int tick = Runner != null ? Runner.Tick.Raw : Time.frameCount;
            if (_cachedPlayerBlockTick == tick)
            {
                _cachedPlayerBlockNormal += accumulated;
            }
            else
            {
                _cachedPlayerBlockNormal = accumulated;
            }

            _cachedPlayerBlockTick = tick;
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

            if (_collisionController == null)
            {
                _collisionController = GetComponent<SumoCollisionController>();
            }

            if (physicsConfig == null)
            {
                physicsConfig = GetComponent<SumoBallPhysicsConfig>();
            }

            if (_meshRenderer == null)
            {
                _meshRenderer = GetComponent<MeshRenderer>();
            }
        }

        public void SetDashState(bool isDashing, float dashPower = 1f)
        {
            _isDashing = isDashing;
            _dashPower = Mathf.Max(0f, dashPower);

            if (_isDashing && Runner != null)
            {
                _lastDashTick = Runner.Tick;
            }
        }

        public float GetDashImpactMultiplier(float configuredDashMultiplier)
        {
            if (!_isDashing)
            {
                return 1f;
            }

            float dashScale = Mathf.Max(0f, _dashPower);
            float clampedConfig = Mathf.Max(1f, configuredDashMultiplier);
            if (dashScale <= 0f)
            {
                return clampedConfig;
            }

            if (dashScale <= 1f)
            {
                return Mathf.Lerp(1f, clampedConfig, dashScale);
            }

            return 1f + (clampedConfig - 1f) * dashScale;
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
            if (!Physics.Raycast(
                _rigidbody.worldCenterOfMass,
                Vector3.down,
                out RaycastHit hit,
                checkDistance,
                groundMask,
                QueryTriggerInteraction.Ignore))
            {
                return false;
            }

            return hit.normal.y >= minGroundNormalDot;
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

            if (physicsConfig != null)
            {
                physicsConfig.ApplyTo(_rigidbody, _sphereCollider);
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
            airMaxSpeedMultiplier = Mathf.Max(0f, airMaxSpeedMultiplier);
            groundCheckDistance = Mathf.Max(0f, groundCheckDistance);
            minGroundNormalDot = Mathf.Clamp01(minGroundNormalDot);
            fallGravityMultiplier = Mathf.Max(1f, fallGravityMultiplier);
            wallDetachAcceleration = Mathf.Max(0f, wallDetachAcceleration);
            wallSurfaceMaxUpDot = Mathf.Clamp(wallSurfaceMaxUpDot, 0f, 1f);
            hardBrakeMultiplier = Mathf.Max(1f, hardBrakeMultiplier);
            mass = Mathf.Max(0.01f, mass);
            drag = Mathf.Max(0f, drag);
            angularDrag = Mathf.Max(0f, angularDrag);

            if (visualTarget == transform)
            {
                visualTarget = null;
            }

            if (physicsConfig == null)
            {
                physicsConfig = GetComponent<SumoBallPhysicsConfig>();
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
