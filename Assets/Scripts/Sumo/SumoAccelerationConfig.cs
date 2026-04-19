using UnityEngine;

namespace Sumo
{
    [DisallowMultipleComponent]
    public sealed class SumoAccelerationConfig : MonoBehaviour
    {
        [Header("Top Speed")]
        [SerializeField] private float minMoveSpeed = 4.5f;
        [SerializeField] private float maxSpeed = 50f;

        [Header("Nonlinear Acceleration")]
        [SerializeField] private float initialAccelerationResponse = 36f;
        [SerializeField] private float accelerationPower = 2.2f;
        [SerializeField] private float topSpeedApproachStrength = 1.85f;

        [Header("Control")]
        [SerializeField] private float steeringResponsiveness = 9f;
        [SerializeField] private float braking = 22f;
        [SerializeField] private float hardBrakeMultiplier = 1.8f;
        [SerializeField] private float rollingDrag = 1.1f;

        public float MinMoveSpeed => minMoveSpeed;
        public float MaxSpeed => maxSpeed;
        public float InitialAccelerationResponse => initialAccelerationResponse;
        public float AccelerationPower => accelerationPower;
        public float TopSpeedApproachStrength => topSpeedApproachStrength;
        public float SteeringResponsiveness => steeringResponsiveness;
        public float Braking => braking;
        public float HardBrakeMultiplier => hardBrakeMultiplier;
        public float RollingDrag => rollingDrag;

        private void OnValidate()
        {
            minMoveSpeed = Mathf.Max(0f, minMoveSpeed);
            maxSpeed = Mathf.Max(0.01f, maxSpeed);
            initialAccelerationResponse = Mathf.Max(0f, initialAccelerationResponse);
            accelerationPower = Mathf.Max(0.01f, accelerationPower);
            topSpeedApproachStrength = Mathf.Max(0.01f, topSpeedApproachStrength);
            steeringResponsiveness = Mathf.Max(0.01f, steeringResponsiveness);
            braking = Mathf.Max(0f, braking);
            hardBrakeMultiplier = Mathf.Max(1f, hardBrakeMultiplier);
            rollingDrag = Mathf.Max(0f, rollingDrag);
        }
    }
}
