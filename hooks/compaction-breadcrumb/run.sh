#!/usr/bin/env bash
# total-recall compaction breadcrumb hook (Claude Code; SessionStart, matcher "compact").
#
# Claude Code runs this once per compaction (hooks/hooks.json registers it in a group whose matcher is
# `compact`, so the payload's `source` is never read here). It tells the model that the earlier text of
# the session is archived and how to search it, but only while the transcript archive is on: the
# sentinel file `<dataDir>/live/enabled` (written by the engine and by `transcripts enable`) says so.
# Decision 11 of the transcript archive design: no count of exchanges, no ingest, no engine.
#
# It prints nothing and starts no process until the sentinel test passes, and even after it only
# builtins run (parameter expansion, case, [ -f ], printf), so with the archive off a compaction pays
# nothing but this shell. It never reads stdin and always exits 0, so it cannot hurt a compaction.
set -u

# The data directory, by the engine's rule (ConfigLoader.GetDataDir) and by the same rule as
# hooks/live-marker/run.sh (the shared table is hooks/live-marker/data-dir-cases.json, and both scripts
# are tested against it): a non-empty TOTAL_RECALL_HOME as it is, else `.total-recall` under HOME when
# HOME is set at all (even empty), else under USERPROFILE. Anything that is not an absolute path is
# left alone (the engine's relative directory cannot be matched from a hook's working directory).
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

# Archive off: no sentinel file, nothing is said.
[ -f "$dir/live/enabled" ] || exit 0

# The note is one JSON line. The text holds no digits and no session id: the tools find the current
# session themselves (their answer's `currentSession` says how), and on a platform with no live-session
# marker (Windows) `scope "session"` is refused, which the last line covers.
printf '%s\n' '{"hookSpecificOutput":{"hookEventName":"SessionStart","additionalContext":"total-recall: this conversation was just compacted, but the full text of this session is archived and searchable.\nTo recall what the summary left out, call transcript_search with scope \"session\" and a query for the topic; the answer'"'"'s currentSession says how the session was found.\nRead around a hit with transcript_get, passing the id of the result.\nIf the answer says the current session is not known here, search again without scope."}}'
exit 0
