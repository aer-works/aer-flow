#!/usr/bin/env bash
# Applies patches/tailscale-0.5.0-epoll-pwait.patch to the `tailscale` package in the local pub
# cache, so Aer.Mobile can run on an x86_64 Android emulator (aer-works/aer-flow#303).
#
# Deliberately a patch-over-the-cache rather than a `dependency_overrides` path override: an
# override pins version resolution and would silently withhold future upstream releases, including
# tsnet security updates (tsnet pulls in all of Tailscale). Patching leaves resolution untouched —
# a future `tailscale` version simply lands in its own cache directory and this patch stops being
# used.
#
# Idempotent: re-running after the patch is applied is a no-op, not an error.
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
EXPECTED_VERSION="0.7.1"
PATCH_FILE="$REPO_ROOT/patches/tailscale-$EXPECTED_VERSION-epoll-pwait.patch"

# PUB_CACHE wins if set; otherwise the per-OS default.
if [ -n "${PUB_CACHE:-}" ]; then
  CACHE="$PUB_CACHE"
elif [ -n "${LOCALAPPDATA:-}" ]; then
  CACHE="$LOCALAPPDATA/Pub/Cache"
else
  CACHE="$HOME/.pub-cache"
fi

PKG="$CACHE/hosted/pub.dev/tailscale-$EXPECTED_VERSION"
TARGET="$PKG/go/reactor_linux.go"

# Fail loudly on a version bump rather than silently leaving the crash in place: this patch is
# pinned to a known-good source revision, and a different version means upstream may already have
# fixed it (in which case delete the patch, the task, and #303's workaround).
if [ ! -d "$PKG" ]; then
  echo "tailscale-$EXPECTED_VERSION not found in pub cache at:"
  echo "  $PKG"
  echo
  echo "If pubspec.lock has moved to a newer tailscale, check whether upstream fixed"
  echo "the epoll_wait bug (danReynolds/tailscale_dart) before re-pinning this patch."
  exit 1
fi

if grep -q "func epollPwait" "$TARGET"; then
  echo "already patched: $TARGET"
  exit 0
fi

if ! grep -q "unix.EpollWait" "$TARGET"; then
  echo "neither EpollWait nor EpollPwait found in:"
  echo "  $TARGET"
  echo "Upstream has changed this code — re-check whether the patch is still needed."
  exit 1
fi

echo "applying $(basename "$PATCH_FILE") to $PKG"
( cd "$PKG" && git apply -p0 "$PATCH_FILE" )

# The native-asset build hook caches by a dependency hash, so editing the Go source alone can
# leave a stale prebuilt library in place and the crash would appear "unfixed". Dropping the
# hook cache forces an actual Go rebuild on the next `flutter build`.
HOOKS="$REPO_ROOT/src/Aer.Mobile/.dart_tool/hooks_runner"
if [ -d "$HOOKS" ]; then
  echo "clearing native-asset hook cache: $HOOKS"
  rm -rf "$HOOKS"
fi

echo "done — rebuild with: pixi run mobile-build"
