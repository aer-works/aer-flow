# The Non-Expert Audit

M19 Phase 1's requirements capture (#186): `docs/walkthroughs/first-real-workflow.md` walked as
a deliberate non-expert — someone who wants two models to draft and critique a design, and who
is neither an AI-tooling expert nor a systems engineer. Every step below assumes knowledge that
person does not have. Each becomes a requirement on Phases 2–4; none changes what the engine
does, only what the human must know to reach it.

| # | Walkthrough step | Expertise it assumes | Requirement | Phase |
|---|---|---|---|---|
| 1 | §1: two vendor CLIs must be installed and subscription-authenticated | Knowing what `claude`/`agy` are, how to verify them; failures surface only when a run breaks | The UI shows vendor readiness up front ("Claude: available / Gemini: not found") wherever a run can start — a read-only PATH/presence check, never credential handling | 4 |
| 2 | §1–§2: copy two JSON files; understand the template/bindings split before doing anything | The architecture's separation of structure vs. binding | Authoring presents one "new workflow" flow; the split is explained where it appears, in plain words ("the plan" / "who runs each role"), and both files are produced without the user managing them as files | 4 |
| 3 | §2: hand-edit `RetryPolicy`, `PausePoint`, `SupersedeTargets`, name-matched `Inputs`/`Outputs` in raw JSON | The template schema and the §11.3 name-matching rule | Form-first authoring, Dagster-Launchpad style: schema-driven fields, inline validation before run, the DAG preview in lockstep (Stately-style visual ↔ config sync — the M16 preview generalized) | 4 |
| 4 | §2: `PermissionScope: "Bash,Read,Write"` — with M11's recorded failure if you get it wrong | Vendor CLI permission vocabularies and their failure modes | Vendor presets encode this knowledge; scopes render as plain-word capabilities ("can run commands / read files / write files") with the preset preselecting what the step's role needs | 4 |
| 5 | §2: the prompt must tell the model to inspect `AER_SUPPLEMENTARY_INPUT` and hold a shell tool to do it | The adapter seam's per-role resolution constraint (M11 decision of record) and env-var plumbing | Preset prompt templates carry the revision branch; a step that is a send-back target gets it (and the needed scope) suggested automatically | 4 |
| 6 | §3: task directory defaults to the workflow *file* stem; execution ids copied by hand from CLI output | Path conventions; id discipline | In the UI, ids are handles controls carry, never strings a human copies (M15 already does this — keep it a stated principle); tasks reopen from Home cards, never retyped paths | 2, 3 |
| 7 | §3: `cat .aer/…/artifacts/execution_<id>/critique` to read the critique before deciding | Artifact directory layout | The decision surface leads with the thing to review: an inbox item shows the critique (artifact preview) next to the actions, zero navigation | 2, 3 |
| 8 | §5: the `supply` + `decide` two-command dance, inventing `--worker`/`--output` names | §17.3's supplementary-execution model | One "Send back with feedback" action (M15 built it); the name boxes get sensible defaults and move behind progressive disclosure | 3 |
| 9 | §5: "run commands from the same directory, or pass absolute paths throughout" | Working-directory pitfalls of specific vendor CLIs | The UI always resolves absolute paths internally; the concern never surfaces | 2–4 |
| 10 | §6: the bindings file is re-picked on every mutation | Why bindings are input, not authority (§4) | Pre-fill the last-used bindings per task as a *convenience* (Local UI Configuration), still visible and swappable — pre-filling a picker is not remembering authority | 3 |
| 11 | Everywhere: `supersede`, `supplementary execution`, `snapshot`, `Terminal`, `staleness` as user-facing words | The spec's vocabulary | The vocabulary map (`ux-principles.md`); spec terms survive in tooltips for §12 traceability | 2–5 |
| 12 | §3/§6: status lines like `Paused (outcome=Succeeded, supersede-targets: architect)` | Reading projected engine state | Plain-language status ("Waiting for your review — critique ready"), with the precise state one disclosure away | 3 |

Out of scope, noted for honesty: §7's stub-CLI dry run stays a developer activity (it is
scripting by nature); nothing in M19 owes it a UI. The audit found no requirement that changes
engine behavior — every row is organization, language, or authoring ergonomics, which is what
the M19 plan predicted ("Flow, Core, and the workers change by zero lines").
