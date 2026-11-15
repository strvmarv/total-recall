using System;
using System.Collections.Generic;
using System.Text;
using TotalRecall.Core;
using TotalRecall.Infrastructure.Storage;
using MsSqliteConnection = Microsoft.Data.Sqlite.SqliteConnection;

namespace TotalRecall.Infrastructure.Tests.TestSupport;

/// <summary>
/// A stable text dump of everything a store transaction can touch: every
/// content table (ordered by id), the vector row joined to each content row
/// by rowid plus the total vector-row count of each vector table (so an
/// orphan vector row shows up), and the import log. Two databases (or one
/// database before and after a rolled-back transaction) are "the same" when
/// their snapshots are equal.
/// </summary>
internal static class StoreSnapshot
{
    private static readonly HashSet<string> TimeColumns = new(StringComparer.Ordinal)
    {
        "created_at", "updated_at", "last_accessed_at",
    };

    public static string Take(MsSqliteConnection conn, bool maskTimes = false)
    {
        var sb = new StringBuilder();
        foreach (var tier in new[] { Tier.Hot, Tier.Warm, Tier.Cold })
        {
            foreach (var type in new[] { ContentType.Memory, ContentType.Knowledge })
            {
                var table = MigrationRunner.TableName(tier, type);
                var vec = MigrationRunner.VecTableName(tier, type);
                sb.Append("## ").AppendLine(table);
                DumpRows(conn, sb, $"SELECT * FROM {table} ORDER BY id", maskTimes, TimeColumns);

                sb.Append("vec ").AppendLine(vec);
                DumpRows(conn, sb,
                    $"SELECT m.id AS id, v.embedding AS embedding FROM {table} m " +
                    $"JOIN {vec} v ON v.rowid = m.rowid ORDER BY m.id",
                    maskTimes: false, TimeColumns);
                using var count = conn.CreateCommand();
                count.CommandText = $"SELECT count(*) FROM {vec}";
                sb.Append("vecrows=").AppendLine(Convert.ToString(count.ExecuteScalar()));
            }
        }

        sb.AppendLine("## import_log");
        DumpRows(conn, sb, "SELECT * FROM import_log ORDER BY id", maskTimes,
            new HashSet<string>(StringComparer.Ordinal) { "timestamp" });
        return sb.ToString();
    }

    public static string Hex(ReadOnlyMemory<float> embedding) =>
        Convert.ToHexString(System.Runtime.InteropServices.MemoryMarshal.AsBytes(embedding.Span));

    private static void DumpRows(
        MsSqliteConnection conn, StringBuilder sb, string sql,
        bool maskTimes, HashSet<string> maskedColumns)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            for (var i = 0; i < reader.FieldCount; i++)
            {
                var name = reader.GetName(i);
                sb.Append(name).Append('=');
                if (maskTimes && maskedColumns.Contains(name)) sb.Append('*');
                else if (reader.IsDBNull(i)) sb.Append("NULL");
                else if (reader.GetValue(i) is byte[] blob) sb.Append(Convert.ToHexString(blob));
                else sb.Append(Convert.ToString(reader.GetValue(i), System.Globalization.CultureInfo.InvariantCulture));
                sb.Append('|');
            }
            sb.AppendLine();
        }
    }
}
