namespace Sumo.Gameplay
{
    public enum MatchState
    {
        WaitingForPlayers = 0,
        PreRoundBox = 1,
        Countdown = 2,
        DropPlayers = 3,
        SafeZonePhase = 4,
        EliminateOutsideZone = 5,
        NextZone = 6,
        RoundFinished = 7,
        ResetRound = 8
    }
}
