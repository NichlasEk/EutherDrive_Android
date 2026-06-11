#!/bin/sh
set -eu

SCRIPT_DIR=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
REPO_ROOT=$(CDPATH= cd -- "$SCRIPT_DIR/.." && pwd)
OPENRA_DIR=${OPENRA_DIR:-"$REPO_ROOT/external/OpenRA"}
OPENRA_CONTENT_DIR=${OPENRA_CONTENT_DIR:-"$REPO_ROOT/external/openra-content"}
OPENRA_MOD=${OPENRA_MOD:-ra}

if [ ! -d "$OPENRA_DIR" ]; then
  echo "Missing OpenRA checkout: $OPENRA_DIR" >&2
  echo "Run scripts/fetch-openra.sh first." >&2
  exit 1
fi

mkdir -p "$OPENRA_CONTENT_DIR"
cd "$OPENRA_DIR"

if [ ! -x ./launch-game.sh ]; then
  echo "Missing launch-game.sh in $OPENRA_DIR; build/fetch may be incomplete." >&2
  exit 1
fi

./launch-game.sh "Game.Mod=$OPENRA_MOD" "$@"
