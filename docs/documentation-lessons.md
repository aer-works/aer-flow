# What reading ~380 pages of vendor documentation taught us about writing our own

**Written 2026-07-25, from #527.** Every rule below is derived from a specific thing that cost this
project real time — a wrong conclusion, a re-derivation, or a design decision built on a
misreading. None of it is general advice; if a rule isn't traceable to a scar, it isn't here.

The vendors' documentation is, on the whole, unusually good. That is the point. **These failure
modes survive good documentation**, which is why they're worth writing down rather than assuming
care will prevent them.

Two audiences, and the rules differ:

- **Outward** — what a user of AER reads: README, CLI help, error messages, published docs.
- **Inward** — what a developer or agent working *on* AER reads: `CLAUDE.md`, decisions, specs,
  runbooks, code comments.

---

## The one-sentence version

**A reader's wrong conclusion is a documentation defect, even when every sentence is true.**

Almost everything below is a case where the docs were accurate and the reader still ended up
wrong. Accuracy is the floor, not the goal.

---

## Outward-facing rules

### 1. Document every invocation form, not just the primary one

**What happened.** We recorded "AER cannot give a worker its own config root" as a hard constraint
and designed around it. Wrong. The docs describe `/login` as a TUI slash command; `claude auth
login` exists as a CLI subcommand and appears only in `--help`. One sentence in the reference would
have saved the wrong conclusion and the work built on it.

**Rule.** If a capability is reachable three ways — TUI command, CLI subcommand, config file,
environment variable — all three appear on the page describing the capability. A reader searching
for *what they want to do* must not have to already know *how it is spelled*.

**Test.** For each capability, grep the docs for every invocation form. Missing one is a bug.

### 2. Mark extensions as extensions

**What happened.** `_meta["anthropic/requiresUserInteraction"]` is documented on a page about MCP.
It is not in the MCP specification — it's a vendor extension. Nothing said so. We came within a
step of designing a portable gate on a non-portable primitive, and only caught it by reading the
spec directly.

**Rule.** When AER documents something layered on a standard, the sentence says which is which:
"this is AER's, not the protocol's." Portability is a property readers plan around, and they cannot
infer it.

### 3. Document the boundary, not just the capability

**What happened.** `--add-dir` is documented as granting file access. It also loads *no*
configuration from the added directory — which decides where a gate must live. That absence appears
nowhere; we established it by running it.

**Rule.** For any feature where a reader will reasonably assume "and therefore also B", state
whether B holds. **"X does A" invites "X does B."** The sentence that prevents a week of wrong
design is usually a negative one.

### 4. State the negative where a positive is expected

**What happened.** agy's permissions page says permission rules live in global settings. It never
says *there is no project-scoped equivalent*. Every other config in the ecosystem has project
scope, so the reader supplies the missing positive. Our own register recorded "three permission
scopes (Project/Shared/Global)" — a claim the docs never made and that measurement contradicted.

**Rule.** Where a reader's prior will fill a gap, fill it explicitly: "Permissions are global-only.
There is no project-scoped equivalent." Silence is read as "the usual thing applies."

### 5. Say which execution modes a feature exists in

**What happened.** The `PermissionRequest` hook is documented as firing "when a permission dialog
appears." Accurate. Under `-p` no dialog ever appears, so it never fires — and a design decision
(0018) was built on notifying through it. The docs never mention mode-dependence.

**Rule.** Anything whose availability depends on how AER was launched — headless vs interactive,
daemon vs foreground, paired vs unpaired — carries that scope in its description. Prefer a table
column over a sentence, because readers scan.

### 6. Never let a mechanism read as a guarantee

**What happened.** `--disallowedTools` is documented as removing tools. True. A reader concludes it
bounds what the model can *do*. It doesn't — with `Write` disallowed, the model used `Bash` and
created the file anyway. **This is live in our own code** ([#529](https://github.com/aer-works/aer-flow/issues/529)):
a withheld write category withholds `Edit,Write,NotebookEdit` and leaves `Bash`. (Since
[#649](https://github.com/aer-works/aer-flow/issues/649) those three names ride the `PreToolUse` hook
rather than `--disallowedTools`, so the hook can allow the one write landing in `AER_OUTPUT_DIR`;
which mechanism denies them does not change what `Bash` still reaches.)

**Rule.** For anything security-adjacent, the docs state the *guarantee*, separately from the
mechanism, and state it in terms of what an adversarial-or-just-creative agent can still achieve.
If the answer is "the goal is still reachable another way," say exactly that.

### 7. Be exact about concurrency semantics

**What happened.** "Two processes cannot write one transcript" reads as a lock. Measured: a
sequential reuse of a `--session-id` is refused, but two concurrent processes both race past the
check and both run. The guard is an existence check. "Cannot" was doing work it hadn't earned.

**Rule.** Distinguish *prevented*, *detected*, and *not expected*. If it's an existence check, say
"existence check" — a reader building on it needs to know it loses the race.

### 8. The error message is documentation, and it must name the right problem

**What happened.** `--json-schema` takes inline JSON, not a path. Passing a filename fails with
`not valid JSON: Unexpected identifier "C"` — which describes a malformed schema and sends you
debugging your schema. The wrong *kind* of argument should say so.

**Rule.** An error that misdirects is worse than a generic one. Where an argument has a surprising
kind, the failure names the kind: *"expects inline JSON, not a file path."*

**Do keep the good pattern we saw:** agy's headless denial names the exact rule that would permit
the action, and claude's budget stop is a machine-readable `error_max_budget_usd`. **A refusal that
names its own remedy is the single best thing in either vendor's surface.** Copy it everywhere.

### 9. Don't let the changelog be the only home of a behavioural fact

**What happened.** claude's changelog was the densest page in the whole corpus — 200+ constraint
sentences, more than any reference page — and it sits outside `/docs/`. Behaviour changes lived
there and nowhere else, so a reader of the reference page gets the old mental model.

**Rule.** A changelog entry announcing a behaviour change is not done until the reference page
describes the new behaviour. The changelog says *what changed*; the reference says *what is true*.
Two different jobs.

### 10. Publish a machine-readable index

**What happened.** The single highest-leverage thing both vendors did was publish `llms.txt` with a
per-page `.md`. It is what made a ~380-page corpus readable at all. Without it, we'd have
hand-picked pages and missed the ones that mattered — which is exactly what the first pass did.

**Rule.** AER publishes one. Cheap, and it decides whether anyone can systematically read us.

---

## Inward-facing rules

### 11. A doc in the live tree is a claim that it is current

This repo already enforces this (`docs/archive/`), and the audit validated it hard: our own
`vendor-capabilities.md` carried rows that were wrong, and decision 0015 inverted its whole
mechanism on one of them. **A stale doc is worse than a missing one** — a missing doc makes you
look; a stale one makes you confident.

**Rule.** Fix it or archive it. There is no third state.

### 12. Record *why*, and record what would falsify it

The decisions that survived the audit are the ones that said why. The ones that broke — 0015, 0018
— asserted a mechanism without recording what it rested on, so when the mechanism turned out to be
wrong there was no way to see what else fell with it.

**Rule.** Every decision names the facts it depends on. When one is measured false, the blast
radius is then mechanical to compute rather than a re-read of everything.

### 13. Distinguish "verified" from "documented" in our own registers, always

This audit found four vendor statements to be wrong and two that contradicted each other.
`docs/vendor-coverage.md` now marks every row **R** (read) / **V** (verified by a run) / **·** (not
read — a gap, not an absence) / **X** (cannot be established here, with the reason).

**Rule.** Never let those four collapse into one list. "We haven't checked" and "it isn't true" and
"we can't check from here" are different, and a single "TODO" hides which.

### 14. When correcting a wrong entry, correct it in place — don't just delete it

Two backlog rows here were *wrong as written*, not merely unverified. Deleting them would have let
a future reader re-derive the same wrong claim from the same source. They are struck through with
the correction beside them.

**Rule.** A corrected claim keeps its corpse. `~~wrong thing~~ — actually X, because Y.`

### 15. Make coverage checkable, not asserted

"We read the docs" is unfalsifiable. The audit register gives **every** page a disposition, so coverage is
a number anyone can recompute. That's what made "31 agy pages were invisible to the harvest"
discoverable at all.

**Rule.** Any claim of completeness ships with the artifact that lets someone check it.

### 16. Your own tooling encodes your assumptions — test it against something you didn't write

Three real bugs in our survey tool, each invisible from inside:

- it skipped **table rows** — 27% of constraints, and vendors put limits in tables
- its vocabulary was **claude-shaped**, so 31 agy pages scored `NO-SIGNAL` — we could not see a
  concept we had no word for
- it had **no change detection**, so a re-run couldn't say what moved

**Rule.** A search or indexing tool is a hypothesis about what matters. Test it against a corpus
written by someone with different vocabulary, and make it report **what it could not see** —
`vendor-survey` now prints a blind-spot section, including the 7,726 lines that state things
plainly and carry no constraint word at all.

### 17. Prefer the negative-space report over the confident summary

The most useful output of the survey tool is not the 1,621 constraints. It's the paragraph saying
*77% of topic-relevant lines are invisible to this method.* Every register in this repo should be
able to say what it doesn't cover.

### 18. A CLI that answers instead of erroring turns a typo into a bill

`claude models` is not a subcommand. The words are taken as a **prompt** and answered — so probing for
a capability that does not exist costs a turn, and reports success. The usual signal that you guessed
wrong (a non-zero exit, an "unknown command") never arrives.

This is why claude's valid model set cannot be enumerated for free, and why `smoke-preflight` checks
claude pins by shape and leans on claude's own rejection instead (#538).

**Applied to our own docs and tooling:** when a runbook tells a reader to try a subcommand against a
CLI whose default behaviour is *answer the prompt*, say what it costs if the subcommand does not
exist. And never verify a vendor capability by "running it and seeing if it worked" on a tool that
cannot fail — establish a control that would have failed. Same family as the rule below.

---

## The method rule underneath all of it

**Before recording a negative, ask what else would produce the same observation.**

Five times in this audit the instrument, not the vendor, was what needed fixing — and every one was
a negative with more than one possible cause:

| what we saw | what we concluded | what was actually true |
|---|---|---|
| agy hooks didn't fire | "hooks don't work headless" | the hook command had a bad path — exit 127 |
| no nested subagent | "nesting is off by default" | prose proved nothing; counting spawns showed nesting is **on** |
| file written despite `--disallowedTools Write` | "the flag doesn't work" | it works — `Bash` wrote the file |
| capped arm started only 2 subagents | "the model didn't fan out" | plausible either way; two capped arms settled it |
| concurrent session ids refused | "one writer per transcript" | sequential reuse is refused; concurrency isn't |

And once in reverse — a *positive* reading stronger than its evidence: a fresh config root saying
`Not logged in` was read as "this can never be logged in."

**Applied to our own docs:** when we write "X cannot happen", the reviewer's question is *how do we
know, and what else would look like this?* If the answer is "we tried it once and nothing happened,"
that is not knowledge, and the doc should say so.
