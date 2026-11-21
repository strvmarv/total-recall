using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Microsoft.Data.Sqlite;

namespace TotalRecall.TestSupport.Transcripts;

/// <summary>
/// The "nothing of a planted secret is stored" assertion (spec section 10): no window of the secret, ignoring case,
/// in any text of the archive tables, in the text index, or in the bytes of the database, WAL and shared-memory files.
/// Reads the database through Microsoft.Data.Sqlite directly, so it needs no internal API.
/// </summary>
public static class LeakScan
{
    /// <summary>Every window of <paramref name="width"/> characters of <paramref name="planted"/> (the text itself when it is shorter).</summary>
    public static IReadOnlyList<string> Windows(string planted, int width = 8)
    {
        if (planted.Length == 0) return [];
        if (planted.Length <= width) return [planted];
        var windows = new List<string>(planted.Length - width + 1);
        for (var i = 0; i + width <= planted.Length; i++) windows.Add(planted.Substring(i, width));
        return windows;
    }

    /// <summary>Throws when any window of <paramref name="planted"/> is found anywhere in the database or its side files.</summary>
    public static void AssertNotStored(string dbPath, string planted)
    {
        var windows = Windows(planted);

        // The files first: closing the last connection below may checkpoint and remove the WAL.
        foreach (var path in new[] { dbPath, dbPath + "-wal", dbPath + "-shm" })
        {
            if (!File.Exists(path)) continue;
            var text = ReadShared(path);
            foreach (var w in windows)
                if (text.Contains(w, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"a window of the planted secret is in the bytes of {Path.GetFileName(path)}");
        }

        foreach (var (table, column, value) in TextValues(dbPath))
            foreach (var w in windows)
                if (value.Contains(w, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"a window of the planted secret is in {table}.{column}");
    }

    /// <summary>Throws unless <paramref name="text"/> is found (ordinal) in some text of <c>tx_session</c> or <c>tx_chunk</c>.</summary>
    public static void AssertStored(string dbPath, string text)
    {
        foreach (var (table, _, value) in TextValues(dbPath))
            if (table != "tx_chunk_fts" && value.Contains(text, StringComparison.Ordinal)) return;
        throw new InvalidOperationException("the text is not stored in tx_session or tx_chunk: " + text);
    }

    private static string ReadShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var ms = new MemoryStream();
        fs.CopyTo(ms);
        // Latin-1 maps each byte to one char, so an ASCII window is found wherever its bytes lie.
        return Encoding.Latin1.GetString(ms.GetBuffer(), 0, (int)ms.Length);
    }

    private static IEnumerable<(string Table, string Column, string Value)> TextValues(string dbPath)
    {
        var csb = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        };
        var found = new List<(string, string, string)>();
        using var conn = new SqliteConnection(csb.ToString());
        conn.Open();
        foreach (var (table, columns) in new (string, string[])[]
                 {
                     ("tx_session", Columns(conn, "tx_session")),
                     ("tx_chunk", Columns(conn, "tx_chunk")),
                     ("tx_chunk_fts", ["content"]),
                 })
        {
            foreach (var column in columns)
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = $"SELECT CAST(\"{column}\" AS TEXT) FROM {table}";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                    if (!reader.IsDBNull(0)) found.Add((table, column, reader.GetString(0)));
            }
        }
        return found;
    }

    private static string[] Columns(SqliteConnection conn, string table)
    {
        var names = new List<string>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT name FROM pragma_table_info('{table}')";
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) names.Add(reader.GetString(0));
        return names.ToArray();
    }
}
