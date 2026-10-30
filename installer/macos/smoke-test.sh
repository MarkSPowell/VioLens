#!/usr/bin/env bash
#
# Smoke-tests a finished VioLens .dmg on the macOS release runner, so a
# broken bundle fails the build instead of reaching someone with a Mac:
#
#   1. The .dmg mounts, and holds VioLens.app and an Applications link.
#   2. The app's signature verifies the way macOS checks it (strict, and
#      every nested file sealed), Contents/MacOS holds only the executable
#      and violens-exec (anything else there breaks the signature), and
#      violens-exec runs. For a
#      Developer ID build, also: every Mach-O in the app is signed by the
#      same team with the hardened runtime and a timestamp, the executable
#      has the JIT entitlement, the app and the .dmg have stapled
#      notarization tickets, and Gatekeeper accepts both.
#   3. The User Guide is in the bundle (Help > Documentation), and so is
#      the app icon Info.plist names.
#   4. The Optional Extensions folder has each extension, its licence, the
#      notices file and the README.
#   5. The app launches through Launch Services (the same path as
#      double-clicking it) and is still running after a short wait.
#
# What it can't test: the first-launch prompt a downloaded (quarantined)
# app gets, the certificate and system proxy prompts, and anything visual.
# Those still need a person on a real Mac.
#
# Usage: smoke-test.sh <dmgPath> [developer-id|adhoc]
#   The signature to expect. Without it, whichever the app has is checked,
#   which is fine by hand; CI says which, so that a build meant to be
#   signed can't pass as ad-hoc.

set -euo pipefail

if [[ $# -ne 1 && $# -ne 2 ]]; then
  echo "Usage: $0 <dmgPath> [developer-id|adhoc]" >&2
  exit 1
fi

DMG_PATH="$1"
EXPECTED_SIGNATURE="${2:-}"
if [[ -n "$EXPECTED_SIGNATURE" && "$EXPECTED_SIGNATURE" != "developer-id" && "$EXPECTED_SIGNATURE" != "adhoc" ]]; then
  echo "error: expected signature must be 'developer-id' or 'adhoc', not '$EXPECTED_SIGNATURE'" >&2
  exit 1
fi
APP_NAME="VioLens"
EXECUTABLE_NAME="Violens.DesktopUi"
LAUNCH_WAIT_SECONDS=20

failures=0
pass() { echo "  ok: $1"; }
fail() { echo "  FAILED: $1" >&2; failures=$((failures + 1)); }

WORK_DIR="$(mktemp -d)"
MOUNT_POINT="$WORK_DIR/mount"
mkdir -p "$MOUNT_POINT"

cleanup() {
  pkill -f "$APP_NAME.app/Contents/MacOS/$EXECUTABLE_NAME" 2>/dev/null || true
  hdiutil detach "$MOUNT_POINT" -quiet 2>/dev/null || true
  rm -rf "$WORK_DIR"
}
trap cleanup EXIT

echo "1. Mounting $DMG_PATH"
hdiutil attach "$DMG_PATH" -nobrowse -readonly -mountpoint "$MOUNT_POINT" -quiet
MOUNTED_APP="$MOUNT_POINT/$APP_NAME.app"
[[ -d "$MOUNTED_APP" ]] && pass "$APP_NAME.app is in the .dmg" || fail "$APP_NAME.app is missing from the .dmg"
[[ -L "$MOUNT_POINT/Applications" ]] && pass "Applications link is in the .dmg" || fail "Applications link is missing"

# Copied off the read-only image first, the way a user drags it to
# Applications. ditto keeps the signature and extended attributes intact.
APP="$WORK_DIR/$APP_NAME.app"
ditto "$MOUNTED_APP" "$APP"

echo "2. Signature"
if codesign --verify --deep --strict --verbose=2 "$APP"; then
  pass "signature verifies (--deep --strict)"
else
  fail "signature doesn't verify"
fi
macos_entries="$(ls -A "$APP/Contents/MacOS" | sort | tr '\n' ' ')"
expected_entries="$(printf '%s\n' "$EXECUTABLE_NAME" violens-exec | sort | tr '\n' ' ')"
if [[ "$macos_entries" == "$expected_entries" ]]; then
  pass "Contents/MacOS holds only $EXECUTABLE_NAME and violens-exec"
else
  fail "Contents/MacOS should hold $expected_entries but holds: $macos_entries"
fi

# violens-exec runs (exit code 2 is its usage message, printed when it's
# given no command), which shows its signature lets macOS start it.
exec_status=0
"$APP/Contents/MacOS/violens-exec" > /dev/null 2>&1 || exec_status=$?
if [[ "$exec_status" == 2 ]]; then
  pass "violens-exec runs"
else
  fail "violens-exec didn't run (exit code $exec_status)"
fi

signature_info="$(codesign --display --verbose=2 "$APP" 2>&1 || true)"
if grep -q '^Authority=Developer ID Application: ' <<< "$signature_info"; then
  actual_signature="developer-id"
elif grep -q '^Signature=adhoc' <<< "$signature_info"; then
  actual_signature="adhoc"
else
  actual_signature="unknown"
fi
if [[ -z "$EXPECTED_SIGNATURE" ]]; then
  EXPECTED_SIGNATURE="$actual_signature"
fi
if [[ "$actual_signature" == "$EXPECTED_SIGNATURE" ]]; then
  pass "signed $actual_signature"
else
  fail "signed $actual_signature, expected $EXPECTED_SIGNATURE"
  echo "$signature_info" | sed 's/^/    /'
fi

if [[ "$EXPECTED_SIGNATURE" == "developer-id" ]]; then
  team="$(sed -n 's/^TeamIdentifier=//p' <<< "$signature_info")"
  # Every Mach-O, not just the executable: notarization rejects nested
  # code without the hardened runtime or a secure timestamp.
  while IFS= read -r -d '' file; do
    [[ "$(file -b "$file")" == *Mach-O* ]] || continue
    name="${file#"$APP"/}"
    info="$(codesign --display --verbose=2 "$file" 2>&1 || true)"
    problems=()
    [[ "$(sed -n 's/^TeamIdentifier=//p' <<< "$info")" == "$team" ]] || problems+=("not signed by team $team")
    grep -Eq '^CodeDirectory .*flags=0x[0-9a-f]+\([^)]*runtime' <<< "$info" || problems+=("no hardened runtime")
    grep -q '^Timestamp=' <<< "$info" || problems+=("no secure timestamp")
    if [[ ${#problems[@]} -eq 0 ]]; then
      pass "$name: team $team, hardened runtime, timestamp"
    else
      fail "$name: $(IFS=,; echo "${problems[*]}" | sed "s/,/, /g")"
    fi
  done < <(find "$APP" -type f -print0)

  if codesign --display --entitlements - --xml "$APP" 2>/dev/null | grep -q 'com.apple.security.cs.allow-jit'; then
    pass "executable has the allow-jit entitlement"
  else
    fail "executable lacks the allow-jit entitlement (the JIT can't run under the hardened runtime)"
  fi

  for target in "$APP" "$DMG_PATH"; do
    if xcrun stapler validate "$target" > /dev/null 2>&1; then
      pass "notarization ticket is stapled to $(basename "$target")"
    else
      fail "no valid notarization ticket stapled to $(basename "$target")"
    fi
  done

  # What a double-click does: execute for the app, open for the .dmg.
  gatekeeper_app="$(spctl --assess --type execute --verbose=2 "$APP" 2>&1)" \
    && pass "Gatekeeper accepts the app ($(sed -n 's/^source=//p' <<< "$gatekeeper_app"))" \
    || { fail "Gatekeeper rejects the app"; echo "$gatekeeper_app" | sed 's/^/    /'; }
  gatekeeper_dmg="$(spctl --assess --type open --context context:primary-signature --verbose=2 "$DMG_PATH" 2>&1)" \
    && pass "Gatekeeper accepts the .dmg ($(sed -n 's/^source=//p' <<< "$gatekeeper_dmg"))" \
    || { fail "Gatekeeper rejects the .dmg"; echo "$gatekeeper_dmg" | sed 's/^/    /'; }
else
  # Informational only: Gatekeeper is expected to reject an ad-hoc signed
  # app. Printed so a change in what it says shows up in the log.
  echo "  (Gatekeeper's view, expected to reject an ad-hoc signed app:)"
  spctl --assess --type execute --verbose=2 "$APP" 2>&1 | sed 's/^/    /' || true
fi

echo "3. Documentation and icon"
[[ -f "$APP/Contents/Resources/Documentation/User Guide.md" ]] \
  && pass "User Guide is in Contents/Resources/Documentation" \
  || fail "User Guide is missing from Contents/Resources/Documentation"
# The icon file, and Info.plist naming it -- either one missing means
# Finder and the Dock show the generic app icon.
icon_file="$(/usr/libexec/PlistBuddy -c 'Print :CFBundleIconFile' "$APP/Contents/Info.plist" 2>/dev/null || true)"
if [[ -n "$icon_file" && -f "$APP/Contents/Resources/$icon_file" ]]; then
  pass "app icon $icon_file is in Contents/Resources"
else
  fail "app icon is missing (CFBundleIconFile '${icon_file:-not set}')"
fi

echo "4. Optional Extensions"
OPTIONAL_DIR="$MOUNT_POINT/Optional Extensions"
for expected in \
  VioLensNetLog.dll VioLensCSP.dll PrivacyScanner.dll \
  README.txt THIRD-PARTY-NOTICES.txt \
  licenses/VioLensNetLog-LICENSE.txt licenses/VioLensCSP-LICENSE.txt licenses/PrivacyScanner-LICENSE.txt; do
  [[ -f "$OPTIONAL_DIR/$expected" ]] && pass "$expected" || fail "Optional Extensions/$expected is missing"
done

echo "5. Launch"
LOG="$WORK_DIR/launch.log"
# -n: a new instance; --stdout/--stderr: keep the app's console output.
open -n --stdout "$LOG" --stderr "$LOG" "$APP"
sleep "$LAUNCH_WAIT_SECONDS"
if pgrep -f "$APP_NAME.app/Contents/MacOS/$EXECUTABLE_NAME" > /dev/null; then
  pass "still running after ${LAUNCH_WAIT_SECONDS}s"
else
  fail "not running ${LAUNCH_WAIT_SECONDS}s after launch (crashed or quit)"
fi
echo "  App output:"
sed 's/^/    /' "$LOG" 2>/dev/null || echo "    (none)"
# The app's own crash report, if macOS wrote one.
crash="$(ls -t "$HOME/Library/Logs/DiagnosticReports/"*"$EXECUTABLE_NAME"* 2>/dev/null | head -1 || true)"
if [[ -n "$crash" ]]; then
  echo "  Crash report: $crash"
  head -60 "$crash" | sed 's/^/    /'
fi

echo
if [[ $failures -gt 0 ]]; then
  echo "Smoke test FAILED: $failures check(s)." >&2
  exit 1
fi
echo "Smoke test passed."
