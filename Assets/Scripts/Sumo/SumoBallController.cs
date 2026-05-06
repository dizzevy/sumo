using System;
using Fusion;
using Fusion.Addons.Physics;
using Sumo.Gameplay;
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
        [SerializeField] private float comicMotionReferenceSpeed = 10f;
        [SerializeField] private float comicMotionShadowStrength = 0.1f;
        [SerializeField] private float comicMotionSmoothing = 12f;

        private Rigidbody _rigidbody;
        private SphereCollider _sphereCollider;
        private NetworkRigidbody3D _networkRigidbody;
        private SumoCollisionController _collisionController;
        private PlayerRoundState _roundState;
        private MatchRoundManager _matchRoundManager;
        private MeshRenderer _meshRenderer;
        private MaterialPropertyBlock _materialPropertyBlock;
        private float _scaledRadius = 0.5f;
        private Vector3 _baseLocalScale = Vector3.one;
        private bool _hasBaseLocalScale;
        private int _lastAuthorityAbilitySequence;
        private int _lastPredictedAbilitySequence;
        private int _lastProcessedClassRaw = int.MinValue;
        private int _predictedClassRaw = int.MinValue;
        private float _predictedAbilityStamina01 = FullAbilityStamina;
        private bool _predictedAbilityActive;
        private float _predictedScaleMultiplier = 1f;
        private int _lastAppliedClassRaw = int.MinValue;
        private float _lastAppliedScaleMultiplier = -1f;
        private int _ignoreGroundedUntilTick = int.MinValue;
        private static Material _sharedRuntimeMaterial;
        private Vector3 _cachedWallNormal;
        private int _cachedWallTick = int.MinValue;
        private Vector3 _comicShadowDirection = Vector3.forward;
        private float _comicShadowAmount;

        private static readonly int ComicMotionDirectionId = Shader.PropertyToID("_ComicMotionDirection");
        private static readonly int ComicMotionAmountId = Shader.PropertyToID("_ComicMotionAmount");
        private static readonly int ComicShadePhaseId = Shader.PropertyToID("_ComicShadePhase");
        private static readonly int ComicMotionShadowStrengthId = Shader.PropertyToID("_ComicMotionShadowStrength");

        private const float FallbackAntiBulldozeSpeedThreshold = 3.8f;
        private const float FallbackIntoPlayerAccelerationScale = 0.12f;
        private const float RamDriveAssistRisePerSecond = 14f;
        private const float RamDriveAssistFallPerSecond = 5.5f;
        private const float RamDriveAssistBypassThreshold = 0.42f;
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
        private const float FullAbilityStamina = 1f;
        private const float AbilityReadyEpsilon = 0.001f;
        private const int JumperGroundIgnoreTicks = 6;
        private const float JumperGroundedVelocityEpsilon = 0.05f;
        private const float JumperInstantLift = 0.045f;
        private Vector3 _currentMoveDirection = Vector3.zero;
        private float _currentSpeed01;
        private bool _isDashing;
        private float _dashPower;
        private Tick _lastDashTick;
        private MovementCommand _lastResolvedMovementCommand;
        private bool _hasLastResolvedMovementCommand;
        private MovementCommand _externalMovementCommand;
        private bool _hasExternalMovementCommand;
        private int _contactCommandTick = int.MinValue;
        private MovementCommand _contactCommandCache;
        private bool _hasContactCommandCache;
        private float _smoothedRamDriveAssist01;
        [Networked] private Vector3 ReplicatedMoveDirection { get; set; }
        [Networked] private float ReplicatedMoveStrength01 { get; set; }
        [Networked] private NetworkBool ReplicatedBrake { get; set; }
        [Networked] private Vector3 ReplicatedContactIntent { get; set; }
        [Networked] private float ReplicatedContactSpeed01 { get; set; }
        [Networked] public float AbilityStamina01 { get; private set; }
        [Networked] public NetworkBool AbilityActive { get; private set; }
        [Networked] private int ReplicatedClassRaw { get; set; }
        [Networked] private float ReplicatedScaleMultiplier { get; set; }

        private struct MovementCommand
        {
            public Vector3 TargetHorizontalVelocity;
            public Vector3 MoveDirection;
            public float MoveStrength01;
            public bool HasMoveInput;
            public bool HardBrake;
            public bool AbilityPressed;
            public int AbilitySequence;
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
        public float CombatReferenceTopSpeed => GetCombatReferenceTopSpeed();
        public bool IsDashing => _isDashing;
        public float DashPower => _dashPower;
        public Tick LastDashTick => _lastDashTick;
        public SumoBallPhysicsConfig PhysicsConfig => physicsConfig;
        public SumoPlayerClass CurrentClass => GetDisplayedPlayerClass();
        public bool IsClassAbilityActive => DisplayedAbilityActive;
        public float DisplayedAbilityStamina01 => GetDisplayedAbilityStamina01();
        public bool DisplayedAbilityActive => GetDisplayedAbilityActive();
        public SumoPlayerClass AuthoritativeClass => GetReplicatedPlayerClass();
        public float AuthoritativeAbilityStamina01 => Mathf.Clamp01(AbilityStamina01);
        public bool AuthoritativeAbilityActive => AbilityActive;
        public float ClassOutgoingPushMultiplier => ResolveOutgoingPushMultiplier();
        public float ClassIncomingPushMultiplier => ResolveIncomingPushMultiplier();
        public float ClassCombatSpeedMultiplier => ResolveCombatSpeedMultiplier();
        public float ClassPushSpeedFloorShare => ResolvePushSpeedFloorShare();
        public float ClassShoveForceFloor => ResolveShoveForceFloor();

        public void SetExternalMovementTarget(Vector3 worldMoveDirection, float targetSpeed, bool hardBrake = false)
        {
            Vector3 horizontalDirection = new Vector3(worldMoveDirection.x, 0f, worldMoveDirection.z);
            Vector3 targetHorizontalVelocity = Vector3.zero;

            if (horizontalDirection.sqrMagnitude > 0.0001f && targetSpeed > 0.0001f)
            {
                targetHorizontalVelocity = horizontalDirection.normalized * Mathf.Max(0f, targetSpeed);
            }

            SetExternalTargetHorizontalVelocity(targetHorizontalVelocity, hardBrake);
        }

        public void PreviewClassAbilityPress(int abilitySequence)
        {
            if (!HasInputAuthority || HasStateAuthority || abilitySequence <= 0)
            {
                return;
            }

            CacheComponents();

            SumoPlayerClass selectedClass = ResolveSelectedPlayerClass();
            int selectedRaw = (int)selectedClass;
            if (_predictedClassRaw != selectedRaw)
            {
                ResetPredictedClassAbilityState(selectedClass);
            }

            if (!TryConsumeAbilitySequence(abilitySequence, ref _lastPredictedAbilitySequence))
            {
                return;
            }

            if (selectedClass == SumoPlayerClass.None || !ResolveClassAbilitiesUnlocked())
            {
                _predictedAbilityActive = false;
                _predictedAbilityStamina01 = FullAbilityStamina;
                _predictedScaleMultiplier = 1f;
                return;
            }

            SumoPlayerClassDefinition definition = SumoPlayerClassCatalog.GetDefinition(selectedClass);
            if (!CanStartPredictedAbility())
            {
                return;
            }

            _predictedAbilityActive = true;
            _predictedAbilityStamina01 = FullAbilityStamina;

            if (selectedClass == SumoPlayerClass.Jumper && IsGrounded())
            {
                TryApplyJumperJump(definition);
            }

            _predictedScaleMultiplier = selectedClass == SumoPlayerClass.Fatso
                ? Mathf.Max(0.01f, definition.ScaleMultiplier)
                : 1f;
            ApplyClassPresentation();
        }

        public void SetExternalTargetHorizontalVelocity(Vector3 targetHorizontalVelocity, bool hardBrake = false)
        {
            BuildMovementCommandFromTargetHorizontalVelocity(targetHorizontalVelocity, hardBrake, out _externalMovementCommand);
            _hasExternalMovementCommand = true;
            _contactCommandTick = int.MinValue;
        }

        public void ClearExternalMovementTarget()
        {
            _hasExternalMovementCommand = false;
            _externalMovementCommand = default;
            _contactCommandTick = int.MinValue;
        }

        public Vector3 GetContactIntentDirection(Vector3 fallbackVelocity)
        {
            if (TryResolveContactCommandForCurrentTick(out MovementCommand command)
                && command.MoveDirection.sqrMagnitude > 0.0001f)
            {
                return command.MoveDirection.normalized;
            }

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
            float maxSpeed = Mathf.Max(0.01f, GetMaxSpeed());

            if (!IsLocallyDrivenForContact())
            {
                if (TryResolveContactCommandForCurrentTick(out MovementCommand command))
                {
                    float commandSpeed = Mathf.Clamp01(command.MoveStrength01) * maxSpeed;
                    speed = Mathf.Max(speed, commandSpeed);
                }

                float replicatedSpeed = Mathf.Clamp01(ReplicatedContactSpeed01) * maxSpeed;
                float replicatedMoveSpeed = Mathf.Clamp01(ReplicatedMoveStrength01) * maxSpeed;
                speed = Mathf.Max(speed, Mathf.Max(replicatedSpeed, replicatedMoveSpeed));
                return speed;
            }

            float controllerSpeedEstimate = _currentSpeed01 * maxSpeed;
            return Mathf.Max(speed, controllerSpeedEstimate);
        }

        private void Awake()
        {
            EnsurePresentationComponents();
            CacheComponents();
            CaptureBaseLocalScaleIfNeeded();
        }

        public override void Spawned()
        {
            EnsurePresentationComponents();
            CacheComponents();
            CaptureBaseLocalScaleIfNeeded();
            _lastResolvedMovementCommand = default;
            _hasLastResolvedMovementCommand = false;
            _smoothedRamDriveAssist01 = 0f;
            _comicShadowDirection = Vector3.forward;
            _comicShadowAmount = 0f;
            _lastAuthorityAbilitySequence = 0;
            _lastPredictedAbilitySequence = 0;
            _lastProcessedClassRaw = int.MinValue;
            _predictedClassRaw = int.MinValue;
            _predictedAbilityStamina01 = FullAbilityStamina;
            _predictedAbilityActive = false;
            _predictedScaleMultiplier = 1f;
            ApplyRigidbodySettings();
            ConfigureInterpolationTarget();
            RefreshScaledRadius();
            EnsureVisibleRenderer();

            if (HasStateAuthority)
            {
                ServerResetClassAbilityState();
            }

            ApplyClassPresentation();
            ApplyComicMotionPresentation();

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
            UpdateClassAbilityState(command.AbilityPressed, command.AbilitySequence, grounded, deltaTime);
            ApplyClassPresentation();
            grounded = IsGrounded();

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

            float rawRamDriveAssist = _collisionController != null
                ? _collisionController.GetRamDriveAssist01()
                : 0f;
            float blendSpeed = rawRamDriveAssist >= _smoothedRamDriveAssist01
                ? RamDriveAssistRisePerSecond
                : RamDriveAssistFallPerSecond;
            _smoothedRamDriveAssist01 = Mathf.MoveTowards(
                _smoothedRamDriveAssist01,
                Mathf.Clamp01(rawRamDriveAssist),
                Mathf.Max(0.01f, blendSpeed) * deltaTime);
            float ramDriveAssist = _smoothedRamDriveAssist01;
            float effectiveIntoPlayerScale = intoPlayerScale;
            float antiBulldozeSpeedThreshold = physicsConfig != null
                ? physicsConfig.AntiBulldozeSpeedThreshold
                : FallbackAntiBulldozeSpeedThreshold;
            float speed01 = Mathf.Clamp01(horizontalVelocity.magnitude / Mathf.Max(0.01f, antiBulldozeSpeedThreshold));
            float minIntoScale = Mathf.Max(0.02f, intoPlayerScale * 0.45f);
            float antiBulldozeScale = Mathf.Lerp(intoPlayerScale, minIntoScale, speed01);
            effectiveIntoPlayerScale = Mathf.Lerp(antiBulldozeScale, 1f, Mathf.Clamp01(ramDriveAssist));
            if (ramDriveAssist >= RamDriveAssistBypassThreshold)
            {
                effectiveIntoPlayerScale = 1f;
            }

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

        private bool TryResolveContactCommandForCurrentTick(out MovementCommand command)
        {
            int tick = Runner != null ? Runner.Tick.Raw : Time.frameCount;
            if (_contactCommandTick == tick)
            {
                command = _contactCommandCache;
                return _hasContactCommandCache;
            }

            _contactCommandTick = tick;
            _hasContactCommandCache = TryResolveMovementCommand(out _contactCommandCache);
            command = _contactCommandCache;
            return _hasContactCommandCache;
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

            if (_hasExternalMovementCommand && (HasStateAuthority || HasInputAuthority))
            {
                command = _externalMovementCommand;
                _lastResolvedMovementCommand = command;
                _hasLastResolvedMovementCommand = true;
                return true;
            }

            if (HasStateAuthority || HasInputAuthority)
            {
                if (GetInput(out SumoInputData input))
                {
                    BuildMovementCommandFromInput(
                        input.Move,
                        input.CameraYaw,
                        input.Buttons.IsSet((int)SumoInputButton.Brake),
                        input.Buttons.IsSet((int)SumoInputButton.Ability),
                        input.AbilitySequence,
                        out command);
                    _lastResolvedMovementCommand = command;
                    _hasLastResolvedMovementCommand = true;
                    return true;
                }

                if (_hasLastResolvedMovementCommand)
                {
                    command = _lastResolvedMovementCommand;
                    return true;
                }

                BuildMovementCommandFromInput(Vector2.zero, 0f, false, false, 0, out command);
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
            bool abilityPressed,
            int abilitySequence,
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

            BuildMovementCommandFromTargetHorizontalVelocity(targetHorizontalVelocity, hardBrake, out command);
            command.AbilityPressed = abilityPressed;
            command.AbilitySequence = Mathf.Max(0, abilitySequence);
        }

        private void BuildMovementCommandFromTargetHorizontalVelocity(
            Vector3 targetHorizontalVelocity,
            bool hardBrake,
            out MovementCommand command)
        {
            targetHorizontalVelocity.y = 0f;

            Vector3 moveDirection = targetHorizontalVelocity.sqrMagnitude > 0.0001f
                ? targetHorizontalVelocity.normalized
                : Vector3.zero;
            bool hasMoveInput = moveDirection.sqrMagnitude > 0.0001f;

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
                HardBrake = hardBrake,
                AbilityPressed = false,
                AbilitySequence = 0
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
                HardBrake = ReplicatedBrake,
                AbilityPressed = false,
                AbilitySequence = 0
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

            if (_roundState == null)
            {
                _roundState = GetComponent<PlayerRoundState>();
            }

            if (_matchRoundManager == null)
            {
                _matchRoundManager = FindFirstObjectByType<MatchRoundManager>(FindObjectsInactive.Include);
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

        public override void Render()
        {
            ApplyClassPresentation();
            ApplyComicMotionPresentation();
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

            if (GetComponent<SumoPlayerDebugOverlay>() == null)
            {
                gameObject.AddComponent<SumoPlayerDebugOverlay>();
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

        public void ServerResetClassAbilityState()
        {
            if (!HasStateAuthority)
            {
                return;
            }

            SumoPlayerClass selectedClass = ResolveSelectedPlayerClass();
            ReplicatedClassRaw = (int)selectedClass;
            AbilityStamina01 = FullAbilityStamina;
            AbilityActive = false;
            ReplicatedScaleMultiplier = 1f;
            _lastAuthorityAbilitySequence = 0;
            _lastPredictedAbilitySequence = 0;
            _ignoreGroundedUntilTick = int.MinValue;
            _lastProcessedClassRaw = ReplicatedClassRaw;
            ResetPredictedClassAbilityState(selectedClass);
            ApplyClassPresentation();
        }

        private void UpdateClassAbilityState(bool abilityPressed, int abilitySequence, bool grounded, float deltaTime)
        {
            if (HasStateAuthority)
            {
                UpdateAuthorityClassAbilityState(abilityPressed, abilitySequence, grounded, deltaTime);
                return;
            }

            if (HasInputAuthority)
            {
                UpdatePredictedClassAbilityState(abilityPressed, abilitySequence, grounded, deltaTime);
            }
        }

        private void UpdateAuthorityClassAbilityState(bool abilityPressed, int abilitySequence, bool grounded, float deltaTime)
        {
            SumoPlayerClass selectedClass = ResolveSelectedPlayerClass();
            int selectedRaw = (int)selectedClass;
            if (_lastProcessedClassRaw != selectedRaw)
            {
                AbilityActive = false;
                AbilityStamina01 = FullAbilityStamina;
                _lastAuthorityAbilitySequence = 0;
                _lastProcessedClassRaw = selectedRaw;
            }

            ReplicatedClassRaw = selectedRaw;

            bool pressedThisTick = abilityPressed && TryConsumeAbilitySequence(abilitySequence, ref _lastAuthorityAbilitySequence);

            if (selectedClass == SumoPlayerClass.None)
            {
                AbilityActive = false;
                AbilityStamina01 = FullAbilityStamina;
                ReplicatedScaleMultiplier = 1f;
                return;
            }

            SumoPlayerClassDefinition definition = SumoPlayerClassCatalog.GetDefinition(selectedClass);
            if (!ResolveClassAbilitiesUnlocked())
            {
                AbilityActive = false;
                AbilityStamina01 = FullAbilityStamina;
                ReplicatedScaleMultiplier = 1f;
                return;
            }

            if (pressedThisTick)
            {
                bool startedAbility = false;
                if (IsAbilityReady(AbilityStamina01, AbilityActive))
                {
                    AbilityActive = true;
                    AbilityStamina01 = FullAbilityStamina;
                    startedAbility = true;
                }

                if (selectedClass == SumoPlayerClass.Jumper && AbilityActive && grounded)
                {
                    TryApplyJumperJump(definition);
                }
                else if (selectedClass == SumoPlayerClass.Fatso && startedAbility)
                {
                    ApplyFatsoActivationBurst(definition);
                }
            }

            if (AbilityActive)
            {
                float activeSeconds = Mathf.Max(0.01f, definition.AbilityActiveSeconds);
                AbilityStamina01 = Mathf.Max(0f, AbilityStamina01 - deltaTime / activeSeconds);
                if (AbilityStamina01 <= AbilityReadyEpsilon)
                {
                    AbilityStamina01 = 0f;
                    AbilityActive = false;
                }
            }
            else
            {
                float rechargeSeconds = Mathf.Max(0.01f, definition.AbilityRechargeSeconds);
                AbilityStamina01 = Mathf.Min(FullAbilityStamina, AbilityStamina01 + deltaTime / rechargeSeconds);
            }

            ReplicatedScaleMultiplier = selectedClass == SumoPlayerClass.Fatso && AbilityActive
                ? Mathf.Max(0.01f, definition.ScaleMultiplier)
                : 1f;
        }

        private void UpdatePredictedClassAbilityState(bool abilityPressed, int abilitySequence, bool grounded, float deltaTime)
        {
            SumoPlayerClass selectedClass = ResolveSelectedPlayerClass();
            int selectedRaw = (int)selectedClass;
            if (_predictedClassRaw != selectedRaw)
            {
                ResetPredictedClassAbilityState(selectedClass);
            }
            else
            {
                SyncPredictedClassAbilityStateWithAuthority();
            }

            bool pressedThisTick = abilityPressed && TryConsumeAbilitySequence(abilitySequence, ref _lastPredictedAbilitySequence);

            if (selectedClass == SumoPlayerClass.None)
            {
                _predictedAbilityActive = false;
                _predictedAbilityStamina01 = FullAbilityStamina;
                _predictedScaleMultiplier = 1f;
                return;
            }

            SumoPlayerClassDefinition definition = SumoPlayerClassCatalog.GetDefinition(selectedClass);
            if (!ResolveClassAbilitiesUnlocked())
            {
                _predictedAbilityActive = false;
                _predictedAbilityStamina01 = FullAbilityStamina;
                _predictedScaleMultiplier = 1f;
                return;
            }

            if (pressedThisTick)
            {
                if (CanStartPredictedAbility())
                {
                    _predictedAbilityActive = true;
                    _predictedAbilityStamina01 = FullAbilityStamina;
                }

                if (selectedClass == SumoPlayerClass.Jumper && _predictedAbilityActive && grounded)
                {
                    TryApplyJumperJump(definition);
                }
            }

            if (_predictedAbilityActive)
            {
                float activeSeconds = Mathf.Max(0.01f, definition.AbilityActiveSeconds);
                _predictedAbilityStamina01 = Mathf.Max(0f, _predictedAbilityStamina01 - deltaTime / activeSeconds);
                if (_predictedAbilityStamina01 <= AbilityReadyEpsilon)
                {
                    _predictedAbilityStamina01 = 0f;
                    _predictedAbilityActive = false;
                }
            }
            else
            {
                float rechargeSeconds = Mathf.Max(0.01f, definition.AbilityRechargeSeconds);
                _predictedAbilityStamina01 = Mathf.Min(FullAbilityStamina, _predictedAbilityStamina01 + deltaTime / rechargeSeconds);
            }

            _predictedScaleMultiplier = selectedClass == SumoPlayerClass.Fatso && _predictedAbilityActive
                ? Mathf.Max(0.01f, definition.ScaleMultiplier)
                : 1f;
        }

        private void ResetPredictedClassAbilityState(SumoPlayerClass selectedClass)
        {
            _predictedClassRaw = (int)selectedClass;
            _predictedAbilityStamina01 = FullAbilityStamina;
            _predictedAbilityActive = false;
            _predictedScaleMultiplier = 1f;
            _lastPredictedAbilitySequence = 0;
        }

        private static bool TryConsumeAbilitySequence(int abilitySequence, ref int lastConsumedSequence)
        {
            if (abilitySequence <= 0 || abilitySequence <= lastConsumedSequence)
            {
                return false;
            }

            lastConsumedSequence = abilitySequence;
            return true;
        }

        private static bool IsAbilityReady(float stamina01, bool active)
        {
            return !active && stamina01 >= FullAbilityStamina - AbilityReadyEpsilon;
        }

        private bool CanStartPredictedAbility()
        {
            return IsAbilityReady(_predictedAbilityStamina01, _predictedAbilityActive);
        }

        private bool ResolveClassAbilitiesUnlocked()
        {
            if (_matchRoundManager == null)
            {
                _matchRoundManager = FindFirstObjectByType<MatchRoundManager>(FindObjectsInactive.Include);
            }

            if (_matchRoundManager == null)
            {
                return Runner == null || Object == null || !Object.IsInSimulation;
            }

            return _matchRoundManager.IsNetworkSpawned && _matchRoundManager.ClassAbilitiesUnlocked;
        }

        private void SyncPredictedClassAbilityStateWithAuthority()
        {
            if (HasStateAuthority)
            {
                return;
            }

            float authorityStamina = Mathf.Clamp01(AbilityStamina01);
            if (AbilityActive)
            {
                if (!_predictedAbilityActive)
                {
                    _predictedAbilityActive = true;
                    _predictedAbilityStamina01 = authorityStamina;
                }
                else if (authorityStamina < _predictedAbilityStamina01)
                {
                    _predictedAbilityStamina01 = authorityStamina;
                }

                return;
            }

            if (_predictedAbilityActive && authorityStamina < FullAbilityStamina - AbilityReadyEpsilon)
            {
                _predictedAbilityActive = false;
                _predictedAbilityStamina01 = Mathf.Min(_predictedAbilityStamina01, authorityStamina);
                _predictedScaleMultiplier = 1f;
                return;
            }

            if (!_predictedAbilityActive && authorityStamina >= FullAbilityStamina - AbilityReadyEpsilon)
            {
                _predictedAbilityStamina01 = FullAbilityStamina;
                _predictedScaleMultiplier = 1f;
                return;
            }

            if (!_predictedAbilityActive)
            {
                _predictedAbilityStamina01 = Mathf.Min(_predictedAbilityStamina01, authorityStamina);
                _predictedScaleMultiplier = 1f;
            }
        }

        private bool TryApplyJumperJump(SumoPlayerClassDefinition definition)
        {
            if (_rigidbody == null)
            {
                return false;
            }

            float jumpVelocity = Mathf.Max(0f, definition.JumpVelocityChange);
            if (jumpVelocity <= 0f)
            {
                return false;
            }

            Vector3 velocity = _rigidbody.linearVelocity;
            velocity.y = jumpVelocity;
            _rigidbody.position += Vector3.up * Mathf.Max(JumperInstantLift, _scaledRadius * 0.04f);
            _rigidbody.linearVelocity = velocity;
            _rigidbody.WakeUp();
            _ignoreGroundedUntilTick = GetCurrentSimulationTick() + JumperGroundIgnoreTicks;
            return true;
        }

        private void ApplyFatsoActivationBurst(SumoPlayerClassDefinition definition)
        {
            if (definition.Class != SumoPlayerClass.Fatso)
            {
                return;
            }

            if (_collisionController == null)
            {
                CacheComponents();
            }

            _collisionController?.ApplyFatsoActivationBurst();
        }

        private SumoPlayerClass ResolveSelectedPlayerClass()
        {
            if (_roundState == null)
            {
                CacheComponents();
            }

            if (_roundState != null)
            {
                if (_roundState.SelectedClassRaw == (int)SumoPlayerClass.None
                    && Object != null
                    && Object.InputAuthority == PlayerRef.None)
                {
                    return SumoPlayerClass.None;
                }

                return _roundState.SelectedClass;
            }

            return SumoPlayerClassCatalog.DefaultClass;
        }

        private SumoPlayerClass GetReplicatedPlayerClass()
        {
            return ReplicatedClassRaw == int.MinValue || ReplicatedClassRaw == (int)SumoPlayerClass.None
                ? SumoPlayerClass.None
                : SumoPlayerClassCatalog.FromRaw(ReplicatedClassRaw);
        }

        private SumoPlayerClass GetPredictedPlayerClass()
        {
            if (_predictedClassRaw == int.MinValue || _predictedClassRaw == (int)SumoPlayerClass.None)
            {
                return SumoPlayerClass.None;
            }

            return SumoPlayerClassCatalog.FromRaw(_predictedClassRaw);
        }

        private SumoPlayerClass GetDisplayedPlayerClass()
        {
            return ShouldUsePredictedAbilityState()
                ? GetPredictedPlayerClass()
                : GetReplicatedPlayerClass();
        }

        private float GetDisplayedAbilityStamina01()
        {
            return ShouldUsePredictedAbilityState()
                ? _predictedAbilityStamina01
                : AbilityStamina01;
        }

        private bool GetDisplayedAbilityActive()
        {
            return ShouldUsePredictedAbilityState()
                ? _predictedAbilityActive
                : AbilityActive;
        }

        private bool ShouldUsePredictedAbilityState()
        {
            return HasInputAuthority && !HasStateAuthority && _predictedClassRaw != int.MinValue;
        }

        private SumoPlayerClass GetSimulationPlayerClass()
        {
            return ShouldUsePredictedAbilityState()
                ? GetPredictedPlayerClass()
                : GetReplicatedPlayerClass();
        }

        private bool GetSimulationAbilityActive()
        {
            return ShouldUsePredictedAbilityState()
                ? _predictedAbilityActive
                : AbilityActive;
        }

        private float ResolveOutgoingPushMultiplier()
        {
            SumoPlayerClass playerClass = GetSimulationPlayerClass();
            if (playerClass != SumoPlayerClass.Fatso || !GetSimulationAbilityActive())
            {
                return 1f;
            }

            return Mathf.Max(0.01f, SumoPlayerClassCatalog.GetDefinition(playerClass).OutgoingPushMultiplier);
        }

        private float ResolveIncomingPushMultiplier()
        {
            SumoPlayerClass playerClass = GetSimulationPlayerClass();
            if (playerClass != SumoPlayerClass.Fatso || !GetSimulationAbilityActive())
            {
                return 1f;
            }

            return Mathf.Max(0.01f, SumoPlayerClassCatalog.GetDefinition(playerClass).IncomingPushMultiplier);
        }

        private float ResolveCombatSpeedMultiplier()
        {
            SumoPlayerClass playerClass = GetSimulationPlayerClass();
            if (playerClass != SumoPlayerClass.Fatso || !GetSimulationAbilityActive())
            {
                return 1f;
            }

            float movementMultiplier = SumoPlayerClassCatalog.GetDefinition(playerClass).SpeedMultiplier;
            if (movementMultiplier >= 1f || movementMultiplier <= 0.0001f)
            {
                return 1f;
            }

            return Mathf.Clamp(1f / movementMultiplier, 1f, 6f);
        }

        private float ResolvePushSpeedFloorShare()
        {
            SumoPlayerClass playerClass = GetSimulationPlayerClass();
            if (playerClass != SumoPlayerClass.Fatso || !GetSimulationAbilityActive())
            {
                return 0f;
            }

            return Mathf.Clamp(SumoPlayerClassCatalog.GetDefinition(playerClass).PushSpeedFloorShare, 0f, 2f);
        }

        private float ResolveShoveForceFloor()
        {
            SumoPlayerClass playerClass = GetSimulationPlayerClass();
            if (playerClass != SumoPlayerClass.Fatso || !GetSimulationAbilityActive())
            {
                return 1f;
            }

            return Mathf.Clamp(SumoPlayerClassCatalog.GetDefinition(playerClass).ShoveForceFloor, 1f, 5f);
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
            return GetCombatReferenceTopSpeed() * GetClassSpeedMultiplier();
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
            return baseBraking * GetBrakingMultiplier();
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
            return Mathf.Max(0.01f, configured * GetClassSpeedMultiplier());
        }

        private float GetCombatReferenceTopSpeed()
        {
            float baseMaxSpeed = accelerationConfig != null
                ? accelerationConfig.MaxSpeed
                : FallbackMaxSpeed;
            float configured = movementSpeedMultiplier > 0.0001f
                ? movementSpeedMultiplier
                : DefaultMovementSpeedMultiplier;
            return Mathf.Max(0.01f, baseMaxSpeed * configured);
        }

        private float GetClassSpeedMultiplier()
        {
            float classMultiplier = 1f;
            SumoPlayerClass playerClass = GetSimulationPlayerClass();
            if (playerClass == SumoPlayerClass.Fatso && GetSimulationAbilityActive())
            {
                classMultiplier = SumoPlayerClassCatalog.GetDefinition(playerClass).SpeedMultiplier;
            }

            return Mathf.Max(0.01f, classMultiplier);
        }

        private float GetBrakingMultiplier()
        {
            float configured = movementSpeedMultiplier > 0.0001f
                ? movementSpeedMultiplier
                : DefaultMovementSpeedMultiplier;
            SumoPlayerClass playerClass = GetSimulationPlayerClass();
            if (playerClass == SumoPlayerClass.Fatso && GetSimulationAbilityActive())
            {
                return Mathf.Max(0.01f, configured);
            }

            return GetMovementSpeedMultiplier();
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

            if (ShouldIgnoreGroundedAfterJumperJump())
            {
                return false;
            }

            if (_rigidbody.linearVelocity.y > JumperGroundedVelocityEpsilon)
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

        private bool ShouldIgnoreGroundedAfterJumperJump()
        {
            if (_ignoreGroundedUntilTick == int.MinValue)
            {
                return false;
            }

            return GetCurrentSimulationTick() <= _ignoreGroundedUntilTick;
        }

        private int GetCurrentSimulationTick()
        {
            return Runner != null ? Runner.Tick.Raw : Time.frameCount;
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

            Shader shader = Shader.Find("Sumo/ComicToon");
            if (shader == null)
            {
                shader = Shader.Find("Universal Render Pipeline/Lit");
            }

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

                if (_sharedRuntimeMaterial.HasProperty("_BaseColor"))
                {
                    _sharedRuntimeMaterial.SetColor("_BaseColor", runtimeBallColor);
                }

                if (_sharedRuntimeMaterial.HasProperty("_Color"))
                {
                    _sharedRuntimeMaterial.SetColor("_Color", runtimeBallColor);
                }

                if (_sharedRuntimeMaterial.HasProperty("_ShadowColor"))
                {
                    _sharedRuntimeMaterial.SetColor("_ShadowColor", new Color(0.32f, 0.32f, 0.32f, 1f));
                }

                if (_sharedRuntimeMaterial.HasProperty("_InkWidth"))
                {
                    _sharedRuntimeMaterial.SetFloat("_InkWidth", 2.8f);
                }

                if (_sharedRuntimeMaterial.HasProperty("_ShadeSteps"))
                {
                    _sharedRuntimeMaterial.SetFloat("_ShadeSteps", 4f);
                }

                if (_sharedRuntimeMaterial.HasProperty("_ComicMotionShadowStrength"))
                {
                    _sharedRuntimeMaterial.SetFloat("_ComicMotionShadowStrength", comicMotionShadowStrength);
                }
            }

            targetRenderer.sharedMaterial = _sharedRuntimeMaterial;
            Debug.Log($"SumoBallController: applied fallback material on {name}.");
        }

        private void ApplyClassPresentation()
        {
            CaptureBaseLocalScaleIfNeeded();

            float scaleMultiplier = ResolvePresentationScaleMultiplier();
            if (!Mathf.Approximately(_lastAppliedScaleMultiplier, scaleMultiplier))
            {
                Vector3 targetScale = new Vector3(
                    _baseLocalScale.x * scaleMultiplier,
                    _baseLocalScale.y * scaleMultiplier,
                    _baseLocalScale.z * scaleMultiplier);

                if ((transform.localScale - targetScale).sqrMagnitude > 0.000001f)
                {
                    transform.localScale = targetScale;
                    RefreshScaledRadius();
                }

                _lastAppliedScaleMultiplier = scaleMultiplier;
            }

            int classRaw = ResolvePresentationClassRaw();
            if (_lastAppliedClassRaw == classRaw)
            {
                return;
            }

            MeshRenderer targetRenderer = ResolveTargetRenderer();
            if (targetRenderer == null)
            {
                _lastAppliedClassRaw = classRaw;
                return;
            }

            if (_materialPropertyBlock == null)
            {
                _materialPropertyBlock = new MaterialPropertyBlock();
            }

            Color classColor = ResolvePresentationColor(classRaw);
            targetRenderer.GetPropertyBlock(_materialPropertyBlock);
            _materialPropertyBlock.SetColor("_Color", classColor);
            _materialPropertyBlock.SetColor("_BaseColor", classColor);
            targetRenderer.SetPropertyBlock(_materialPropertyBlock);
            _lastAppliedClassRaw = classRaw;
        }

        private void ApplyComicMotionPresentation()
        {
            MeshRenderer targetRenderer = ResolveTargetRenderer();
            if (targetRenderer == null)
            {
                return;
            }

            if (_materialPropertyBlock == null)
            {
                _materialPropertyBlock = new MaterialPropertyBlock();
            }

            Vector3 horizontalVelocity = _rigidbody != null ? _rigidbody.linearVelocity : Vector3.zero;
            horizontalVelocity.y = 0f;
            float speed = horizontalVelocity.magnitude;

            Vector3 targetDirection = speed > 0.08f ? horizontalVelocity / speed : _currentMoveDirection;
            targetDirection.y = 0f;
            if (targetDirection.sqrMagnitude < 0.0001f)
            {
                targetDirection = _comicShadowDirection.sqrMagnitude > 0.0001f ? _comicShadowDirection : transform.forward;
                targetDirection.y = 0f;
            }

            if (targetDirection.sqrMagnitude < 0.0001f)
            {
                targetDirection = Vector3.forward;
            }

            targetDirection.Normalize();

            float targetAmount = speed > 0.05f
                ? Mathf.Clamp01(speed / Mathf.Max(0.01f, comicMotionReferenceSpeed))
                : 0f;
            float deltaTime = Mathf.Max(0f, Time.deltaTime);
            float blend = 1f - Mathf.Exp(-Mathf.Max(0.01f, comicMotionSmoothing) * deltaTime);

            if (targetAmount > 0.005f)
            {
                _comicShadowDirection = Vector3.Slerp(_comicShadowDirection, targetDirection, blend).normalized;
            }

            _comicShadowAmount = Mathf.Lerp(_comicShadowAmount, targetAmount, blend);
            float phase = Vector3.Dot(transform.position, new Vector3(0.37f, 0f, 0.29f));

            targetRenderer.GetPropertyBlock(_materialPropertyBlock);
            _materialPropertyBlock.SetVector(ComicMotionDirectionId, new Vector4(_comicShadowDirection.x, _comicShadowDirection.y, _comicShadowDirection.z, 0f));
            _materialPropertyBlock.SetFloat(ComicMotionAmountId, _comicShadowAmount);
            _materialPropertyBlock.SetFloat(ComicShadePhaseId, phase);
            _materialPropertyBlock.SetFloat(ComicMotionShadowStrengthId, Mathf.Clamp01(comicMotionShadowStrength));
            targetRenderer.SetPropertyBlock(_materialPropertyBlock);
        }

        private float ResolvePresentationScaleMultiplier()
        {
            float scaleMultiplier = ReplicatedScaleMultiplier;
            if (ShouldUsePredictedAbilityState())
            {
                SumoPlayerClass predictedClass = GetPredictedPlayerClass();
                if (predictedClass == SumoPlayerClass.Fatso
                    && (_predictedAbilityActive
                        || AbilityActive
                        || _predictedScaleMultiplier > 1.01f
                        || ReplicatedScaleMultiplier > 1.01f))
                {
                    scaleMultiplier = SumoPlayerClassCatalog.GetDefinition(predictedClass).ScaleMultiplier;
                }
                else
                {
                    scaleMultiplier = _predictedScaleMultiplier;
                }
            }

            return scaleMultiplier > 0.0001f ? scaleMultiplier : 1f;
        }

        private int ResolvePresentationClassRaw()
        {
            return ShouldUsePredictedAbilityState()
                ? _predictedClassRaw
                : ReplicatedClassRaw;
        }

        private Color ResolvePresentationColor(int classRaw)
        {
            SumoPlayerClass playerClass = classRaw == int.MinValue || classRaw == (int)SumoPlayerClass.None
                ? SumoPlayerClass.None
                : SumoPlayerClassCatalog.FromRaw(classRaw);
            if (playerClass == SumoPlayerClass.None)
            {
                return runtimeBallColor;
            }

            return SumoPlayerClassCatalog.GetDefinition(playerClass).Color;
        }

        private MeshRenderer ResolveTargetRenderer()
        {
            if (_meshRenderer != null)
            {
                return _meshRenderer;
            }

            if (visualTarget != null)
            {
                MeshRenderer visualRenderer = visualTarget.GetComponentInChildren<MeshRenderer>(true);
                if (visualRenderer != null)
                {
                    return visualRenderer;
                }
            }

            return GetComponentInChildren<MeshRenderer>(true);
        }

        private void CaptureBaseLocalScaleIfNeeded()
        {
            if (_hasBaseLocalScale)
            {
                return;
            }

            _baseLocalScale = transform.localScale;
            if (_baseLocalScale.sqrMagnitude < 0.0001f)
            {
                _baseLocalScale = Vector3.one;
            }

            _hasBaseLocalScale = true;
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
            comicMotionReferenceSpeed = Mathf.Max(0.01f, comicMotionReferenceSpeed);
            comicMotionShadowStrength = Mathf.Clamp01(comicMotionShadowStrength);
            comicMotionSmoothing = Mathf.Max(0.01f, comicMotionSmoothing);

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

            if (_roundState == null)
            {
                _roundState = GetComponent<PlayerRoundState>();
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
