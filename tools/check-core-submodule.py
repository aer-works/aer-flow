"""Fails loudly when external/aer-core is an uninitialized submodule (#845).

`cargo build` against the empty directory exits 0 having built nothing, and the failure
surfaces later in whichever downstream shape runs first -- CS0234 from dotnet build, or
daemon tests that silently produce zero turns and die on poll timeouts. Three separate
sessions paid that diagnosis in one night (#845 records each); every fresh
`git worktree add` starts in this state, not only fresh clones.
"""
import pathlib
import sys

core = pathlib.Path(__file__).resolve().parents[1] / "external" / "aer-core"
if not (core / "Cargo.toml").is_file():
    print("!! external/aer-core has no Cargo.toml -- the submodule is not initialized in this")
    print("   checkout. Every fresh `git worktree add` starts this way, not only fresh clones.")
    print("   Fix: git submodule update --init   (then re-run this task)")
    sys.exit(1)
