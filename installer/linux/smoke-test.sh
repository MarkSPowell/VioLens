#!/usr/bin/env bash
#
# Smoke-tests the Linux packages on the release runner, so a broken one
# fails the build instead of reaching someone:
#
#   1. The tarball unpacks into one folder with the app, violens-exec, the
#      User Guide, the third-party notices, the icon and the desktop-entry
#      installer, and violens-exec runs (no arguments: usage, exit code 2).
#   2. The AppImage extracts, holds the same files and a valid .desktop
#      entry, and runs violens-exec through --exec.
#   3. The app launches on a virtual display (Xvfb) and is still running
#      after a short wait.
#
# What it can't test: certutil and the browsers' certificate databases,
# the GNOME and KDE proxy settings, Wayland, and anything visual. Those
# still need a person on a real Linux desktop.
#
# Usage: smoke-test.sh <appImagePath> <tarballPath>

set -euo pipefail

if [[ $# -ne 2 ]]; then
  echo "Usage: $0 <appImagePath> <tarballPath>" >&2
  exit 1
fi

APPIMAGE="$(readlink -f "$1")"
TARBALL="$(readlink -f "$2")"
LAUNCH_WAIT_SECONDS=20

failures=0
pass() { echo "  ok: $1"; }
fail() { echo "  FAILED: $1" >&2; failures=$((failures + 1)); }

WORK_DIR="$(mktemp -d)"
APP_PID=""
cleanup() {
  if [[ -n "$APP_PID" ]]; then
    kill "$APP_PID" 2>/dev/null || true
  fi
  rm -rf "$WORK_DIR"
}
trap cleanup EXIT

check_files() {
  local folder="$1" label="$2"
  for file in violens violens-exec "Documentation/User Guide.md" Documentation/THIRD-PARTY-NOTICES.md violens.png; do
    if [[ -e "$folder/$file" ]]; then
      pass "$label has $file"
    else
      fail "$label has no $file"
    fi
  done
  for file in violens violens-exec; do
    [[ -x "$folder/$file" ]] && pass "$label: $file is executable" || fail "$label: $file isn't executable"
  done
}

# violens-exec with no command prints its usage and exits with 2.
check_exec_usage() {
  local label="$1"
  shift
  set +e
  "$@" > /dev/null 2>&1
  local code=$?
  set -e
  [[ $code -eq 2 ]] && pass "$label runs (usage, exit code 2)" || fail "$label exited with $code, not 2"
}

echo "1. Tarball"
tar -xzf "$TARBALL" -C "$WORK_DIR"
mapfile -t folders < <(find "$WORK_DIR" -mindepth 1 -maxdepth 1 -type d)
if [[ ${#folders[@]} -eq 1 ]]; then
  pass "unpacks into one folder ($(basename "${folders[0]}"))"
else
  fail "unpacks into ${#folders[@]} folders, not one"
fi
FOLDER="${folders[0]}"
check_files "$FOLDER" "tarball"
[[ -x "$FOLDER/install-desktop-entry.sh" ]] && pass "tarball has install-desktop-entry.sh" || fail "tarball has no install-desktop-entry.sh"
sh -n "$FOLDER/install-desktop-entry.sh" && pass "install-desktop-entry.sh parses" || fail "install-desktop-entry.sh doesn't parse"
check_exec_usage "tarball's violens-exec" "$FOLDER/violens-exec"

echo "2. AppImage"
[[ -x "$APPIMAGE" ]] && pass "AppImage is executable" || fail "AppImage isn't executable"
(cd "$WORK_DIR" && "$APPIMAGE" --appimage-extract > /dev/null)
EXTRACTED="$WORK_DIR/squashfs-root"
check_files "$EXTRACTED/usr/lib/violens" "AppImage"
[[ -x "$EXTRACTED/AppRun" ]] && pass "AppImage has AppRun" || fail "AppImage has no AppRun"
if command -v desktop-file-validate > /dev/null; then
  desktop-file-validate "$EXTRACTED/violens.desktop" && pass "violens.desktop is valid" || fail "violens.desktop isn't valid"
else
  grep -q '^Exec=violens' "$EXTRACTED/violens.desktop" && pass "violens.desktop runs violens" || fail "violens.desktop doesn't run violens"
fi
check_exec_usage "AppImage --exec" "$APPIMAGE" --appimage-extract-and-run --exec

echo "3. Launch"
if command -v xvfb-run > /dev/null; then
  # A data folder of its own, so a launch here never touches real settings.
  export HOME="$WORK_DIR/home"
  mkdir -p "$HOME"
  xvfb-run -a "$FOLDER/violens" > "$WORK_DIR/launch.log" 2>&1 &
  APP_PID=$!
  sleep "$LAUNCH_WAIT_SECONDS"
  if kill -0 "$APP_PID" 2>/dev/null; then
    pass "VioLens is still running after $LAUNCH_WAIT_SECONDS seconds"
  else
    fail "VioLens exited within $LAUNCH_WAIT_SECONDS seconds"
    tail -n 40 "$WORK_DIR/launch.log" >&2 || true
  fi
else
  fail "xvfb-run isn't installed, so the launch wasn't tested"
fi

if [[ $failures -gt 0 ]]; then
  echo "$failures check(s) failed." >&2
  exit 1
fi

echo "All checks passed."
