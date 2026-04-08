using System;
using System.IO;
using MsSqliteConnection = Microsoft.Data.Sqlite.SqliteConnection;

namespace TotalRecall.Infrastructure.Storage;

/// <summary>
/// Factory for opening <see cref="MsSqliteConnection"/> instances that have
/// the sqlite-vec extension loaded and the standard total-recall pragmas
/// applied.
///
/// Mirrors <c>src-ts/db/connection.ts</c> which calls
/// <c>sqlite-vec.load(db)</c> right after opening the bun:sqlite database.
/// The .NET equivalent is <see cref="MsSqliteConnection.EnableExtensions"/>
/// followed by <see cref="MsSqliteConnection.LoadExtension(string)"/>.
/// </summary>
public static class SqliteConnection
{
    /// <summary>
    /// Open a SQLite connection, load the sqlite-vec extension, and apply
    /// the standard pragmas (WAL, foreign keys, synchronous=NORMAL).
    /// </summary>
    /// <param name="dbPath">
    /// Path to the database file, or <c>:memory:</c> for an in-memory DB.
    /// </param>
    /// <returns>An open connection ready for schema initialization.</returns>
    public static MsSqliteConnection Open(string dbPath)
    {
        var conn = new MsSqliteConnection($"Data Source={dbPath}");
        try
        {
            conn.Open();
            conn.EnableExtensions(true);
            conn.LoadExtension(ResolveVecExtensionPath());

            using (var cmd = conn.CreateCommand())
            {
                // journal_mode is a no-op for :memory: but harmless.
                cmd.CommandText =
                    "PRAGMA journal_mode=WAL;" +
                    "PRAGMA foreign_keys=ON;" +
                    "PRAGMA synchronous=NORMAL;";
                cmd.ExecuteNonQuery();
            }

            return conn;
        }
        catch
        {
            conn.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Resolve the sqlite-vec native library path. Convention: placed under
    /// <c>{AppContext.BaseDirectory}/runtimes/vec0.so</c> by an MSBuild copy
    /// step (see <c>TotalRecall.Infrastructure.csproj</c>).
    /// </summary>
    private static string ResolveVecExtensionPath()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "runtimes");
        var path = Path.Combine(dir, "vec0.so");
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"sqlite-vec native library not found at {path}. " +
                "It should be copied to the output directory by the " +
                "Infrastructure project's MSBuild step. Download from " +
                "https://github.com/asg017/sqlite-vec/releases if missing.",
                path);
        }
        return path;
    }
}
