#!/usr/bin/env bash
# Builds Aer.Mobile's debug APK through a `go` shim on PATH.
#
# Flutter's native-asset hook runner spawns the tailscale package's build hook -- and in turn the
# Go toolchain -- with a near-empty environment. GOCACHE, LOCALAPPDATA and APPDATA are all absent,
# so Go cannot locate its build cache and fails with:
#
#     build cache is required, but could not be located:
#     GOCACHE is not defined and %LocalAppData% is not defined
#
# It also cannot fall back on `go env -w`, because that config lives under a directory Go resolves
# from the same stripped variables. PATH is the only channel that survives (the hook finds Go via
# `where`/`which`), so a shim earlier on PATH restores the variables and delegates to the real
# toolchain.
#
# This is a Flutter hook-runner problem, unrelated to the epoll patch in scripts/patch-tailscale-dart.sh.
# Retire this wrapper if a Flutter release stops scrubbing the hook environment: delete it, point
# mobile-build back at plain `flutter build apk --debug`, and confirm the build still succeeds.
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
MOBILE="$REPO_ROOT/src/Aer.Mobile"
SHIM_DIR="$MOBILE/build/.toolchain-shim"

# #469: refuse a WSL-captured invocation loudly, naming the real cause.
#
# This script configures the *Windows* Go/Flutter toolchain for a Flutter native-asset hook that
# runs on the Windows host. `pixi run mobile-build` invokes a bare `bash`, and on a Windows machine
# that can resolve to WSL bash before Git Bash. Under WSL the Windows toolchain is not on PATH, so
# the `command -v go` check below fails with "Go not found" -- a misleading message that sent the
# owner looking for a PATH problem that did not exist (#469). Go is installed; it is simply invisible
# from inside WSL, and even with Go present the `*)` branch below would build for the wrong host.
# The decision lives in scripts/lib/wsl-capture.sh so it can be tested off a WSL host.
source "$REPO_ROOT/scripts/lib/wsl-capture.sh"
if looks_like_wsl_capture /proc/version "$REPO_ROOT"; then
  cat >&2 <<'WSLMSG'
error: mobile-build was invoked under WSL bash, but this is a Windows checkout (#469).

  Go and Flutter live on the Windows host and are not on PATH inside WSL, so the
  toolchain checks below would fail with a misleading "Go not found". `bash` resolved
  to WSL before Git Bash. A Windows-style path is not runnable from this WSL prompt
  (no `C:\` drive resolution, `\` is not a path separator here) -- switch shells instead:

      Open a Git Bash window (not WSL) and run: pixi run mobile-build

  or make Git Bash precede WSL on PATH for the shell that runs `pixi run mobile-build`.
WSLMSG
  exit 1
fi

# The shim itself lives in scripts/lib/toolchain-shim.sh so mobile-test.sh can reuse it (#958).
source "$REPO_ROOT/scripts/lib/toolchain-shim.sh"
setup_go_toolchain_shim "$SHIM_DIR" || exit 1

cd "$MOBILE"
exec flutter build apk --debug
