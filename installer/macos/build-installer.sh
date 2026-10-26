#!/usr/bin/env bash
#
# Builds VioLens.app from a `dotnet publish -r osx-arm64` output and packages
# it as a .dmg: the macOS counterpart of installer/VioLens.iss. Called by
# .github/workflows/release-macos.yml, and runs the same on a Mac by hand.
#
#   - arm64 only.
#   - Signed with a Developer ID and notarized when MACOS_SIGNING_IDENTITY
#     is set (see "Signing" below): the app with the hardened runtime and
#     VioLens.entitlements, then the .dmg, and Apple's notarization ticket
#     stapled to both, so Gatekeeper opens it without asking.
#   - Otherwise ad-hoc signed and not notarized, for forks and test builds.
#     Copying the SDK-signed executable into a hand-built bundle breaks its
#     signature, and macOS calls such a downloaded app "damaged", so the
#     whole bundle is ad-hoc signed instead. Gatekeeper then asks once
#     (System Settings > Privacy & Security > Open Anyway, or
#     `xattr -dr com.apple.quarantine /Applications/VioLens.app`).
#   - The icon (VioLens.icns) is made at build time with sips and iconutil
#     from violens-icon-1024.png beside this script, the app icon at the
#     largest size macOS uses.
#
# Usage: build-installer.sh <version> <bundleVersion> <publishDir> <outputDmgPath> [extensionsDir]
#   version        Full version, e.g. "0.1.1-preview.1", for
#                   CFBundleShortVersionString.
#   bundleVersion  Numeric form, e.g. "0.1.1.0", for CFBundleVersion.
#   publishDir     The `dotnet publish -r osx-arm64 --self-contained
#                   -p:PublishSingleFile=true` output, with the
#                   Violens.DesktopUi executable directly inside.
#   outputDmgPath  Where to write the .dmg.
#   extensionsDir  Optional: installer/build-extensions.ps1's output, put in
#                   the .dmg as an "Optional Extensions" folder with a README.
#
# Signing, from environment variables (all unset means ad-hoc):
#   MACOS_SIGNING_IDENTITY  A "Developer ID Application" identity in the
#                            keychain search list, by name or SHA-1 hash.
#   and for notarization, which is required with it, either
#   NOTARY_KEYCHAIN_PROFILE A profile saved with
#                            `xcrun notarytool store-credentials` (handy on
#                            your own Mac), or all three of
#   NOTARY_API_KEY_PATH     the App Store Connect API key's .p8 file,
#   NOTARY_API_KEY_ID       its key ID, and
#   NOTARY_API_ISSUER_ID    its issuer ID (what release-macos.yml uses).

set -euo pipefail

if [[ $# -ne 4 && $# -ne 5 ]]; then
  echo "Usage: $0 <version> <bundleVersion> <publishDir> <outputDmgPath> [extensionsDir]" >&2
  exit 1
fi

VERSION="$1"
BUNDLE_VERSION="$2"
PUBLISH_DIR="$3"
OUTPUT_DMG_PATH="$4"
EXTENSIONS_DIR="${5:-}"

APP_NAME="VioLens"
EXECUTABLE_NAME="Violens.DesktopUi"
# Reverse-DNS of violens.dev, which the project owns. Changed from
# net.clearinet.desktopui with the rename, so macOS sees VioLens as a new
# app; its settings are carried over by the app itself, not by macOS.
BUNDLE_IDENTIFIER="dev.violens.desktopui"

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
ICON_SOURCE="$SCRIPT_DIR/violens-icon-1024.png"
if [[ ! -f "$ICON_SOURCE" ]]; then
  echo "error: app icon artwork not found at '$ICON_SOURCE'" >&2
  exit 1
fi

# The hardened runtime exceptions the Developer ID signed app gets. They're
# the ones the .NET runtime's own host has (dotnet/runtime
# eng/native/entitlements.plist), less those only for debugging .NET
# itself (debugger, get-task-allow, allow-dyld-environment-variables):
#   allow-jit                   The JIT writes managed code into MAP_JIT
#                               memory. Since .NET 7 that's all it needs;
#                               allow-unsigned-executable-memory isn't.
#   disable-library-validation  The native libraries (libAvaloniaNative,
#                               libSkiaSharp, libHarfBuzzSharp) are packed
#                               in the single-file executable and extracted
#                               on first launch with the signatures they
#                               came with: Microsoft's Developer ID for
#                               Skia and HarfBuzz, ad-hoc for Avalonia.
#                               Library validation loads only libraries
#                               signed by our own team.
ENTITLEMENTS="$SCRIPT_DIR/VioLens.entitlements"

SIGNING_IDENTITY="${MACOS_SIGNING_IDENTITY:-}"
NOTARY_AUTH=()
if [[ -n "$SIGNING_IDENTITY" ]]; then
  if [[ -n "${NOTARY_KEYCHAIN_PROFILE:-}" ]]; then
    NOTARY_AUTH=(--keychain-profile "$NOTARY_KEYCHAIN_PROFILE")
  elif [[ -n "${NOTARY_API_KEY_PATH:-}" && -n "${NOTARY_API_KEY_ID:-}" && -n "${NOTARY_API_ISSUER_ID:-}" ]]; then
    NOTARY_AUTH=(--key "$NOTARY_API_KEY_PATH" --key-id "$NOTARY_API_KEY_ID" --issuer "$NOTARY_API_ISSUER_ID")
  else
    # A Developer ID app that isn't notarized is blocked outright by
    # Gatekeeper, which is worse than ad-hoc, so don't build one.
    echo "error: MACOS_SIGNING_IDENTITY is set but notarization credentials aren't:" >&2
    echo "  set NOTARY_KEYCHAIN_PROFILE, or NOTARY_API_KEY_PATH, NOTARY_API_KEY_ID and NOTARY_API_ISSUER_ID" >&2
    exit 1
  fi
  if [[ ! -f "$ENTITLEMENTS" ]]; then
    echo "error: entitlements not found at '$ENTITLEMENTS'" >&2
    exit 1
  fi
  echo "Signing with Developer ID identity '$SIGNING_IDENTITY', and notarizing"
else
  echo "MACOS_SIGNING_IDENTITY isn't set: ad-hoc signing, no notarization"
fi

EXECUTABLE_PATH="$PUBLISH_DIR/$EXECUTABLE_NAME"
if [[ ! -f "$EXECUTABLE_PATH" ]]; then
  echo "error: expected published executable not found at '$EXECUTABLE_PATH'" >&2
  echo "  (did the dotnet publish step run with -r osx-arm64 first?)" >&2
  exit 1
fi

WORK_DIR="$(mktemp -d)"
trap 'rm -rf "$WORK_DIR"' EXIT

APP_BUNDLE="$WORK_DIR/$APP_NAME.app"
CONTENTS_DIR="$APP_BUNDLE/Contents"
MACOS_DIR="$CONTENTS_DIR/MacOS"
RESOURCES_DIR="$CONTENTS_DIR/Resources"

mkdir -p "$MACOS_DIR" "$RESOURCES_DIR"

# The executable goes in Contents/MacOS under its own name, which
# CFBundleExecutable names.
cp "$EXECUTABLE_PATH" "$MACOS_DIR/$EXECUTABLE_NAME"
chmod +x "$MACOS_DIR/$EXECUTABLE_NAME"

# Everything else PublishSingleFile left beside the executable (the
# Documentation folder) goes in Resources. Contents/MacOS may hold only
# code, or codesign fails with "code object is not signed at all". .pdb
# files aren't shipped.
for entry in "$PUBLISH_DIR"/*; do
  name="$(basename "$entry")"
  if [[ "$name" == "$EXECUTABLE_NAME" || "$name" == *.pdb ]]; then
    continue
  fi
  cp -R "$entry" "$RESOURCES_DIR/$name"
done

# The app icon Finder, the Dock and Launchpad show. Without one (and
# CFBundleIconFile pointing at it) macOS shows the generic app icon. An
# .iconset folder holds each size under the names iconutil expects; each
# @2x is the Retina version of the size before it.
ICONSET_DIR="$WORK_DIR/$APP_NAME.iconset"
mkdir -p "$ICONSET_DIR"
for size in 16 32 128 256 512; do
  sips -z "$size" "$size" "$ICON_SOURCE" --out "$ICONSET_DIR/icon_${size}x${size}.png" > /dev/null
  double=$((size * 2))
  sips -z "$double" "$double" "$ICON_SOURCE" --out "$ICONSET_DIR/icon_${size}x${size}@2x.png" > /dev/null
done
iconutil --convert icns --output "$RESOURCES_DIR/$APP_NAME.icns" "$ICONSET_DIR"

# A minimal Info.plist. Avalonia runs its own application lifecycle, so
# there's no NSPrincipalClass. LSMinimumSystemVersion 11.0 is the first
# release on Apple Silicon.
cat > "$CONTENTS_DIR/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleName</key>
    <string>$APP_NAME</string>
    <key>CFBundleDisplayName</key>
    <string>$APP_NAME</string>
    <key>CFBundleIdentifier</key>
    <string>$BUNDLE_IDENTIFIER</string>
    <key>CFBundleVersion</key>
    <string>$BUNDLE_VERSION</string>
    <key>CFBundleShortVersionString</key>
    <string>$VERSION</string>
    <key>CFBundleExecutable</key>
    <string>$EXECUTABLE_NAME</string>
    <key>CFBundleIconFile</key>
    <string>$APP_NAME.icns</string>
    <key>CFBundlePackageType</key>
    <string>APPL</string>
    <key>CFBundleSignature</key>
    <string>????</string>
    <key>CFBundleInfoDictionaryVersion</key>
    <string>6.0</string>
    <key>LSMinimumSystemVersion</key>
    <string>11.0</string>
    <key>NSHighResolutionCapable</key>
    <true/>
    <key>LSApplicationCategoryType</key>
    <string>public.app-category.developer-tools</string>
</dict>
</plist>
PLIST

# Prints one string field of notarytool's JSON output. grep rather than a
# JSON parser, which a stock Mac doesn't have from the shell.
json_field() {
  grep -o "\"$1\" *: *\"[^\"]*\"" "$2" | head -1 | sed 's/.*"\([^"]*\)"$/\1/'
}

# Submits a file to Apple's notary service, waits for the verdict (usually
# a few minutes), and staples the ticket to the given target so Gatekeeper
# can check it offline. The verdict is read from notarytool's output rather
# than its exit status, and on a rejection Apple's log, which says what was
# wrong, is printed before failing.
notarize_and_staple() {
  local submission="$1" staple_target="$2"
  local result="$WORK_DIR/notary-result.json"
  echo "Notarizing $(basename "$submission")"
  xcrun notarytool submit "$submission" "${NOTARY_AUTH[@]}" --wait --output-format json > "$result" || true
  cat "$result"
  echo
  local id status
  id="$(json_field id "$result")"
  status="$(json_field status "$result")"
  if [[ "$status" != "Accepted" ]]; then
    echo "error: notarization of $(basename "$submission") finished with status '${status:-none}'" >&2
    if [[ -n "$id" ]]; then
      xcrun notarytool log "$id" "${NOTARY_AUTH[@]}" >&2 || true
    fi
    exit 1
  fi
  xcrun stapler staple "$staple_target"
  xcrun stapler validate "$staple_target"
}

if [[ -n "$SIGNING_IDENTITY" ]]; then
  # Each Mach-O on its own, inside out, then the bundle, which signs the
  # main executable with the entitlements and seals everything else.
  # codesign --deep would sign nested code without the hardened runtime or
  # a timestamp, which notarization rejects. Today the executable is the
  # only Mach-O (the native libraries are packed inside it), but anything
  # native that's left beside it in future is covered too.
  developer_id_sign() {
    codesign --force --sign "$SIGNING_IDENTITY" --timestamp --options runtime "$@"
  }
  while IFS= read -r -d '' file; do
    if [[ "$file" != "$MACOS_DIR/$EXECUTABLE_NAME" ]] && [[ "$(file -b "$file")" == *Mach-O* ]]; then
      developer_id_sign "$file"
    fi
  done < <(find "$APP_BUNDLE" -depth -type f -print0)
  developer_id_sign --entitlements "$ENTITLEMENTS" "$APP_BUNDLE"
else
  # Ad-hoc ("-" means no identity: free, no account), over the whole bundle
  # so its signature covers Info.plist and every file in it.
  codesign --force --sign - "$APP_BUNDLE"
fi
# Verify straight away, so a bad bundle fails the build rather than
# reaching users.
codesign --verify --deep --strict --verbose=2 "$APP_BUNDLE"

# The app is notarized on its own before it goes in the .dmg, so its own
# ticket can be stapled to it: once it's copied to Applications it no
# longer has the .dmg's, and without one Gatekeeper has to look it up
# online on first launch.
if [[ -n "$SIGNING_IDENTITY" ]]; then
  APP_ZIP="$WORK_DIR/$APP_NAME.zip"
  ditto -c -k --keepParent "$APP_BUNDLE" "$APP_ZIP"
  notarize_and_staple "$APP_ZIP" "$APP_BUNDLE"
fi

mkdir -p "$(dirname "$OUTPUT_DMG_PATH")"
rm -f "$OUTPUT_DMG_PATH"

# A compressed, read-only .dmg with an Applications link beside the app,
# for the usual drag-to-install window. ditto keeps the stapled ticket and
# the signature intact.
DMG_STAGING_DIR="$WORK_DIR/dmg-staging"
mkdir -p "$DMG_STAGING_DIR"
ditto "$APP_BUNDLE" "$DMG_STAGING_DIR/$APP_NAME.app"
ln -s /Applications "$DMG_STAGING_DIR/Applications"

if [[ -n "$EXTENSIONS_DIR" ]]; then
  if [[ ! -d "$EXTENSIONS_DIR" ]]; then
    echo "error: extensions folder not found at '$EXTENSIONS_DIR'" >&2
    exit 1
  fi
  OPTIONAL_DIR="$DMG_STAGING_DIR/Optional Extensions"
  mkdir -p "$OPTIONAL_DIR"
  cp -R "$EXTENSIONS_DIR"/. "$OPTIONAL_DIR/"
  cat > "$OPTIONAL_DIR/README.txt" <<'README'
Optional extensions for VioLens
=================================

VioLens works without these. To add one, copy its .dll into

    ~/Documents/VioLens/Extensions

(create the folder if it isn't there), then restart VioLens.

  VioLensNetLog.dll      NetLog importer: File > Import via Extension,
                           for Chromium NetLog JSON captures.
  VioLensCSP.dll         CSP Rule Collector: builds a
                           Content-Security-Policy for the sites you browse.
  PrivacyScanner.dll       Privacy Scanner: colours responses that set
                           cookies and checks P3P headers (P3P is obsolete;
                           mostly useful as an example extension).

To remove one, delete its .dll from that folder and restart VioLens.

Each extension is a separate work under its own licence: see
THIRD-PARTY-NOTICES.txt and the licenses folder.
README
fi

hdiutil create \
  -volname "$APP_NAME" \
  -srcfolder "$DMG_STAGING_DIR" \
  -ov \
  -format UDZO \
  "$OUTPUT_DMG_PATH"

# The .dmg gets its own signature and ticket, so Gatekeeper accepts it when
# it's opened, before anything is copied out of it.
if [[ -n "$SIGNING_IDENTITY" ]]; then
  codesign --force --sign "$SIGNING_IDENTITY" --timestamp "$OUTPUT_DMG_PATH"
  codesign --verify --verbose=2 "$OUTPUT_DMG_PATH"
  notarize_and_staple "$OUTPUT_DMG_PATH" "$OUTPUT_DMG_PATH"
fi

echo "Built $OUTPUT_DMG_PATH"
