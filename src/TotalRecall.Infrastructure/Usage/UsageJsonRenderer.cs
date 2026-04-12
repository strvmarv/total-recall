// src/TotalRecall.Infrastructure/Usage/UsageJsonRenderer.cs
//
// Shared JSON emitter for the usage report wire shape consumed by both
// the `total-recall usage --json` CLI (Task 12) and the `usage_status`
// MCP tool handler (Task 13). Extracted so both call sites share the
// exact same byte-for-byte shape (`query`, `buckets[]`, `grand_total`,
// `coverage`). Hand-rolled against JsonWriter to stay AOT-safe and off
// System.Text.Json source-gen contexts.

using System.Collections.Generic;
using System.Text;
using TotalRecall.Infrastructure.Json;

namespace TotalRecall.Infrastructure.Usage;

public static class UsageJsonRenderer
{
    /// <summary>
    /// Render a <see cref="UsageReport"/> + the originating
    /// <see cref="UsageQuery"/> into the canonical usage JSON shape.
    /// The returned string contains no trailing newline.
    /// </summary>
    public static string Render(UsageReport report, UsageQuery query)
    {
        var sb = new StringBuilder();
        sb.Append('{');

        // query block
        sb.Append("\"query\":{");
        sb.Append("\"start_ms\":");
        JsonWriter.AppendNumber(sb, query.Start.ToUnixTimeMilliseconds());
        sb.Append(",\"end_ms\":");
        JsonWriter.AppendNumber(sb, query.End.ToUnixTimeMilliseconds());
        sb.Append(",\"group_by\":");
        JsonWriter.AppendString(sb, query.GroupBy.ToString().ToLowerInvariant());
        sb.Append(",\"host_filter\":");
        AppendStringArrayOrNull(sb, query.HostFilter);
        sb.Append(",\"project_filter\":");
        AppendStringArrayOrNull(sb, query.ProjectFilter);
        sb.Append('}');

        // buckets array
        sb.Append(",\"buckets\":[");
        for (var i = 0; i < report.Buckets.Count; i++)
        {
            if (i > 0) sb.Append(',');
            AppendBucket(sb, report.Buckets[i]);
        }
        sb.Append(']');

        // grand_total
        sb.Append(",\"grand_total\":");
        AppendTotals(sb, report.GrandTotal);

        // coverage
        var full = report.SessionsWithFullTokenData;
        var partial = report.SessionsWithPartialTokenData;
        var totalSessions = full + partial;
        var pct = totalSessions == 0 ? 0.0 : 100.0 * full / totalSessions;
        sb.Append(",\"coverage\":{\"sessions_with_full_token_data\":");
        JsonWriter.AppendNumber(sb, full);
        sb.Append(",\"sessions_with_partial_token_data\":");
        JsonWriter.AppendNumber(sb, partial);
        sb.Append(",\"fidelity_percent\":");
        sb.Append(pct.ToString("F2", System.Globalization.CultureInfo.InvariantCulture));
        sb.Append('}');

        sb.Append('}');
        return sb.ToString();
    }

    private static void AppendStringArrayOrNull(StringBuilder sb, IReadOnlyList<string>? list)
    {
        if (list is null)
        {
            JsonWriter.AppendNull(sb);
            return;
        }
        sb.Append('[');
        for (var i = 0; i < list.Count; i++)
        {
            if (i > 0) sb.Append(',');
            JsonWriter.AppendString(sb, list[i]);
        }
        sb.Append(']');
    }

    private static void AppendBucket(StringBuilder sb, UsageBucket b)
    {
        sb.Append('{');
        sb.Append("\"key\":");
        JsonWriter.AppendString(sb, b.Key);
        sb.Append(',');
        AppendTotalsBody(sb, b.Totals);
        sb.Append('}');
    }

    private static void AppendTotals(StringBuilder sb, UsageTotals t)
    {
        sb.Append('{');
        AppendTotalsBody(sb, t);
        sb.Append('}');
    }

    private static void AppendTotalsBody(StringBuilder sb, UsageTotals t)
    {
        sb.Append("\"session_count\":");
        JsonWriter.AppendNumber(sb, t.SessionCount);
        sb.Append(",\"turn_count\":");
        JsonWriter.AppendNumber(sb, t.TurnCount);
        sb.Append(",\"input_tokens\":");
        AppendNullableLong(sb, t.InputTokens);
        sb.Append(",\"cache_creation_tokens\":");
        AppendNullableLong(sb, t.CacheCreationTokens);
        sb.Append(",\"cache_read_tokens\":");
        AppendNullableLong(sb, t.CacheReadTokens);
        sb.Append(",\"output_tokens\":");
        AppendNullableLong(sb, t.OutputTokens);
    }

    private static void AppendNullableLong(StringBuilder sb, long? v)
    {
        if (v is long n) JsonWriter.AppendNumber(sb, n);
        else JsonWriter.AppendNull(sb);
    }
}
