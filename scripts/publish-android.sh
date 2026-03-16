#!/bin/sh
set -eu

SCRIPT_DIR=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
REPO_ROOT=$(CDPATH= cd -- "$SCRIPT_DIR/.." && pwd)
PROJECT_DIR="$REPO_ROOT/EutherDrive.Android"
PROJECT_FILE="$PROJECT_DIR/EutherDrive.Android.csproj"
OUTPUT_DIR="$REPO_ROOT/artifacts/android-publish"

JAVA_HOME=${JAVA_HOME:-/usr/lib/jvm/java-11-openjdk}
ANDROID_SDK_ROOT=${ANDROID_SDK_ROOT:-/opt/android-sdk}
CONFIGURATION=${CONFIGURATION:-Release}

export JAVA_HOME
export ANDROID_SDK_ROOT

echo "Publishing Android project..."
echo "  JAVA_HOME=$JAVA_HOME"
echo "  ANDROID_SDK_ROOT=$ANDROID_SDK_ROOT"
echo "  CONFIGURATION=$CONFIGURATION"

cd "$PROJECT_DIR"
rm -rf bin obj "$OUTPUT_DIR"

dotnet publish "$PROJECT_FILE" \
  -c "$CONFIGURATION" \
  -p:AndroidSdkDirectory="$ANDROID_SDK_ROOT" \
  -p:EmbedAssembliesIntoApk=true \
  -p:AndroidUseSharedRuntime=false \
  -p:AndroidFastDeploymentType=None \
  -p:PublishTrimmed=false \
  -o "$OUTPUT_DIR" \
  "$@"

echo "Android publish output:"
echo "  $OUTPUT_DIR"
