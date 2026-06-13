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
set -euo pipefail
RID="${1:-$(dotnet --info | awk -F': ' '/RID:/{print $2; exit}')}"
echo "Publishing Host (AOT) for $RID ..."
dotnet publish src/TotalRecall.Host/TotalRecall.Host.csproj \
  -c Release -r "$RID" -p:PublishAot=true 2>&1 | tee /tmp/ui-aot-publish.log

if grep -E "warning IL[0-9]" /tmp/ui-aot-publish.log; then
  echo "AOT trim/warnings detected — failing." >&2
  exit 1
fi

BIN="src/TotalRecall.Host/bin/Release/net8.0/$RID/publish/total-recall"
[ -f "$BIN" ] || BIN="$BIN.exe"
echo "Smoke-booting $BIN ui --smoke ..."
"$BIN" ui --smoke
echo "AOT UI smoke OK"
