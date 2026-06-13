using System;
using System.IO;
using System.Runtime.InteropServices;
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
    /// <remarks>
    /// The pragma batch (WAL + busy_timeout=5000ms + foreign_keys + synchronous) is
    /// connection-scoped, not file-level. All read-write connections to the
    /// total-recall DB MUST be obtained through this method so they consistently
    /// carry the busy_timeout needed for safe cross-process access (the MCP server
    /// and <c>total-recall ui</c> share one DB file). Read-only importer paths that build
    /// their own connection string intentionally bypass this.
    /// </remarks>
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
                // busy_timeout lets the total-recall UI process wait up to
                // 5 s instead of immediately throwing SQLITE_BUSY when the
                // MCP server holds a write lock on the same file.
                cmd.CommandText =
                    "PRAGMA journal_mode=WAL;" +
                    "PRAGMA busy_timeout=5000;" +
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
    /// <c>{AppContext.BaseDirectory}/runtimes/vec0.{so,dll,dylib}</c> by the
    /// Infrastructure project's MSBuild step, which in turn sources from
    /// <c>node_modules/sqlite-vec-&lt;rid&gt;/</c> (an optionalDependency of
    /// the <c>sqlite-vec</c> npm package — one native lib per platform).
    /// The file extension is platform-specific and picked here at runtime.
    /// </summary>
    private static string ResolveVecExtensionPath()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "runtimes");
        string fileName;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            fileName = "vec0.dll";
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            fileName = "vec0.dylib";
        }
        else
        {
            fileName = "vec0.so";
        }

        var path = Path.Combine(dir, fileName);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"sqlite-vec native library not found at {path}. " +
                "It should be copied to the output directory by the " +
                "Infrastructure project's MSBuild step, which pulls from " +
                "node_modules/sqlite-vec-<rid>/. Run `npm install` at the " +
                "repo root to populate node_modules for your platform. " +
                "If the problem persists, download the lib from " +
                "https://github.com/asg017/sqlite-vec/releases and place it " +
                "at the expected path.",
                path);
        }
        return path;
    }
}
