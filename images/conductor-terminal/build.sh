#!/usr/bin/env bash
# rivoli-ai/conductor#1029 (M1.9.2). Build wrapper for the
# `conductor-terminal` base image. Produces an OCI tarball at
# `images/_out/conductor-terminal-base.tar` the M1.9.6 (image
# bundling) work can pick up for first-run registry seeding.
#
# Idempotent: re-running with no changes is a docker-cache hit; the
# tarball is written every time.
#
# Usage (from the andy-containers repo root):
#   images/conductor-terminal/build.sh
#   images/conductor-terminal/build.sh --tag custom-tag

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT_DIR="$(cd "$SCRIPT_DIR/../.." && pwd)"
OUT_DIR="$ROOT_DIR/images/_out"
TAG="conductor-terminal:base"

while [[ $# -gt 0 ]]; do
    case "$1" in
        --tag)
            TAG="$2"
            shift 2
            ;;
        --out-dir)
            OUT_DIR="$2"
            shift 2
            ;;
        *)
            echo "unknown argument: $1" >&2
            exit 2
            ;;
    esac
done

mkdir -p "$OUT_DIR"

echo "[build] docker build --tag $TAG $SCRIPT_DIR" >&2
docker build --tag "$TAG" "$SCRIPT_DIR"

tarball="$OUT_DIR/conductor-terminal-base.tar"
echo "[build] docker save -> $tarball" >&2
docker save --output "$tarball" "$TAG"

# Print the resulting size so CI logs make the < 200 MB target visible.
size_bytes=$(stat -f%z "$tarball" 2>/dev/null || stat -c%s "$tarball")
size_mb=$(( size_bytes / 1024 / 1024 ))
echo "[build] $tarball ($size_mb MB)" >&2
