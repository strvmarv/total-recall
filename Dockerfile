# syntax=docker/dockerfile:1

# total-recall container image.
#
# Multi-arch (linux/amd64 + linux/arm64) via buildx. The NativeAOT binary is
# glibc, so the base is Debian trixie (node:22-trixie-slim), NOT Alpine/musl.
# The base is chosen because the staged binary requires GLIBC_2.38 (verified via
# objdump -T), and trixie ships glibc 2.41 — bookworm (2.36) would fail to load
# it. The image reproduces the repo layout the Node shim (bin/start.js) expects,
# with the per-RID publish tree baked in so provisioning is a no-op at runtime.

ARG NODE_VERSION=22
FROM node:${NODE_VERSION}-trixie-slim

# buildx sets TARGETARCH (amd64/arm64) per platform. Map it to the RID-named
# tree the shim's detectRid() expects (linux-x64 / linux-arm64).
ARG TARGETARCH

# Non-root user for bind-mount ownership safety (see the design spec §5).
ARG UID=10001

WORKDIR /app

# Reproduce the repo layout the shim expects. Only fetch-binary.js is needed from
# scripts/ (the shim imports it for detectRid); the rest of scripts/ is install-
# time tooling that never runs in the container.
#
# libstdc++6 is required by the sibling native libs the binary P/Invokes into:
# libonnxruntime.so and libonnxruntime_providers_shared.so both DT_NEEDED
# libstdc++.so.6 (verified via objdump -p). It is NOT Essential in Debian and is
# absent from the -slim base, so it must be installed explicitly or the embedder
# fails at first DB open with a DllNotFoundException (the 0.8.0-beta.4 failure
# mode documented in scripts/fetch-binary.js). libgcc_s.so.1 is Essential and
# already present.
RUN apt-get update \
    && apt-get install -y --no-install-recommends libstdc++6 \
    && rm -rf /var/lib/apt/lists/*

COPY bin/ ./bin/
COPY scripts/fetch-binary.js ./scripts/fetch-binary.js
COPY catalog.json package.json ./

# Copy both RID trees into the build context, then keep only the one matching
# TARGETARCH. This avoids a per-platform build-arg (buildx builds one Dockerfile
# for all platforms) while keeping the final image lean (one RID tree, one model).
# NOTE: this doubles the build context (~450 MB) because both trees are
# transferred before one is discarded; a --build-arg RID + single-tree COPY would
# halve it, at the cost of a per-platform build. Accepted tradeoff for simplicity.
COPY binaries/ /tmp/all-binaries/
RUN set -eu; \
    case "$TARGETARCH" in \
      amd64) RID=linux-x64 ;; \
      arm64) RID=linux-arm64 ;; \
      *) echo "unsupported TARGETARCH: $TARGETARCH" >&2; exit 1 ;; \
    esac; \
    mkdir -p "/app/binaries/$RID"; \
    cp -a "/tmp/all-binaries/$RID/." "/app/binaries/$RID/"; \
    rm -rf /tmp/all-binaries; \
    test -f "/app/binaries/$RID/models/bge-small-en-v1.5/model.onnx"

# Data dir (TOTAL_RECALL_HOME). Writable by the non-root user even without a
# bind mount (e.g. the CI smoke test). `useradd` is provided by the `passwd`
# package, which is Essential:yes in Debian and therefore present even in the
# -slim image.
ENV TOTAL_RECALL_HOME=/data
RUN useradd --uid "$UID" --create-home --shell /usr/sbin/nologin totalrecall \
    && mkdir -p /data \
    && chown "$UID" /data

USER ${UID}

ENTRYPOINT ["node", "bin/start.js"]
