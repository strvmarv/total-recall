#!/bin/sh
# Fetch the bge-small-en-v1.5 fp32 ONNX model + tokenizer.json into <dest-dir>
# (default: models/bge-small-en-v1.5). Pins an immutable HuggingFace revision and
# SHA256-verifies model.onnx so the bytes are byte-identical to the eval-validated
# model. The 133 MB fp32 model is fetched (not committed) — over GitHub's 100 MB limit.
#
# Usage: scripts/fetch-bge-small.sh [dest-dir]
set -eu

DEST="${1:-models/bge-small-en-v1.5}"
REV="5c38ec7c405ec4b44b94cc5a9bb96e735b38267a"
BASE="https://huggingface.co/BAAI/bge-small-en-v1.5/resolve/${REV}"

mkdir -p "$DEST"
curl -fsSL --retry 3 --retry-delay 5 "${BASE}/onnx/model.onnx"   -o "${DEST}/model.onnx"
curl -fsSL --retry 3 --retry-delay 5 "${BASE}/tokenizer.json"    -o "${DEST}/tokenizer.json"

echo "828e1496d7fabb79cfa4dcd84fa38625c0d3d21da474a00f08db0f559940cf35  ${DEST}/model.onnx" | sha256sum -c -

echo "fetched + verified bge-small-en-v1.5 (fp32 onnx + tokenizer.json) into ${DEST}"
