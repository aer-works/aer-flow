using Aer.Flow.Domain;

namespace Aer.Flow.Projection;

/// <summary>
/// Reconstructs <see cref="FlowState"/> from event history (spec §12):
/// <c>FlowState = Project(EventStore, WorkflowDefinitionSnapshot)</c>. A pure function — no I/O, no
/// wall-clock time, no live process state (§13) — so identical inputs always produce an identical
/// result.
/// </summary>
public static class StateProjector
{
    /// <summary>
    /// Projects <paramref name="events"/> — read linearly, in append order, from Flow's half of the
    /// Event Store — against <paramref name="snapshot"/> into a <see cref="FlowState"/>. Every
    /// cross-reference below is keyed by <see cref="ExecutionId"/> or <see cref="DecisionId"/> —
    /// never by an event's position — per §6's causal-linking rule. Append order is used only to
    /// determine which accepted attempt is "most recent" for a step, which is exactly what append
    /// order of a single writer's own log means.
    /// </summary>
    public static FlowState Project(IReadOnlyList<FlowEvent> events, WorkflowDefinitionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(snapshot);

        var latestExecutionIdByStepId = new Dictionary<StepId, ExecutionId>();
        var upstreamExecutionIdsByStepId = new Dictionary<StepId, IReadOnlyDictionary<StepId, ExecutionId>>();
        var terminalStatusByExecutionId = new Dictionary<ExecutionId, StepStatus>();
        var pausedExecutionIds = new HashSet<ExecutionId>();
        var everPausedExecutionIds = new HashSet<ExecutionId>();
        var referencedExecutionIdByDecisionId = new Dictionary<DecisionId, ExecutionId>();
        var decisionTypeByDecisionId = new Dictionary<DecisionId, DecisionType>();
        var targetStepIdByDecisionId = new Dictionary<DecisionId, StepId>();
        var supplementaryExecutionIdByDecisionId = new Dictionary<DecisionId, ExecutionId>();
        var stepIdByExecutionId = new Dictionary<ExecutionId, StepId>();
        var consecutiveFailureCountByStepId = new Dictionary<StepId, int>();
        var latestFailureClassificationByStepId = new Dictionary<StepId, FailureClassification?>();
        var latestFailureReasonByStepId = new Dictionary<StepId, string?>();
        var latestExecutionFailedRetryNotBeforeByStepId = new Dictionary<StepId, DateTimeOffset?>();
        var cancellationRequestedExecutionIds = new HashSet<ExecutionId>();

        // Step-less executions (spec §17.3) never associate with any StepId — tracked separately,
        // in append order, so a pending one can be surfaced to completion detection without
        // perturbing any step's latest-attempt projection above.
        var stepLessExecutionsInOrder = new List<StepLessExecutionState>();

        // §17.5's decision consequences, tracked the same "derive, don't remember" way as everything
        // else here: set when a resolving decision names this step (RetryWithRevision's own referent,
        // or Supersede's TargetStepId) and cleared the moment a newer ExecutionRequestAccepted lands
        // for that step — i.e. once the consequence has actually been dispatched. Replaying a log cut
        // off between the decision and its dispatch re-derives the same pending fact (§7, §13).
        var pendingSupplementaryExecutionIdByStepId = new Dictionary<StepId, ExecutionId>();
        var pendingSupersedeTargetStepIds = new HashSet<StepId>();

        var retryNotBeforeByStepId = new Dictionary<StepId, DateTimeOffset>();
        var retryDelayMsByStepId = new Dictionary<StepId, int>();
        var retryScheduledForExecutionIdByStepId = new Dictionary<StepId, ExecutionId>();

        foreach (var flowEvent in events)
        {
            switch (flowEvent)
            {
                case FlowEvent.ExecutionRequestAccepted accepted:
                    if (accepted.Request.StepId is { } acceptedStepId)
                    {
                        latestExecutionIdByStepId[acceptedStepId] = accepted.Request.ExecutionId;
                        upstreamExecutionIdsByStepId[acceptedStepId] = accepted.Request.UpstreamExecutionIds;
                        stepIdByExecutionId[accepted.Request.ExecutionId] = acceptedStepId;

                        // This dispatch is the consequence a prior decision was owed, if any — fulfilled now.
                        pendingSupplementaryExecutionIdByStepId.Remove(acceptedStepId);
                        pendingSupersedeTargetStepIds.Remove(acceptedStepId);
                        retryNotBeforeByStepId.Remove(acceptedStepId);
                        retryDelayMsByStepId.Remove(acceptedStepId);
                        retryScheduledForExecutionIdByStepId.Remove(acceptedStepId);
                    }
                    else
                    {
                        stepLessExecutionsInOrder.Add(new StepLessExecutionState(accepted.Request.ExecutionId, accepted.Request.Worker));
                    }

                    break;

                case FlowEvent.ExecutionSucceeded succeeded:
                    terminalStatusByExecutionId[succeeded.ExecutionId] = StepStatus.Succeeded;
                    if (stepIdByExecutionId.TryGetValue(succeeded.ExecutionId, out var succeededStepId))
                    {
                        consecutiveFailureCountByStepId[succeededStepId] = 0;
                        latestFailureClassificationByStepId[succeededStepId] = null;
                        latestFailureReasonByStepId[succeededStepId] = null;
                        latestExecutionFailedRetryNotBeforeByStepId[succeededStepId] = null;
                    }

                    break;

                case FlowEvent.ExecutionFailed failed:
                    terminalStatusByExecutionId[failed.ExecutionId] = StepStatus.Failed;
                    if (stepIdByExecutionId.TryGetValue(failed.ExecutionId, out var failedStepId))
                    {
                        // ExhaustedUntil never increments: 0026's "consumes no retry budget" is
                        // enforced here at the source, so a later real failure starts from the
                        // real-failure count and the backoff attempt number never inflates from
                        // waiting out a quota window. RetryEngine's attempts check and the
                        // ExhaustedUntil arm of GetRetryObligations both lean on this.
                        if (failed.FailureClassification != FailureClassification.ExhaustedUntil)
                        {
                            consecutiveFailureCountByStepId[failedStepId] =
                                consecutiveFailureCountByStepId.GetValueOrDefault(failedStepId) + 1;
                        }

                        latestFailureClassificationByStepId[failedStepId] = failed.FailureClassification;
                        latestFailureReasonByStepId[failedStepId] = failed.Reason;
                        latestExecutionFailedRetryNotBeforeByStepId[failedStepId] = failed.RetryNotBefore;
                    }

                    break;

                case FlowEvent.ExecutionCancelled cancelled:
                    terminalStatusByExecutionId[cancelled.ExecutionId] = StepStatus.Cancelled;
                    break;

                case FlowEvent.WorkflowPaused paused:
                    pausedExecutionIds.Add(paused.ExecutionId);
                    everPausedExecutionIds.Add(paused.ExecutionId);
                    break;

                case FlowEvent.ExternalDecisionRecorded decision:
                    referencedExecutionIdByDecisionId[decision.DecisionId] = decision.ReferencedExecutionId;
                    decisionTypeByDecisionId[decision.DecisionId] = decision.DecisionType;
                    if (decision.TargetStepId is { } declaredTargetStepId)
                    {
                        targetStepIdByDecisionId[decision.DecisionId] = declaredTargetStepId;
                    }

                    if (decision.SupplementaryExecutionId is { } declaredSupplementaryExecutionId)
                    {
                        supplementaryExecutionIdByDecisionId[decision.DecisionId] = declaredSupplementaryExecutionId;
                    }

                    break;

                case FlowEvent.WorkflowResumed resumed:
                    if (referencedExecutionIdByDecisionId.TryGetValue(resumed.DecisionId, out var resumedExecutionId))
                    {
                        pausedExecutionIds.Remove(resumedExecutionId);
                        var resumedDecisionType = decisionTypeByDecisionId.GetValueOrDefault(resumed.DecisionId);
                        ExecutionId? supplementaryExecutionId = supplementaryExecutionIdByDecisionId.TryGetValue(
                            resumed.DecisionId, out var declaredSupplement)
                            ? declaredSupplement
                            : null;

                        // Reject is the one decision type that changes the referenced execution's
                        // outcome rather than letting it stand (§17.2) — terminally failed, retry
                        // foreclosed, regardless of whether the underlying outcome was itself a
                        // success. Never a stored event; derived here from the decision it resolves.
                        if (resumedDecisionType == DecisionType.Reject)
                        {
                            terminalStatusByExecutionId[resumedExecutionId] = StepStatus.Rejected;
                        }

                        // RetryWithRevision reopens the referenced (not-yet-succeeded) step's retry
                        // round: a fresh budget, the same way a success resets it (M8 Phase 1), so
                        // the reopened attempt flows through ordinary §10 readiness rather than
                        // finding itself already exhausted.
                        if (resumedDecisionType == DecisionType.RetryWithRevision &&
                            stepIdByExecutionId.TryGetValue(resumedExecutionId, out var retryStepId))
                        {
                            consecutiveFailureCountByStepId[retryStepId] = 0;
                            // The classification clears with the count, mirroring the success
                            // reset above: a reopen is a fresh round, and a stale ExhaustedUntil
                            // left here would send the operator's explicit retry-now back through
                            // GetRetryObligations' reset-moment pacing instead of dispatching it.
                            latestFailureClassificationByStepId[retryStepId] = null;
                            latestFailureReasonByStepId[retryStepId] = null;
                            latestExecutionFailedRetryNotBeforeByStepId[retryStepId] = null;
                            retryNotBeforeByStepId.Remove(retryStepId);
                            retryDelayMsByStepId.Remove(retryStepId);
                            retryScheduledForExecutionIdByStepId.Remove(retryStepId);

                            if (supplementaryExecutionId is { } retrySupplement)
                            {
                                pendingSupplementaryExecutionIdByStepId[retryStepId] = retrySupplement;
                            }
                            else
                            {
                                pendingSupplementaryExecutionIdByStepId.Remove(retryStepId);
                            }
                        }

                        // Supersede's target — already Succeeded, therefore never "ready" through
                        // §11.3 alone — gets a new ExecutionRequest as the decision's direct
                        // consequence (§17.5); the mandatory supplement rides along as an input.
                        if (resumedDecisionType == DecisionType.Supersede &&
                            targetStepIdByDecisionId.TryGetValue(resumed.DecisionId, out var supersedeTargetStepId))
                        {
                            pendingSupersedeTargetStepIds.Add(supersedeTargetStepId);

                            if (supplementaryExecutionId is { } supersedeSupplement)
                            {
                                pendingSupplementaryExecutionIdByStepId[supersedeTargetStepId] = supersedeSupplement;
                            }
                        }
                    }

                    break;

                case FlowEvent.StepRetryScheduled retryScheduled:
                    retryNotBeforeByStepId[retryScheduled.StepId] = retryScheduled.RetryNotBefore;
                    retryDelayMsByStepId[retryScheduled.StepId] = retryScheduled.RetryDelayMs;
                    retryScheduledForExecutionIdByStepId[retryScheduled.StepId] = retryScheduled.ForExecutionId;
                    break;

                // Mid-execution, not an outcome — the step stays Running (or Paused) until the
                // matching terminal event arrives (§9). Tracked here only so a later derived
                // obligation can find it; membership is trimmed to "still unfulfilled" below.
                case FlowEvent.CancellationRequested cancellationRequested:
                    cancellationRequestedExecutionIds.Add(cancellationRequested.ExecutionId);
                    break;

                // ExecutionRequestRejected carries no StepId and never received an
                // ExecutionRequestAccepted, so it never becomes "the latest attempt" for any step.
                case FlowEvent.ExecutionRequestRejected:
                    break;
            }
        }

        var steps = new List<StepState>(snapshot.Steps.Count);
        foreach (var stepDefinition in snapshot.Steps)
        {
            if (!latestExecutionIdByStepId.TryGetValue(stepDefinition.StepId, out var latestExecutionId))
            {
                steps.Add(new StepState(
                    stepDefinition.StepId,
                    StepStatus.Pending,
                    LatestExecutionId: null,
                    UpstreamExecutionIds: new Dictionary<StepId, ExecutionId>()));
                continue;
            }

            // No terminal event yet for the latest attempt means either it is genuinely still
            // running, or Flow crashed before recording its outcome — the two are indistinguishable
            // from the event log alone (§6), and both project to Running.
            var rawStatus = terminalStatusByExecutionId.GetValueOrDefault(latestExecutionId, StepStatus.Running);
            var isPaused = pausedExecutionIds.Contains(latestExecutionId);
            var status = isPaused ? StepStatus.Paused : rawStatus;

            steps.Add(new StepState(
                stepDefinition.StepId,
                status,
                latestExecutionId,
                upstreamExecutionIdsByStepId[stepDefinition.StepId],
                consecutiveFailureCountByStepId.GetValueOrDefault(stepDefinition.StepId),
                latestFailureClassificationByStepId.GetValueOrDefault(stepDefinition.StepId),
                latestFailureReasonByStepId.GetValueOrDefault(stepDefinition.StepId),
                everPausedExecutionIds.Contains(latestExecutionId),
                isPaused ? rawStatus : null,
                pendingSupplementaryExecutionIdByStepId.TryGetValue(stepDefinition.StepId, out var pendingSupplement)
                    ? pendingSupplement
                    : null,
                pendingSupersedeTargetStepIds.Contains(stepDefinition.StepId),
                retryNotBeforeByStepId.TryGetValue(stepDefinition.StepId, out var rnb) ? rnb : null,
                retryDelayMsByStepId.TryGetValue(stepDefinition.StepId, out var rdm) ? rdm : null,
                retryScheduledForExecutionIdByStepId.TryGetValue(stepDefinition.StepId, out var rfe) ? rfe : null,
                latestExecutionFailedRetryNotBeforeByStepId.GetValueOrDefault(stepDefinition.StepId)));
        }

        // Paused outranks a pending deferral: a deferred sibling must not make a workflow that is
        // waiting on a human read as Running — the operator's decision surface keys on Paused, and
        // the pump deliberately returns rather than waits while any step is paused (#712). A
        // deferral only means Running when nothing needs a person first.
        var workflowStatus = DeriveWorkflowStatus(steps, snapshot);

        // Still pending: accepted, but no terminal event recorded for it yet — exactly the same
        // "no terminal event means Running-or-crashed" rule §6 already applies to step-tied
        // executions, just without a StepState to attach it to.
        var pendingStepLessExecutions = stepLessExecutionsInOrder
            .Where(execution => !terminalStatusByExecutionId.ContainsKey(execution.ExecutionId))
            .ToList();

        // A too-late request (§9 step 4) named an ExecutionId that already has a terminal event —
        // the same rule that keeps a StepLessExecutionState "pending" above.
        var unfulfilledCancellationRequestExecutionIds = cancellationRequestedExecutionIds
            .Where(executionId => !terminalStatusByExecutionId.ContainsKey(executionId))
            .ToList();

        return new FlowState(
            snapshot.WorkflowDefinitionSnapshotId,
            steps,
            workflowStatus,
            pendingStepLessExecutions,
            unfulfilledCancellationRequestExecutionIds);
    }

    /// <summary>
    /// <see cref="WorkflowStatus.Terminal"/> promises "nothing further to dispatch", and this is
    /// where that clause is actually checked (#810). The old derivation only asked
    /// Running/Paused/deferred, so every reader saw a phantom Terminal in two live windows: between
    /// one step's success and the next step's <see cref="FlowEvent.ExecutionRequestAccepted"/>, and
    /// after a failure whose retry the pump had not yet scheduled. The pump never consumes this
    /// value (it re-derives readiness), which is why the gap survived unseen until a follow exited
    /// mid-run. Pure over state + snapshot — no clock, so the resolver's time-gated readiness is
    /// deliberately NOT consulted (§13); a deferred retry reads Running however far away its
    /// <see cref="StepState.RetryNotBefore"/> is.
    /// </summary>
    private static WorkflowStatus DeriveWorkflowStatus(
        IReadOnlyList<StepState> steps, WorkflowDefinitionSnapshot snapshot)
    {
        if (steps.Any(step => step.Status == StepStatus.Running))
        {
            return WorkflowStatus.Running;
        }

        if (steps.Any(step => step.Status == StepStatus.Paused))
        {
            return WorkflowStatus.Paused;
        }

        var stepById = steps.ToDictionary(step => step.StepId);
        var definitionById = snapshot.Steps.ToDictionary(definition => definition.StepId);

        // A step that can still progress on its own: a scheduled deferral, a failure the
        // RetryPolicy (or 0026's ExhaustedUntil exemption) still permits another attempt for, or a
        // decision consequence awaiting its dispatch (§17.2/§17.5).
        bool CanProgressAlone(StepState step) =>
            step.RetryNotBefore is not null
            || (step.Status == StepStatus.Failed
                && Scheduling.RetryEngine.MayRetry(step, definitionById[step.StepId].RetryPolicy))
            || step.PendingSupplementaryExecutionId is not null
            || step.IsPendingSupersedeTarget;

        if (steps.Any(CanProgressAlone))
        {
            return WorkflowStatus.Running;
        }

        // A Pending step is alive exactly when every upstream dependency can still deliver its
        // inputs — Succeeded already has, and a Pending-or-progressing chain still might. A chain
        // dead at any link (permanent/exhausted failure, cancellation, rejection) leaves its
        // dependents Pending forever, and THAT is the legitimate Terminal-with-Pending-steps case.
        // Memoized; the validator forbids dependency cycles, and the false-before-recursing seed
        // makes an impossible cycle read as dead rather than looping.
        var aliveByStepId = new Dictionary<StepId, bool>();
        bool IsAlive(StepId stepId)
        {
            if (aliveByStepId.TryGetValue(stepId, out var known))
            {
                return known;
            }

            aliveByStepId[stepId] = false;
            var step = stepById[stepId];
            var alive = step.Status switch
            {
                StepStatus.Succeeded => true,
                StepStatus.Pending => definitionById[stepId].DependsOn.All(IsAlive),
                _ => CanProgressAlone(step),
            };
            aliveByStepId[stepId] = alive;
            return alive;
        }

        return steps.Any(step => step.Status == StepStatus.Pending && IsAlive(step.StepId))
            ? WorkflowStatus.Running
            : WorkflowStatus.Terminal;
    }
}
