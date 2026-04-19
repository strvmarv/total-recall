using System;
using System.Globalization;
using System.Text;

namespace TotalRecall.Infrastructure.Sync;

/// <summary>
/// AOT-safe JSON payload builder for compaction telemetry sync queue entries.
/// Emits a single-element JSON array matching cortex's <c>SyncCompactionEntry[]</c>
/// wire shape (consistent with <see cref="UsageSyncPayload"/> and
/// <see cref="RetrievalSyncPayload"/>), so <c>SyncService.FlushAsync</c>'s
/// compaction branch deserializes cleanly.
/// </summary>
public static class CompactionSyncPayload
{
    public static string Event(
        string entryId,
        string fromTier,
        string toTier,
        string action,
        double? semanticDrift,
        double? decayScore,
        DateTime timestampUtc)
    {
        var sb = new StringBuilder("[{");
        Append(sb, "entry_id", entryId, first: true);
        Append(sb, "from_tier", fromTier);
        Append(sb, "to_tier", toTier);
        Append(sb, "action", action);
        AppendDoubleOrNull(sb, "semantic_drift", semanticDrift);
        AppendDoubleOrNull(sb, "decay_score", decayScore);
        Append(sb, "timestamp", timestampUtc.ToString("O", CultureInfo.InvariantCulture));
        sb.Append("}]");
        return sb.ToString();
    }

    private static void Append(StringBuilder sb, string name, string value, bool first = false)
    {
        if (!first) sb.Append(',');
        sb.Append('"').Append(name).Append("\":\"").Append(Escape(value)).Append('"');
    }

    private static void AppendDoubleOrNull(StringBuilder sb, string name, double? value)
    {
        sb.Append(",\"").Append(name).Append("\":");
        sb.Append(value?.ToString("G17", CultureInfo.InvariantCulture) ?? "null");
    }

    private static string Escape(string s)
        => s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");
}
