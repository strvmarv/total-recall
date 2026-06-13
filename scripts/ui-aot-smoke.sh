#!/usr/bin/env bash
# AOT-publish the Host with the embedded Web UI and smoke-boot the server.
# Usage: scripts/ui-aot-smoke.sh [rid]   (default rid: host platform)
#
# SPIKE-CONFIRMED PREREQUISITES:
#   * win-x64 AOT needs the MSVC linker + vswhere on PATH. Run this from a
#     "Developer Command Prompt for VS" or first init vcvars64.bat, e.g.:
#       cmd //c '"C:\Program Files\Microsoft Visual Studio\18\Professional\VC\Auxiliary\Build\vcvars64.bat" && bash scripts/ui-aot-smoke.sh'
#     Without it the publish FALSE-fails with vswhere/link.exe (code 123).
#     Requires the VS "Desktop development with C++" workload.
#   * linux-x64 AOT cannot be cross-compiled from Windows ("Cross-OS native
#     compilation is not supported") — run the linux leg on a Linux CI runner.
#   * Run `npm ci` at the repo root first so the sqlite-vec native libs are
#     present (the Host publish guard fails without them).
#   * -p:BuildSpa=true makes the TotalRecall.Web build run `npm ci && vite build`
#     in src/TotalRecall.Web/ClientApp and embed the real SPA, so Node/npm must be
#     available. Without it the embedded UI is only the Node-free placeholder.
set -euo pipefail
# Resolve the host RID, trimming the alignment padding `dotnet --info` adds (and any CR).
RID="${1:-$(dotnet --info | sed -n 's/.*RID:[[:space:]]*//p' | head -1 | tr -d '[:space:]')}"
echo "Publishing Host (AOT, real SPA) for $RID ..."
dotnet publish src/TotalRecall.Host/TotalRecall.Host.csproj \
  -c Release -r "$RID" -p:PublishAot=true -p:BuildSpa=true 2>&1 | tee /tmp/ui-aot-publish.log

if grep -E "warning IL[0-9]" /tmp/ui-aot-publish.log; then
  echo "AOT trim/warnings detected — failing." >&2
  exit 1
fi

# Locate the published binary. A vcvars64-initialized shell exports Platform=x64,
# nesting output under bin/x64/Release/...; a plain publish uses bin/Release/...
BIN=""
for base in "src/TotalRecall.Host/bin/Release" "src/TotalRecall.Host/bin/x64/Release"; do
  for cand in \
    "$base/net8.0/$RID/publish/total-recall" \
    "$base/net8.0/$RID/publish/total-recall.exe"; do
    if [ -f "$cand" ]; then BIN="$cand"; break 2; fi
  done
done
if [ -z "$BIN" ]; then
  echo "Could not find published total-recall binary for $RID under bin/Release or bin/x64/Release." >&2
  exit 1
fi
echo "Smoke-booting $BIN ui (real SPA) ..."
"$BIN" ui --no-open --port 5588 &
UI_PID=$!
trap 'kill "$UI_PID" 2>/dev/null || true' EXIT
# Wait for readiness — the real Host opens the DB and runs the migration guard on boot.
for _ in $(seq 1 20); do
  curl -sf http://127.0.0.1:5588/api/health >/dev/null 2>&1 && break
  sleep 1
done
ROOT=$(curl -s http://127.0.0.1:5588/)
echo "$ROOT" | grep -q "window.__TR_BOOTSTRAP__" || { echo "FAIL: bootstrap not injected" >&2; exit 1; }
ASSET=$(echo "$ROOT" | grep -oE '/assets/[^"]+\.js' | head -1)
[ -n "$ASSET" ] || { echo "FAIL: no SPA asset referenced in index.html" >&2; exit 1; }
curl -sf "http://127.0.0.1:5588$ASSET" >/dev/null || { echo "FAIL: SPA asset not served ($ASSET)" >&2; exit 1; }
echo "AOT UI smoke OK (real SPA embedded + served)"
