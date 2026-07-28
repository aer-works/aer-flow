"""Run every local gate, report one verdict, exit once.

WHY THIS EXISTS, and it is not convenience. Each gate below already reports correctly on its own.
The failure this removes is in how they get READ: a checker was run, its stdout filtered for a
success token, and the filtered text reported as green while the process exited 1. That has now
happened twice on this repo -- `audit-completeness` was reported passing 16/16 while exiting 1
because its output was filtered for OK/FAIL and its failure prefix is `!!`, and `audit-recordonce`
was reported as exit 0 from a stale shell variable while it was flagging 8 duplications.

Both times the gate worked and the reading of it did not. So this collapses seven exit codes into
one: there is no per-gate status to sample, no shell variable to go stale between commands, and the
only thing worth reporting is this process's own exit code.

Run every gate even after one fails -- fail-fast hides the other six, and a session that has to
re-run the whole set to discover the next problem starts filtering output again.
"""
import subprocess
import sys

# Order is cheapest-first so a broken tree reports in seconds rather than after the UI suite.
GATES = [
    "fmt-check",
    "lint",
    "audit-completeness",
    "audit-selfcheck",
    "audit-controls",
    "audit-recordonce",
    "test",
]

PASS_MARK = "GATES: PASS"
FAIL_MARK = "GATES: FAIL"


def run_gates(names, runner):
    """Run each gate, print a per-gate line, return the names that failed."""
    failed = []
    for name in names:
        code = runner(name)
        print(f"  {'pass' if code == 0 else 'FAIL':>4}  {name}  (exit {code})", flush=True)
        if code != 0:
            failed.append(name)
    return failed


def summarise(names, failed):
    """The single line worth reading. Exit code, not this text, is the contract."""
    if failed:
        return f"{FAIL_MARK} {len(failed)} of {len(names)} -- {', '.join(failed)}"
    return f"{PASS_MARK} {len(names)} of {len(names)}"


def pixi_runner(name):
    # Output is inherited, not captured: a captured gate would have to be re-printed to be readable,
    # and re-printing is where the filtering that caused this file creeps back in.
    return subprocess.run(["pixi", "run", name], check=False).returncode


def selftest():
    """The control arm. An aggregator that cannot go red is a green light with extra steps.

    Discriminating in both directions: a runner where every gate passes must produce PASS, and one
    where a single gate fails must produce FAIL, name it, and exit non-zero. Without the second arm
    this file would keep reporting PASS after the summary logic broke, which is the exact class of
    fault it exists to stop.

    Covers the summary logic only. That `pixi run <gate>`'s own exit code survives the subprocess
    boundary was proven end to end by introducing a real formatting violation and watching
    `fmt-check` come back `(exit 2)` with the other six still reported -- see the commit that added
    this file.
    """
    ok = True

    failed = run_gates(["a", "b"], lambda name: 0)
    line = summarise(["a", "b"], failed)
    if failed or not line.startswith(PASS_MARK):
        print(f"  control FAILED: an all-pass run did not report pass -- {line}")
        ok = False

    failed = run_gates(["a", "b"], lambda name: 1 if name == "b" else 0)
    line = summarise(["a", "b"], failed)
    if failed != ["b"] or not line.startswith(FAIL_MARK) or "b" not in line:
        print(f"  control FAILED: a failing gate was not reported -- {line}")
        ok = False

    print("selftest: pass" if ok else "selftest: FAIL")
    return 0 if ok else 1


def main():
    if "--selftest" in sys.argv:
        return selftest()

    failed = run_gates(GATES, pixi_runner)
    print()
    print(summarise(GATES, failed))
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
