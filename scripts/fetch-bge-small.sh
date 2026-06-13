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

# Portable SHA-256 check (macOS runner has shasum, not sha256sum).
verify() {  # $1=expected-hex  $2=path
    if command -v sha256sum >/dev/null 2>&1; then
        echo "$1  $2" | sha256sum -c -
    else
        echo "$1  $2" | shasum -a 256 -c -
    fi
}
verify "828e1496d7fabb79cfa4dcd84fa38625c0d3d21da474a00f08db0f559940cf35" "${DEST}/model.onnx"
verify "d241a60d5e8f04cc1b2b3e9ef7a4921b27bf526d9f6050ab90f9267a1f9e5c66" "${DEST}/tokenizer.json"

echo "fetched + verified bge-small-en-v1.5 (fp32 onnx + tokenizer.json) into ${DEST}"
