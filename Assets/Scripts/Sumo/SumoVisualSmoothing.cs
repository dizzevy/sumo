using System;
using UnityEngine;

namespace Sumo
{
    [DisallowMultipleComponent]
    public sealed class SumoVisualSmoothing : MonoBehaviour
    {
        private const float EmergencyPositionSnapDistance = 25f;
        private const float EmergencyRotationSnapDegrees = 170f;

        [Serializable]
        public struct PositionSettings
        {
            public float DeadZone;
            public float SmallError;
            public float MediumError;
            public float SmallSharpness;
            public float MediumSharpness;
            public float LargeSharpness;
            public float SmallCatchUpSpeed;
            public float MediumCatchUpSpeed;
            public float LargeCatchUpSpeed;
            public float HardSnapDistance;
        }

        [Serializable]
        public struct RotationSettings
        {
            public float DeadZoneDegrees;
            public float SmallErrorDegrees;
            public float MediumErrorDegrees;
            public float SmallSharpness;
            public float MediumSharpness;
            public float LargeSharpness;
            public float SmallCatchUpDegreesPerSecond;
            public float MediumCatchUpDegreesPerSecond;
            public float LargeCatchUpDegreesPerSecond;
            public float HardSnapDegrees;
        }

        [Serializable]
        public struct SmoothingProfile
        {
            public PositionSettings Position;
            public RotationSettings Rotation;

            public static SmoothingProfile CreateLocalDefault()
            {
                return new SmoothingProfile
                {
                    Position = new PositionSettings
                    {
                        DeadZone = 0.0035f,
                        SmallError = 0.08f,
                        MediumError = 0.42f,
                        SmallSharpness = 3.2f,
                        MediumSharpness = 5.2f,
                        LargeSharpness = 8.4f,
                        SmallCatchUpSpeed = 2.4f,
                        MediumCatchUpSpeed = 5.8f,
                        LargeCatchUpSpeed = 14f,
                        HardSnapDistance = 60f
                    },
                    Rotation = new RotationSettings
                    {
                        DeadZoneDegrees = 0.5f,
                        SmallErrorDegrees = 5f,
                        MediumErrorDegrees = 24f,
                        SmallSharpness = 2.8f,
                        MediumSharpness = 4.4f,
                        LargeSharpness = 7.8f,
                        SmallCatchUpDegreesPerSecond = 30f,
                        MediumCatchUpDegreesPerSecond = 78f,
                        LargeCatchUpDegreesPerSecond = 210f,
                        HardSnapDegrees = 350f
                    }
                };
            }

            public static SmoothingProfile CreateProxyDefault()
            {
                return new SmoothingProfile
                {
                    Position = new PositionSettings
                    {
                        DeadZone = 0.004f,
                        SmallError = 0.12f,
                        MediumError = 0.60f,
                        SmallSharpness = 2.6f,
                        MediumSharpness = 4.2f,
                        LargeSharpness = 7.2f,
                        SmallCatchUpSpeed = 2f,
                        MediumCatchUpSpeed = 5f,
                        LargeCatchUpSpeed = 13f,
                        HardSnapDistance = 70f
                    },
                    Rotation = new RotationSettings
                    {
                        DeadZoneDegrees = 0.6f,
                        SmallErrorDegrees = 6.5f,
                        MediumErrorDegrees = 30f,
                        SmallSharpness = 2.4f,
                        MediumSharpness = 3.8f,
                        LargeSharpness = 6.8f,
                        SmallCatchUpDegreesPerSecond = 25f,
                        MediumCatchUpDegreesPerSecond = 68f,
                        LargeCatchUpDegreesPerSecond = 185f,
                        HardSnapDegrees = 355f
                    }
                };
            }

            public static SmoothingProfile CreateLocalVictimDefault()
            {
                return new SmoothingProfile
                {
                    Position = new PositionSettings
                    {
                        DeadZone = 0.012f,
                        SmallError = 0.40f,
                        MediumError = 2.00f,
                        SmallSharpness = 0.75f,
                        MediumSharpness = 1.25f,
                        LargeSharpness = 2.40f,
                        SmallCatchUpSpeed = 0.55f,
                        MediumCatchUpSpeed = 1.10f,
                        LargeCatchUpSpeed = 3.20f,
                        HardSnapDistance = 80f
                    },
                    Rotation = new RotationSettings
                    {
                        DeadZoneDegrees = 1.8f,
                        SmallErrorDegrees = 18f,
                        MediumErrorDegrees = 80f,
                        SmallSharpness = 0.70f,
                        MediumSharpness = 1.20f,
                        LargeSharpness = 2.20f,
                        SmallCatchUpDegreesPerSecond = 12f,
                        MediumCatchUpDegreesPerSecond = 28f,
                        LargeCatchUpDegreesPerSecond = 85f,
                        HardSnapDegrees = 359f
                    }
                };
            }

            public static SmoothingProfile CreateProxyVictimDefault()
            {
                return new SmoothingProfile
                {
                    Position = new PositionSettings
                    {
                        DeadZone = 0.004f,
                        SmallError = 0.22f,
                        MediumError = 1.10f,
                        SmallSharpness = 1.2f,
                        MediumSharpness = 2.3f,
                        LargeSharpness = 4.5f,
                        SmallCatchUpSpeed = 1f,
                        MediumCatchUpSpeed = 2.4f,
                        LargeCatchUpSpeed = 6.5f,
                        HardSnapDistance = 90f
                    },
                    Rotation = new RotationSettings
                    {
                        DeadZoneDegrees = 0.6f,
                        SmallErrorDegrees = 9f,
                        MediumErrorDegrees = 40f,
                        SmallSharpness = 1.2f,
                        MediumSharpness = 2.2f,
                        LargeSharpness = 4.2f,
                        SmallCatchUpDegreesPerSecond = 20f,
                        MediumCatchUpDegreesPerSecond = 55f,
                        LargeCatchUpDegreesPerSecond = 150f,
                        HardSnapDegrees = 358f
                    }
                };
            }
        }

        [Header("Targets")]
        [SerializeField] private Transform source;
        [SerializeField] private Transform visual;

        [Header("Profile")]
        [SerializeField] private SmoothingProfile profile = default;

        [Header("Timing")]
        [SerializeField] private bool useUnscaledTime = true;
        [SerializeField] private bool snapOnEnable = true;

        private bool _initialized;

        public Transform Source => source;
        public Transform Visual => visual;
        public SmoothingProfile Profile => profile;

        private void Reset()
        {
            profile = SmoothingProfile.CreateProxyDefault();
        }

        private void OnEnable()
        {
            profile = Sanitize(profile);

            if (snapOnEnable)
            {
                SnapVisualToSource();
            }
        }

        public void SetTargets(Transform sourceTarget, Transform visualTarget, bool snapToSource)
        {
            source = sourceTarget;
            visual = visualTarget;
            _initialized = !snapToSource;

            if (snapToSource)
            {
                SnapVisualToSource();
            }
        }

        public void SetProfile(SmoothingProfile newProfile, bool snapNow = false)
        {
            profile = Sanitize(newProfile);

            if (snapNow)
            {
                SnapVisualToSource();
            }
        }

        public void SnapVisualToSource()
        {
            if (source == null || visual == null || ReferenceEquals(source, visual))
            {
                return;
            }

            visual.SetPositionAndRotation(source.position, source.rotation);
            _initialized = true;
        }

        private void LateUpdate()
        {
            if (source == null || visual == null || ReferenceEquals(source, visual))
            {
                return;
            }

            float deltaTime = useUnscaledTime ? Time.unscaledDeltaTime : Time.deltaTime;
            if (deltaTime <= 0f)
            {
                return;
            }

            if (!_initialized)
            {
                SnapVisualToSource();
                return;
            }

            SmoothPosition(deltaTime);
            SmoothRotation(deltaTime);
        }

        private void SmoothPosition(float deltaTime)
        {
            Vector3 currentPosition = visual.position;
            Vector3 targetPosition = source.position;
            Vector3 offset = targetPosition - currentPosition;
            float error = offset.magnitude;

            if (error <= profile.Position.DeadZone)
            {
                return;
            }

            if (error >= EmergencyPositionSnapDistance)
            {
                visual.position = targetPosition;
                return;
            }

            float sharpness = SelectByErrorBand(
                error,
                profile.Position.SmallError,
                profile.Position.MediumError,
                profile.Position.SmallSharpness,
                profile.Position.MediumSharpness,
                profile.Position.LargeSharpness);

            float blend = 1f - Mathf.Exp(-sharpness * deltaTime);
            Vector3 blendedTarget = Vector3.Lerp(currentPosition, targetPosition, blend);

            float maxCatchUpSpeed = SelectByErrorBand(
                error,
                profile.Position.SmallError,
                profile.Position.MediumError,
                profile.Position.SmallCatchUpSpeed,
                profile.Position.MediumCatchUpSpeed,
                profile.Position.LargeCatchUpSpeed);

            float maxStep = Mathf.Max(0.01f, maxCatchUpSpeed) * deltaTime;
            visual.position = Vector3.MoveTowards(currentPosition, blendedTarget, maxStep);
        }

        private void SmoothRotation(float deltaTime)
        {
            Quaternion currentRotation = visual.rotation;
            Quaternion targetRotation = source.rotation;
            float error = Quaternion.Angle(currentRotation, targetRotation);

            if (error <= profile.Rotation.DeadZoneDegrees)
            {
                return;
            }

            if (error >= EmergencyRotationSnapDegrees)
            {
                visual.rotation = targetRotation;
                return;
            }

            float sharpness = SelectByErrorBand(
                error,
                profile.Rotation.SmallErrorDegrees,
                profile.Rotation.MediumErrorDegrees,
                profile.Rotation.SmallSharpness,
                profile.Rotation.MediumSharpness,
                profile.Rotation.LargeSharpness);

            float blend = 1f - Mathf.Exp(-sharpness * deltaTime);
            Quaternion blendedRotation = Quaternion.Slerp(currentRotation, targetRotation, blend);

            float maxCatchUpDegrees = SelectByErrorBand(
                error,
                profile.Rotation.SmallErrorDegrees,
                profile.Rotation.MediumErrorDegrees,
                profile.Rotation.SmallCatchUpDegreesPerSecond,
                profile.Rotation.MediumCatchUpDegreesPerSecond,
                profile.Rotation.LargeCatchUpDegreesPerSecond);

            float maxStep = Mathf.Max(1f, maxCatchUpDegrees) * deltaTime;
            visual.rotation = Quaternion.RotateTowards(currentRotation, blendedRotation, maxStep);
        }

        private static float SelectByErrorBand(
            float error,
            float smallThreshold,
            float mediumThreshold,
            float smallValue,
            float mediumValue,
            float largeValue)
        {
            if (error <= smallThreshold)
            {
                return smallValue;
            }

            if (error <= mediumThreshold)
            {
                return mediumValue;
            }

            return largeValue;
        }

        private static SmoothingProfile Sanitize(SmoothingProfile value)
        {
            PositionSettings position = value.Position;
            position.DeadZone = Mathf.Max(0f, position.DeadZone);
            position.SmallError = Mathf.Max(position.DeadZone + 0.0001f, position.SmallError);
            position.MediumError = Mathf.Max(position.SmallError + 0.0001f, position.MediumError);
            position.SmallSharpness = Mathf.Max(0.01f, position.SmallSharpness);
            position.MediumSharpness = Mathf.Max(0.01f, position.MediumSharpness);
            position.LargeSharpness = Mathf.Max(0.01f, position.LargeSharpness);
            position.SmallCatchUpSpeed = Mathf.Max(0.01f, position.SmallCatchUpSpeed);
            position.MediumCatchUpSpeed = Mathf.Max(position.SmallCatchUpSpeed, position.MediumCatchUpSpeed);
            position.LargeCatchUpSpeed = Mathf.Max(position.MediumCatchUpSpeed, position.LargeCatchUpSpeed);
            position.HardSnapDistance = Mathf.Max(position.MediumError + 0.0001f, position.HardSnapDistance);

            RotationSettings rotation = value.Rotation;
            rotation.DeadZoneDegrees = Mathf.Max(0f, rotation.DeadZoneDegrees);
            rotation.SmallErrorDegrees = Mathf.Max(rotation.DeadZoneDegrees + 0.0001f, rotation.SmallErrorDegrees);
            rotation.MediumErrorDegrees = Mathf.Max(rotation.SmallErrorDegrees + 0.0001f, rotation.MediumErrorDegrees);
            rotation.SmallSharpness = Mathf.Max(0.01f, rotation.SmallSharpness);
            rotation.MediumSharpness = Mathf.Max(0.01f, rotation.MediumSharpness);
            rotation.LargeSharpness = Mathf.Max(0.01f, rotation.LargeSharpness);
            rotation.SmallCatchUpDegreesPerSecond = Mathf.Max(1f, rotation.SmallCatchUpDegreesPerSecond);
            rotation.MediumCatchUpDegreesPerSecond = Mathf.Max(rotation.SmallCatchUpDegreesPerSecond, rotation.MediumCatchUpDegreesPerSecond);
            rotation.LargeCatchUpDegreesPerSecond = Mathf.Max(rotation.MediumCatchUpDegreesPerSecond, rotation.LargeCatchUpDegreesPerSecond);
            rotation.HardSnapDegrees = Mathf.Clamp(rotation.HardSnapDegrees, rotation.MediumErrorDegrees + 0.0001f, 360f);

            value.Position = position;
            value.Rotation = rotation;
            return value;
        }

        private void OnValidate()
        {
            if (source == transform)
            {
                source = null;
            }

            if (visual == transform)
            {
                visual = null;
            }

            profile = Sanitize(profile);
        }
    }
}
