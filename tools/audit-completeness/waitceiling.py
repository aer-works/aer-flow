"""Audit checker for fixed sub-60s wait ceilings added in test code (#910).

See issue #910 for the wait-ceiling taxonomy and rationale.
"""
from __future__ import annotations

import os
import re
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]

# Real wait idioms enumerated from tests/ on 2026-08-05 per #910
WAIT_IDIOMS = (
    "WaitForConditionAsync",
    "WaitForMarkerAsync",
    "WaitForExit",
    "Task.Delay",
    "Thread.Sleep",
    "new CancellationTokenSource(",
    "CancellationTokenSource",
    "WaitOne",
    "AssertMarkerNeverAppearsAsync",
    "AcquireWithin",
    "WaitAsync",
    "WaitForLogConditionAsync",
    "WaitForEventAsync",
)

GIT_TEXT = {"encoding": "utf-8", "errors": "replace"}


def find_wait_ok_marker(text: str) -> dict[str, str] | None:
    """Check if text contains a wait-ok: marker and extract reason if present."""
    if not text or "wait-ok" not in text:
        return None

    m = re.search(r"wait-ok:(.*)", text)
    if m:
        raw_reason = m.group(1)
        if "*/" in raw_reason:
            raw_reason = raw_reason.split("*/")[0]
        if "-->" in raw_reason:
            raw_reason = raw_reason.split("-->")[0]
        reason = raw_reason.strip()
        return {"reason": reason}

    if re.search(r"\bwait-ok\b", text):
        return {"reason": ""}

    return None


def get_sub_60s_literal(line_text: str, has_context: bool) -> str | None:
    """Return description of sub-60s duration literal if present, else None."""
    for m in re.finditer(r"\bTimeSpan\.FromSeconds\(\s*([0-9]+(?:\.[0-9]+)?)\s*\)", line_text):
        val = float(m.group(1))
        if val < 60:
            return f"TimeSpan.FromSeconds({m.group(1)})"

    for m in re.finditer(r"\bTimeSpan\.FromMilliseconds\(\s*([0-9]+(?:\.[0-9]+)?)\s*\)", line_text):
        val = float(m.group(1))
        if val < 60000:
            return f"TimeSpan.FromMilliseconds({m.group(1)})"

    for m in re.finditer(r"\bTimeSpan\.FromMinutes\(\s*([0-9]+(?:\.[0-9]+)?)\s*\)", line_text):
        val = float(m.group(1))
        if val < 1:
            return f"TimeSpan.FromMinutes({m.group(1)})"

    if has_context:
        escaped_idioms = [re.escape(i.rstrip("(")) for i in WAIT_IDIOMS]
        pattern = r"(?:|\.)(?:" + "|".join(escaped_idioms) + r")\s*\(\s*([0-9]+)\s*[\),]"
        for m in re.finditer(pattern, line_text):
            val = int(m.group(1))
            if val < 60000:
                return f"{val}ms"

    return None


def inspect_lines(added_items: list[tuple[str, int, str]], file_lines_map: dict[str, list[str]]) -> list[str]:
    """Inspect added lines against file content. Return list of error strings."""
    faults = []
    for rel_path, lineno, line_text in added_items:
        lines = file_lines_map.get(rel_path, [])
        prev_line_text = lines[lineno - 2] if (lineno > 1 and len(lines) >= lineno - 1) else ""

        marker = find_wait_ok_marker(line_text)
        if marker is None and prev_line_text:
            marker = find_wait_ok_marker(prev_line_text)

        if marker is not None:
            if not marker["reason"]:
                faults.append(
                    f"{rel_path}:{lineno}: a comment contains 'wait-ok:' with an empty reason, "
                    "so it exempts nothing. Expected 'wait-ok: <reason>'."
                )
            continue

        has_context = any(idiom in line_text for idiom in WAIT_IDIOMS)
        short_literal = get_sub_60s_literal(line_text, has_context)

        if has_context and short_literal:
            faults.append(
                f"{rel_path}:{lineno}: fixed wait ceiling literal '{short_literal}' under 60s in test code. "
                "Raise ceiling to >=60s, or mark 'wait-ok: <reason>' on the line or line above."
            )

    return faults


def _selftest() -> int:
    """Polarity arms against fixture content."""
    failures = []

    # (a) unmarked added short wait -> fires
    file_map_a = {"tests/FooTests.cs": ["class Foo {", "    await Task.Delay(TimeSpan.FromSeconds(5));", "}"]}
    added_a = [("tests/FooTests.cs", 2, "    await Task.Delay(TimeSpan.FromSeconds(5));")]
    faults_a = inspect_lines(added_a, file_map_a)
    if not faults_a or "TimeSpan.FromSeconds(5)" not in faults_a[0]:
        failures.append("Arm (a) FAIL: unmarked added short wait did not fire")

    # (b) same line with wait-ok: reason -> silent
    file_map_b = {"tests/FooTests.cs": ["class Foo {", "    await Task.Delay(TimeSpan.FromSeconds(5)); // wait-ok: testing fast timeout", "}"]}
    added_b = [("tests/FooTests.cs", 2, "    await Task.Delay(TimeSpan.FromSeconds(5)); // wait-ok: testing fast timeout")]
    faults_b = inspect_lines(added_b, file_map_b)
    if faults_b:
        failures.append("Arm (b) FAIL: line with valid wait-ok marker fired")

    # (c) marker with empty reason -> fails loudly as its own fault, distinct from (a)
    file_map_c = {"tests/FooTests.cs": ["class Foo {", "    await Task.Delay(TimeSpan.FromSeconds(5)); // wait-ok:", "}"]}
    added_c = [("tests/FooTests.cs", 2, "    await Task.Delay(TimeSpan.FromSeconds(5)); // wait-ok:")]
    faults_c = inspect_lines(added_c, file_map_c)
    if not faults_c or "empty reason" not in faults_c[0]:
        failures.append("Arm (c) FAIL: marker with empty reason did not fail loudly as empty reason fault")

    # (d) added wait >=60s -> silent
    file_map_d = {"tests/FooTests.cs": ["class Foo {", "    await Task.Delay(TimeSpan.FromSeconds(60));", "}"]}
    added_d = [("tests/FooTests.cs", 2, "    await Task.Delay(TimeSpan.FromSeconds(60));")]
    faults_d = inspect_lines(added_d, file_map_d)
    if faults_d:
        failures.append("Arm (d) FAIL: wait >=60s fired")

    # (e) added sub-60s TimeSpan with NO wait context on the line -> silent
    file_map_e = {"tests/FooTests.cs": ["class Foo {", "    var ts = TimeSpan.FromSeconds(5);", "}"]}
    added_e = [("tests/FooTests.cs", 2, "    var ts = TimeSpan.FromSeconds(5);")]
    faults_e = inspect_lines(added_e, file_map_e)
    if faults_e:
        failures.append("Arm (e) FAIL: sub-60s TimeSpan without wait context fired")

    # (f) marker on the line above -> silent
    file_map_f = {"tests/FooTests.cs": ["class Foo {", "    // wait-ok: fast timeout test", "    await Task.Delay(TimeSpan.FromSeconds(5));", "}"]}
    added_f = [("tests/FooTests.cs", 3, "    await Task.Delay(TimeSpan.FromSeconds(5));")]
    faults_f = inspect_lines(added_f, file_map_f)
    if faults_f:
        failures.append("Arm (f) FAIL: wait with wait-ok marker on line above fired")

    if failures:
        print("waitceiling selftest: FAIL -- " + "; ".join(failures), file=sys.stderr)
        return 1
    print("waitceiling selftest: pass (all 6 arms discriminate)")
    return 0


def added_test_lines(base: str) -> tuple[list[tuple[str, int, str]], dict[str, list[str]]]:
    """Get added lines in tests/**/*.cs relative to base, and current file contents."""
    out = subprocess.run(
        ["git", "diff", "--unified=0", base],
        capture_output=True, text=True, check=True, cwd=ROOT, **GIT_TEXT
    ).stdout

    added_items: list[tuple[str, int, str]] = []
    current_file = None
    current_line_no = 0

    hunk_re = re.compile(r"^@@\s+-[0-9,]+\s+\+([0-9]+)(?:,[0-9]+)?\s+@@")

    for line in out.splitlines():
        if line.startswith("+++"):
            path = line[4:].strip()
            if path == "/dev/null":
                current_file = None
            else:
                path = path.strip('"')
                if path.startswith("b/"):
                    path = path[2:]
                current_file = path if (path.startswith("tests/") and path.endswith(".cs")) else None
        elif line.startswith("@@"):
            if current_file:
                m = hunk_re.match(line)
                if m:
                    current_line_no = int(m.group(1))
        elif current_file and line.startswith("+") and not line.startswith("+++"):
            added_items.append((current_file, current_line_no, line[1:]))
            current_line_no += 1

    file_lines_map: dict[str, list[str]] = {}
    needed_files = {path for path, _, _ in added_items}
    for rel_path in needed_files:
        p = ROOT / rel_path
        if p.exists():
            file_lines_map[rel_path] = p.read_text(encoding="utf-8", errors="replace").splitlines()

    return added_items, file_lines_map


def main(argv: list[str]) -> int:
    for stream in (sys.stdout, sys.stderr):
        if hasattr(stream, "reconfigure"):
            stream.reconfigure(errors="replace")

    if "--selftest" in argv:
        return _selftest()

    base = argv[1] if len(argv) > 1 and not argv[1].startswith("-") else "origin/main"
    try:
        added_items, file_lines_map = added_test_lines(base)
    except subprocess.CalledProcessError as exc:
        print(f"!! cannot diff against '{base}' -- {exc.stderr.strip()}", file=sys.stderr)
        return 1

    print(f"waitceiling: {len(added_items)} added line(s) in tests/ examined against {base}")
    faults = inspect_lines(added_items, file_lines_map)
    if faults:
        print(f" !! {len(faults)} problem(s) found:\n", file=sys.stderr)
        for f in faults:
            print(f"  {f}", file=sys.stderr)
        return 1

    print(" OK no fixed sub-60s wait ceilings found in added test lines")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
