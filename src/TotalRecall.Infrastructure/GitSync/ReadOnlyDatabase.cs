using Microsoft.Data.Sqlite;
using TotalRecall.Infrastructure.Storage;
using MsSqliteConnection = Microsoft.Data.Sqlite.SqliteConnection;

namespace TotalRecall.Infrastructure.GitSync;

/// <summary>The outcome of <see cref="ReadOnlyDatabase.Open"/>: a connection, or nothing (no file), or why it was not opened.</summary>
public sealed record ReadOnlyOpen(MsSqliteConnection? Connection, string? Unknown);

/// <summary>
/// Opens the database file without being able to change anything: no migration, no pragma, no extension, no
/// <c>-wal</c> or <c>-shm</c> file created. A cleanly closed database (no sidecar) is opened <c>immutable=1</c>, which
/// reads the main file and writes nothing even in a read-only directory; a database with a live writer (both
/// sidecars) is opened read-only, which reads the current state; exactly one sidecar (a crashed writer, a copied file)
/// is not opened at all, because a read-only open would have to create the other one.
/// </summary>
public static class ReadOnlyDatabase
{
    public const string PendingWalText = "a write-ahead log is pending: start total-recall once";

    public static ReadOnlyOpen Open(string dbPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);
        try
        {
            var path = Path.GetFullPath(dbPath);
            if (!File.Exists(path))
                return new ReadOnlyOpen(null, null); // also a missing directory or a dangling link
            var target = File.ResolveLinkTarget(path, returnFinalTarget: true)?.FullName ?? path;
            if (!File.Exists(target))
                return new ReadOnlyOpen(null, null);

            var wal = File.Exists(target + "-wal");
            var shm = File.Exists(target + "-shm");
            if (wal != shm)
                return new ReadOnlyOpen(null, PendingWalText);

            var csb = new SqliteConnectionStringBuilder
            {
                DataSource = wal ? target : ImmutableUri(target, OperatingSystem.IsWindows()),
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            };
            var conn = new MsSqliteConnection(csb.ConnectionString);
            try
            {
                conn.Open();
                // a file that is not a database opens without error and fails on the first statement
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT count(*) FROM sqlite_master";
                cmd.ExecuteScalar();
                return new ReadOnlyOpen(conn, null);
            }
            catch
            {
                conn.Dispose();
                throw;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new ReadOnlyOpen(null, ex.Message);
        }
    }

    /// <summary>
    /// A <c>file:</c> URI for the path with <c>%</c>, <c>?</c> and <c>#</c> percent-encoded and <c>?immutable=1</c> appended
    /// (<c>file:///C:/...</c> for a Windows drive path).
    /// </summary>
    internal static string ImmutableUri(string fullPath, bool windows)
    {
        var p = fullPath.Replace("%", "%25", StringComparison.Ordinal)
            .Replace("?", "%3F", StringComparison.Ordinal)
            .Replace("#", "%23", StringComparison.Ordinal);
        if (windows)
            p = "/" + p.Replace('\\', '/');
        return "file://" + p + "?immutable=1";
    }
}

/// <summary>
/// An <see cref="IMetaStore"/> that only reads, over a read-only connection (or none: every read is null). A missing
/// <c>_meta</c> table reads as null; any other error propagates to the caller, which reports it as unknown.
/// </summary>
public sealed class ReadOnlyMeta(MsSqliteConnection? connection) : IMetaStore
{
    public string? GetMeta(string key)
    {
        if (connection is null)
            return null;
        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT value FROM _meta WHERE key = $k";
            cmd.Parameters.AddWithValue("$k", key);
            return cmd.ExecuteScalar() as string;
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 1 && ex.Message.Contains("no such table", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
    }

    public void SetMeta(string key, string value) => throw new NotSupportedException("this meta store is read-only");

    public void DeleteMeta(string key) => throw new NotSupportedException("this meta store is read-only");
}
