using Fusion;
using UnityEngine;

namespace Sumo
{
    public enum SumoInputButton
    {
        Brake = 0,
        Ability = 1
    }

    public struct SumoInputData : INetworkInput
    {
        public Vector2 Move;
        public float CameraYaw;
        public NetworkButtons Buttons;
        public int AbilitySequence;
    }

    public enum SumoPlayerClass
    {
        None = 0,
        Jumper = 1,
        Fatso = 2
    }

    public readonly struct SumoPlayerClassDefinition
    {
        public readonly SumoPlayerClass Class;
        public readonly string DisplayName;
        public readonly string AbilityName;
        public readonly string Description;
        public readonly string Stats;
        public readonly Color Color;
        public readonly float AbilityActiveSeconds;
        public readonly float AbilityRechargeSeconds;
        public readonly float JumpVelocityChange;
        public readonly float ScaleMultiplier;
        public readonly float SpeedMultiplier;
        public readonly float OutgoingPushMultiplier;
        public readonly float IncomingPushMultiplier;
        public readonly float PushSpeedFloorShare;
        public readonly float ShoveForceFloor;

        public SumoPlayerClassDefinition(
            SumoPlayerClass playerClass,
            string displayName,
            string abilityName,
            string description,
            string stats,
            Color color,
            float abilityActiveSeconds,
            float abilityRechargeSeconds,
            float jumpVelocityChange,
            float scaleMultiplier,
            float speedMultiplier,
            float outgoingPushMultiplier,
            float incomingPushMultiplier,
            float pushSpeedFloorShare,
            float shoveForceFloor)
        {
            Class = playerClass;
            DisplayName = displayName;
            AbilityName = abilityName;
            Description = description;
            Stats = stats;
            Color = color;
            AbilityActiveSeconds = abilityActiveSeconds;
            AbilityRechargeSeconds = abilityRechargeSeconds;
            JumpVelocityChange = jumpVelocityChange;
            ScaleMultiplier = scaleMultiplier;
            SpeedMultiplier = speedMultiplier;
            OutgoingPushMultiplier = outgoingPushMultiplier;
            IncomingPushMultiplier = incomingPushMultiplier;
            PushSpeedFloorShare = pushSpeedFloorShare;
            ShoveForceFloor = shoveForceFloor;
        }
    }

    public static class SumoPlayerClassCatalog
    {
        public const SumoPlayerClass DefaultClass = SumoPlayerClass.Jumper;

        private static readonly SumoPlayerClassDefinition JumperDefinition = new SumoPlayerClassDefinition(
            SumoPlayerClass.Jumper,
            "Jumper",
            "Jump Mode",
            "A light ball with a charged jump window.",
            "Ability: strong jumps on F\nActive: 10s\nRecharge: 30s\nJump height: high",
            new Color(1f, 0.35f, 0.52f, 1f),
            10f,
            30f,
            12f,
            1f,
            1f,
            1f,
            1f,
            0f,
            1f);

        private static readonly SumoPlayerClassDefinition FatsoDefinition = new SumoPlayerClassDefinition(
            SumoPlayerClass.Fatso,
            "Fatso",
            "Giant Size",
            "A heavy ball that becomes huge and hard to move.",
            "Ability: size x3\nSpeed: x1/3 while active\nIncoming push: x0.15\nOutgoing push: x5\nActive: 10s\nRecharge: 30s",
            new Color(1f, 0.54f, 0.16f, 1f),
            10f,
            30f,
            0f,
            3f,
            1f / 3f,
            5f,
            0.15f,
            0.9f,
            5f);

        public static int Count => 2;

        public static SumoPlayerClass Sanitize(SumoPlayerClass playerClass)
        {
            switch (playerClass)
            {
                case SumoPlayerClass.Jumper:
                case SumoPlayerClass.Fatso:
                    return playerClass;
                default:
                    return DefaultClass;
            }
        }

        public static SumoPlayerClass GetByIndex(int index)
        {
            int wrapped = ((index % Count) + Count) % Count;
            return wrapped == 0 ? SumoPlayerClass.Jumper : SumoPlayerClass.Fatso;
        }

        public static int GetIndex(SumoPlayerClass playerClass)
        {
            return Sanitize(playerClass) == SumoPlayerClass.Fatso ? 1 : 0;
        }

        public static SumoPlayerClassDefinition GetDefinition(SumoPlayerClass playerClass)
        {
            return Sanitize(playerClass) == SumoPlayerClass.Fatso
                ? FatsoDefinition
                : JumperDefinition;
        }

        public static SumoPlayerClass FromRaw(int rawClass)
        {
            return Sanitize((SumoPlayerClass)rawClass);
        }
    }
}
