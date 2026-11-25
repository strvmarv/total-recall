#!/usr/bin/env bash
# total-recall live-session marker hook (Claude Code; SessionStart and UserPromptSubmit).
#
# Decides whether to start hooks/live-marker/write-marker.js, which records which session this
# host process is running. It never prints and always exits 0, so it cannot hurt a prompt.
#
# Until the sentinel test passes this script starts NO process: only builtins run (parameter
# expansion, case, [ -f ]), so with the archive off a prompt pays nothing but this shell.
set -u

# 1. The data directory, by the engine's rule (ConfigLoader.GetDataDir): a non-empty
#    TOTAL_RECALL_HOME as it is, else `.total-recall` under HOME when HOME is set at all (even
#    empty), else under USERPROFILE. Anything that is not an absolute path is left alone (the
#    engine's relative directory cannot be matched from a hook's working directory).
dir=""
if [ -n "${TOTAL_RECALL_HOME:-}" ]; then
  dir=$TOTAL_RECALL_HOME
elif [ -n "${HOME+set}" ]; then
  base=$HOME
  case $base in
    "") dir="" ;;
    */) dir="${base}.total-recall" ;;
    *) dir="$base/.total-recall" ;;
  esac
elif [ -n "${USERPROFILE+set}" ]; then
  base=$USERPROFILE
  case $base in
    "") dir="" ;;
    */) dir="${base}.total-recall" ;;
    *) dir="$base/.total-recall" ;;
  esac
fi
case $dir in
  /*) ;;
  *) exit 0 ;;
esac

# 2. Archive off: no sentinel file, nothing more runs.
[ -f "$dir/live/enabled" ] || exit 0

# 3. No marker on Windows (Git Bash's $PPID is not a Windows pid).
case ${OSTYPE:-} in
  msys* | cygwin* | win32*) exit 0 ;;
esac

# 4. The plugin root comes from this script's own location (never from an environment
#    variable), with builtins only.
src=${BASH_SOURCE[0]:-$0}
case $src in
  */*) src_dir=${src%/*} ;;
  *) src_dir=. ;;
esac
PLUGIN_ROOT=$(cd "$src_dir/../.." 2>/dev/null && pwd) || exit 0
[ -n "$PLUGIN_ROOT" ] || exit 0

# The hook's stdin (the payload) passes through. A missing node is a failed command: still exit 0.
node "$PLUGIN_ROOT/hooks/live-marker/write-marker.js" "$PPID" >/dev/null 2>&1
exit 0
