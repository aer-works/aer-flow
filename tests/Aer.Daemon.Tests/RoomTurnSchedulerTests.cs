using Aer.Daemon;
using Xunit;

namespace Aer.Daemon.Tests;

public class RoomTurnSchedulerTests
{
    private static readonly RoomTurnThrottles DefaultThrottles = RoomTurnThrottles.Defaults;
    private static readonly DateTimeOffset BaseTime = new(2026, 8, 4, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void HourlyLimit_11thMachineTurnWaits_ButUserMessageStartsUserTurn()
    {
        // Red arm note: If userMessagePending did not take precedence over machine hourly limit, the second assertion would return Wait instead of StartUserTurn.
        var tenStartsInHour = Enumerable.Range(0, 10)
            .Select(i => BaseTime.AddMinutes(-50 + (i * 4)))
            .ToList();

        // 11th machine turn inside trailing hour -> Wait
        var decisionMachine = RoomTurnScheduler.Schedule(
            now: BaseTime,
            turnInFlight: false,
            userMessagePending: false,
            machineWakesPending: true,
            recentMachineTurnStarts: tenStartsInHour,
            consecutiveUncommittedTurns: 0,
            throttles: DefaultThrottles,
            isDormant: false);

        var waitDecision = Assert.IsType<RoomTurnDecision.Wait>(decisionMachine);
        Assert.Contains("hourly limit", waitDecision.Reason);

        // Same tick with user message -> StartUserTurn
        var decisionUser = RoomTurnScheduler.Schedule(
            now: BaseTime,
            turnInFlight: false,
            userMessagePending: true,
            machineWakesPending: true,
            recentMachineTurnStarts: tenStartsInHour,
            consecutiveUncommittedTurns: 0,
            throttles: DefaultThrottles,
            isDormant: false);

        Assert.IsType<RoomTurnDecision.StartUserTurn>(decisionUser);
    }

    [Fact]
    public void Gap_59SecondsWaits_61SecondsStartsMachineTurn()
    {
        // Red arm note: If minimum gap check was incorrect (< vs <= or ignored), 59s would start a machine turn or 61s would wait.
        var lastStart = BaseTime.AddSeconds(-60);
        var recentStarts = new[] { lastStart };

        // 59 seconds elapsed -> Wait
        var decision59 = RoomTurnScheduler.Schedule(
            now: lastStart.AddSeconds(59),
            turnInFlight: false,
            userMessagePending: false,
            machineWakesPending: true,
            recentMachineTurnStarts: recentStarts,
            consecutiveUncommittedTurns: 0,
            throttles: DefaultThrottles,
            isDormant: false);

        var waitDecision = Assert.IsType<RoomTurnDecision.Wait>(decision59);
        Assert.Contains("minimum gap", waitDecision.Reason);

        // 61 seconds elapsed -> StartMachineTurn
        var decision61 = RoomTurnScheduler.Schedule(
            now: lastStart.AddSeconds(61),
            turnInFlight: false,
            userMessagePending: false,
            machineWakesPending: true,
            recentMachineTurnStarts: recentStarts,
            consecutiveUncommittedTurns: 0,
            throttles: DefaultThrottles,
            isDormant: false);

        Assert.IsType<RoomTurnDecision.StartMachineTurn>(decision61);
    }

    [Fact]
    public void Breaker_AtLimitIsDormant_OneBelowIsNotDormant()
    {
        // Red arm note: If breaker check used > instead of >=, 3 failures would not trigger Dormant.
        // At limit (3 consecutive failures with limit=3) -> Dormant
        var decisionLimit = RoomTurnScheduler.Schedule(
            now: BaseTime,
            turnInFlight: false,
            userMessagePending: false,
            machineWakesPending: true,
            recentMachineTurnStarts: [],
            consecutiveUncommittedTurns: 3,
            throttles: DefaultThrottles,
            isDormant: false);

        Assert.IsType<RoomTurnDecision.Dormant>(decisionLimit);

        // One below limit (2 consecutive failures) -> StartMachineTurn
        var decisionBelow = RoomTurnScheduler.Schedule(
            now: BaseTime,
            turnInFlight: false,
            userMessagePending: false,
            machineWakesPending: true,
            recentMachineTurnStarts: [],
            consecutiveUncommittedTurns: 2,
            throttles: DefaultThrottles,
            isDormant: false);

        Assert.IsType<RoomTurnDecision.StartMachineTurn>(decisionBelow);
    }

    [Fact]
    public void Dormant_BlocksBothMachineAndUserTurns()
    {
        // Red arm note: If user messages were allowed during dormancy, decisionUser would be StartUserTurn.
        var decisionMachine = RoomTurnScheduler.Schedule(
            now: BaseTime,
            turnInFlight: false,
            userMessagePending: false,
            machineWakesPending: true,
            recentMachineTurnStarts: [],
            consecutiveUncommittedTurns: 0,
            throttles: DefaultThrottles,
            isDormant: true);

        Assert.IsType<RoomTurnDecision.Dormant>(decisionMachine);

        var decisionUser = RoomTurnScheduler.Schedule(
            now: BaseTime,
            turnInFlight: false,
            userMessagePending: true,
            machineWakesPending: false,
            recentMachineTurnStarts: [],
            consecutiveUncommittedTurns: 0,
            throttles: DefaultThrottles,
            isDormant: true);

        Assert.IsType<RoomTurnDecision.Dormant>(decisionUser);
    }
}
