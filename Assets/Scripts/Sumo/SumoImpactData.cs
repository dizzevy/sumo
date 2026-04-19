using Fusion;
using UnityEngine;

namespace Sumo
{
    public readonly struct SumoImpactData
    {
        public readonly NetworkObject Attacker;
        public readonly NetworkObject Victim;
        public readonly Vector3 ImpactDirection;
        public readonly Vector3 RelativeVelocity;
        public readonly float ImpactSpeed;
        public readonly float RelativeClosingSpeed;
        public readonly float SpeedStrength;
        public readonly float AngleMultiplier;
        public readonly float DashMultiplier;
        public readonly float FinalImpulse;
        public readonly int Tick;
        public readonly Vector3 ContactPoint;

        public SumoImpactData(
            NetworkObject attacker,
            NetworkObject victim,
            Vector3 impactDirection,
            Vector3 relativeVelocity,
            float impactSpeed,
            float relativeClosingSpeed,
            float speedStrength,
            float angleMultiplier,
            float dashMultiplier,
            float finalImpulse,
            int tick,
            Vector3 contactPoint)
        {
            Attacker = attacker;
            Victim = victim;
            ImpactDirection = impactDirection;
            RelativeVelocity = relativeVelocity;
            ImpactSpeed = impactSpeed;
            RelativeClosingSpeed = relativeClosingSpeed;
            SpeedStrength = speedStrength;
            AngleMultiplier = angleMultiplier;
            DashMultiplier = dashMultiplier;
            FinalImpulse = finalImpulse;
            Tick = tick;
            ContactPoint = contactPoint;
        }
    }
}
