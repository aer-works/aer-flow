#!/usr/bin/env bash
# Runs Aer.Mobile's default test suite through the same `go` shim as mobile-build.sh —
# `flutter test` builds the tailscale native asset for the *host*, and the hook runner
# scrubs the environment the same way it does for APK builds (the why lives at the top of
# scripts/mobile-build.sh). On Windows the host build additionally needs the #958 stub:
# run `pixi run mobile-patch` once per pub-cache fill, and have a C toolchain (mingw-w64
# gcc) on PATH for cgo.
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
MOBILE="$REPO_ROOT/src/Aer.Mobile"
SHIM_DIR="$MOBILE/build/.toolchain-shim"

# Same WSL-capture refusal as mobile-build.sh (#469): a WSL bash cannot drive the Windows
# toolchain this script configures.
source "$REPO_ROOT/scripts/lib/wsl-capture.sh"
if looks_like_wsl_capture /proc/version "$REPO_ROOT"; then
  echo "error: mobile-test was invoked under WSL bash, but this is a Windows checkout (#469)." >&2
  echo "  Open a Git Bash window (not WSL) and run: pixi run mobile-test" >&2
  exit 1
fi

source "$REPO_ROOT/scripts/lib/toolchain-shim.sh"
setup_go_toolchain_shim "$SHIM_DIR" || exit 1

cd "$MOBILE"
exec flutter test --exclude-tags journey "$@"
