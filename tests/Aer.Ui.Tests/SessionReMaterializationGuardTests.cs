using Aer.Adapters;
using Aer.Daemon;
using Aer.Flow.Domain;
using Xunit;

namespace Aer.Ui.Tests;

/// <summary>
/// #354: <see cref="DaemonHost.IsSessionSafeToReMaterialize"/> is the guard that stops a chat turn
/// deleting a live session's flow log / snapshot / artifacts when its <c>turn-anchor</c> is not
/// observably Paused. The pre-#354 code deleted unconditionally in that case -- a two-way test over a
/// multi-state DAG. These cover the decision <em>table</em>: which state snapshots are safe to wipe
/// and which are not. They deliberately do NOT claim to prove the read-then-delete <em>race</em> is
/// closed -- a synchronous stub cannot hold an anchor <c>Running</c> at the instant of the check --
/// which is a by-construction property plus the separately-tracked per-session turn serialisation,
/// in the same spirit the live-vendor smokes are a human gate rather than an automated one.
/// </summary>
public class SessionReMaterializationGuardTests
{
    private static readonly string Chat = InteractiveSessionMaterializer.DefaultStepId;
    private static readonly string Anchor = InteractiveSessionMaterializer.AnchorStepId;

    private static StepState Step(string id, StepStatus status) =>
        new(new StepId(id), status, LatestExecutionId: null, UpstreamExecutionIds: new Dictionary<StepId, ExecutionId>());

    // -- Safe to re-materialize: the flow has no live state to lose -------------------------------

    [Fact]
    public void BrandNewSession_NoProjectionYet_IsSafe()
    {
        Assert.True(DaemonHost.IsSessionSafeToReMaterialize(steps: null, recordedTurnCount: 0));
        Assert.True(DaemonHost.IsSessionSafeToReMaterialize(Array.Empty<StepState>(), recordedTurnCount: 0));
    }

    [Fact]
    public void FirstTurnMaterializedButNeverRun_AllPending_IsSafe()
    {
        StepState[] steps = [Step(Chat, StepStatus.Pending), Step(Anchor, StepStatus.Pending)];
        Assert.True(DaemonHost.IsSessionSafeToReMaterialize(steps, recordedTurnCount: 0));
    }

    [Fact]
    public void FailedFirstTurn_ChatTerminallyFailed_IsSafe()
    {
        StepState[] steps = [Step(Chat, StepStatus.Failed), Step(Anchor, StepStatus.Pending)];
        Assert.True(DaemonHost.IsSessionSafeToReMaterialize(steps, recordedTurnCount: 0));
    }

    [Fact]
    public void DocumentedMidConversationFailureRecovery_ChatFailedAnchorSucceeded_IsSafe()
    {
        // The exact end-state SessionTurnBranchingTests.MidConversationFailure_RecoversOnRetry relies
        // on: a mid-conversation "chat" rerun failed, unpausing the anchor to Succeeded with nothing
        // left to re-trigger its pause. The transcript is non-empty, yet deleting Flow's internal
        // bookkeeping and re-running is the intended recovery (continuity lives in SessionMetadata).
        StepState[] steps = [Step(Chat, StepStatus.Failed), Step(Anchor, StepStatus.Succeeded)];
        Assert.True(DaemonHost.IsSessionSafeToReMaterialize(steps, recordedTurnCount: 2));
    }

    // -- Unsafe: a live session's event log would be destroyed ------------------------------------

    [Fact]
    public void AnchorRerunInFlight_Running_IsRefused()
    {
        // §11.3 condition 2 auto-triggers the anchor's rerun after "chat" succeeds; deleting while it
        // is Running races a live write.
        StepState[] steps = [Step(Chat, StepStatus.Succeeded), Step(Anchor, StepStatus.Running)];
        Assert.False(DaemonHost.IsSessionSafeToReMaterialize(steps, recordedTurnCount: 3));
    }

    [Fact]
    public void PausedAnchorHiddenByALaggingRead_IsRefused()
    {
        // A stale projection can present a real paused continuation as something other than branch-1's
        // Paused case; deleting throws away a continuation we should be Superseding.
        StepState[] steps = [Step(Chat, StepStatus.Succeeded), Step(Anchor, StepStatus.Paused)];
        Assert.False(DaemonHost.IsSessionSafeToReMaterialize(steps, recordedTurnCount: 3));
    }

    [Fact]
    public void SucceededChatAwaitingItsAnchorRerun_IsRefused()
    {
        // The concurrency window: "chat" just succeeded and the anchor's rerun has not fired yet, so
        // nothing is Running or Paused -- but this is a healthy turn, not a stuck one. A status-blind
        // "anchor not Paused -> delete" would wipe it.
        StepState[] steps = [Step(Chat, StepStatus.Succeeded), Step(Anchor, StepStatus.Pending)];
        Assert.False(DaemonHost.IsSessionSafeToReMaterialize(steps, recordedTurnCount: 3));
    }

    [Fact]
    public void EstablishedSession_LaggingOrFailedRead_NoSteps_IsRefused()
    {
        // A non-empty transcript with an empty/null projection is a lagging or failed read, not a
        // genuinely fresh session -- deleting would be data loss.
        Assert.False(DaemonHost.IsSessionSafeToReMaterialize(steps: null, recordedTurnCount: 2));
        Assert.False(DaemonHost.IsSessionSafeToReMaterialize(Array.Empty<StepState>(), recordedTurnCount: 2));
    }
}
