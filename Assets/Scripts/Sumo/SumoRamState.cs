using UnityEngine;

namespace Sumo
{
    public enum SumoPairState : byte
    {
        None = 0,
        InitialImpact = 1,
        Ramming = 2,
        RamDepleted = 3,
        ReengageReady = 4
    }

    public enum SumoTieResolvedBy : byte
    {
        None = 0,
        SpeedDelta = 1,
        ExistingOwner = 2,
        KeyOrderFallback = 3,
        NeutralWithinEpsilon = 4
    }

    public struct SumoRamState
    {
        public long PairKey;
        public int OwnerKey;
        public int FirstRef;
        public int SecondRef;
        public int AttackerRef;
        public int VictimRef;
        public int CreatedTick;
        public int StartTick;
        public int LastContactTick;
        public int LastImpactTick;
        public int LastRamTick;
        public int LastEnterTick;
        public int LastProcessedEnterTick;
        public int PendingEnterTick;
        public int BreakStartTick;
        public int ReengageReadyTick;
        public int ImpactLatchTick;
        public int MaxRamDurationTicks;
        public bool HasPendingEnter;
        public bool ReimpactSuppressedUntilHardBreak;
        public SumoPairState State;
        public SumoTieResolvedBy TieResolvedBy;
        public float EnterFirstApproachSpeed;
        public float EnterSecondApproachSpeed;
        public float EnterRelativeClosingSpeed;
        public float InitialImpactSpeed;
        public float InitialImpulse;
        public float InitialImpactDuration;
        public float InitialImpactElapsed;
        public float InitialVictimDeltaV;
        public float InitialAttackerDeltaV;
        public float RamContactBlend;
        public float InitialRamEnergy;
        public float RamEnergy;
        public float MaxSeparationSinceBreak;
        public float DirectionDot;
        public Vector3 ContactPoint;
        public Vector3 ContactNormal;
        public Vector3 ContactDirection;
        public SumoCollisionController FirstController;
        public SumoCollisionController SecondController;
        public SumoCollisionController AttackerController;
        public SumoCollisionController VictimController;

        public bool HasPairControllers => FirstRef != 0 && SecondRef != 0;
        public bool HasAttacker => AttackerRef != 0 && VictimRef != 0;
    }
}
