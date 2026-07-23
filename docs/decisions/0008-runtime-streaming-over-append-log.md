# 0008 — Runtime: live streaming over a durable append log

Status: accepted
Date: 2026-07-22

## Context

The room model ([0001](0001-two-nouns-workflow-and-session.md)) needs workers to feel live and to
talk to each other, which raised: do we keep invoking a CLI per turn and appending to the event log,
or move to persistent workers? Two concerns were bundled inside that, and only one touches the log.

- **The cost being chased is mostly intrinsic.** The dominant per-turn cost is the model generating a
  reply over the growing conversation — paid whether a worker is warm or cold. Warmth saves process
  *startup* and, where a vendor supports it, some prefix re-processing. A latency-and-margin win, not
  an order-of-magnitude one.
- **AER is subscription-first.** The adapters own no key handling and shell out to an
  already-authenticated vendor CLI, on purpose (subscriptions, not API keys). Those CLIs are
  invoke-per-turn tools; persistent streaming sessions are the API-key world the project rejected.
- **The append log is the sound part.** The M25 evaluation found every defect in a seam, never in the
  engine. Replay-on-crash, the projection, and determinism all rest on the append log.

## Decision

**Path A — live streaming over a durable append log.**

- Each turn stays one worker invocation that appends its completed events; state remains a projection
  over the log; replay-on-crash and determinism are unchanged.
- A live channel streams the in-flight turn — and worker-to-worker exchanges — into the room as they
  happen. The stream is ephemeral; the durable record stays turn-granular.
- **Worker lifetime is a policy at the dispatch seam, not a hardcoded assumption.** Default is cold
  (invoke per turn). **Path C (scoped warmth)** — hold a worker warm for the life of one active
  exchange, still appending each completed turn — is planned as a near-term policy swap, not a
  rewrite, and is filed as its own issue.
- **Path B (persistent long-lived sessions) is not adopted.** It rethinks the durable log and pushes
  toward vendor streaming APIs, reopening subscription-first — a product-identity pivot, not a runtime
  tune-up, whose incremental benefit over A is small. Available only as a deliberate future decision
  to make AER an always-on agent runtime, eyes open to that cost.

## Consequences

**Easier.** The sound engine is preserved; the live, multi-worker feel is additive; the subscription
premise stays intact; C slots in cleanly when rapid worker rounds warrant it.

**Obliges us to.** Build worker lifetime as a swappable policy from the start; treat per-turn cost as
intrinsic and give the user a **cross-vendor usage view** (its own journey) as the real cost lever;
stream worker-to-worker exchanges over the existing progress channel.

**Leaves open.** When to build C (near-term issue, not someday); the persistent-agent pivot (B), off
the table absent a deliberate identity decision.

Relates: [0001](0001-two-nouns-workflow-and-session.md) (room model),
[0009](0009-session-lifecycle-and-retention.md) (retention), #335 (multi-task daemon), and the
cross-vendor usage-view journey.
