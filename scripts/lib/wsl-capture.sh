#!/usr/bin/env bash
# #469: decide whether a mobile-build invocation is WSL bash capturing a Windows checkout.
#
# Factored out of scripts/mobile-build.sh so the decision is unit-testable off a WSL host
# (scripts/tests/mobile-build-wsl-guard.test.sh drives it with fixtures) — the real trigger is only
# reachable when actually running under WSL, which CI is not.
#
# Returns success (fires) only for the exact misfire: running under WSL — the marker lives in the
# kernel version string, which a genuine Linux CI runner does not carry — against a checkout on a
# Windows drive (/mnt/*). A Linux CI runner and a WSL-native checkout under $HOME both return
# failure and let the build proceed.
looks_like_wsl_capture() {
  local version_file="${1:-/proc/version}" repo_root="$2"
  [ -r "$version_file" ] && grep -qiE 'microsoft|wsl' "$version_file" && [[ "$repo_root" == /mnt/* ]]
}
