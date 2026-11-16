using Microsoft.Data.Sqlite;
using TotalRecall.Infrastructure.Storage;
using MsSqliteConnection = Microsoft.Data.Sqlite.SqliteConnection;

namespace TotalRecall.Infrastructure.Tests.GitSync;

/// <summary>Helpers that make small database files for the sync setup tests (pooling off, so a file can be moved or probed after).</summary>
internal static class SyncDb
{
    /// <summary>A fully migrated database file at <paramref name="path"/>, closed cleanly (no <c>-wal</c> or <c>-shm</c> left).</summary>
    public static void CreateMigrated(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var conn = TotalRecall.Infrastructure.Storage.SqliteConnection.Open(path, pooling: false))
            MigrationRunner.RunMigrations(conn);
        MsSqliteConnection.ClearAllPools();
    }

    public static MsSqliteConnection OpenMigrated(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var conn = TotalRecall.Infrastructure.Storage.SqliteConnection.Open(path, pooling: false);
        MigrationRunner.RunMigrations(conn);
        return conn;
    }

    /// <summary>A plain SQLite file with exactly the given statements, closed cleanly and in rollback-journal mode.</summary>
    public static void CreatePlain(string path, params string[] statements)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var csb = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false };
        using (var conn = new MsSqliteConnection(csb.ConnectionString))
        {
            conn.Open();
            foreach (var sql in statements)
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = sql;
                cmd.ExecuteNonQuery();
            }
        }
        MsSqliteConnection.ClearAllPools();
    }

    public static void Exec(MsSqliteConnection conn, string sql, params (string Name, object Value)[] parameters)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value);
        cmd.ExecuteNonQuery();
    }

    public static long Count(MsSqliteConnection conn, string table)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT count(*) FROM {table}";
        return (long)cmd.ExecuteScalar()!;
    }

    public static string? Meta(MsSqliteConnection conn, string key)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM _meta WHERE key = $k";
        cmd.Parameters.AddWithValue("$k", key);
        return cmd.ExecuteScalar() as string;
    }

    /// <summary>Every file below the directory with its bytes' hash, for before/after comparisons.</summary>
    public static Dictionary<string, string> Snapshot(string directory) =>
        Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.AllDirectories)
            .ToDictionary(
                p => Path.GetRelativePath(directory, p),
                p => File.Exists(p) ? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(p))) : "<dir>");
}
