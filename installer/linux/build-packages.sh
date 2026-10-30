#!/usr/bin/env bash
#
# Packages a published linux-x64 VioLens build two ways:
#
#   VioLens-<version>-x86_64.AppImage   one file: chmod +x and run it
#   VioLens-<version>-linux-x64.tar.gz  a folder to unpack anywhere, with
#                                       install-desktop-entry.sh to add
#                                       VioLens to the applications menu
#
# Both hold the same files: the app (renamed "violens"), violens-exec, the
# Documentation folder (User Guide, third-party notices), the icon and a
# .desktop entry. The optional extensions, when built, go in the tarball as
# an "Optional Extensions" folder with a README, as on macOS; they're
# copied into ~/Documents/VioLens/Extensions by hand.
#
# Snap and Flatpak are deliberately not built: their sandboxes keep an app
# from changing the desktop's proxy settings and the browsers' certificate
# databases, which is what VioLens needs to do.
#
# appimagetool and the AppImage runtime are downloaded at pinned versions
# and checked against their SHA-256, then run with
# --appimage-extract-and-run, so FUSE isn't needed on the build machine.
#
# Usage: build-packages.sh <version> <publishDir> <outputDir> [extensionsDir]

set -euo pipefail

if [[ $# -lt 3 || $# -gt 4 ]]; then
  echo "Usage: $0 <version> <publishDir> <outputDir> [extensionsDir]" >&2
  exit 1
fi

VERSION="$1"
PUBLISH_DIR="$(cd "$2" && pwd)"
OUTPUT_DIR="$3"
EXTENSIONS_DIR="${4:-}"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"

APPIMAGETOOL_URL="https://github.com/AppImage/appimagetool/releases/download/1.9.1/appimagetool-x86_64.AppImage"
APPIMAGETOOL_SHA256="ed4ce84f0d9caff66f50bcca6ff6f35aae54ce8135408b3fa33abfc3cb384eb0"
RUNTIME_URL="https://github.com/AppImage/type2-runtime/releases/download/20251108/runtime-x86_64"
RUNTIME_SHA256="2fca8b443c92510f1483a883f60061ad09b46b978b2631c807cd873a47ec260d"

for required in Violens.DesktopUi violens-exec "Documentation/User Guide.md" Documentation/THIRD-PARTY-NOTICES.md; do
  if [[ ! -e "$PUBLISH_DIR/$required" ]]; then
    echo "error: $PUBLISH_DIR has no $required" >&2
    exit 1
  fi
done

mkdir -p "$OUTPUT_DIR"
OUTPUT_DIR="$(cd "$OUTPUT_DIR" && pwd)"
WORK_DIR="$(mktemp -d)"
trap 'rm -rf "$WORK_DIR"' EXIT

download() {
  local url="$1" sha256="$2" target="$3"
  curl -fsSL "$url" -o "$target"
  if [[ "$(sha256sum "$target" | cut -d' ' -f1)" != "$sha256" ]]; then
    echo "error: $(basename "$target") doesn't match its pinned SHA-256" >&2
    exit 1
  fi
}

# The files both packages share, laid out under $1.
stage_app() {
  local target="$1"
  mkdir -p "$target"
  cp "$PUBLISH_DIR/Violens.DesktopUi" "$target/violens"
  cp "$PUBLISH_DIR/violens-exec" "$target/violens-exec"
  chmod 755 "$target/violens" "$target/violens-exec"
  cp -R "$PUBLISH_DIR/Documentation" "$target/Documentation"
  # Anything else the publish left beside the executable (native libraries
  # not bundled into it), but never debug symbols or API docs.
  for entry in "$PUBLISH_DIR"/*; do
    local name
    name="$(basename "$entry")"
    case "$name" in
      Violens.DesktopUi|violens-exec|Documentation|*.pdb|*.dbg|*.xml) ;;
      *) cp -R "$entry" "$target/$name" ;;
    esac
  done
  cp "$REPO_ROOT/apps/Violens.DesktopUi/Assets/violens-icon.png" "$target/violens.png"
}

desktop_entry() {
  local exec="$1" icon="$2"
  cat <<DESKTOP
[Desktop Entry]
Type=Application
Name=VioLens
GenericName=Web Debugging Proxy
Comment=Capture, inspect and change HTTP and HTTPS traffic
Exec=$exec %F
Icon=$icon
Terminal=false
Categories=Development;Network;
Keywords=proxy;http;https;debugger;fiddler;saz;har;
StartupWMClass=Violens.DesktopUi
DESKTOP
}

# -- AppImage -----------------------------------------------------------------

APPDIR="$WORK_DIR/VioLens.AppDir"
stage_app "$APPDIR/usr/lib/violens"
mkdir -p "$APPDIR/usr/bin" "$APPDIR/usr/share/applications" "$APPDIR/usr/share/icons/hicolor/256x256/apps"
ln -s ../lib/violens/violens "$APPDIR/usr/bin/violens"
ln -s ../lib/violens/violens-exec "$APPDIR/usr/bin/violens-exec"
cp "$REPO_ROOT/apps/Violens.DesktopUi/Assets/violens-icon.png" "$APPDIR/usr/share/icons/hicolor/256x256/apps/violens.png"
cp "$REPO_ROOT/apps/Violens.DesktopUi/Assets/violens-icon.png" "$APPDIR/violens.png"
desktop_entry violens violens > "$APPDIR/violens.desktop"
cp "$APPDIR/violens.desktop" "$APPDIR/usr/share/applications/violens.desktop"

# Runs VioLens, or violens-exec when the AppImage is started under that
# name (a symlink called violens-exec) or with --exec as its first argument.
cat > "$APPDIR/AppRun" <<'APPRUN'
#!/bin/sh
here="$(dirname "$(readlink -f "$0")")"
if [ "$(basename "${ARGV0:-$0}")" = "violens-exec" ]; then
  exec "$here/usr/lib/violens/violens-exec" "$@"
fi
if [ "$1" = "--exec" ]; then
  shift
  exec "$here/usr/lib/violens/violens-exec" "$@"
fi
exec "$here/usr/lib/violens/violens" "$@"
APPRUN
chmod 755 "$APPDIR/AppRun"

download "$APPIMAGETOOL_URL" "$APPIMAGETOOL_SHA256" "$WORK_DIR/appimagetool"
download "$RUNTIME_URL" "$RUNTIME_SHA256" "$WORK_DIR/runtime"
chmod 755 "$WORK_DIR/appimagetool"

APPIMAGE="$OUTPUT_DIR/VioLens-$VERSION-x86_64.AppImage"
rm -f "$APPIMAGE"
ARCH=x86_64 "$WORK_DIR/appimagetool" --appimage-extract-and-run \
  --runtime-file "$WORK_DIR/runtime" \
  --no-appstream \
  "$APPDIR" "$APPIMAGE"
chmod 755 "$APPIMAGE"
echo "Built $APPIMAGE"

# -- Tarball ------------------------------------------------------------------

FOLDER_NAME="VioLens-$VERSION-linux-x64"
FOLDER="$WORK_DIR/$FOLDER_NAME"
stage_app "$FOLDER"

cat > "$FOLDER/install-desktop-entry.sh" <<'INSTALL'
#!/bin/sh
# Adds VioLens to your applications menu, pointing at this folder, and
# violens-exec to ~/.local/bin. Run it again after moving the folder.
# Nothing outside your home folder is touched.
set -eu
here="$(cd "$(dirname "$0")" && pwd)"
apps="${XDG_DATA_HOME:-$HOME/.local/share}/applications"
icons="${XDG_DATA_HOME:-$HOME/.local/share}/icons/hicolor/256x256/apps"
mkdir -p "$apps" "$icons" "$HOME/.local/bin"
cp "$here/violens.png" "$icons/violens.png"
cat > "$apps/violens.desktop" <<DESKTOP
[Desktop Entry]
Type=Application
Name=VioLens
GenericName=Web Debugging Proxy
Comment=Capture, inspect and change HTTP and HTTPS traffic
Exec="$here/violens" %F
Icon=violens
Terminal=false
Categories=Development;Network;
Keywords=proxy;http;https;debugger;fiddler;saz;har;
StartupWMClass=Violens.DesktopUi
DESKTOP
ln -sf "$here/violens-exec" "$HOME/.local/bin/violens-exec"
command -v update-desktop-database >/dev/null 2>&1 && update-desktop-database "$apps" || true
echo "VioLens is in your applications menu, and violens-exec is in ~/.local/bin."
INSTALL
chmod 755 "$FOLDER/install-desktop-entry.sh"

cat > "$FOLDER/README.txt" <<README
VioLens $VERSION for Linux (x86-64)

Run ./violens to start it, or ./install-desktop-entry.sh once to add it to
your applications menu (and violens-exec to ~/.local/bin).

Capturing HTTPS needs certutil, from your distribution's NSS tools:
  Debian, Ubuntu:  sudo apt install libnss3-tools
  Fedora:          sudo dnf install nss-tools
  Arch:            sudo pacman -S nss

VioLens sets the proxy in GNOME's and KDE Plasma's settings. On other
desktops, point your browser at the address VioLens shows.

The User Guide is in Documentation/, and Help > Documentation opens it.
README

if [[ -n "$EXTENSIONS_DIR" && -d "$EXTENSIONS_DIR" ]]; then
  mkdir -p "$FOLDER/Optional Extensions"
  cp -R "$EXTENSIONS_DIR"/. "$FOLDER/Optional Extensions/"
  cat > "$FOLDER/Optional Extensions/README.txt" <<'README'
Optional extensions for VioLens. To use one, copy its .dll into
~/Documents/VioLens/Extensions (make the folder if it isn't there) and
restart VioLens; Tools > Extensions shows what loaded. Each extension's
licence is in the licenses folder, with THIRD-PARTY-NOTICES.txt.
README
fi

TARBALL="$OUTPUT_DIR/$FOLDER_NAME.tar.gz"
tar -czf "$TARBALL" -C "$WORK_DIR" "$FOLDER_NAME"
echo "Built $TARBALL"
