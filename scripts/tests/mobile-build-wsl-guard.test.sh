#!/usr/bin/env bash
# #469: proves scripts/lib/wsl-capture.sh's decision discriminates — it must FIRE on the exact
# misfire (WSL + a /mnt Windows-drive checkout) and stay SILENT on the three legitimate cases.
# Runs anywhere (fixtures stand in for /proc/version), so CI verifies the logic the real trigger
# can only exercise under WSL.
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=../lib/wsl-capture.sh
source "$HERE/../lib/wsl-capture.sh"

tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT
printf 'Linux version 5.15.167.4-microsoft-standard-WSL2 (x)\n' > "$tmp/wsl"
printf 'Linux version 6.8.0-1021-azure (buildd@lcy02) generic\n'  > "$tmp/linuxci"

fails=0
# want_fire=0 means the predicate should return success (fire); 1 means it should stay silent.
expect() {
  local desc="$1" want_fire="$2" version_file="$3" repo_root="$4"
  if looks_like_wsl_capture "$version_file" "$repo_root"; then got=0; else got=1; fi
  if [ "$got" = "$want_fire" ]; then
    echo "PASS  $desc"
  else
    echo "FAIL  $desc (wanted fire=$want_fire, got fire=$got)"
    fails=1
  fi
}

# The red arm: the one case the guard exists for.
expect "WSL + /mnt Windows checkout -> fires"        0 "$tmp/wsl"     "/mnt/c/Users/x/repo"
# The three green arms it must not touch.
expect "WSL + \$HOME native checkout -> silent"       1 "$tmp/wsl"     "/home/x/repo"
expect "Linux CI (no WSL marker) + /mnt -> silent"    1 "$tmp/linuxci" "/mnt/c/Users/x/repo"
expect "no /proc/version (Git Bash) + /mnt -> silent" 1 "$tmp/absent"  "/mnt/c/Users/x/repo"

if [ "$fails" = 0 ]; then
  echo "OK  mobile-build WSL guard discriminates (1 fires, 3 silent)"
else
  echo "FAILED  mobile-build WSL guard did not discriminate"
fi
exit "$fails"
