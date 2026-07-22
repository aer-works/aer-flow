# 0003 — Templates collapse to three shapes

Status: accepted
Date: 2026-07-22

## Context

Five templates ship today: `chat-session`, `codebase-session`, `two-vendor-dialogue`, `solo-run`,
`review-run`. Exercising all of them against the live daemon showed they are not five kinds of
thing.

**`chat-session` and `codebase-session` produce byte-identical `bindings.json`** — the same
`chat-worker` + `turn-anchor-worker` pair, the same permission grant. The only difference in the
entire materialised artefact is `WorkingDirectory: null` versus a path. Verified by starting one of
each from the mobile app and diffing what landed on disk.

`solo-run` is the same object again with the turn loop removed — a conversation you send exactly one
message to.

So the catalogue is a cross-product of three orthogonal booleans:

- multi-turn, or one-shot
- bound to a directory, or not
- how many workers, and in what relationship

...enumerated as five menu items. That is why the picker needs a five-item radio list, two vendor
dropdowns, a conditional project selector, and "(Advanced)" suffixes to disambiguate items that
mostly are not different. The "(Advanced)" labels sit on `solo-run` and `review-run` — discouraging
precisely the multi-vendor orchestration that differentiates this product.

## Decision

Three shapes, distinguished by **who drives the loop**:

| Shape | Loop driven by | Absorbs |
|---|---|---|
| **Conversation** | the human, turn by turn | `chat-session`, `codebase-session`, `solo-run` |
| **Pipeline** | the dependency graph, with optional human gates | `review-run` |
| **Dialogue** | the machine, alternating until a budget is reached | `two-vendor-dialogue` |

Dialogue is genuinely distinct rather than a pipeline variant: a DAG cannot express "alternate until
done." Conversation and pipeline differ in the same way — one is open-ended, one is a plan.

**Working directory, vendor, second vendor and turn budget are fields on a shape, never separate
shapes.**

**The common case should not involve choosing a shape at all.** Pick a folder (or don't), start
talking, and *promote* into structure when it is needed — "have Gemini review this" turns a
conversation into a pipeline in place, carrying its context. Three of the five current templates
disappear rather than get redesigned.

Templates survive only as **saved shapes worth reusing** — genuinely multi-step things — plus
whatever a user authors.

## Consequences

**Easier.** The picker stops being a taxonomy quiz. A user who wants to talk to an agent about a
folder does not first have to decide whether that is a "Codebase Session" or a "Chat (Interactive
Session)". The vocabulary problem shrinks with it: three shapes need three words, and the current
five need labels like "Task / Session Type".

**Harder.** Promotion has to actually work — a conversation must be able to become a pipeline
without losing its history, which is a real feature, not a rename. It is the thing the product does
not do today and the reason the current split exists.

**Obliges us to.** Decide what a promoted session's lineage looks like in the list (tree, indent, or
a badge on the parent — still open). Keep `solo-run`'s one-shot behaviour reachable, since a
one-message conversation is not quite the same as a run you never intend to continue.

**Supersedes** the assumption in `BuiltInWorkflowTemplates.Catalog` that a template id and a
user-facing "task type" are the same concept.

Related: #283 (rethink index), #312 (journeys — promotion is one), and the picker defects in #327.
