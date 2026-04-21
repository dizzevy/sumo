using System;
using UnityEngine;

namespace Sumo
{
    [DisallowMultipleComponent]
    public sealed class SumoVisualSmoothing : MonoBehaviour
    {
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
                        DeadZone = 0.002f,
                        SmallError = 0.03f,
                        MediumError = 0.16f,
                        SmallSharpness = 20f,
                        MediumSharpness = 26f,
                        LargeSharpness = 34f,
                        SmallCatchUpSpeed = 8f,
                        MediumCatchUpSpeed = 16f,
                        LargeCatchUpSpeed = 34f,
                        HardSnapDistance = 2.75f
                    },
                    Rotation = new RotationSettings
                    {
                        DeadZoneDegrees = 0.2f,
                        SmallErrorDegrees = 1.5f,
                        MediumErrorDegrees = 7.5f,
                        SmallSharpness = 18f,
                        MediumSharpness = 24f,
                        LargeSharpness = 30f,
                        SmallCatchUpDegreesPerSecond = 140f,
                        MediumCatchUpDegreesPerSecond = 280f,
                        LargeCatchUpDegreesPerSecond = 620f,
                        HardSnapDegrees = 130f
                    }
                };
            }

            public static SmoothingProfile CreateProxyDefault()
            {
                return new SmoothingProfile
                {
                    Position = new PositionSettings
                    {
                        DeadZone = 0.002f,
                        SmallError = 0.04f,
                        MediumError = 0.22f,
                        SmallSharpness = 12f,
                        MediumSharpness = 17f,
                        LargeSharpness = 24f,
                        SmallCatchUpSpeed = 6f,
                        MediumCatchUpSpeed = 13f,
                        LargeCatchUpSpeed = 30f,
                        HardSnapDistance = 6.5f
                    },
                    Rotation = new RotationSettings
                    {
                        DeadZoneDegrees = 0.25f,
                        SmallErrorDegrees = 2.2f,
                        MediumErrorDegrees = 10f,
                        SmallSharpness = 10f,
                        MediumSharpness = 15f,
                        LargeSharpness = 22f,
                        SmallCatchUpDegreesPerSecond = 110f,
                        MediumCatchUpDegreesPerSecond = 220f,
                        LargeCatchUpDegreesPerSecond = 520f,
                        HardSnapDegrees = 220f
                    }
                };
            }

            public static SmoothingProfile CreateLocalVictimDefault()
            {
                return new SmoothingProfile
                {
                    Position = new PositionSettings
                    {
                        DeadZone = 0.003f,
                        SmallError = 0.08f,
                        MediumError = 0.42f,
                        SmallSharpness = 4.5f,
                        MediumSharpness = 7f,
                        LargeSharpness = 12f,
                        SmallCatchUpSpeed = 3.5f,
                        MediumCatchUpSpeed = 8f,
                        LargeCatchUpSpeed = 19f,
                        HardSnapDistance = 20f
                    },
                    Rotation = new RotationSettings
                    {
                        DeadZoneDegrees = 0.3f,
                        SmallErrorDegrees = 3.4f,
                        MediumErrorDegrees = 16f,
                        SmallSharpness = 4f,
                        MediumSharpness = 6.5f,
                        LargeSharpness = 11f,
                        SmallCatchUpDegreesPerSecond = 65f,
                        MediumCatchUpDegreesPerSecond = 135f,
                        LargeCatchUpDegreesPerSecond = 320f,
                        HardSnapDegrees = 320f
                    }
                };
            }

            public static SmoothingProfile CreateProxyVictimDefault()
            {
                return new SmoothingProfile
                {
                    Position = new PositionSettings
                    {
                        DeadZone = 0.003f,
                        SmallError = 0.1f,
                        MediumError = 0.5f,
                        SmallSharpness = 3.5f,
                        MediumSharpness = 6f,
                        LargeSharpness = 10f,
                        SmallCatchUpSpeed = 3f,
                        MediumCatchUpSpeed = 7f,
                        LargeCatchUpSpeed = 17f,
                        HardSnapDistance = 24f
                    },
                    Rotation = new RotationSettings
                    {
                        DeadZoneDegrees = 0.35f,
                        SmallErrorDegrees = 4f,
                        MediumErrorDegrees = 18f,
                        SmallSharpness = 3.2f,
                        MediumSharpness = 5.4f,
                        LargeSharpness = 9.2f,
                        SmallCatchUpDegreesPerSecond = 55f,
                        MediumCatchUpDegreesPerSecond = 120f,
                        LargeCatchUpDegreesPerSecond = 290f,
                        HardSnapDegrees = 330f
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

            if (profile.Position.HardSnapDistance > 0f && error >= profile.Position.HardSnapDistance)
            {
                float fastCatchUpSpeed = Mathf.Max(profile.Position.LargeCatchUpSpeed * 2f, profile.Position.MediumCatchUpSpeed);
                float fastStep = Mathf.Max(0.01f, fastCatchUpSpeed) * deltaTime;
                visual.position = Vector3.MoveTowards(currentPosition, targetPosition, fastStep);
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

            if (profile.Rotation.HardSnapDegrees > 0f && error >= profile.Rotation.HardSnapDegrees)
            {
                float fastCatchUpDegrees = Mathf.Max(profile.Rotation.LargeCatchUpDegreesPerSecond * 2f, profile.Rotation.MediumCatchUpDegreesPerSecond);
                float fastStepDegrees = Mathf.Max(1f, fastCatchUpDegrees) * deltaTime;
                visual.rotation = Quaternion.RotateTowards(currentRotation, targetRotation, fastStepDegrees);
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
