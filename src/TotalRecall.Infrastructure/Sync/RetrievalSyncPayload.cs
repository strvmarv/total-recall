using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace TotalRecall.Infrastructure.Sync;

/// <summary>
/// AOT-safe JSON payload builder for retrieval telemetry sync queue entries.
/// Emits a single-element JSON array matching cortex's <c>SyncRetrievalEvent[]</c>
/// wire shape (consistent with <see cref="UsageSyncPayload"/>), so
/// <c>SyncService.FlushAsync</c>'s retrieval branch deserializes cleanly.
/// </summary>
public static class RetrievalSyncPayload
{
    public static string Event(
        string query,
        IReadOnlyList<string> tiersSearched,
        int topK,
        double topScore,
        int resultCount,
        double latencyMs,
        string? outcomeSignal,
        DateTime timestampUtc)
    {
        var sb = new StringBuilder("[{");
        Append(sb, "query", query, first: true);
        sb.Append(",\"tiers_searched\":");
        AppendStringArray(sb, tiersSearched);
        AppendInt(sb, "top_k", topK);
        AppendDouble(sb, "top_score", topScore);
        AppendInt(sb, "result_count", resultCount);
        AppendDouble(sb, "latency_ms", latencyMs);
        sb.Append(",\"outcome_signal\":");
        if (outcomeSignal is null) sb.Append("null");
        else sb.Append('"').Append(Escape(outcomeSignal)).Append('"');
        Append(sb, "timestamp", timestampUtc.ToString("O", CultureInfo.InvariantCulture));
        sb.Append("}]");
        return sb.ToString();
    }

    private static void Append(StringBuilder sb, string name, string value, bool first = false)
    {
        if (!first) sb.Append(',');
        sb.Append('"').Append(name).Append("\":\"").Append(Escape(value)).Append('"');
    }

    private static void AppendInt(StringBuilder sb, string name, int value)
        => sb.Append(",\"").Append(name).Append("\":").Append(value.ToString(CultureInfo.InvariantCulture));

    private static void AppendDouble(StringBuilder sb, string name, double value)
        => sb.Append(",\"").Append(name).Append("\":").Append(value.ToString("G17", CultureInfo.InvariantCulture));

    private static void AppendStringArray(StringBuilder sb, IReadOnlyList<string> items)
    {
        sb.Append('[');
        for (var i = 0; i < items.Count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append('"').Append(Escape(items[i])).Append('"');
        }
        sb.Append(']');
    }

    private static string Escape(string s)
        => s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");
}
