using Sumo.Gameplay;
using UnityEngine;

#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace Sumo
{
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Rigidbody))]
    public sealed class SumoNpcBallDriver : MonoBehaviour
    {
        private enum DirectionMode
        {
            WorldDirection = 0,
            LocalDirection = 1,
            TowardTransform = 2
        }

        private enum RotationMode
        {
            None = 0,
            FaceMoveDirectionYaw = 1,
            ManualAngularVelocity = 2
        }

        [Header("Runtime")]
        [SerializeField] private bool startMovingOnEnable = true;
        [SerializeField] private KeyCode stopStartBind = KeyCode.T;
        [SerializeField] private bool hardBrakeWhenStopped = true;
        [SerializeField] private bool disablePlayerOnlyComponents = true;

        [Header("Movement")]
        [SerializeField] private bool usePlayerControllerWhenAvailable = true;
        [SerializeField] private DirectionMode directionMode = DirectionMode.WorldDirection;
        [SerializeField] private Vector3 worldDirection = Vector3.forward;
        [SerializeField] private Vector3 localDirection = Vector3.forward;
        [SerializeField] private Transform directionTarget;
        [SerializeField] private float targetSpeed = 12f;
        [Range(0f, 1f)]
        [SerializeField] private float inputStrength = 1f;
        [SerializeField] private bool hardBrakeWithoutMoveInput;

        [Header("Zone Seeking")]
        [SerializeField] private bool moveTowardZone;
        [SerializeField] private Transform zoneTransform;
        [SerializeField] private Collider zoneCollider;
        [SerializeField] private Vector3 zoneCenterOffset;
        [SerializeField] private float zoneStopRadius = 0.75f;
        [SerializeField] private float zoneResumeRadius = 1.1f;
        [SerializeField] private bool brakeInsideZone = true;

        [Header("Standalone Movement")]
        [SerializeField] private bool driveStandaloneRigidbody = true;
        [SerializeField] private SumoAccelerationConfig accelerationConfig;
        [SerializeField] private SumoBallPhysicsConfig physicsConfig;
        [SerializeField] private float movementSpeedMultiplier = 1.5f;
        [SerializeField] private ForceMode movementForceMode = ForceMode.Acceleration;
        [SerializeField] private float groundCheckDistance = 0.2f;
        [SerializeField] private float minGroundNormalDot = 0.55f;
        [SerializeField] private LayerMask groundMask = ~0;
        [SerializeField] private float airControlMultiplier = 0.12f;
        [SerializeField] private float airMaxSpeedMultiplier = 1f;
        [SerializeField] private float fallGravityMultiplier = 1.15f;
        [SerializeField] private bool applyPhysicsConfigOnAwake = true;

        [Header("Rotation")]
        [SerializeField] private RotationMode rotationMode = RotationMode.None;
        [SerializeField] private float faceMoveDirectionTurnSpeed = 720f;
        [SerializeField] private Vector3 angularVelocityDegreesPerSecond = Vector3.zero;
        [SerializeField] private Space angularVelocitySpace = Space.Self;

        [Header("Debug")]
        [SerializeField] private bool drawGizmos = true;
        [SerializeField] private Color directionGizmoColor = new Color(0.2f, 0.85f, 1f, 1f);
        [SerializeField] private Color zoneGizmoColor = new Color(1f, 0.85f, 0.1f, 1f);

        private const float FallbackMaxSpeed = 50f;
        private const float FallbackMinMoveSpeed = 4.5f;
        private const float FallbackInitialAccelerationResponse = 36f;
        private const float FallbackAccelerationPower = 2.2f;
        private const float FallbackTopSpeedApproachStrength = 1.85f;
        private const float FallbackBraking = 22f;
        private const float FallbackHardBrakeMultiplier = 1.8f;
        private const float FallbackRollingDrag = 1.1f;
        private const float FallbackSteeringResponsiveness = 9f;

        private Rigidbody _rigidbody;
        private SphereCollider _sphereCollider;
        private SumoBallController _ballController;
        private SafeZoneController _autoSafeZoneController;
        private bool _isMoving;
        private bool _zoneHolding;
        private bool _localStopStartBindEnabled = true;
        private float _scaledRadius = 0.5f;

        public bool IsMoving => _isMoving;
        public bool DisablePlayerOnlyComponents => disablePlayerOnlyComponents;

        private void Awake()
        {
            CacheComponents();
            DisablePlayerComponentsIfNeeded();
            EnsureNpcGameplaySurfaceEnabled();
            RefreshScaledRadius();

            if (applyPhysicsConfigOnAwake && _ballController == null && physicsConfig != null)
            {
                physicsConfig.ApplyTo(_rigidbody, _sphereCollider);
            }
        }

        private void OnEnable()
        {
            _isMoving = startMovingOnEnable;
            _zoneHolding = false;
            EnsureNpcGameplaySurfaceEnabled();
        }

        private void OnDisable()
        {
            if (_ballController != null)
            {
                _ballController.SetExternalMovementTarget(Vector3.zero, 0f, true);
            }
        }

        private void Update()
        {
            if (IsNetworkedWithoutLocalAuthority())
            {
                return;
            }

            if (_localStopStartBindEnabled && WasStopStartPressed())
            {
                SetMoving(!_isMoving);
            }
        }

        private void FixedUpdate()
        {
            if (_rigidbody == null)
            {
                return;
            }

            if (IsNetworkedWithoutLocalAuthority())
            {
                return;
            }

            bool hasMoveInput = TryResolveMoveDirection(out Vector3 moveDirection);
            float effectiveTargetSpeed = hasMoveInput
                ? Mathf.Max(0f, targetSpeed) * Mathf.Clamp01(inputStrength)
                : 0f;
            bool hardBrake = (!_isMoving && hardBrakeWhenStopped)
                || (!hasMoveInput && hardBrakeWithoutMoveInput)
                || (_zoneHolding && brakeInsideZone)
                || (moveTowardZone && !hasMoveInput);

            if (ShouldDriveThroughPlayerController())
            {
                _ballController.SetExternalMovementTarget(moveDirection, effectiveTargetSpeed, hardBrake);
                ApplyRotation(moveDirection, Time.fixedDeltaTime);
                return;
            }

            if (driveStandaloneRigidbody)
            {
                Vector3 targetHorizontalVelocity = hasMoveInput
                    ? moveDirection * effectiveTargetSpeed
                    : Vector3.zero;
                ApplyStandaloneMovement(targetHorizontalVelocity, hasMoveInput, hardBrake, Time.fixedDeltaTime);
            }

            ApplyRotation(moveDirection, Time.fixedDeltaTime);
        }

        public void SetMoving(bool moving)
        {
            _isMoving = moving;
            if (!moving)
            {
                _zoneHolding = false;
            }
        }

        public void ToggleMoving()
        {
            SetMoving(!_isMoving);
        }

        public void ConfigureRuntimeSpawnedNpc()
        {
            CacheComponents();
            DisablePlayerComponentsIfNeeded();
            EnsureNpcGameplaySurfaceEnabled();
            RefreshScaledRadius();

            moveTowardZone = true;
            zoneTransform = null;
            zoneCollider = null;
            _autoSafeZoneController = null;
            _localStopStartBindEnabled = false;
            _isMoving = startMovingOnEnable;
            _zoneHolding = false;

            if (_ballController != null)
            {
                _ballController.SetExternalMovementTarget(Vector3.zero, 0f, true);
            }

            if (_rigidbody != null)
            {
                _rigidbody.isKinematic = false;
                _rigidbody.detectCollisions = true;
                _rigidbody.linearVelocity = Vector3.zero;
                _rigidbody.angularVelocity = Vector3.zero;
            }
        }

        private bool TryResolveMoveDirection(out Vector3 moveDirection)
        {
            moveDirection = Vector3.zero;
            if (!_isMoving)
            {
                return false;
            }

            if (moveTowardZone && TryResolveZoneDirection(out moveDirection))
            {
                return true;
            }

            if (moveTowardZone)
            {
                return false;
            }

            switch (directionMode)
            {
                case DirectionMode.LocalDirection:
                    moveDirection = transform.TransformDirection(localDirection);
                    break;
                case DirectionMode.TowardTransform:
                    if (directionTarget != null)
                    {
                        moveDirection = directionTarget.position - transform.position;
                    }
                    break;
                case DirectionMode.WorldDirection:
                default:
                    moveDirection = worldDirection;
                    break;
            }

            moveDirection.y = 0f;
            if (moveDirection.sqrMagnitude < 0.0001f)
            {
                return false;
            }

            moveDirection.Normalize();
            return true;
        }

        private bool TryResolveZoneDirection(out Vector3 moveDirection)
        {
            moveDirection = Vector3.zero;
            if (!TryGetZoneCenter(out Vector3 zoneCenter))
            {
                _zoneHolding = false;
                return false;
            }

            Vector3 toCenter = zoneCenter - transform.position;
            toCenter.y = 0f;
            float distance = toCenter.magnitude;
            float stopRadius = Mathf.Max(0f, zoneStopRadius);
            float resumeRadius = Mathf.Max(stopRadius, zoneResumeRadius);

            if (_zoneHolding)
            {
                if (distance > resumeRadius)
                {
                    _zoneHolding = false;
                }
            }
            else if (distance <= stopRadius)
            {
                _zoneHolding = true;
            }

            if (_zoneHolding || distance <= 0.0001f)
            {
                return false;
            }

            moveDirection = toCenter / distance;
            return true;
        }

        private bool TryGetZoneCenter(out Vector3 center)
        {
            if (zoneCollider != null)
            {
                center = zoneCollider.bounds.center + zoneCenterOffset;
                center.y = transform.position.y;
                return true;
            }

            if (zoneTransform != null)
            {
                center = zoneTransform.position + zoneCenterOffset;
                center.y = transform.position.y;
                return true;
            }

            if (TryGetAutoSafeZoneCenter(out center))
            {
                center += zoneCenterOffset;
                center.y = transform.position.y;
                return true;
            }

            center = Vector3.zero;
            return false;
        }

        private bool TryGetAutoSafeZoneCenter(out Vector3 center)
        {
            if (_autoSafeZoneController == null)
            {
                _autoSafeZoneController = FindObjectOfType<SafeZoneController>(true);
            }

            if (_autoSafeZoneController != null && _autoSafeZoneController.IsZoneActive)
            {
                center = _autoSafeZoneController.ZoneCenter;
                return true;
            }

            center = Vector3.zero;
            return false;
        }

        private bool ShouldDriveThroughPlayerController()
        {
            return usePlayerControllerWhenAvailable
                && _ballController != null
                && _ballController.Runner != null
                && _ballController.Object != null
                && _ballController.Object.IsInSimulation
                && (_ballController.HasStateAuthority || _ballController.HasInputAuthority);
        }

        private bool IsNetworkedWithoutLocalAuthority()
        {
            return _ballController != null
                && _ballController.Runner != null
                && _ballController.Object != null
                && _ballController.Object.IsInSimulation
                && !_ballController.HasStateAuthority
                && !_ballController.HasInputAuthority;
        }

        private void ApplyStandaloneMovement(
            Vector3 targetHorizontalVelocity,
            bool hasMoveInput,
            bool hardBrake,
            float deltaTime)
        {
            if (deltaTime <= 0f)
            {
                return;
            }

            Vector3 velocity = _rigidbody.linearVelocity;
            Vector3 horizontalVelocity = new Vector3(velocity.x, 0f, velocity.z);

            if (IsGrounded())
            {
                ApplyStandaloneGroundMovement(
                    targetHorizontalVelocity,
                    horizontalVelocity,
                    hasMoveInput,
                    hardBrake,
                    deltaTime);
                return;
            }

            Vector3 airAcceleration = ComputeAirAcceleration(targetHorizontalVelocity, horizontalVelocity);
            ApplyAcceleration(airAcceleration, deltaTime);
            ApplyAdditionalFallForces(velocity.y);
        }

        private void ApplyStandaloneGroundMovement(
            Vector3 targetHorizontalVelocity,
            Vector3 horizontalVelocity,
            bool hasMoveInput,
            bool hardBrake,
            float deltaTime)
        {
            Vector3 requiredAcceleration = Vector3.zero;
            float maxSpeed = GetMaxSpeed();

            if (hasMoveInput && targetHorizontalVelocity.sqrMagnitude > 0.0001f)
            {
                Vector3 desiredDirection = targetHorizontalVelocity.normalized;
                float input01 = Mathf.Clamp01(targetHorizontalVelocity.magnitude / Mathf.Max(0.01f, maxSpeed));
                float speedAlongDesired = Vector3.Dot(horizontalVelocity, desiredDirection);
                float forwardSpeed = Mathf.Max(0f, speedAlongDesired);

                float driveAccelerationMagnitude = EvaluateDriveAccelerationMagnitude(forwardSpeed, input01);
                Vector3 forwardAcceleration = desiredDirection * driveAccelerationMagnitude;

                Vector3 desiredVelocity = desiredDirection * (maxSpeed * input01);
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
                    float deceleration = GetBraking() * (hardBrake ? GetHardBrakeMultiplier() : 1f);
                    requiredAcceleration = -horizontalVelocity / speed * deceleration;
                }
            }

            requiredAcceleration += EvaluateRollingDragAcceleration(horizontalVelocity, hasMoveInput);
            ApplyAcceleration(requiredAcceleration, deltaTime);
            ApplySoftSpeedLimit(deltaTime);
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

            float input01 = Mathf.Clamp01(targetHorizontalVelocity.magnitude / Mathf.Max(0.01f, GetMaxSpeed()));
            float limitScale = Mathf.Clamp01((speedCap - speedAlongInput) / speedCap);
            float speed01 = Mathf.Clamp01(Mathf.Max(0f, speedAlongInput) / speedCap);
            float curveFactor = EvaluateDriveCurve(speed01);
            float accelerationMagnitude = GetInitialAccelerationResponse() * airControlMultiplier * input01 * limitScale * curveFactor;
            return inputDirection * Mathf.Max(0f, accelerationMagnitude);
        }

        private float EvaluateDriveAccelerationMagnitude(float forwardSpeed, float input01)
        {
            float clampedInput = Mathf.Clamp01(input01);
            if (clampedInput <= 0f)
            {
                return 0f;
            }

            float maxSpeed = Mathf.Max(0.01f, GetMaxSpeed());
            float speed01 = Mathf.Clamp01(Mathf.Max(0f, forwardSpeed) / maxSpeed);
            float driveAcceleration = GetInitialAccelerationResponse() * EvaluateDriveCurve(speed01);

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
            float shapedSpeed = Mathf.Pow(clampedSpeed, Mathf.Max(0.01f, GetAccelerationPower()));
            float remaining = Mathf.Clamp01(1f - shapedSpeed);
            return Mathf.Pow(remaining, Mathf.Max(0.01f, GetTopSpeedApproachStrength()));
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
            return -horizontalVelocity / speed * (dragCoefficient * speed * inputDragScale);
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
                case ForceMode.Impulse:
                    _rigidbody.AddForce(accelerationVector * _rigidbody.mass * deltaTime, ForceMode.Impulse);
                    break;
                case ForceMode.VelocityChange:
                    _rigidbody.AddForce(accelerationVector * deltaTime, ForceMode.VelocityChange);
                    break;
                case ForceMode.Acceleration:
                default:
                    _rigidbody.AddForce(accelerationVector, ForceMode.Acceleration);
                    break;
            }
        }

        private void ApplySoftSpeedLimit(float deltaTime)
        {
            if (deltaTime <= 0f)
            {
                return;
            }

            Vector3 velocity = _rigidbody.linearVelocity;
            Vector3 horizontalVelocity = new Vector3(velocity.x, 0f, velocity.z);
            float speed = horizontalVelocity.magnitude;
            float speedLimit = Mathf.Max(0.01f, GetMaxSpeed());

            if (speed <= speedLimit)
            {
                return;
            }

            float overspeed = speed - speedLimit;
            float correctionAcceleration = Mathf.Min(overspeed / deltaTime, GetBraking());
            if (correctionAcceleration <= 0f)
            {
                return;
            }

            ApplyAcceleration(-horizontalVelocity / speed * correctionAcceleration, deltaTime);
        }

        private void ApplyAdditionalFallForces(float verticalVelocity)
        {
            if (verticalVelocity >= 0f)
            {
                return;
            }

            Vector3 gravity = Physics.gravity;
            float gravityMagnitude = gravity.magnitude;
            if (gravityMagnitude <= 0.0001f)
            {
                return;
            }

            float extraGravity = gravityMagnitude * Mathf.Max(0f, fallGravityMultiplier - 1f);
            if (extraGravity > 0f)
            {
                _rigidbody.AddForce(gravity.normalized * extraGravity, ForceMode.Acceleration);
            }
        }

        private bool IsGrounded()
        {
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

        private void ApplyRotation(Vector3 moveDirection, float deltaTime)
        {
            if (deltaTime <= 0f)
            {
                return;
            }

            switch (rotationMode)
            {
                case RotationMode.FaceMoveDirectionYaw:
                    Vector3 faceDirection = moveDirection;
                    if (faceDirection.sqrMagnitude < 0.0001f)
                    {
                        Vector3 velocity = _rigidbody.linearVelocity;
                        faceDirection = new Vector3(velocity.x, 0f, velocity.z);
                    }

                    if (faceDirection.sqrMagnitude < 0.0001f)
                    {
                        return;
                    }

                    Quaternion targetRotation = Quaternion.LookRotation(faceDirection.normalized, Vector3.up);
                    Quaternion nextRotation = Quaternion.RotateTowards(
                        _rigidbody.rotation,
                        targetRotation,
                        Mathf.Max(0f, faceMoveDirectionTurnSpeed) * deltaTime);
                    _rigidbody.MoveRotation(nextRotation);
                    break;

                case RotationMode.ManualAngularVelocity:
                    Vector3 radians = angularVelocityDegreesPerSecond * Mathf.Deg2Rad;
                    if (angularVelocitySpace == Space.Self)
                    {
                        radians = transform.TransformDirection(radians);
                    }

                    _rigidbody.angularVelocity = radians;
                    break;
            }
        }

        private float GetMaxSpeed()
        {
            float baseMaxSpeed = accelerationConfig != null ? accelerationConfig.MaxSpeed : FallbackMaxSpeed;
            return Mathf.Max(0.01f, baseMaxSpeed * GetMovementSpeedMultiplier());
        }

        private float GetMinMoveSpeed()
        {
            float baseMinMoveSpeed = accelerationConfig != null ? accelerationConfig.MinMoveSpeed : FallbackMinMoveSpeed;
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
            return accelerationConfig != null ? accelerationConfig.AccelerationPower : FallbackAccelerationPower;
        }

        private float GetTopSpeedApproachStrength()
        {
            return accelerationConfig != null ? accelerationConfig.TopSpeedApproachStrength : FallbackTopSpeedApproachStrength;
        }

        private float GetBraking()
        {
            float baseBraking = accelerationConfig != null ? accelerationConfig.Braking : FallbackBraking;
            return baseBraking * GetMovementSpeedMultiplier();
        }

        private float GetHardBrakeMultiplier()
        {
            return accelerationConfig != null ? accelerationConfig.HardBrakeMultiplier : FallbackHardBrakeMultiplier;
        }

        private float GetRollingDrag()
        {
            return accelerationConfig != null ? accelerationConfig.RollingDrag : FallbackRollingDrag;
        }

        private float GetSteeringResponsiveness()
        {
            return accelerationConfig != null
                ? accelerationConfig.SteeringResponsiveness
                : FallbackSteeringResponsiveness;
        }

        private float GetMovementSpeedMultiplier()
        {
            return Mathf.Max(0.01f, movementSpeedMultiplier);
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

            if (_ballController == null)
            {
                _ballController = GetComponent<SumoBallController>();
            }

            if (accelerationConfig == null)
            {
                accelerationConfig = GetComponent<SumoAccelerationConfig>();
            }

            if (physicsConfig == null)
            {
                physicsConfig = GetComponent<SumoBallPhysicsConfig>();
            }
        }

        private void DisablePlayerComponentsIfNeeded()
        {
            if (!disablePlayerOnlyComponents)
            {
                return;
            }

            // Keep Fusion NetworkBehaviour components enabled until the object is registered.
            // Disabling them in Awake can leave scene NetworkObjects deactivated on clients.
            SetComponentEnabled(GetComponent<SumoCameraFollow>(), false);
            SetComponentEnabled(GetComponent<SumoSpeedometerUI>(), false);
        }

        private void EnsureNpcGameplaySurfaceEnabled()
        {
            if (!disablePlayerOnlyComponents)
            {
                return;
            }

            if (_rigidbody != null)
            {
                _rigidbody.detectCollisions = true;
            }

            Collider[] colliders = GetComponentsInChildren<Collider>(includeInactive: true);
            for (int i = 0; i < colliders.Length; i++)
            {
                if (colliders[i] != null)
                {
                    colliders[i].enabled = true;
                }
            }

            Renderer[] renderers = GetComponentsInChildren<Renderer>(includeInactive: true);
            for (int i = 0; i < renderers.Length; i++)
            {
                if (renderers[i] != null)
                {
                    renderers[i].enabled = true;
                }
            }
        }

        private static void SetComponentEnabled(Behaviour component, bool enabled)
        {
            if (component != null)
            {
                component.enabled = enabled;
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

        private void OnValidate()
        {
            targetSpeed = Mathf.Max(0f, targetSpeed);
            inputStrength = Mathf.Clamp01(inputStrength);
            zoneStopRadius = Mathf.Max(0f, zoneStopRadius);
            zoneResumeRadius = Mathf.Max(zoneStopRadius, zoneResumeRadius);
            movementSpeedMultiplier = Mathf.Max(0.01f, movementSpeedMultiplier);
            groundCheckDistance = Mathf.Max(0f, groundCheckDistance);
            minGroundNormalDot = Mathf.Clamp01(minGroundNormalDot);
            airControlMultiplier = Mathf.Max(0f, airControlMultiplier);
            airMaxSpeedMultiplier = Mathf.Max(0f, airMaxSpeedMultiplier);
            fallGravityMultiplier = Mathf.Max(1f, fallGravityMultiplier);
            faceMoveDirectionTurnSpeed = Mathf.Max(0f, faceMoveDirectionTurnSpeed);

            if (Application.isPlaying)
            {
                CacheComponents();
                RefreshScaledRadius();
            }
        }

        private void OnDrawGizmosSelected()
        {
            if (!drawGizmos)
            {
                return;
            }

            Gizmos.color = directionGizmoColor;
            if (TryResolveMoveDirectionForGizmo(out Vector3 moveDirection))
            {
                Vector3 origin = transform.position;
                Gizmos.DrawLine(origin, origin + moveDirection * Mathf.Max(0.5f, targetSpeed * 0.2f));
            }

            if (TryGetZoneCenterForGizmo(out Vector3 zoneCenter))
            {
                Gizmos.color = zoneGizmoColor;
                DrawWireCircle(zoneCenter, Mathf.Max(0f, zoneStopRadius));
                DrawWireCircle(zoneCenter, Mathf.Max(zoneStopRadius, zoneResumeRadius));
            }
        }

        private bool TryResolveMoveDirectionForGizmo(out Vector3 moveDirection)
        {
            moveDirection = Vector3.zero;

            if (moveTowardZone && TryGetZoneCenterForGizmo(out Vector3 center))
            {
                moveDirection = center - transform.position;
            }
            else
            {
                switch (directionMode)
                {
                    case DirectionMode.LocalDirection:
                        moveDirection = transform.TransformDirection(localDirection);
                        break;
                    case DirectionMode.TowardTransform:
                        if (directionTarget != null)
                        {
                            moveDirection = directionTarget.position - transform.position;
                        }
                        break;
                    case DirectionMode.WorldDirection:
                    default:
                        moveDirection = worldDirection;
                        break;
                }
            }

            moveDirection.y = 0f;
            if (moveDirection.sqrMagnitude < 0.0001f)
            {
                return false;
            }

            moveDirection.Normalize();
            return true;
        }

        private bool TryGetZoneCenterForGizmo(out Vector3 center)
        {
            if (zoneCollider != null)
            {
                center = zoneCollider.bounds.center + zoneCenterOffset;
                return true;
            }

            if (zoneTransform != null)
            {
                center = zoneTransform.position + zoneCenterOffset;
                return true;
            }

            if (TryGetAutoSafeZoneCenter(out center))
            {
                center += zoneCenterOffset;
                return true;
            }

            center = Vector3.zero;
            return false;
        }

        private static void DrawWireCircle(Vector3 center, float radius)
        {
            if (radius <= 0f)
            {
                Gizmos.DrawWireSphere(center, 0.05f);
                return;
            }

            const int segments = 48;
            Vector3 previous = center + new Vector3(radius, 0f, 0f);
            for (int i = 1; i <= segments; i++)
            {
                float angle = i * Mathf.PI * 2f / segments;
                Vector3 next = center + new Vector3(Mathf.Cos(angle) * radius, 0f, Mathf.Sin(angle) * radius);
                Gizmos.DrawLine(previous, next);
                previous = next;
            }
        }

        private bool WasStopStartPressed()
        {
            return WasKeyPressedThisFrame(stopStartBind);
        }

        internal static bool WasKeyPressedThisFrame(KeyCode keyCode)
        {
            if (keyCode == KeyCode.None)
            {
                return false;
            }

#if ENABLE_INPUT_SYSTEM
            if (TryReadInputSystemButtonDown(keyCode))
            {
                return true;
            }
#endif

#if ENABLE_LEGACY_INPUT_MANAGER
            return Input.GetKeyDown(keyCode);
#else
            return false;
#endif
        }

#if ENABLE_INPUT_SYSTEM
        private static bool TryReadInputSystemButtonDown(KeyCode keyCode)
        {
            if (TryReadInputSystemMouseButtonDown(keyCode))
            {
                return true;
            }

            Keyboard keyboard = Keyboard.current;
            if (keyboard == null || !TryConvertToInputSystemKey(keyCode, out Key key) || key == Key.None)
            {
                return false;
            }

            return keyboard[key].wasPressedThisFrame;
        }

        private static bool TryReadInputSystemMouseButtonDown(KeyCode keyCode)
        {
            Mouse mouse = Mouse.current;
            if (mouse == null)
            {
                return false;
            }

            switch (keyCode)
            {
                case KeyCode.Mouse0:
                    return mouse.leftButton.wasPressedThisFrame;
                case KeyCode.Mouse1:
                    return mouse.rightButton.wasPressedThisFrame;
                case KeyCode.Mouse2:
                    return mouse.middleButton.wasPressedThisFrame;
                case KeyCode.Mouse3:
                    return mouse.backButton.wasPressedThisFrame;
                case KeyCode.Mouse4:
                    return mouse.forwardButton.wasPressedThisFrame;
                default:
                    return false;
            }
        }

        private static bool TryConvertToInputSystemKey(KeyCode keyCode, out Key key)
        {
            if (keyCode >= KeyCode.A && keyCode <= KeyCode.Z)
            {
                key = (Key)((int)Key.A + keyCode - KeyCode.A);
                return true;
            }

            if (keyCode >= KeyCode.Alpha0 && keyCode <= KeyCode.Alpha9)
            {
                key = (Key)((int)Key.Digit0 + keyCode - KeyCode.Alpha0);
                return true;
            }

            if (keyCode >= KeyCode.Keypad0 && keyCode <= KeyCode.Keypad9)
            {
                key = (Key)((int)Key.Numpad0 + keyCode - KeyCode.Keypad0);
                return true;
            }

            if (keyCode >= KeyCode.F1 && keyCode <= KeyCode.F12)
            {
                key = (Key)((int)Key.F1 + keyCode - KeyCode.F1);
                return true;
            }

            switch (keyCode)
            {
                case KeyCode.Space:
                    key = Key.Space;
                    return true;
                case KeyCode.Return:
                    key = Key.Enter;
                    return true;
                case KeyCode.KeypadEnter:
                    key = Key.NumpadEnter;
                    return true;
                case KeyCode.Tab:
                    key = Key.Tab;
                    return true;
                case KeyCode.Escape:
                    key = Key.Escape;
                    return true;
                case KeyCode.Backspace:
                    key = Key.Backspace;
                    return true;
                case KeyCode.Delete:
                    key = Key.Delete;
                    return true;
                case KeyCode.Insert:
                    key = Key.Insert;
                    return true;
                case KeyCode.Home:
                    key = Key.Home;
                    return true;
                case KeyCode.End:
                    key = Key.End;
                    return true;
                case KeyCode.PageUp:
                    key = Key.PageUp;
                    return true;
                case KeyCode.PageDown:
                    key = Key.PageDown;
                    return true;
                case KeyCode.UpArrow:
                    key = Key.UpArrow;
                    return true;
                case KeyCode.DownArrow:
                    key = Key.DownArrow;
                    return true;
                case KeyCode.LeftArrow:
                    key = Key.LeftArrow;
                    return true;
                case KeyCode.RightArrow:
                    key = Key.RightArrow;
                    return true;
                case KeyCode.LeftShift:
                    key = Key.LeftShift;
                    return true;
                case KeyCode.RightShift:
                    key = Key.RightShift;
                    return true;
                case KeyCode.LeftControl:
                    key = Key.LeftCtrl;
                    return true;
                case KeyCode.RightControl:
                    key = Key.RightCtrl;
                    return true;
                case KeyCode.LeftAlt:
                    key = Key.LeftAlt;
                    return true;
                case KeyCode.RightAlt:
                    key = Key.RightAlt;
                    return true;
                case KeyCode.Minus:
                    key = Key.Minus;
                    return true;
                case KeyCode.Equals:
                    key = Key.Equals;
                    return true;
                case KeyCode.LeftBracket:
                    key = Key.LeftBracket;
                    return true;
                case KeyCode.RightBracket:
                    key = Key.RightBracket;
                    return true;
                case KeyCode.Backslash:
                    key = Key.Backslash;
                    return true;
                case KeyCode.Semicolon:
                    key = Key.Semicolon;
                    return true;
                case KeyCode.Quote:
                    key = Key.Quote;
                    return true;
                case KeyCode.Comma:
                    key = Key.Comma;
                    return true;
                case KeyCode.Period:
                    key = Key.Period;
                    return true;
                case KeyCode.Slash:
                    key = Key.Slash;
                    return true;
                case KeyCode.BackQuote:
                    key = Key.Backquote;
                    return true;
                case KeyCode.KeypadDivide:
                    key = Key.NumpadDivide;
                    return true;
                case KeyCode.KeypadMultiply:
                    key = Key.NumpadMultiply;
                    return true;
                case KeyCode.KeypadMinus:
                    key = Key.NumpadMinus;
                    return true;
                case KeyCode.KeypadPlus:
                    key = Key.NumpadPlus;
                    return true;
                case KeyCode.KeypadPeriod:
                    key = Key.NumpadPeriod;
                    return true;
                case KeyCode.CapsLock:
                    key = Key.CapsLock;
                    return true;
                case KeyCode.Numlock:
                    key = Key.NumLock;
                    return true;
                case KeyCode.ScrollLock:
                    key = Key.ScrollLock;
                    return true;
                case KeyCode.Pause:
                    key = Key.Pause;
                    return true;
                case KeyCode.Print:
                    key = Key.PrintScreen;
                    return true;
                default:
                    key = Key.None;
                    return false;
            }
        }
#endif
    }
}
