# Archive — superseded documents

**Nothing in this folder is current.** Every file here described the product accurately when it was
written and does not now. It is kept because deleting it would erase *why* decisions were made, not
because any of it should be read as a target.

If you are looking for what is true today:

| You want | Read |
|---|---|
| What the product **is** | [`docs/decisions/0012`](../decisions/0012-what-aer-flow-is.md) and the records after it |
| What each surface **does** | The decision records — the UI spec that used to answer this is archived here, and its replacement is written when the rebuilt UI lands (`#367`) |
| Which docs to **trust** | [`docs/documentation-trust-index.md`](../documentation-trust-index.md) |
| What the **engine** does | [`spec/aer-flow-behavioral-spec-v1.0.md`](../../spec/aer-flow-behavioral-spec-v1.0.md) — *not* archived; the engine was never rebuilt |

## What is here, and what replaced it

**`spec/aer-flow-ui-behavioral-spec-v1.0.md`** — the UI behavioural contract. Superseded on three
axes at once: its vocabulary (`task` → **room**,
[0013](../decisions/0013-room-is-the-user-facing-noun.md)), its authoring model (freeform DAG canvas →
an ordered list that renders as a graph, [0014](../decisions/0014-shapes-are-a-list-not-a-canvas.md)),
and its surface split (Home/Task/Author → one switcher shell). It was **not** rewritten in place:
writing a UI contract against a UI that does not exist yet produces fiction, and fiction becomes the
next stale document. The replacement is written when the surface exists.

**`ux/`** — the M19 UX set: information architecture, presentation principles and vocabulary map,
the visual design language and its reference set, and a point-in-time usability audit. Superseded on
visual direction by [0006](../decisions/0006-visual-direction-quiet.md) (Quiet), on structure by the
switcher shell, and on vocabulary by 0013. Two things in it are still worth reading as *evidence*:
the reference set (owner-supplied; changing it is an owner decision) and the audit (dated evidence,
never a spec).

**`walkthroughs/`** — "your first real workflow", plus its example workflow/binding JSON. The
send-back **mechanics** it teaches are real and still run; the engine was not rebuilt. What is
superseded is the framing — it treats *authoring a workflow* as the day job and casts the operator as
"the relay", hand-carrying each round. [0012](../decisions/0012-what-aer-flow-is.md) makes the day job
a **room you talk to**, and hand-carrying rounds is precisely what the product now exists to absorb.

## Rules for this folder

- **Do not cite an archived document as justification** for a current decision. If something in here
  is still true, promote it into a live document and cite that instead.
- **Do not update these files.** They are a record of a moment; editing them destroys the only thing
  they are good for. The one exception is the supersession banner at the top of each.
- **Do not link into here from live docs** except to say "this was superseded, here is where it went".
