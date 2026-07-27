# 0031 — Skills are account-wide, not project-scoped

Status: accepted
Date: 2026-07-26

## Context

[0010](0010-skills-and-advisor.md) defined the canonical skill model — app-level, vendor-neutral,
realized per-vendor by the adapter — but explicitly left one thing open: *"where app-level skills
are stored and scoped (project vs. app). None decided here — tuned when M26 is scoped."*

M27's design made this concrete and no longer deferrable: a worker attaches Skills directly
([0033](0033-skills-attach-directly-no-persona.md)), and needs somewhere to attach them *from*.

## Decision

**Skills are account-wide.** One library per person, available in every room regardless of which
directory or repo that room's worker is pointed at. There is no project-level skill store in M27.

This keeps the model simple: no project-boundary logic, no precedence rule between two stores, no
namespacing question between "this repo's skills" and "my skills." A skill built while working in
one project is immediately usable the next time a different room is opened, which is the point of
building one at all.

## Rests on

| fact | how we know | if false |
|---|---|---|
| 0010 left skill storage and scoping explicitly open rather than deciding it | **measured** — [0010](0010-skills-and-advisor.md)'s own text | this record re-decides something already settled, and must be reconciled against 0010 instead of extending it |
| A worker attaches skills directly, with no Persona object for them to be scoped to | **measured** — [0033](0033-skills-attach-directly-no-persona.md) | "account-wide" is scoped to the wrong object, and skills would inherit a preset's scope instead of a person's |
| Neither vendor CLI requires a skill to be project-local | **assumed** — no check probes skill storage scope on either vendor | account-wide is undeliverable on a vendor that scopes per project, and the one-library-per-person promise degrades to per-folder |

## Consequences

**Easier.** One flat, personal skill library — no new storage model beyond "belongs to the
person."

**Harder / explicitly deferred.** A skill authored for one project's conventions (e.g. this repo's
own commit-message style) is not automatically scoped away from a room pointed at an unrelated
project — the person is trusted to attach the right skill to a worker in that room, the same way
they're trusted to pick the right worker. A project-level tier (mirroring Claude Code's own
two-tier `~/.claude/skills` + `.claude/skills` model) is a real future option if account-wide
sharing proves too coarse in practice, but is out of scope for M27 and not designed here.

**Obliges us to.** Update [0010](0010-skills-and-advisor.md) with a pointer to this record rather
than rewriting its "leaves open" section — the body is left as written, per that record's own
amendment convention.

Relates: [0010](0010-skills-and-advisor.md) (the skill model this resolves one open question of),
[0013](0013-room-is-the-user-facing-noun.md) (room is one directory; this record is what does *not*
scope by that directory), [0033](0033-skills-attach-directly-no-persona.md) (what a skill attaches
to).
