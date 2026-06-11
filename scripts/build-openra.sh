#!/bin/sh
set -eu

SCRIPT_DIR=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
REPO_ROOT=$(CDPATH= cd -- "$SCRIPT_DIR/.." && pwd)
OPENRA_DIR=${OPENRA_DIR:-"$REPO_ROOT/external/OpenRA"}
OPENRA_RESTORE=${OPENRA_RESTORE:-0}
OPENRA_TARGET_FRAMEWORK=${OPENRA_TARGET_FRAMEWORK:-net8.0}

if [ ! -d "$OPENRA_DIR" ]; then
  echo "Missing OpenRA checkout: $OPENRA_DIR" >&2
  echo "Run scripts/fetch-openra.sh first." >&2
  exit 1
fi

if [ -n "$OPENRA_TARGET_FRAMEWORK" ] && [ -f "$OPENRA_DIR/Directory.Build.props" ]; then
  sed -i 's|<TargetFramework Condition="'\''$(MSBuildRuntimeType)'\''!='\''Mono'\''">net[0-9][0-9]*\.0</TargetFramework>|<TargetFramework Condition="'\''$(MSBuildRuntimeType)'\''!='\''Mono'\''">'"$OPENRA_TARGET_FRAMEWORK"'</TargetFramework>|' "$OPENRA_DIR/Directory.Build.props"
fi

if [ "$OPENRA_RESTORE" = "1" ]; then
  dotnet restore "$OPENRA_DIR/OpenRA.sln" -p:TargetPlatform="${TARGETPLATFORM:-linux-x64}"
fi

dotnet build "$OPENRA_DIR/OpenRA.sln" -c "${CONFIGURATION:-Release}" -nologo -p:TargetPlatform="${TARGETPLATFORM:-linux-x64}" --no-restore "$@"
