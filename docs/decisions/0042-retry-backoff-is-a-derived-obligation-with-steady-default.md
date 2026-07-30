# 0042 — Retry backoff is a derived obligation, and an unspecified backoff means steady, not immediate

Status: accepted
Date: 2026-07-29

## Context

Before #712, a failed step with remaining retry budget re-dispatched immediately — measured at
40–90 ms between attempts (the measurement is on [#712](https://github.com/aer-works/aer-flow/issues/712)).
Against a rate-limited vendor CLI that is not a retry policy; it is a way to spend the whole attempt
budget inside the same outage that failed attempt one.

The mechanics that shipped are recorded once, in spec §10.2. This record holds what §10.2 cannot:
which shapes were rejected, and why the ones chosen won. The design was converged through a
six-turn adversarial dialogue between two vendor models (transcript under the #712 run artifacts),
then implemented in stages; the alternatives below are the ones that dialogue actually killed,
not a decorative survey.

## Decision

**Pacing is recorded as a derived obligation event (`StepRetryScheduled`), computed by the engine
loop's clock and jitter, consumed by readiness — and a `RetryPolicy` that says nothing about
backoff gets `steady`, not `none`.**

Rejected shapes, and why:

- **Compute the delay at readiness time (no event).** Readiness is a pure function replayed on
  every projection; giving it a clock and a jitter source makes replay nondeterministic and makes
  "why hasn't this retried yet" invisible in the log. The event is the audit trail and the
  replay-safety mechanism at once.
- **Store the deadline as mutable step state outside the log.** Nothing outside the log survives a
  crash (§5); a deferral that evaporates on restart is a zero-delay retry with extra steps.
- **Default `none` (preserve old behavior).** The old behavior is the defect being fixed.
  Every template that never thought about pacing gets the fix, not the bug; a template that
  genuinely wants immediate retries says `"backoff": "none"`, which is one line and self-documenting.
  This deliberately changes the timing of every existing definition that omitted `Backoff`.
- **Tolerate unknown preset names (fall back to default).** A typo (`"stedy"`) silently becoming
  `steady` is indistinguishable from working; becoming a hard load error is caught on the first run.
- **Pace operator retries too (uniform rule).** Backoff exists to pace the machine's own
  persistence. A person clicking retry has already decided now is the time; the engine
  second-guessing them by minutes (patient) is the tool overriding its operator.

## Rests on

| fact | how we know | if false |
|---|---|---|
| Zero-delay retries re-dispatch in 40–90 ms | **measured** — the instrumented run posted on [#712](https://github.com/aer-works/aer-flow/issues/712) | there is no hot loop, and immediate retry was an acceptable default all along |
| Projection replays byte-identically with no time source available | **measured** — the replay test constructs the projector with a throwing clock and jitter source (`MutationInterfaceRetryBackoffTests`, Test4) | the derived-obligation shape loses its main advantage over readiness-time computation |
| A deferral deadline wakes the engine while a sibling is mid-flight | **measured** — `StartWorkflowAsync_retries_a_failed_step_while_an_unrelated_step_stays_in_flight` hung for exactly this before the wake-timer joined the in-flight wait | a sub-second backoff silently stretches to the slowest sibling's runtime, and the concurrency suite's core claim is false |
| `ConsecutiveFailureCount == 0` on a Failed step occurs only as the RetryWithRevision reset marker | **measured** — `StateProjector` resets the count for exactly that decision; Test9 pins the no-deferral consequence | the operator-exemption guard skips machine retries it should pace, or paces operator retries it should not |

## Consequences

**Easier.** Retry storms against rate-limited vendors stop being the default failure mode; "why
hasn't this retried yet" is answerable from the log alone; herds of identical workflows
desynchronize by construction (jitter floor at half the delay).

**Harder.** Every existing template that omitted `Backoff` changes timing on upgrade — retries that
were instant now wait seconds. This is intended, but it is a behavior change shipped to definitions
that never asked for one, and the PR carrying it says so explicitly.

**Obliges us to.** Keep `MayRetry` pure and clock-free — any future contributor handing it a
`TimeProvider` is re-opening the replay hole this shape exists to close. And keep preset parameter
values canonical in `BackoffPolicy` alone; the spec deliberately records only the intent ladder.

Relates: spec §10.2 (the mechanics, recorded once), [#712](https://github.com/aer-works/aer-flow/issues/712)
(measurement and scope), [#718](https://github.com/aer-works/aer-flow/issues/718) (host-stop
convergence gap found while testing this), [0039](0039-dialogue-turns-use-vendor-session-continuation-not-full-history-resend.md)
(the dialogue worker used to converge this design).
