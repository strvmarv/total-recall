// src/TotalRecall.Infrastructure/Config/ConfigJsonSerializer.cs
//
// Plan 5 Task 5.3b — hand-rolled, AOT-safe JSON serializer for
// Core.Config.TotalRecallConfig. Used by `eval snapshot` to build the
// config JSON body that ConfigSnapshotStore persists. The field names
// mirror the TS wire shape exactly (snake_case top-level keys, camel or
// snake depending on the TS source) so TS-generated snapshots and
// .NET-generated snapshots round-trip against the same config_snapshots
// table. Uses stable ordering so the byte-equal dedup in
// ConfigSnapshotStore.CreateSnapshot is meaningful.

using System.Globalization;
using System.Text;
using TotalRecall.Core;

namespace TotalRecall.Infrastructure.Config;

public static class ConfigJsonSerializer
{
    /// <summary>
    /// Serialize <paramref name="config"/> as a compact, stable-key-order
    /// JSON object. Key order is fixed in source code, not alphabetical,
    /// to match the TS createConfigSnapshot output.
    /// </summary>
    public static string Serialize(Core.Config.TotalRecallConfig config)
    {
        var sb = new StringBuilder();
        sb.Append('{');

        // tiers
        sb.Append("\"tiers\":{");
        sb.Append("\"hot\":{");
        AppendInt(sb, "max_entries", config.Tiers.Hot.MaxEntries); sb.Append(',');
        AppendInt(sb, "token_budget", config.Tiers.Hot.TokenBudget); sb.Append(',');
        AppendDouble(sb, "carry_forward_threshold", config.Tiers.Hot.CarryForwardThreshold);
        sb.Append("},\"warm\":{");
        AppendInt(sb, "max_entries", config.Tiers.Warm.MaxEntries); sb.Append(',');
        AppendInt(sb, "retrieval_top_k", config.Tiers.Warm.RetrievalTopK); sb.Append(',');
        AppendDouble(sb, "similarity_threshold", config.Tiers.Warm.SimilarityThreshold); sb.Append(',');
        AppendInt(sb, "cold_decay_days", config.Tiers.Warm.ColdDecayDays);
        sb.Append("},\"cold\":{");
        AppendInt(sb, "chunk_max_tokens", config.Tiers.Cold.ChunkMaxTokens); sb.Append(',');
        AppendInt(sb, "chunk_overlap_tokens", config.Tiers.Cold.ChunkOverlapTokens); sb.Append(',');
        AppendInt(sb, "lazy_summary_threshold", config.Tiers.Cold.LazySummaryThreshold);
        sb.Append("}},");

        // compaction
        sb.Append("\"compaction\":{");
        AppendDouble(sb, "decay_half_life_hours", config.Compaction.DecayHalfLifeHours); sb.Append(',');
        AppendDouble(sb, "warm_threshold", config.Compaction.WarmThreshold); sb.Append(',');
        AppendDouble(sb, "promote_threshold", config.Compaction.PromoteThreshold); sb.Append(',');
        AppendInt(sb, "warm_sweep_interval_days", config.Compaction.WarmSweepIntervalDays);
        sb.Append("},");

        // embedding
        sb.Append("\"embedding\":{");
        AppendString(sb, "model", config.Embedding.Model); sb.Append(',');
        AppendInt(sb, "dimensions", config.Embedding.Dimensions);
        sb.Append('}');

        // regression (optional)
        if (Microsoft.FSharp.Core.FSharpOption<Core.Config.RegressionConfig>.get_IsSome(config.Regression))
        {
            var reg = config.Regression.Value;
            sb.Append(",\"regression\":{");
            bool firstR = true;
            if (Microsoft.FSharp.Core.FSharpOption<double>.get_IsSome(reg.MissRateDelta))
            {
                AppendDouble(sb, "miss_rate_delta", reg.MissRateDelta.Value);
                firstR = false;
            }
            if (Microsoft.FSharp.Core.FSharpOption<double>.get_IsSome(reg.LatencyRatio))
            {
                if (!firstR) sb.Append(','); firstR = false;
                AppendDouble(sb, "latency_ratio", reg.LatencyRatio.Value);
            }
            if (Microsoft.FSharp.Core.FSharpOption<int>.get_IsSome(reg.MinEvents))
            {
                if (!firstR) sb.Append(',');
                AppendInt(sb, "min_events", reg.MinEvents.Value);
            }
            sb.Append('}');
        }

        // search (optional)
        if (Microsoft.FSharp.Core.FSharpOption<Core.Config.SearchConfig>.get_IsSome(config.Search))
        {
            var search = config.Search.Value;
            sb.Append(",\"search\":{");
            if (Microsoft.FSharp.Core.FSharpOption<double>.get_IsSome(search.FtsWeight))
            {
                AppendDouble(sb, "fts_weight", search.FtsWeight.Value);
            }
            sb.Append('}');
        }

        sb.Append('}');
        return sb.ToString();
    }

    private static void AppendInt(StringBuilder sb, string name, int value)
    {
        AppendKey(sb, name);
        sb.Append(value.ToString(CultureInfo.InvariantCulture));
    }

    private static void AppendDouble(StringBuilder sb, string name, double value)
    {
        AppendKey(sb, name);
        sb.Append(value.ToString("R", CultureInfo.InvariantCulture));
    }

    private static void AppendString(StringBuilder sb, string name, string value)
    {
        AppendKey(sb, name);
        sb.Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20)
                        sb.Append("\\u").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
                    else
                        sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
    }

    private static void AppendKey(StringBuilder sb, string name)
    {
        sb.Append('"').Append(name).Append("\":");
    }
}
