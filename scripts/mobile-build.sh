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

REAL_GO="$(command -v go || true)"
if [ -z "$REAL_GO" ]; then
  echo "Go toolchain not found on PATH -- required to build the tailscale native asset."
  exit 1
fi

mkdir -p "$SHIM_DIR"

case "$(uname -s)" in
  MINGW*|MSYS*|CYGWIN*)
    # The hook runs Go through cmd, so the shim has to be a .bat.
    GO_CACHE="${GOCACHE:-$LOCALAPPDATA\\go-build}"
    cat > "$SHIM_DIR/go.bat" <<EOF
@echo off
set GOCACHE=$GO_CACHE
set LOCALAPPDATA=$LOCALAPPDATA
set APPDATA=$APPDATA
set USERPROFILE=$USERPROFILE
"$(cygpath -w "$REAL_GO")" %*
EOF
    ;;
  *)
    cat > "$SHIM_DIR/go" <<EOF
#!/usr/bin/env bash
export GOCACHE="\${GOCACHE:-$HOME/.cache/go-build}"
export HOME="$HOME"
exec "$REAL_GO" "\$@"
EOF
    chmod +x "$SHIM_DIR/go"
    ;;
esac

export PATH="$SHIM_DIR:$PATH"
cd "$MOBILE"
exec flutter build apk --debug
