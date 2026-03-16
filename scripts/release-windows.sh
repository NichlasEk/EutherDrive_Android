#!/bin/sh
set -eu

SCRIPT_DIR=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
REPO_ROOT=$(CDPATH= cd -- "$SCRIPT_DIR/.." && pwd)

VERSION=${1:-dev}
RID=${RID:-win-x64}
CONFIGURATION=${CONFIGURATION:-Release}
SELF_CONTAINED=${SELF_CONTAINED:-false}

PUBLISH_DIR="$REPO_ROOT/artifacts/publish/$RID"
PACKAGE_ROOT="$REPO_ROOT/artifacts/release"
PACKAGE_DIR="$PACKAGE_ROOT/EutherDrive-$VERSION-$RID"
ARCHIVE_PATH="$PACKAGE_ROOT/EutherDrive-$VERSION-$RID.zip"

echo "Publishing $RID ($CONFIGURATION)..."
dotnet restore "$REPO_ROOT/EutherDrive.UI/EutherDrive.UI.csproj" -r "$RID"
dotnet publish "$REPO_ROOT/EutherDrive.UI/EutherDrive.UI.csproj" \
  -c "$CONFIGURATION" \
  -r "$RID" \
  --no-restore \
  --self-contained "$SELF_CONTAINED" \
  -o "$PUBLISH_DIR"

mkdir -p "$PACKAGE_ROOT"
rm -rf "$PACKAGE_DIR" "$ARCHIVE_PATH"
mkdir -p "$PACKAGE_DIR"

cp -R "$PUBLISH_DIR"/. "$PACKAGE_DIR"/
cp "$REPO_ROOT/README.md" "$PACKAGE_DIR/"
cp "$REPO_ROOT/MIT_License.txt" "$PACKAGE_DIR/"
mkdir -p "$PACKAGE_DIR/bios" "$PACKAGE_DIR/saves" "$PACKAGE_DIR/savestates" "$PACKAGE_DIR/logs"

if [ ! -f "$PACKAGE_DIR/SDL2.dll" ]; then
  echo "warning: SDL2.dll missing from Windows package; audio will likely fail." >&2
fi

(
  cd "$PACKAGE_ROOT"
  zip -qr "$(basename "$ARCHIVE_PATH")" "$(basename "$PACKAGE_DIR")"
)

echo "Windows release package created:"
echo "  $ARCHIVE_PATH"
