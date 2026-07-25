# Runbook — SEP-1036 URL-mode elicitation, and the durable gate (#531)

**Result, 2026-07-25, `agy` 1.1.7 / protocol `2025-11-25`.** Read this before running anything: the
question is answered, and this runbook exists to re-check it after a vendor update, not to discover
it.

| question | answer |
|---|---|
| `agy` declares `elicitation` | **yes** — `{'form': {}, 'url': {}}` |
| `agy` accepts and routes a `mode: "url"` request | **yes** |
| `agy` surfaces the URL to a person | **no** |
| `agy`'s answer, interactive, human present | **`cancel`** — form in **2.7 ms**, url in **0.6 ms** |
| `agy` retries after `-32042` + `notifications/elicitation/complete` | **no** |
| a blocking `tools/call` can be answered out of band and resumed | **yes — held 162 s, answer accepted** |

**So the non-blocking gate exists on no vendor today**: `claude` declares form-only, `agy` declares
url mode and does not implement it. [0029](../decisions/0029-the-gate-is-three-mechanisms.md) is
unchanged in its conclusion — the durable gate is the blocking `tools/call`, and that is now measured
rather than reasoned.

## Why this needs a human, and exactly how little

Almost none of it does. Three of the original questions are pure protocol and were automated in
`tools/vendor-verify/url_gate_probe.py`. **One thing cannot be automated:** distinguishing

- *`agy` declares an elicitation capability it never surfaces* — a vendor finding, from
- *a pty-driven session is not a context where `agy` prompts* — a fact about the harness

An automated arm cannot separate those, because both produce an identical instant `cancel`. A person
watching a real terminal can. That is the whole human step.

**The control that settles it is one you get for free:** `agy` asks for tool permission before
calling each probe tool. If you are answering those prompts and *no elicitation prompt ever appears*,
"agy will not prompt in this context" is ruled out by the vendor's own behaviour in the same session.

## Procedure

Two terminals. **Run form mode first** — it is the control, and it decides whether any url-mode
result means anything: if form mode cannot elicit, nothing this probe reports about url mode is about
`agy`.

**Terminal A** — the watcher:

```
python -u tools/vendor-verify/url_gate_probe.py --manual C:\path\to\scratch --flow form
```

**Terminal B** — a real terminal. Not under `winpty`, not this session:

```
cd C:\path\to\scratch
agy -i "Call the MCP tool control_tool, then call elicit_tool. Call both." --add-dir C:/path/to/scratch
```

`--add-dir` is **required** even though cwd is already the workspace. Without it `agy` does not load
`.agents/mcp_config.json` and reports *"the requested MCP tools … are not available in the active
environment or tool set"* — which reads exactly like a model declining to call a tool, and cost one
confused run before `CAPS.json` showed the server had never started.

1. Accept the folder-trust prompt if it appears (first run in a new directory only).
2. **Wait for `[CAPS.json]` in Terminal A.** That is the probe server completing its MCP handshake.
   If it never appears the server did not load and nothing after it means anything.
3. Approve the tool-permission prompts for `control_tool` and `elicit_tool`.
4. **Watch the screen.** The only question that matters: does `agy` ever ask you anything *about the
   elicitation* — a prompt, a link, a consent dialog?
5. Ctrl-C both. Terminal A prints the verdict.

Then repeat with `--flow hold` for url mode. If a link appears, open it — that completes the gate out
of band and the probe records it.

## Reading the result

`latency_s` is the discriminator, not the `action`:

- **near zero with nothing on screen** → no UI was attempted. A vendor finding.
- **a prompt you actually saw** → the automated harness was suppressing it, and every automated
  url-mode arm needs re-reading.

What was observed: **0.0006 s**. Machine speed, with a person sitting there answering other prompts.

## What the automated arms cover, and what they cannot

`python -u tools/vendor-verify/url_gate_probe.py --flow all` runs six arms, paired on one variable —
whether the out-of-band endpoint is ever opened. The `unanswered` arm of each pair is the control: if
its gated tool completes, the gate opened on its own and the answered arm proves nothing.

It **cannot** tell you whether a prompt was rendered and dismissed versus never rendered. That is
step 4 above, and it is why this runbook exists.

Two hazards worth knowing, both of which produced wrong-looking results once:

- **An arm with no terminal output measured the harness, not the vendor.** The probe returns
  `HARNESS_FAILED` and exits non-zero rather than reporting zeros, because four all-zero arms read
  exactly like a damning vendor finding when the real cause was a shell-quoting bug that stopped
  `agy` ever starting.
- **`action: "accept"` is not approval.** Per the specification it means the user consented to *open
  the URL*; the interaction completes out of band. A gate that completed on `accept` would open on
  consent rather than on the answer — and would have looked like a pass.

## Cost

`agy`'s subscription, not `claude`'s. Roughly 3–4 minutes per arm. The MCP server itself is verifiable
for free — `python tools/vendor-verify/servers/mcp_url_gate_server.py` speaks the protocol over stdin
and serves its endpoint with no vendor involved, which is how it was checked before any arm ran.
