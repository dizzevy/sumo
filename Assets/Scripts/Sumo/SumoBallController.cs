using System;
using Fusion;
using Fusion.Addons.Physics;
using UnityEngine;

namespace Sumo
{
    [DisallowMultipleComponent]
    [RequireComponent(typeof(NetworkObject))]
    [RequireComponent(typeof(Rigidbody))]
    [RequireComponent(typeof(SphereCollider))]
    [RequireComponent(typeof(NetworkRigidbody3D))]
    [RequireComponent(typeof(SumoBallPhysicsConfig))]
    [RequireComponent(typeof(SumoCollisionController))]
    [RequireComponent(typeof(SumoAccelerationConfig))]
    public sealed class SumoBallController : NetworkBehaviour
    {
        [Header("Movement")]
        [SerializeField] private SumoAccelerationConfig accelerationConfig;
        [SerializeField] private float movementSpeedMultiplier = DefaultMovementSpeedMultiplier;
        [SerializeField] private float airControlMultiplier = 0.12f;
        [SerializeField] private float airMaxSpeedMultiplier = 1f;
        [SerializeField] private float groundCheckDistance = 0.2f;
        [SerializeField] private float minGroundNormalDot = 0.55f;
        [SerializeField] private float fallGravityMultiplier = 1.15f;
        [SerializeField] private float wallDetachAcceleration = 6f;
        [SerializeField] private float wallSurfaceMaxUpDot = 0.3f;
        [SerializeField] private ForceMode movementForceMode = ForceMode.Acceleration;
        [SerializeField] private LayerMask groundMask = ~0;

        [Header("Visual Smoothing")]
        [SerializeField] private Transform visualTarget;

        [Header("Network Physics")]
        [SerializeField] private bool forceClientSimulationForRemotePlayers = false;

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

        private const float FallbackAntiBulldozeSpeedThreshold = 3.8f;
        private const float FallbackIntoPlayerAccelerationScale = 0.12f;
        private const float FallbackMaxSpeed = 50f;
        private const float FallbackMinMoveSpeed = 4.5f;
        private const float FallbackInitialAccelerationResponse = 36f;
        private const float FallbackAccelerationPower = 2.2f;
        private const float FallbackTopSpeedApproachStrength = 1.85f;
        private const float FallbackBraking = 22f;
        private const float FallbackHardBrakeMultiplier = 1.8f;
        private const float FallbackRollingDrag = 1.1f;
        private const float FallbackSteeringResponsiveness = 9f;
        private const float DefaultMovementSpeedMultiplier = 1.5f;
        private Vector3 _currentMoveDirection = Vector3.zero;
        private float _currentSpeed01;
        private bool _isDashing;
        private float _dashPower;
        private Tick _lastDashTick;
        private MovementCommand _lastResolvedMovementCommand;
        private bool _hasLastResolvedMovementCommand;
        [Networked] private Vector3 ReplicatedMoveDirection { get; set; }
        [Networked] private float ReplicatedMoveStrength01 { get; set; }
        [Networked] private NetworkBool ReplicatedBrake { get; set; }
        [Networked] private Vector3 ReplicatedContactIntent { get; set; }
        [Networked] private float ReplicatedContactSpeed01 { get; set; }

        private struct MovementCommand
        {
            public Vector3 TargetHorizontalVelocity;
            public Vector3 MoveDirection;
            public float MoveStrength01;
            public bool HasMoveInput;
            public bool HardBrake;
        }

        public Transform CameraFollowTarget => visualTarget != null ? visualTarget : transform;
        public Vector3 CurrentVelocity => _rigidbody != null ? _rigidbody.linearVelocity : Vector3.zero;
        public float CurrentHorizontalSpeed
        {
            get
            {
                if (_rigidbody == null)
                {
                    return 0f;
                }

                Vector3 velocity = _rigidbody.linearVelocity;
                velocity.y = 0f;
                return velocity.magnitude;
            }
        }

        public Vector3 CurrentMoveDirection => _currentMoveDirection;
        public float CurrentSpeed01 => _currentSpeed01;
        public float MaxSpeed => GetMaxSpeed();
        public bool IsDashing => _isDashing;
        public float DashPower => _dashPower;
        public Tick LastDashTick => _lastDashTick;
        public SumoBallPhysicsConfig PhysicsConfig => physicsConfig;

        public Vector3 GetContactIntentDirection(Vector3 fallbackVelocity)
        {
            if (IsLocallyDrivenForContact() && _currentMoveDirection.sqrMagnitude > 0.0001f)
            {
                return _currentMoveDirection.normalized;
            }

            if (!IsLocallyDrivenForContact())
            {
                Vector3 replicatedIntent = new Vector3(ReplicatedMoveDirection.x, 0f, ReplicatedMoveDirection.z);
                if (replicatedIntent.sqrMagnitude < 0.0001f)
                {
                    replicatedIntent = new Vector3(ReplicatedContactIntent.x, 0f, ReplicatedContactIntent.z);
                }

                if (replicatedIntent.sqrMagnitude > 0.0001f)
                {
                    return replicatedIntent.normalized;
                }
            }

            Vector3 horizontalVelocity = new Vector3(fallbackVelocity.x, 0f, fallbackVelocity.z);
            if (horizontalVelocity.sqrMagnitude > 0.0001f)
            {
                return horizontalVelocity.normalized;
            }

            return Vector3.zero;
        }

        public float GetContactPlanarSpeed(Vector3 fallbackVelocity)
        {
            Vector3 horizontalVelocity = new Vector3(fallbackVelocity.x, 0f, fallbackVelocity.z);
            float speed = horizontalVelocity.magnitude;

            if (!IsLocallyDrivenForContact())
            {
                float replicatedSpeed = Mathf.Clamp01(ReplicatedContactSpeed01) * Mathf.Max(0.01f, GetMaxSpeed());
                float replicatedMoveSpeed = Mathf.Clamp01(ReplicatedMoveStrength01) * Mathf.Max(0.01f, GetMaxSpeed());
                speed = Mathf.Max(speed, Mathf.Max(replicatedSpeed, replicatedMoveSpeed));
                return speed;
            }

            float controllerSpeedEstimate = _currentSpeed01 * Mathf.Max(0.01f, GetMaxSpeed());
            return Mathf.Max(speed, controllerSpeedEstimate);
        }

        private void Awake()
        {
            EnsurePresentationComponents();
            CacheComponents();
        }

        public override void Spawned()
        {
            EnsurePresentationComponents();
            CacheComponents();
            _lastResolvedMovementCommand = default;
            _hasLastResolvedMovementCommand = false;
            ApplyRigidbodySettings();
            ConfigureInterpolationTarget();
            RefreshScaledRadius();
            EnsureVisibleRenderer();

            if (ShouldEnableRemoteProxySimulation()
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

            if (!TryResolveMovementCommand(out MovementCommand command))
            {
                return;
            }

            float deltaTime = Runner.DeltaTime;
            if (deltaTime <= 0f)
            {
                return;
            }

            bool hasMoveInput = command.HasMoveInput;
            bool hardBrake = command.HardBrake;
            bool grounded = IsGrounded();

            Vector3 velocity = _rigidbody.linearVelocity;
            Vector3 horizontalVelocity = new Vector3(velocity.x, 0f, velocity.z);
            Vector3 targetHorizontalVelocity = command.TargetHorizontalVelocity;

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

            _currentSpeed01 = Mathf.Clamp01(horizontalVelocity.magnitude / Mathf.Max(0.01f, GetMaxSpeed()));
            PublishMovementCommandSnapshot(command);

            if (grounded)
            {
                bool hasPlayerBlockContact = false;
                Vector3 playerBlockNormal = Vector3.zero;

                if (_collisionController != null
                    && _collisionController.TryGetMovementBlockNormal(out Vector3 solverBlockNormal))
                {
                    hasPlayerBlockContact = true;
                    playerBlockNormal = solverBlockNormal;
                }

                ApplyGroundMovement(
                    targetHorizontalVelocity,
                    horizontalVelocity,
                    hasMoveInput,
                    hardBrake,
                    hasPlayerBlockContact,
                    playerBlockNormal,
                    deltaTime);
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
        }

        private void OnCollisionStay(Collision collision)
        {
            CacheWallContactFromCollision(collision);
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
            Vector3 requiredAcceleration = Vector3.zero;
            float maxSpeed = GetMaxSpeed();

            if (hasMoveInput && targetHorizontalVelocity.sqrMagnitude > 0.0001f)
            {
                Vector3 desiredDirection = targetHorizontalVelocity.normalized;
                float inputStrength = Mathf.Clamp01(targetHorizontalVelocity.magnitude / Mathf.Max(0.01f, maxSpeed));
                float speedAlongDesired = Vector3.Dot(horizontalVelocity, desiredDirection);
                float forwardSpeed = Mathf.Max(0f, speedAlongDesired);

                float driveAccelerationMagnitude = EvaluateDriveAccelerationMagnitude(forwardSpeed, inputStrength);
                Vector3 forwardAcceleration = desiredDirection * driveAccelerationMagnitude;

                Vector3 desiredVelocity = desiredDirection * (maxSpeed * inputStrength);
                Vector3 velocityError = desiredVelocity - horizontalVelocity;
                Vector3 steeringAcceleration = velocityError * GetSteeringResponsiveness();
                float steeringCap = GetBraking() * (hardBrake ? GetHardBrakeMultiplier() : 1f);
                steeringAcceleration = Vector3.ClampMagnitude(steeringAcceleration, steeringCap);

                float forwardSteering = Vector3.Dot(steeringAcceleration, desiredDirection);
                if (forwardSteering > 0f)
                {
                    steeringAcceleration -= desiredDirection * forwardSteering;
                }

                requiredAcceleration = forwardAcceleration + steeringAcceleration;
            }
            else
            {
                float speed = horizontalVelocity.magnitude;
                if (speed > 0.0001f)
                {
                    float deceleration = EvaluateCoastDeceleration(hardBrake);
                    requiredAcceleration = -horizontalVelocity / speed * deceleration;
                }
            }

            Vector3 rollingDragAcceleration = EvaluateRollingDragAcceleration(horizontalVelocity, hasMoveInput);
            requiredAcceleration += rollingDragAcceleration;

            bool limitIntoPlayers = physicsConfig == null || physicsConfig.LimitAccelerationIntoPlayers;
            float intoPlayerScale = physicsConfig != null
                ? physicsConfig.IntoPlayerAccelerationScale
                : FallbackIntoPlayerAccelerationScale;

            float ramDriveAssist = _collisionController != null
                ? _collisionController.GetRamDriveAssist01()
                : 0f;
            float effectiveIntoPlayerScale = intoPlayerScale;
            float antiBulldozeSpeedThreshold = physicsConfig != null
                ? physicsConfig.AntiBulldozeSpeedThreshold
                : FallbackAntiBulldozeSpeedThreshold;
            float speed01 = Mathf.Clamp01(horizontalVelocity.magnitude / Mathf.Max(0.01f, antiBulldozeSpeedThreshold));
            float minIntoScale = Mathf.Max(0.02f, intoPlayerScale * 0.45f);
            float antiBulldozeScale = Mathf.Lerp(intoPlayerScale, minIntoScale, speed01);
            effectiveIntoPlayerScale = Mathf.Lerp(antiBulldozeScale, 1f, Mathf.Clamp01(ramDriveAssist));

            if (limitIntoPlayers
                && hasPlayerBlockContact)
            {
                // The analytic collision solver owns player-vs-player separation later in
                // the tick. Hard pre-clamping attacker velocity/acceleration here was
                // using the previous tick's block state and reintroduced stop-go pushes.
                requiredAcceleration = ScaleAccelerationIntoBlocker(requiredAcceleration, playerBlockNormal, effectiveIntoPlayerScale);
            }

            ApplyAcceleration(requiredAcceleration, deltaTime);
            ApplySoftSpeedLimit(1f, deltaTime);
        }

        private Vector3 ComputeAirAcceleration(Vector3 targetHorizontalVelocity, Vector3 horizontalVelocity)
        {
            if (airControlMultiplier <= 0f || targetHorizontalVelocity.sqrMagnitude < 0.0001f)
            {
                return Vector3.zero;
            }

            float speedCap = Mathf.Max(0.01f, GetMaxSpeed() * Mathf.Max(0f, airMaxSpeedMultiplier));
            Vector3 inputDirection = targetHorizontalVelocity.normalized;
            float speedAlongInput = Vector3.Dot(horizontalVelocity, inputDirection);
            if (speedAlongInput >= speedCap)
            {
                return Vector3.zero;
            }

            float inputStrength = Mathf.Clamp01(targetHorizontalVelocity.magnitude / Mathf.Max(0.01f, GetMaxSpeed()));
            float limitScale = Mathf.Clamp01((speedCap - speedAlongInput) / speedCap);
            float speed01 = Mathf.Clamp01(Mathf.Max(0f, speedAlongInput) / speedCap);
            float curveFactor = EvaluateDriveCurve(speed01);
            float accelerationMagnitude = GetInitialAccelerationResponse() * airControlMultiplier * inputStrength * limitScale * curveFactor;
            return inputDirection * Mathf.Max(0f, accelerationMagnitude);
        }

        private float EvaluateDriveAccelerationMagnitude(float forwardSpeed, float inputStrength)
        {
            float clampedInput = Mathf.Clamp01(inputStrength);
            if (clampedInput <= 0f)
            {
                return 0f;
            }

            float maxSpeed = Mathf.Max(0.01f, GetMaxSpeed());
            float speed01 = Mathf.Clamp01(Mathf.Max(0f, forwardSpeed) / maxSpeed);
            float curveFactor = EvaluateDriveCurve(speed01);
            float driveAcceleration = GetInitialAccelerationResponse() * curveFactor;

            float minMoveSpeed = GetMinMoveSpeed();
            if (minMoveSpeed > 0.0001f)
            {
                float launchTarget = minMoveSpeed * clampedInput;
                float launchBoost01 = Mathf.Clamp01((launchTarget - Mathf.Max(0f, forwardSpeed)) / minMoveSpeed);
                driveAcceleration += GetInitialAccelerationResponse() * 0.35f * launchBoost01;
            }

            return driveAcceleration * clampedInput;
        }

        private float EvaluateDriveCurve(float speed01)
        {
            float clampedSpeed = Mathf.Clamp01(speed01);
            float speedPower = Mathf.Max(0.01f, GetAccelerationPower());
            float approachStrength = Mathf.Max(0.01f, GetTopSpeedApproachStrength());

            float shapedSpeed = Mathf.Pow(clampedSpeed, speedPower);
            float remaining = Mathf.Clamp01(1f - shapedSpeed);
            return Mathf.Pow(remaining, approachStrength);
        }

        private float EvaluateCoastDeceleration(bool hardBrake)
        {
            float baseBraking = GetBraking() * (hardBrake ? GetHardBrakeMultiplier() : 1f);
            return baseBraking;
        }

        private Vector3 EvaluateRollingDragAcceleration(Vector3 horizontalVelocity, bool hasMoveInput)
        {
            float speed = horizontalVelocity.magnitude;
            if (speed <= 0.0001f)
            {
                return Vector3.zero;
            }

            float dragCoefficient = GetRollingDrag();
            if (dragCoefficient <= 0f)
            {
                return Vector3.zero;
            }

            float inputDragScale = hasMoveInput ? 0.28f : 1f;
            float dragAcceleration = dragCoefficient * speed * inputDragScale;
            return -horizontalVelocity / speed * dragAcceleration;
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

        private bool IsLocallyDrivenForContact()
        {
            return HasInputAuthority || HasStateAuthority;
        }

        public bool IsRemoteProxyPredictionActive()
        {
            return ShouldEnableRemoteProxySimulation()
                && Runner != null
                && Runner.IsClient
                && !HasStateAuthority
                && !HasInputAuthority
                && Object != null
                && Object.IsInSimulation;
        }

        private bool ShouldEnableRemoteProxySimulation()
        {
            if (forceClientSimulationForRemotePlayers)
            {
                return true;
            }

            if (_collisionController == null)
            {
                CacheComponents();
            }

            return _collisionController != null && _collisionController.ShouldPredictRemoteProxyForces();
        }

        private bool TryResolveMovementCommand(out MovementCommand command)
        {
            command = default;

            if (Runner == null || Object == null || !Object.IsInSimulation)
            {
                return false;
            }

            if (HasStateAuthority || HasInputAuthority)
            {
                if (GetInput(out SumoInputData input))
                {
                    BuildMovementCommandFromInput(input.Move, input.CameraYaw, input.Buttons.IsSet((int)SumoInputButton.Brake), out command);
                    _lastResolvedMovementCommand = command;
                    _hasLastResolvedMovementCommand = true;
                    return true;
                }

                if (_hasLastResolvedMovementCommand)
                {
                    command = _lastResolvedMovementCommand;
                    return true;
                }

                BuildMovementCommandFromInput(Vector2.zero, 0f, false, out command);
                return true;
            }

            if (!IsRemoteProxyPredictionActive())
            {
                return false;
            }

            BuildReplicatedMovementCommand(out command);
            return true;
        }

        private void BuildMovementCommandFromInput(
            Vector2 moveInput,
            float cameraYaw,
            bool hardBrake,
            out MovementCommand command)
        {
            Vector2 clampedMoveInput = moveInput;
            if (clampedMoveInput.sqrMagnitude > 1f)
            {
                clampedMoveInput.Normalize();
            }
            else if (clampedMoveInput.sqrMagnitude < 0.0001f)
            {
                clampedMoveInput = Vector2.zero;
            }

            bool hasMoveInput = clampedMoveInput.sqrMagnitude > 0.0001f;
            Vector3 targetHorizontalVelocity = hasMoveInput
                ? GetTargetHorizontalVelocity(clampedMoveInput, cameraYaw)
                : Vector3.zero;

            Vector3 moveDirection = targetHorizontalVelocity.sqrMagnitude > 0.0001f
                ? targetHorizontalVelocity.normalized
                : Vector3.zero;

            float maxSpeed = Mathf.Max(0.01f, GetMaxSpeed());
            float moveStrength01 = targetHorizontalVelocity.sqrMagnitude > 0.0001f
                ? Mathf.Clamp01(targetHorizontalVelocity.magnitude / maxSpeed)
                : 0f;

            command = new MovementCommand
            {
                TargetHorizontalVelocity = targetHorizontalVelocity,
                MoveDirection = moveDirection,
                MoveStrength01 = moveStrength01,
                HasMoveInput = hasMoveInput,
                HardBrake = hardBrake
            };
        }

        private void BuildReplicatedMovementCommand(out MovementCommand command)
        {
            Vector3 moveDirection = new Vector3(ReplicatedMoveDirection.x, 0f, ReplicatedMoveDirection.z);
            if (moveDirection.sqrMagnitude < 0.0001f)
            {
                moveDirection = new Vector3(ReplicatedContactIntent.x, 0f, ReplicatedContactIntent.z);
            }

            if (moveDirection.sqrMagnitude > 0.0001f)
            {
                moveDirection.Normalize();
            }
            else
            {
                moveDirection = Vector3.zero;
            }

            float moveStrength01 = Mathf.Clamp01(ReplicatedMoveStrength01);
            bool hasMoveInput = moveDirection.sqrMagnitude > 0.0001f && moveStrength01 > 0.0001f;
            Vector3 targetHorizontalVelocity = hasMoveInput
                ? moveDirection * (Mathf.Max(0.01f, GetMaxSpeed()) * moveStrength01)
                : Vector3.zero;

            command = new MovementCommand
            {
                TargetHorizontalVelocity = targetHorizontalVelocity,
                MoveDirection = moveDirection,
                MoveStrength01 = moveStrength01,
                HasMoveInput = hasMoveInput,
                HardBrake = ReplicatedBrake
            };
        }

        private void PublishMovementCommandSnapshot(in MovementCommand command)
        {
            if (!HasStateAuthority)
            {
                return;
            }

            Vector3 horizontalIntent = command.MoveDirection;
            if (horizontalIntent.sqrMagnitude > 0.0001f)
            {
                horizontalIntent.Normalize();
            }
            else
            {
                horizontalIntent = Vector3.zero;
            }

            ReplicatedMoveDirection = horizontalIntent;
            ReplicatedMoveStrength01 = Mathf.Clamp01(command.MoveStrength01);
            ReplicatedBrake = command.HardBrake;
            ReplicatedContactIntent = horizontalIntent;
            ReplicatedContactSpeed01 = Mathf.Clamp01(_currentSpeed01);
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

            if (accelerationConfig == null)
            {
                accelerationConfig = GetComponent<SumoAccelerationConfig>();
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

        private void EnsurePresentationComponents()
        {
            if (GetComponent<SumoProxyPresentation>() == null)
            {
                gameObject.AddComponent<SumoProxyPresentation>();
            }

            if (GetComponent<SumoImpactPresentation>() == null)
            {
                gameObject.AddComponent<SumoImpactPresentation>();
            }

            if (TryGetComponent(out SumoImpactFeedback legacyImpactFeedback))
            {
                legacyImpactFeedback.enabled = false;
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

        private float GetMaxSpeed()
        {
            float baseMaxSpeed = accelerationConfig != null
                ? accelerationConfig.MaxSpeed
                : FallbackMaxSpeed;
            return baseMaxSpeed * GetMovementSpeedMultiplier();
        }

        private float GetMinMoveSpeed()
        {
            float baseMinMoveSpeed = accelerationConfig != null
                ? accelerationConfig.MinMoveSpeed
                : FallbackMinMoveSpeed;
            return baseMinMoveSpeed * GetMovementSpeedMultiplier();
        }

        private float GetInitialAccelerationResponse()
        {
            float baseAcceleration = accelerationConfig != null
                ? accelerationConfig.InitialAccelerationResponse
                : FallbackInitialAccelerationResponse;
            return baseAcceleration * GetMovementSpeedMultiplier();
        }

        private float GetAccelerationPower()
        {
            return accelerationConfig != null
                ? accelerationConfig.AccelerationPower
                : FallbackAccelerationPower;
        }

        private float GetTopSpeedApproachStrength()
        {
            return accelerationConfig != null
                ? accelerationConfig.TopSpeedApproachStrength
                : FallbackTopSpeedApproachStrength;
        }

        private float GetBraking()
        {
            float baseBraking = accelerationConfig != null
                ? accelerationConfig.Braking
                : FallbackBraking;
            return baseBraking * GetMovementSpeedMultiplier();
        }

        private float GetHardBrakeMultiplier()
        {
            return accelerationConfig != null
                ? accelerationConfig.HardBrakeMultiplier
                : FallbackHardBrakeMultiplier;
        }

        private float GetRollingDrag()
        {
            return accelerationConfig != null
                ? accelerationConfig.RollingDrag
                : FallbackRollingDrag;
        }

        private float GetSteeringResponsiveness()
        {
            return accelerationConfig != null
                ? accelerationConfig.SteeringResponsiveness
                : FallbackSteeringResponsiveness;
        }

        private float GetMovementSpeedMultiplier()
        {
            float configured = movementSpeedMultiplier > 0.0001f
                ? movementSpeedMultiplier
                : DefaultMovementSpeedMultiplier;
            return Mathf.Max(0.01f, configured);
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
            return worldDirection * (GetMaxSpeed() * inputMagnitude);
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
            float speedLimit = Mathf.Max(0.01f, GetMaxSpeed());

            if (speed <= speedLimit)
            {
                return;
            }

            float overspeed = speed - speedLimit;
            float correctionAcceleration = Mathf.Min(overspeed / deltaTime, GetBraking() * controlMultiplier);
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
            movementSpeedMultiplier = Mathf.Max(0.01f, movementSpeedMultiplier);
            airControlMultiplier = Mathf.Max(0f, airControlMultiplier);
            airMaxSpeedMultiplier = Mathf.Max(0f, airMaxSpeedMultiplier);
            groundCheckDistance = Mathf.Max(0f, groundCheckDistance);
            minGroundNormalDot = Mathf.Clamp01(minGroundNormalDot);
            fallGravityMultiplier = Mathf.Max(1f, fallGravityMultiplier);
            wallDetachAcceleration = Mathf.Max(0f, wallDetachAcceleration);
            wallSurfaceMaxUpDot = Mathf.Clamp(wallSurfaceMaxUpDot, 0f, 1f);
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

            if (accelerationConfig == null)
            {
                accelerationConfig = GetComponent<SumoAccelerationConfig>();
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
