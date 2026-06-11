#!/bin/sh
set -eu

SCRIPT_DIR=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
REPO_ROOT=$(CDPATH= cd -- "$SCRIPT_DIR/.." && pwd)

OPENRA_REPO_URL=${OPENRA_REPO_URL:-https://github.com/OpenRA/OpenRA.git}
OPENRA_REF=${OPENRA_REF:-release-20250330}
OPENRA_DIR=${OPENRA_DIR:-"$REPO_ROOT/external/OpenRA"}
OPENRA_CONTENT_DIR=${OPENRA_CONTENT_DIR:-"$REPO_ROOT/external/openra-content"}
OPENRA_SKIP_FETCH=${OPENRA_SKIP_FETCH:-0}

mkdir -p "$(dirname -- "$OPENRA_DIR")"
mkdir -p "$OPENRA_CONTENT_DIR"

if [ -d "$OPENRA_DIR/.git" ]; then
  echo "Updating OpenRA checkout at $OPENRA_DIR"
  if [ "$OPENRA_SKIP_FETCH" = "1" ]; then
    echo "Skipping network fetch because OPENRA_SKIP_FETCH=1"
  else
    git -C "$OPENRA_DIR" fetch --tags origin
  fi
else
  echo "Cloning OpenRA into $OPENRA_DIR"
  git clone "$OPENRA_REPO_URL" "$OPENRA_DIR"
fi

git -C "$OPENRA_DIR" checkout "$OPENRA_REF"
git -C "$OPENRA_DIR" submodule update --init --recursive

cat <<EOF
OpenRA checkout ready:
  OPENRA_DIR=$OPENRA_DIR
  OPENRA_REF=$OPENRA_REF
  OPENRA_CONTENT_DIR=$OPENRA_CONTENT_DIR

The checkout and content directory are gitignored by EutherDrive.
Original game assets are not part of OpenRA's GPL license; keep them local.
EOF
