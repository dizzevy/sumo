using Fusion;
using UnityEngine;

namespace Sumo
{
    public enum SumoCollisionTier : byte
    {
        None = 0,
        LowRam = 1,
        MidImpact = 2,
        HighImpact = 3
    }

    public readonly struct SumoImpactData
    {
        public readonly NetworkObject Attacker;
        public readonly NetworkObject Victim;
        public readonly SumoCollisionTier CollisionTier;
        public readonly Vector3 ImpactDirection;
        public readonly Vector3 RelativeVelocity;
        public readonly float ImpactSpeed;
        public readonly float ImpactSpeed01;
        public readonly float RelativeClosingSpeed;
        public readonly float SpeedStrength;
        public readonly float AngleMultiplier;
        public readonly float DashMultiplier;
        public readonly float FinalImpulse;
        public readonly float AttackerBackstepImpulse;
        public readonly float AttackerRecoverySeconds;
        public readonly int Tick;
        public readonly Vector3 ContactPoint;

        public SumoImpactData(
            NetworkObject attacker,
            NetworkObject victim,
            SumoCollisionTier collisionTier,
            Vector3 impactDirection,
            Vector3 relativeVelocity,
            float impactSpeed,
            float impactSpeed01,
            float relativeClosingSpeed,
            float speedStrength,
            float angleMultiplier,
            float dashMultiplier,
            float finalImpulse,
            float attackerBackstepImpulse,
            float attackerRecoverySeconds,
            int tick,
            Vector3 contactPoint)
        {
            Attacker = attacker;
            Victim = victim;
            CollisionTier = collisionTier;
            ImpactDirection = impactDirection;
            RelativeVelocity = relativeVelocity;
            ImpactSpeed = impactSpeed;
            ImpactSpeed01 = impactSpeed01;
            RelativeClosingSpeed = relativeClosingSpeed;
            SpeedStrength = speedStrength;
            AngleMultiplier = angleMultiplier;
            DashMultiplier = dashMultiplier;
            FinalImpulse = finalImpulse;
            AttackerBackstepImpulse = attackerBackstepImpulse;
            AttackerRecoverySeconds = attackerRecoverySeconds;
            Tick = tick;
            ContactPoint = contactPoint;
        }
    }
}
