#!/bin/sh
set -eu

SCRIPT_DIR=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
REPO_ROOT=$(CDPATH= cd -- "$SCRIPT_DIR/.." && pwd)
OUTPUT_DIR="$REPO_ROOT/artifacts/android-publish"

# Ditt befintliga publish-script
PUBLISH_SCRIPT="$SCRIPT_DIR/publish-android.sh"

# ---- Konfig ----
PHONE_USER=${PHONE_USER:-u0_a123}
PHONE_HOST=${PHONE_HOST:-192.168.1.50}
PHONE_PORT=${PHONE_PORT:-8022}

# Exempel för Termux efter termux-setup-storage:
PHONE_DIR=${PHONE_DIR:-/data/data/com.termux/files/home/storage/downloads/EutherDriveBuilds}

APP_NAME=${APP_NAME:-EutherDrive}
TIMESTAMP=$(date +"%Y%m%d-%H%M%S")

# ---- Kör publish först ----
echo "Publishing Android APK..."
"$PUBLISH_SCRIPT" "$@"

# ---- Hitta senaste APK ----
APK_FILE=$(find "$OUTPUT_DIR" -type f -name "*.apk" | sort | tail -n 1)

if [ -z "$APK_FILE" ]; then
    echo "Ingen APK hittades i $OUTPUT_DIR"
    exit 1
fi

BASENAME=$(basename "$APK_FILE")
EXTENSION="${BASENAME##*.}"
NEW_NAME="${APP_NAME}-${TIMESTAMP}.${EXTENSION}"

echo "Senaste APK:"
echo "  $APK_FILE"
echo "Nytt filnamn på telefonen:"
echo "  $NEW_NAME"

# ---- Skapa katalog på telefonen ----
echo "Skapar mål-katalog på telefonen om den saknas..."
ssh -p "$PHONE_PORT" "$PHONE_USER@$PHONE_HOST" "mkdir -p '$PHONE_DIR'"

# ---- Kopiera filen ----
echo "Kopierar APK till telefonen..."
scp -P "$PHONE_PORT" "$APK_FILE" "$PHONE_USER@$PHONE_HOST:$PHONE_DIR/$NEW_NAME"

echo "Klart."
echo "Fil på telefonen:"
echo "  $PHONE_DIR/$NEW_NAME"
