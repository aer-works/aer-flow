# Builds a `go` shim on PATH for Flutter's native-asset hook and prepends it.
#
# Why a PATH shim at all — including why PATH is the only env channel that survives the
# hook runner's environment scrubbing — is documented at the top of scripts/mobile-build.sh,
# the shim's first caller. scripts/mobile-test.sh reuses it for the same hook (#958).
#
# Usage: source this file, then `setup_go_toolchain_shim <shim-dir>`.

setup_go_toolchain_shim() {
  local shim_dir="$1"

  local real_go
  real_go="$(command -v go || true)"
  if [ -z "$real_go" ]; then
    echo "Go toolchain not found on PATH -- required to build the tailscale native asset."
    return 1
  fi

  mkdir -p "$shim_dir"

  case "$(uname -s)" in
    MINGW*|MSYS*|CYGWIN*)
      # The hook runs Go through cmd, so the shim has to be a .bat.
      local go_cache="${GOCACHE:-$LOCALAPPDATA\\go-build}"
      cat > "$shim_dir/go.bat" <<EOF
@echo off
set GOCACHE=$go_cache
set LOCALAPPDATA=$LOCALAPPDATA
set APPDATA=$APPDATA
set USERPROFILE=$USERPROFILE
"$(cygpath -w "$real_go")" %*
EOF
      ;;
    *)
      cat > "$shim_dir/go" <<EOF
#!/usr/bin/env bash
export GOCACHE="\${GOCACHE:-$HOME/.cache/go-build}"
export HOME="$HOME"
exec "$real_go" "\$@"
EOF
      chmod +x "$shim_dir/go"
      ;;
  esac

  export PATH="$shim_dir:$PATH"
}
