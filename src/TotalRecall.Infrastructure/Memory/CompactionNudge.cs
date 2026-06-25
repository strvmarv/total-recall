// src/TotalRecall.Infrastructure/Memory/CompactionNudge.cs
//
// Once-per-session mid-session compaction nudge. Uses a per-session sentinel
// file in the pinned-floor state dir so the nudge fires at most once per
// session, independent of the pinned-floor throttle. Best-effort: any IO
// failure returns null (no nudge) rather than throwing into the hook path.

using System;
using System.IO;

namespace TotalRecall.Infrastructure.Memory;

public static class CompactionNudge
{
    public static string? TryTake(string stateDir, string sessionId, int hotCount, int threshold)
    {
        if (threshold <= 0 || hotCount < threshold) return null;

        try
        {
            Directory.CreateDirectory(stateDir);
            var safeSession = sessionId.Replace(Path.DirectorySeparatorChar, '_').Replace('/', '_');
            var sentinel = Path.Combine(stateDir, $"compaction-nudged-{safeSession}");
            if (File.Exists(sentinel)) return null;
            File.WriteAllText(sentinel, "1");
        }
        catch
        {
            return null;
        }

        return $"total-recall: {hotCount} uncompacted hot-tier entries — consider running "
            + "/total-recall:commands compact (or compact --fast) to free context budget.";
    }
}
