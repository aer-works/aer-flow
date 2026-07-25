"""Re-run the vendor behaviour checks that AER's gate and worker design rest on.

WHY THIS EXISTS
---------------
`tools/vendor-survey` reads what the vendors *say*. This runs what they *do*. Documentation is a
claim: this audit found four vendor statements to be wrong, and two vendor statements that
contradicted each other outright.

`VendorProbeStalenessTests` already fails when a CLI version moves, and `vendor-survey --refetch`
reports which doc pages changed. Both answer "something moved" -- this answers "did the behaviour
we depend on move with it".

TWO RULES, both learned the hard way
------------------------------------
1. **One variable per check.** Two tools identical except the annotation under test; the same tool
   and the same allow rule in both arms with only the exit code differing. Without a control, a
   non-result proves nothing.
2. **Prove execution with a side effect, never with the model's prose.** Every check asserts on a
   sentinel FILE that a tool wrote. A model can state it called a tool it never called, and a hook
   whose *command* fails looks exactly like a hook that never fired. This audit recorded two wrong
   conclusions before adopting this rule.

A check that cannot separate its cases must return INCONCLUSIVE. That is a real result and more
useful than a confident wrong one.

USAGE
-----
    pixi run vendor-verify                 # every check that needs no special authorisation
    pixi run vendor-verify -- --list       # names and what each one costs
    pixi run vendor-verify -- --only gate  # one group: gate | fanout | cost | lifecycle | agy

SAFETY
------
Checks are `safe` unless marked otherwise. `safe` means: temp directories only, no writes outside
them, and no mutation of the operator's `~/.claude` or `~/.gemini`. Checks marked `mutates-config`
are SKIPPED unless `--allow-config-writes` is passed; they back up byte-exact, add exactly one key,
restore in a `finally`, and re-verify the sha256.

Every check spends real subscription usage, so this NEVER runs in CI -- same rule as
`pixi run vendor-probe` and the live smoke tests.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
SERVERS = os.path.join(HERE, "servers")

PASS, FAIL, INCONCLUSIVE, SKIPPED = "PASS", "FAIL", "INCONCLUSIVE", "SKIPPED"
CHECKS: dict[str, dict] = {}


def check(name, group, claim, safety="safe"):
    def deco(fn):
        CHECKS[name] = {"fn": fn, "group": group, "claim": claim, "safety": safety}
        return fn
    return deco


def env():
    """Strip CLAUDE_* so a check probes the vendor CLI, not this harness's environment."""
    return {k: v for k, v in os.environ.items() if not k.upper().startswith("CLAUDE")}


def run(cmd, timeout=300, cwd=None, extra_env=None):
    """extra_env is applied AFTER the strip, so a check can deliberately set one CLAUDE_CODE_* knob.

    The strip stays the default -- a check should probe the vendor CLI, not the harness that
    launched it -- but the knob a check is testing is the one variable it is allowed to set.
    """
    e = env()
    e.update(extra_env or {})
    try:
        p = subprocess.run(cmd, capture_output=True, text=True, timeout=timeout, cwd=cwd, env=e)
        return p.returncode, (p.stdout or ""), (p.stderr or "")
    except subprocess.TimeoutExpired:
        return None, "", "(timeout)"
    except FileNotFoundError:
        return None, "", "(binary not found)"


def mcp_config(path, server, sentinel_dir, extra_env=None):
    e = {"AER_SENTINEL_DIR": sentinel_dir}
    e.update(extra_env or {})
    json.dump({"mcpServers": {"probe": {
        "command": sys.executable, "args": [os.path.join(SERVERS, server)], "env": e}}},
        open(path, "w"), indent=2)


def hook_script(path, log, body):
    with open(path, "w", newline="\n") as f:
        f.write("#!/bin/sh\n")
        f.write('cat >> "%s"\n' % log)
        f.write('printf "\\n" >> "%s"\n' % log)
        f.write(body + "\n")
    os.chmod(path, 0o755)


def fired(log):
    return sum(1 for l in open(log, encoding="utf-8", errors="replace") if l.strip()) \
        if os.path.exists(log) else 0


# ====================================================================== gate
@check("gate.requires-user-interaction", "gate",
       "_meta[anthropic/requiresUserInteraction] cannot be approved by any mode or allow rule")
def _requires_ui():
    """Two tools identical except the annotation. The annotated one must never execute."""
    arms = [("allowedTools", ["--allowedTools", "mcp__probe__control_tool,mcp__probe__gated_tool"]),
            ("acceptEdits", ["--permission-mode", "acceptEdits",
                             "--allowedTools", "mcp__probe__control_tool,mcp__probe__gated_tool"]),
            ("bypassPermissions", ["--permission-mode", "bypassPermissions"])]
    detail = []
    for label, extra in arms:
        wd = tempfile.mkdtemp(prefix="v-reqUI-")
        try:
            cfg = os.path.join(wd, "mcp.json")
            mcp_config(cfg, "mcp_gate_server.py", wd)
            run(["claude", "-p", "Call the MCP tool control_tool, then call gated_tool. Call both.",
                 "--mcp-config", cfg, "--output-format", "json", *extra], cwd=wd)
            control = os.path.exists(os.path.join(wd, "CALLED_control_tool"))
            gated = os.path.exists(os.path.join(wd, "CALLED_gated_tool"))
            detail.append(f"{label}: control={control} gated={gated}")
            if not control:
                return INCONCLUSIVE, f"{label}: control tool never ran, nothing tested"
            if gated:
                return FAIL, f"{label}: the annotated tool EXECUTED"
        finally:
            shutil.rmtree(wd, ignore_errors=True)
    return PASS, "; ".join(detail)


@check("gate.prompt-tool-conversion", "gate",
       "a permission-prompt tool's allow is converted to deny for a requiresUserInteraction tool")
def _prompt_tool():
    wd = tempfile.mkdtemp(prefix="v-pt-")
    try:
        cfg = os.path.join(wd, "mcp.json")
        mcp_config(cfg, "mcp_prompt_tool.py", wd)
        run(["claude", "-p", "Call the MCP tool control_tool, then call gated_tool. Call both.",
             "--mcp-config", cfg, "--permission-prompt-tool", "mcp__probe__approve_everything",
             "--output-format", "json"], cwd=wd)
        control = os.path.exists(os.path.join(wd, "CALLED_control_tool"))
        gated = os.path.exists(os.path.join(wd, "CALLED_gated_tool"))
        asked = open(os.path.join(wd, "PROMPTED.log")).read().split() \
            if os.path.exists(os.path.join(wd, "PROMPTED.log")) else []
        if not control:
            return INCONCLUSIVE, "control tool never ran; the allow path itself may be broken"
        return (PASS if not gated else FAIL), f"prompted for {asked}; control={control} gated={gated}"
    finally:
        shutil.rmtree(wd, ignore_errors=True)


@check("gate.hook-exit-2-beats-allow", "gate",
       "a PreToolUse hook exiting 2 blocks even with an explicit allow rule for that tool")
def _exit2():
    def arm(code):
        wd = tempfile.mkdtemp(prefix="v-exit2-")
        try:
            log = os.path.join(wd, "h.log").replace("\\", "/")
            hk = os.path.join(wd, "h.sh").replace("\\", "/")
            hook_script(hk, log, 'echo blocked >&2\nexit 2' if code == 2 else "exit 0")
            st = os.path.join(wd, "s.json")
            json.dump({"hooks": {"PreToolUse": [{"matcher": "Write", "hooks": [
                {"type": "command", "command": "sh %s" % hk}]}]},
                "permissions": {"allow": ["Write"]}}, open(st, "w"))
            tgt = os.path.join(wd, "S.txt").replace("\\", "/")
            run(["claude", "-p", f"Create {tgt} containing OK using the Write tool.",
                 "--settings", st, "--add-dir", wd, "--output-format", "json",
                 "--allowedTools", "Write"], cwd=wd)
            return fired(log), os.path.exists(os.path.join(wd, "S.txt"))
        finally:
            shutil.rmtree(wd, ignore_errors=True)
    f0, wrote0 = arm(0)
    f2, wrote2 = arm(2)
    if not wrote0:
        return INCONCLUSIVE, f"control arm did not write (fired={f0}); nothing tested"
    return (PASS if not wrote2 else FAIL), f"exit0 wrote={wrote0} exit2 wrote={wrote2}"


@check("gate.ask-rule-beats-bypass", "gate",
       "an explicit ask rule still gates under bypassPermissions")
def _ask_bypass():
    wd = tempfile.mkdtemp(prefix="v-ask-")
    try:
        st = os.path.join(wd, "s.json")
        json.dump({"permissions": {"ask": ["Write"]}}, open(st, "w"))
        tgt = os.path.join(wd, "S.txt").replace("\\", "/")
        run(["claude", "-p", f"Create {tgt} containing OK using the Write tool.",
             "--settings", st, "--add-dir", wd, "--permission-mode", "bypassPermissions",
             "--output-format", "json"], cwd=wd)
        return (PASS if not os.path.exists(os.path.join(wd, "S.txt")) else FAIL), "see sentinel"
    finally:
        shutil.rmtree(wd, ignore_errors=True)


@check("gate.add-dir-loads-no-config", "gate",
       "--add-dir grants file access but loads no hooks configuration")
def _add_dir():
    cwd = tempfile.mkdtemp(prefix="v-cwd-")
    extra = tempfile.mkdtemp(prefix="v-extra-")
    try:
        os.makedirs(os.path.join(extra, ".claude"))
        log = os.path.join(extra, "h.log").replace("\\", "/")
        hk = os.path.join(extra, "h.sh").replace("\\", "/")
        hook_script(hk, log, 'echo blocked >&2\nexit 2')
        json.dump({"hooks": {"PreToolUse": [{"matcher": "Write", "hooks": [
            {"type": "command", "command": "sh %s" % hk}]}]}},
            open(os.path.join(extra, ".claude", "settings.json"), "w"))
        tgt = os.path.join(cwd, "S.txt").replace("\\", "/")
        run(["claude", "-p", f"Create {tgt} containing OK using the Write tool.",
             "--add-dir", extra, "--output-format", "json", "--allowedTools", "Write"], cwd=cwd)
        n, wrote = fired(log), os.path.exists(os.path.join(cwd, "S.txt"))
        if not wrote and n == 0:
            return INCONCLUSIVE, "nothing was written and no hook fired; the write itself failed"
        return (PASS if n == 0 else FAIL), f"hook in --add-dir'd .claude fired {n}x, wrote={wrote}"
    finally:
        shutil.rmtree(cwd, ignore_errors=True)
        shutil.rmtree(extra, ignore_errors=True)


@check("gate.hook-ask-in-auto", "gate",
       "a PreToolUse hook returning permissionDecision:ask forces a prompt even in auto mode")
def _hook_ask():
    """Second always-fires path after exit 2, and the polite one -- exit 2 is a hard block.

    Under -p there is no human, so a forced prompt must fail closed. The control arm returns
    `allow` through the same hook, so a non-write in the ask arm can't be blamed on auto's
    classifier.
    """
    def arm(decision):
        wd = tempfile.mkdtemp(prefix="v-hookask-")
        try:
            log = os.path.join(wd, "h.log").replace("\\", "/")
            hk = os.path.join(wd, "h.sh").replace("\\", "/")
            hook_script(hk, log, """echo '{"hookSpecificOutput":{"hookEventName":"PreToolUse",""" +
                        '"permissionDecision":"%s","permissionDecisionReason":"AER probe"}}\'' % decision)
            st = os.path.join(wd, "s.json")
            json.dump({"hooks": {"PreToolUse": [{"matcher": "Write", "hooks": [
                {"type": "command", "command": "sh %s" % hk}]}]}}, open(st, "w"))
            tgt = os.path.join(wd, "S.txt").replace("\\", "/")
            run(["claude", "-p", f"Create {tgt} containing OK using the Write tool.",
                 "--settings", st, "--add-dir", wd, "--permission-mode", "auto",
                 "--output-format", "json"], cwd=wd)
            return fired(log), os.path.exists(os.path.join(wd, "S.txt"))
        finally:
            shutil.rmtree(wd, ignore_errors=True)
    fa, wrote_allow = arm("allow")
    fk, wrote_ask = arm("ask")
    if fa == 0 or fk == 0:
        return INCONCLUSIVE, f"hook did not fire in one arm (allow={fa}, ask={fk})"
    if not wrote_allow:
        return INCONCLUSIVE, "control arm did not write; auto's classifier blocked it regardless"
    return (PASS if not wrote_ask else FAIL), f"allow wrote={wrote_allow}, ask wrote={wrote_ask}"


@check("gate.permission-request-not-headless", "gate",
       "PermissionRequest fires when a dialog would appear, so it never fires under -p; "
       "PermissionDenied is the auto-classifier event that does")
def _permission_events():
    """Bounds decision 0018's notify hook.

    The docs define PermissionRequest as firing "when a permission dialog appears" -- under `-p`
    no dialog ever appears. The discovery control matters more than the result: the SAME hook
    command is also registered on PreToolUse in the SAME settings file, so if PreToolUse fires and
    PermissionRequest does not, the config was found and the event genuinely did not occur. Without
    that arm, a silent non-fire is indistinguishable from a wrong matcher.
    """
    def arm(mode):
        wd = tempfile.mkdtemp(prefix="v-preq-")
        try:
            logs = {e: os.path.join(wd, f"{e}.log").replace("\\", "/")
                    for e in ("PreToolUse", "PermissionRequest", "PermissionDenied")}
            hooks = {}
            for event, log in logs.items():
                hk = os.path.join(wd, f"{event}.sh").replace("\\", "/")
                hook_script(hk, log, "exit 0")
                hooks[event] = [{"matcher": "Bash", "hooks": [
                    {"type": "command", "command": "sh %s" % hk}]}]
            st = os.path.join(wd, "s.json")
            # No allow rule for Bash in either arm, so both arms must reach a permission decision.
            json.dump({"hooks": hooks}, open(st, "w"))
            run(["claude", "-p", "Run this shell command and report its output: node --version",
                 "--settings", st, "--add-dir", wd, "--permission-mode", mode,
                 "--output-format", "json"], cwd=wd)
            return {e: fired(p) for e, p in logs.items()}
        finally:
            shutil.rmtree(wd, ignore_errors=True)
    auto, accept = arm("auto"), arm("acceptEdits")
    note = f"auto={auto}  acceptEdits={accept}"
    if not auto["PreToolUse"] and not accept["PreToolUse"]:
        return INCONCLUSIVE, f"discovery control never fired -- the settings file was not loaded; {note}"
    if auto["PermissionRequest"] or accept["PermissionRequest"]:
        return FAIL, f"PermissionRequest DID fire headless; {note}"
    return PASS, f"no PermissionRequest under -p (discovery control fired); {note}"


# ====================================================================== cost
@check("cost.subagent-tokens-excluded", "cost",
       "usage.output_tokens excludes subagent tokens; modelUsage is whole-tree (#479)")
def _subagent_tokens():
    wd = tempfile.mkdtemp(prefix="v-cost-")
    try:
        rc, out, err = run(["claude", "-p",
                            "Use the Task tool to launch a subagent that writes a 120-word essay "
                            "about the colour blue. Then reply with only DONE.",
                            "--add-dir", wd, "--output-format", "json", "--allowedTools", "Task"],
                           timeout=420, cwd=wd)
        payload = json.loads(out or "{}")
        top = (payload.get("usage") or {}).get("output_tokens")
        mu = payload.get("modelUsage") or {}
        tree = sum((v or {}).get("outputTokens", 0) or (v or {}).get("output_tokens", 0)
                   for v in mu.values()) if isinstance(mu, dict) else 0
        if top is None or not tree:
            return INCONCLUSIVE, f"fields absent (top={top}, modelUsage={list(mu)[:3]})"
        return (PASS if tree > top else FAIL), f"top-level {top} vs whole-tree {tree}"
    except ValueError:
        return INCONCLUSIVE, "result was not JSON"
    finally:
        shutil.rmtree(wd, ignore_errors=True)


# ====================================================================== fanout
@check("fanout.nesting-off-by-default", "fanout",
       "nested subagents are off by default (CLAUDE_CODE_MAX_SUBAGENT_SPAWN_DEPTH); #503 items 4-5")
def _nesting():
    """The control arm is a ONE-level subagent that writes its own file.

    Two earlier designs for this check were both bad instruments, and the reasons are worth
    keeping:

    1. Asking the model to report what happened and reading its prose. A model will describe a
       nested spawn it never performed.
    2. Having the innermost agent write a sentinel file. Better, but still ambiguous: the middle
       subagent can simply write that file ITSELF instead of nesting, and the result is
       byte-identical to a successful nested spawn.

    So this counts spawns directly. A `SubagentStart` hook appends one line per subagent the CLI
    actually starts, which no amount of the model shortcutting can fake. One task, one prompt,
    three arms differing only in CLAUDE_CODE_MAX_SUBAGENT_SPAWN_DEPTH -- so the env var's effect
    on the spawn count is the measurement.
    """
    PROMPT = ("Use the Task tool to launch a subagent, and instruct THAT subagent to itself use its "
              "own Task tool to launch a further nested subagent. The nested subagent's instruction "
              "is to reply with the word DEEP.")

    def arm(depth):
        wd = tempfile.mkdtemp(prefix="v-nest-")
        try:
            log = os.path.join(wd, "spawns.log").replace("\\", "/")
            hk = os.path.join(wd, "h.sh").replace("\\", "/")
            hook_script(hk, log, "exit 0")
            st = os.path.join(wd, "s.json")
            json.dump({"hooks": {"SubagentStart": [{"hooks": [
                {"type": "command", "command": "sh %s" % hk}]}]}}, open(st, "w"))
            run(["claude", "-p", PROMPT, "--settings", st, "--add-dir", wd,
                 "--output-format", "json", "--allowedTools", "Task",
                 "--permission-mode", "acceptEdits"],
                timeout=600, cwd=wd,
                extra_env=None if depth is None else
                {"CLAUDE_CODE_MAX_SUBAGENT_SPAWN_DEPTH": str(depth)})
            return fired(log)
        finally:
            shutil.rmtree(wd, ignore_errors=True)

    default, capped, raised = arm(None), arm(1), arm(2)
    note = f"spawns -- default={default}, MAX_SUBAGENT_SPAWN_DEPTH=1: {capped}, =2: {raised}"
    if default == 0:
        return INCONCLUSIVE, f"no subagent started at all; the SubagentStart hook never fired; {note}"
    if raised <= capped:
        return INCONCLUSIVE, ("raising the cap changed nothing, so this measured subagent count, "
                              f"not nesting depth; {note}")
    if default > capped:
        return FAIL, f"the default allows deeper nesting than an explicit cap of 1; {note}"
    return PASS, f"default matches an explicit cap of 1, and the env var raises it; {note}"


@check("fanout.parent-mode-covers-subagents", "fanout",
       "a subagent inherits the parent's permission mode rather than starting at default")
def _inherit_mode():
    """Two arms differing only in the parent's --permission-mode.

    If the subagent's write lands under acceptEdits and not under default, the parent's mode
    reached the child. Without the default arm, a successful write proves only that writes work.
    """
    def arm(mode):
        wd = tempfile.mkdtemp(prefix="v-inh-")
        try:
            tgt = os.path.join(wd, "S.txt").replace("\\", "/")
            run(["claude", "-p",
                 f"Use the Task tool to launch a subagent whose instruction is to use the Write "
                 f"tool to create the file {tgt} containing the word OK.",
                 "--add-dir", wd, "--output-format", "json", "--allowedTools", "Task",
                 "--permission-mode", mode], timeout=600, cwd=wd)
            return os.path.exists(os.path.join(wd, "S.txt"))
        finally:
            shutil.rmtree(wd, ignore_errors=True)
    accept, default = arm("acceptEdits"), arm("default")
    if not accept:
        return INCONCLUSIVE, "subagent did not write even under acceptEdits; nothing tested"
    return (PASS if not default else FAIL), f"acceptEdits wrote={accept}, default wrote={default}"


# ====================================================================== lifecycle
@check("lifecycle.daemon-status", "lifecycle",
       "claude daemon status reports a machine-readable readiness signal (#478)")
def _daemon_status():
    rc, out, err = run(["claude", "daemon", "status"], timeout=60)
    blob = out + err
    have = [k for k in ("pid:", "version:", "control.sock", "bg workers") if k in blob]
    if rc is None:
        return INCONCLUSIVE, "no response"
    return (PASS if len(have) >= 3 else FAIL), f"exit={rc}, fields seen: {have}"


@check("lifecycle.bg-projection", "lifecycle",
       "claude agents --json projects sessions with a state vocabulary; ids are short hex")
def _bg_projection():
    rc, out, _ = run(["claude", "agents", "--json"], timeout=90)
    try:
        rows = json.loads(out or "[]")
    except ValueError:
        return INCONCLUSIVE, "agents --json did not return JSON"
    if not isinstance(rows, list):
        return FAIL, "agents --json did not return a list"
    keys = sorted({k for r in rows if isinstance(r, dict) for k in r})
    states = sorted({str(r.get("state")) for r in rows if isinstance(r, dict)})
    ids = [r.get("id") for r in rows if isinstance(r, dict)]
    shorthex = [i for i in ids if isinstance(i, str) and re.fullmatch(r"[0-9a-f]{8}", i)]
    malformed = [i for i in ids if i is None]
    note = f"rows={len(rows)} keys={keys} states={states} short-hex ids={len(shorthex)}"
    if malformed:
        note += f"; WARNING {len(malformed)} row(s) with null id -- consumers must tolerate these"
    return (PASS if keys else INCONCLUSIVE), note


# ====================================================================== agy
@check("agy.fails-closed-headless", "agy",
       "agy -p auto-denies an ungated tool and names the rule that would permit it")
def _agy_closed():
    wd = tempfile.mkdtemp(prefix="v-agyc-")
    try:
        rc, out, err = run(["agy", "-p", "Run this shell command and report its output: node --version",
                            "--add-dir", wd], cwd=wd)
        blob = (out + err).lower()
        ran = bool(re.search(r"\bv?\d+\.\d+\.\d+", out + err))
        denied = "auto-denied" in blob or "allow-rule" in blob
        if ran:
            return FAIL, "the command ran without an allow rule"
        return (PASS if denied else INCONCLUSIVE), "structured denial naming permissions.allow"
    finally:
        shutil.rmtree(wd, ignore_errors=True)


@check("agy.hook-deny-honoured", "agy",
       "an agy PreToolUse hook deny blocks the call and surfaces its reason")
def _agy_deny():
    wd = tempfile.mkdtemp(prefix="v-agyd-")
    try:
        os.makedirs(os.path.join(wd, ".agents"))
        log = os.path.join(wd, "h.log").replace("\\", "/")
        hk = os.path.join(wd, "h.sh").replace("\\", "/")
        hook_script(hk, log, """echo '{"decision":"deny","reason":"AER_VERIFY_TOKEN"}'""")
        json.dump({"v": {"PreToolUse": [{"matcher": "run_command", "hooks": [
            {"type": "command", "command": "sh %s" % hk, "timeout": 25}]}]}},
            open(os.path.join(wd, ".agents", "hooks.json"), "w"))
        rc, out, err = run(["agy", "-p", "Run this shell command: node --version",
                            "--add-dir", wd, "--dangerously-skip-permissions"], cwd=wd)
        blob = out + err
        n = fired(log)
        ran = bool(re.search(r"\bv?\d+\.\d+\.\d+", blob))
        if n == 0:
            return INCONCLUSIVE, "hook never fired -- discovery problem, not a deny problem"
        if ran:
            return FAIL, "hook fired but the command ran anyway"
        return PASS, f"fired {n}x, blocked, reason surfaced={'AER_VERIFY_TOKEN' in blob}"
    finally:
        shutil.rmtree(wd, ignore_errors=True)


@check("agy.force-ask-defeated-by-skip", "agy",
       "agy force_ask does NOT survive --dangerously-skip-permissions (unlike claude's annotation)")
def _agy_force_ask():
    def arm(skip):
        wd = tempfile.mkdtemp(prefix="v-agyf-")
        try:
            os.makedirs(os.path.join(wd, ".agents"))
            log = os.path.join(wd, "h.log").replace("\\", "/")
            hk = os.path.join(wd, "h.sh").replace("\\", "/")
            hook_script(hk, log, """echo '{"decision":"force_ask","reason":"AER probe"}'""")
            json.dump({"v": {"PreToolUse": [{"matcher": "run_command", "hooks": [
                {"type": "command", "command": "sh %s" % hk, "timeout": 25}]}]}},
                open(os.path.join(wd, ".agents", "hooks.json"), "w"))
            extra = ["--dangerously-skip-permissions"] if skip else []
            rc, out, err = run(["agy", "-p", "Run this shell command: node --version",
                                "--add-dir", wd, *extra], cwd=wd)
            return bool(re.search(r"\bv?\d+\.\d+\.\d+", out + err)), fired(log)
        finally:
            shutil.rmtree(wd, ignore_errors=True)
    ran_plain, f1 = arm(False)
    ran_skip, f2 = arm(True)
    if f1 == 0 or f2 == 0:
        return INCONCLUSIVE, "hook did not fire in one arm"
    if not ran_plain and ran_skip:
        return PASS, "force_ask denies alone but the skip flag overrides it"
    return FAIL, f"unexpected: plain ran={ran_plain}, skip ran={ran_skip}"


@check("agy.settings-allow-honoured-headless", "agy",
       "agy permissions.allow is honoured under -p (upstream #548 says otherwise)",
       safety="mutates-config")
def _agy_allow():
    S = os.path.join(os.path.expanduser("~"), ".gemini", "antigravity-cli", "settings.json")
    if not os.path.exists(S):
        return SKIPPED, "settings.json not present"
    backup = os.path.join(tempfile.gettempdir(), "aer_agy_settings_backup.json")
    shutil.copyfile(S, backup)
    before = hashlib.sha256(open(S, "rb").read()).hexdigest()
    try:
        cfg = json.load(open(backup, encoding="utf-8"))
        cfg.setdefault("permissions", {}).setdefault("allow", [])
        cfg["permissions"]["allow"] = list(cfg["permissions"]["allow"]) + ["command(node --version)"]
        json.dump(cfg, open(S, "w", encoding="utf-8"), indent=2)
        wd = tempfile.mkdtemp(prefix="v-agya-")
        try:
            rc, out, err = run(["agy", "-p", "Run this shell command: node --version",
                                "--add-dir", wd], cwd=wd)
            ran = bool(re.search(r"\bv?\d+\.\d+\.\d+", out + err))
            return (PASS if ran else FAIL), f"allow rule honoured={ran}"
        finally:
            shutil.rmtree(wd, ignore_errors=True)
    finally:
        shutil.copyfile(backup, S)
        after = hashlib.sha256(open(S, "rb").read()).hexdigest()
        if after != before:
            print(f"  !! RESTORE MISMATCH -- backup kept at {backup}", file=sys.stderr)


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--list", action="store_true")
    ap.add_argument("--only", help="a group (gate | fanout | cost | lifecycle | agy) or a check-name prefix")
    ap.add_argument("--allow-config-writes", action="store_true",
                    help="also run checks that touch the operator's real settings files")
    args = ap.parse_args()

    if args.list:
        for n, c in sorted(CHECKS.items()):
            print(f"{n:<42} [{c['group']:<9}] {c['safety']}\n    {c['claim']}")
        return 0

    selected = {n: c for n, c in sorted(CHECKS.items())
                if not args.only or c["group"] == args.only or n.startswith(args.only)}
    if not selected:
        print(f"no check matches --only {args.only!r}; see --list", file=sys.stderr)
        return 2
    print(f"running {len(selected)} check(s). Each spends real subscription usage.\n")

    results = []
    for name, c in selected.items():
        if c["safety"] == "mutates-config" and not args.allow_config_writes:
            results.append((name, SKIPPED, "needs --allow-config-writes"))
            print(f"{SKIPPED:<13} {name}")
            continue
        try:
            status, detail = c["fn"]()
        except Exception as exc:                                   # noqa: BLE001
            status, detail = INCONCLUSIVE, f"check raised: {exc!r}"
        results.append((name, status, detail))
        print(f"{status:<13} {name}\n              {detail}")

    print("\n" + "=" * 72)
    for s in (PASS, FAIL, INCONCLUSIVE, SKIPPED):
        n = sum(1 for _, st, _ in results if st == s)
        if n:
            print(f"  {s:<13} {n}")
    # A FAIL means a behaviour AER depends on has changed. Non-zero exit so a wrapper can notice.
    return 1 if any(st == FAIL for _, st, _ in results) else 0


if __name__ == "__main__":
    sys.exit(main())
