#!/usr/bin/env python
"""Phase 3 documentation reorg audit check.

Checks tools/audit-completeness/phase3-ledger.json to verify that all documentation reorg items
(disposition fix or archive) have been resolved with evidence.

Enforces:
1. Every entry with status 'done' MUST have a non-empty 'evidence_link' field.
2. Exits 1 while any entry is 'pending', printing remaining count and pending paths.
3. Exits 0 when 0 entries are pending.
"""

import sys
import json
import pathlib
import tempfile

DEFAULT_LEDGER_PATH = pathlib.Path(__file__).parent / "phase3-ledger.json"


def audit_phase3_ledger(ledger_path: pathlib.Path) -> tuple[int, list[dict], str]:
    """Audits the Phase 3 ledger.

    Returns:
        (exit_code, pending_entries, message)
        exit_code is 1 if invalid entries or pending entries exist, 0 otherwise.
    """
    if not ledger_path.exists():
        return 1, [], f"FAIL: ledger file does not exist: {ledger_path}"

    try:
        data = json.loads(ledger_path.read_text(encoding="utf-8"))
    except Exception as err:
        return 1, [], f"FAIL: unable to parse ledger JSON: {err}"

    if not isinstance(data, list):
        return 1, [], "FAIL: ledger root JSON must be a list"

    invalid_done = []
    pending_entries = []

    for entry in data:
        status = entry.get("status")
        path = entry.get("path", "<unknown>")
        if status == "done":
            evidence_link = entry.get("evidence_link", "")
            if not isinstance(evidence_link, str) or not evidence_link.strip():
                invalid_done.append(path)
        elif status == "pending":
            pending_entries.append(entry)

    if invalid_done:
        msg = f"FAIL: {len(invalid_done)} entry/entries marked 'done' without a non-empty 'evidence_link':\n"
        for p in invalid_done:
            msg += f"  - {p}\n"
        return 1, pending_entries, msg.rstrip()

    pending_count = len(pending_entries)
    if pending_count > 0:
        msg = f"PHASE3: {pending_count} entries remain\nPending paths:"
        for entry in pending_entries:
            msg += f"\n  - {entry['path']}"
        return 1, pending_entries, msg

    return 0, [], "PHASE3: 0 entries remain (all done)"


def run_selftest() -> int:
    """Inline polarity self-tests for phase3.py."""
    print("Running phase3 inline self-tests...")
    with tempfile.TemporaryDirectory() as tmpdir:
        tmp_path = pathlib.Path(tmpdir)

        # Test 1: Ledger with pending entry -> exits 1
        pending_ledger = [
            {"path": "docs/a.md", "disposition": "fix", "evidence": "text", "status": "pending"}
        ]
        f1 = tmp_path / "pending.json"
        f1.write_text(json.dumps(pending_ledger), encoding="utf-8")
        code, pending, msg = audit_phase3_ledger(f1)
        if code != 1 or len(pending) != 1 or "PHASE3: 1 entries remain" not in msg:
            print(f"Self-test 1 FAILED: expected exit 1 for pending entry, got {code}, msg: {msg}")
            return 1
        print("  - Test 1 (pending entry -> exit 1): pass")

        # Test 2: Ledger with all done entries with valid evidence_link -> exits 0
        done_ledger = [
            {
                "path": "docs/a.md",
                "disposition": "fix",
                "evidence": "text",
                "status": "done",
                "evidence_link": "https://github.com/aer-works/aer-flow/pull/123",
            }
        ]
        f2 = tmp_path / "done.json"
        f2.write_text(json.dumps(done_ledger), encoding="utf-8")
        code, pending, msg = audit_phase3_ledger(f2)
        if code != 0 or len(pending) != 0 or "PHASE3: 0 entries remain" not in msg:
            print(f"Self-test 2 FAILED: expected exit 0 for all done entries, got {code}, msg: {msg}")
            return 1
        print("  - Test 2 (all done with evidence_link -> exit 0): pass")

        # Test 3: Ledger with done entry lacking evidence_link -> exits 1
        done_no_link = [
            {"path": "docs/a.md", "disposition": "fix", "evidence": "text", "status": "done"}
        ]
        f3 = tmp_path / "done_no_link.json"
        f3.write_text(json.dumps(done_no_link), encoding="utf-8")
        code, pending, msg = audit_phase3_ledger(f3)
        if code != 1 or "marked 'done' without a non-empty 'evidence_link'" not in msg:
            print(f"Self-test 3 FAILED: expected exit 1 for done without evidence_link, got {code}, msg: {msg}")
            return 1
        print("  - Test 3 (done without evidence_link -> exit 1): pass")

    print("Phase 3 self-tests: pass")
    return 0


def main(argv=None) -> int:
    argv = argv if argv is not None else sys.argv[1:]
    if "--selftest" in argv or "--test" in argv:
        return run_selftest()

    ledger_path = DEFAULT_LEDGER_PATH
    if argv and not argv[0].startswith("-"):
        ledger_path = pathlib.Path(argv[0])

    code, pending, msg = audit_phase3_ledger(ledger_path)
    print(msg)
    return code


if __name__ == "__main__":
    sys.exit(main())
