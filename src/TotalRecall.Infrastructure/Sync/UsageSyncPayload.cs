using System;
using System.Globalization;
using System.Text;
using TotalRecall.Infrastructure.Usage;

namespace TotalRecall.Infrastructure.Sync;

/// <summary>
/// AOT-safe JSON payload builder for usage telemetry sync queue entries.
/// Serializes a <see cref="UsageEvent"/> into the cortex <c>SyncUsageEvent</c>
/// wire JSON shape. Emits a JSON ARRAY with a single element so the payload
/// round-trips through <c>SyncService.FlushAsync</c>'s
/// <c>SelectMany(JsonSerializer.Deserialize&lt;SyncUsageEvent[]&gt;)</c>.
/// </summary>
internal static class UsageSyncPayload
{
    public static string Event(UsageEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);

        // Cortex has no 5m/1h distinction — sum into a single
        // cache_creation_tokens field. Null only when both are null.
        int? cacheCreation = null;
        if (evt.CacheCreation5m.HasValue || evt.CacheCreation1h.HasValue)
            cacheCreation = (evt.CacheCreation5m ?? 0) + (evt.CacheCreation1h ?? 0);

        var sb = new StringBuilder(256);
        sb.Append("[{");
        AppendStringField(sb, "session_id", evt.SessionId, first: true);
        AppendStringFieldNullable(sb, "host", evt.Host);
        AppendStringFieldNullable(sb, "model", evt.Model);
        AppendStringFieldNullable(sb, "project", evt.ProjectPath);
        // input_tokens / output_tokens are non-nullable ints on the wire;
        // coerce null → 0 so cortex deserialization never fails.
        AppendInt(sb, "input_tokens", evt.InputTokens ?? 0);
        AppendInt(sb, "output_tokens", evt.OutputTokens ?? 0);
        AppendIntNullable(sb, "cache_creation_tokens", cacheCreation);
        AppendIntNullable(sb, "cache_read_tokens", evt.CacheRead);
        AppendStringField(
            sb,
            "timestamp",
            DateTimeOffset.FromUnixTimeMilliseconds(evt.TimestampMs)
                .UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
        sb.Append("}]");
        return sb.ToString();
    }

    private static void AppendStringField(StringBuilder sb, string name, string value, bool first = false)
    {
        if (!first) sb.Append(',');
        sb.Append('"').Append(name).Append("\":\"").Append(Escape(value)).Append('"');
    }

    private static void AppendStringFieldNullable(StringBuilder sb, string name, string? value)
    {
        sb.Append(',').Append('"').Append(name).Append("\":");
        if (value is null) sb.Append("null");
        else sb.Append('"').Append(Escape(value)).Append('"');
    }

    private static void AppendInt(StringBuilder sb, string name, int value)
    {
        sb.Append(',').Append('"').Append(name).Append("\":")
          .Append(value.ToString(CultureInfo.InvariantCulture));
    }

    private static void AppendIntNullable(StringBuilder sb, string name, int? value)
    {
        sb.Append(',').Append('"').Append(name).Append("\":");
        if (value.HasValue) sb.Append(value.Value.ToString(CultureInfo.InvariantCulture));
        else sb.Append("null");
    }

    private static string Escape(string s)
        => s.Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("\n", "\\n")
            .Replace("\r", "\\r")
            .Replace("\t", "\\t");
}
