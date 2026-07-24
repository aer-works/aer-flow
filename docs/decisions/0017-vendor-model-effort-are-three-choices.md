# 0017 — Vendor, model and effort are three separate choices

Status: accepted
Date: 2026-07-24

## Context

The running product picks a worker by vendor — `claude` or `agy` — and stops there. Two dropdowns in
Author choose "which vendor," and that is the whole of "who does this work." During the design pass
the owner asked the question that exposes the gap: *where does effort for a model go?* There was no
answer, because the product had no representation for either of the two choices that sit *below*
vendor:

- **Which model.** A vendor's subscription exposes several — the product commits to driving
  subscriptions, not API keys ([0012](0012-what-aer-flow-is.md), CLAUDE.md Adapter Isolation), and a
  subscription is exactly what carries a *choice* of model. Pinning a worker to a vendor but not a
  model throws away the main knob a subscription gives you.
- **How much effort.** Reasoning effort / thinking level is a per-run dial with a real cost/latency
  tradeoff. It is not a property of the vendor and not even a fixed property of the model — it is a
  choice you make for *this* piece of work.

Collapsing these into "vendor" is why there was nowhere for effort to live. They are three different
questions that happen to be answered in the same gesture.

## Decision

**A participant is chosen along three independent axes, all set together on the worker chip:**

| Axis | The question | Example |
|---|---|---|
| **Vendor** | which *tool* drives the work | `claude`, `agy` |
| **Model** | which model within that vendor's subscription | Opus 4.8, Gemini 3 Pro |
| **Effort** | how hard it thinks on this run | low / high (vendor-named) |

- **Vendor is the tool, not the model.** `agy` is the Antigravity CLI, the successor to the Gemini
  CLI; it is invoked as `agy` (`src/Aer.Adapters/GeminiWorkerAdapter.cs`). The vendor names *which
  CLI AER shells out to* — a capability/enforcement question ([0004](0004-permission-scopes.md):
  "vendor is not a scope… it is a capability question"), not a quality one.
- **Model is chosen within the vendor**, from what that subscription offers. AER does not manage keys
  or model access; it selects among what the authenticated CLI already exposes.
- **Effort is per-run and lives on the chip beside the other two** — the answer to the owner's
  question. Its allowed values are vendor-named (each CLI has its own vocabulary), so the chip renders
  the options the chosen vendor actually supports rather than a fabricated universal scale.

**Effort is genuinely orthogonal**, which is why it gets its own axis rather than folding into model:
the same model runs at different efforts, and the choice belongs to the work, not the worker. Modeling
it as a model variant would force a fake "Opus-low / Opus-high" split and still leave no home for the
dial on a mid-run turn.

## Consequences

**Easier.** "Put this vendor's strongest model at high effort on the hard step, and a cheap fast one
on the boilerplate step" becomes expressible per step ([0004](0004-permission-scopes.md)'s step scope
is the natural place). The chip carries all three, so choosing a participant is one compact control,
not a settings excursion.

**Harder.** The three axes are not fully independent in reality — a vendor constrains its model list,
a model constrains its effort vocabulary. The chip has to present them as *dependent dropdowns*
(vendor gates model gates effort) without feeling like three separate decisions, or it recreates the
taxonomy-quiz problem [0003](0003-templates-collapse-to-three-shapes.md) fought. And each adapter must
report its available models and effort levels, which is new surface in
`Aer.Adapters` behind CLAUDE.md's Adapter Isolation rule.

**Obliges us to** keep effort out of the vendor abstraction (it is a run parameter, not an adapter
capability), let a participant's three axes be set both at room start and per-step in an authored
shape ([0014](0014-shapes-are-a-list-not-a-canvas.md)), and default all three sensibly so
[0012](0012-what-aer-flow-is.md)'s simple case — one agent, one folder — never has to touch them.

**Relates to** [0010](0010-skills-and-advisor.md): skills are *what* a worker can do, realized
per-vendor; this record is *which* worker, at *which* model, at *what* effort. Both resolve at the
participant, on the same chip.
