using System;
using System.Text;
using Microsoft.FSharp.Core;
using TotalRecall.Core;
using TotalRecall.Infrastructure.Memory;

namespace TotalRecall.Infrastructure.Sync;

/// <summary>
/// AOT-safe JSON payload builders for sync queue entries.
/// Avoids reflection-based JsonSerializer in the AOT binary.
/// </summary>
internal static class SyncPayload
{
    /// <summary>
    /// Serializes an <see cref="Entry"/> plus its <see cref="ContentType"/> into the
    /// snake_case JSON shape expected by cortex's PluginSyncMemoryRequest DTO. Emits
    /// every sync-relevant field so SyncService.FlushAsync can hydrate a full
    /// SyncEntry without dropping to placeholders.
    /// </summary>
    public static string Upsert(Entry entry, ContentType contentType, Tier tier)
    {
        var sb = new StringBuilder(256);
        sb.Append('{');

        AppendStringField(sb, "id", entry.Id);
        sb.Append(',');
        AppendStringField(sb, "content", entry.Content);
        sb.Append(',');
        AppendStringField(sb, "entry_type", entry.EntryType.ToString());
        sb.Append(',');
        AppendStringField(sb, "content_type", contentType.IsMemory ? "Memory" : "Kb");
        sb.Append(',');
        AppendStringField(sb, "tier", TierNames.TierName(tier));
        sb.Append(',');

        // tags: string list → JSON array
        sb.Append("\"tags\":[");
        var firstTag = true;
        foreach (var tag in entry.Tags)
        {
            if (!firstTag) sb.Append(',');
            firstTag = false;
            sb.Append('"').Append(Escape(tag)).Append('"');
        }
        sb.Append("],");

        // source: string option → nullable string
        sb.Append("\"source\":");
        if (FSharpOption<string>.get_IsSome(entry.Source))
        {
            sb.Append('"').Append(Escape(entry.Source.Value)).Append('"');
        }
        else
        {
            sb.Append("null");
        }
        sb.Append(',');

        sb.Append("\"access_count\":").Append(entry.AccessCount).Append(',');

        // decay_score: use InvariantCulture "R" round-trip format
        sb.Append("\"decay_score\":")
          .Append(entry.DecayScore.ToString("R", System.Globalization.CultureInfo.InvariantCulture))
          .Append(',');

        // created_at / updated_at: int64 epoch-ms → ISO-8601 round-trip UTC
        var createdIso = DateTimeOffset.FromUnixTimeMilliseconds(entry.CreatedAt)
            .UtcDateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        var updatedIso = DateTimeOffset.FromUnixTimeMilliseconds(entry.UpdatedAt)
            .UtcDateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        AppendStringField(sb, "created_at", createdIso);
        sb.Append(',');
        AppendStringField(sb, "updated_at", updatedIso);
        sb.Append(',');

        // scope: non-null string per schema, but emit null when empty to match
        // cortex's optional-scope wire contract.
        sb.Append("\"scope\":");
        if (string.IsNullOrEmpty(entry.Scope))
        {
            sb.Append("null");
        }
        else
        {
            sb.Append('"').Append(Escape(entry.Scope)).Append('"');
        }

        sb.Append('}');
        return sb.ToString();
    }

    public static string Delete(string id)
        => $$"""{"id":"{{Escape(id)}}"}""";

    private static void AppendStringField(StringBuilder sb, string key, string value)
    {
        sb.Append('"').Append(key).Append("\":\"").Append(Escape(value)).Append('"');
    }

    private static string Escape(string s)
        => s.Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("\n", "\\n")
            .Replace("\r", "\\r")
            .Replace("\t", "\\t");
}
