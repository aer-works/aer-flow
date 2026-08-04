namespace Aer.Daemon;

public abstract record RoomTurnDecision
{
    private RoomTurnDecision() { }

    public sealed record StartUserTurn : RoomTurnDecision;
    public sealed record StartMachineTurn : RoomTurnDecision;
    public sealed record Wait(string Reason) : RoomTurnDecision;
    public sealed record Dormant : RoomTurnDecision;
}

public static class RoomTurnScheduler
{
    public static RoomTurnDecision Schedule(
        DateTimeOffset now,
        bool turnInFlight,
        bool userMessagePending,
        bool machineWakesPending,
        IReadOnlyList<DateTimeOffset> recentMachineTurnStarts,
        int consecutiveUncommittedTurns,
        RoomTurnThrottles throttles,
        bool isDormant)
    {
        ArgumentNullException.ThrowIfNull(recentMachineTurnStarts);
        ArgumentNullException.ThrowIfNull(throttles);

        if (turnInFlight)
        {
            return new RoomTurnDecision.Wait("Turn in flight");
        }

        if (isDormant || consecutiveUncommittedTurns >= throttles.ConsecutiveFailureLimit)
        {
            return new RoomTurnDecision.Dormant();
        }

        if (userMessagePending)
        {
            return new RoomTurnDecision.StartUserTurn();
        }

        if (machineWakesPending)
        {
            DateTimeOffset? lastMachineTurnStart = recentMachineTurnStarts.Count > 0
                ? recentMachineTurnStarts.Max()
                : null;

            if (lastMachineTurnStart.HasValue)
            {
                var gap = now - lastMachineTurnStart.Value;
                if (gap < throttles.MachineTurnMinimumGap)
                {
                    return new RoomTurnDecision.Wait(
                        $"Machine turn minimum gap ({throttles.MachineTurnMinimumGap.TotalSeconds}s) has not elapsed; {gap.TotalSeconds:F1}s elapsed");
                }
            }

            var hourlyWindowStart = now.AddHours(-1);
            int turnsInLastHour = recentMachineTurnStarts.Count(t => t >= hourlyWindowStart);
            if (turnsInLastHour >= throttles.MachineTurnsPerHour)
            {
                return new RoomTurnDecision.Wait(
                    $"Machine turn hourly limit ({throttles.MachineTurnsPerHour}) reached ({turnsInLastHour} turns in trailing hour)");
            }

            return new RoomTurnDecision.StartMachineTurn();
        }

        return new RoomTurnDecision.Wait("No wakes or messages pending");
    }
}
