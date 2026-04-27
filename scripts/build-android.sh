#!/bin/sh
set -eu

SCRIPT_DIR=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
REPO_ROOT=$(CDPATH= cd -- "$SCRIPT_DIR/.." && pwd)
PROJECT_DIR="$REPO_ROOT/EutherDrive.Android"
PROJECT_FILE="$PROJECT_DIR/EutherDrive.Android.csproj"

JAVA_HOME=${JAVA_HOME:-/usr/lib/jvm/java-17-openjdk}
ANDROID_SDK_ROOT=${ANDROID_SDK_ROOT:-/opt/android-sdk}

echo "Building Android project..."
echo "  JAVA_HOME=$JAVA_HOME"
echo "  ANDROID_SDK_ROOT=$ANDROID_SDK_ROOT"

cd "$PROJECT_DIR"
dotnet build "$PROJECT_FILE" \
  -p:AndroidSdkDirectory="$ANDROID_SDK_ROOT" \
  -p:JavaSdkDirectory="$JAVA_HOME" \
  "$@"
